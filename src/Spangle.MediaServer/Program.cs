using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Spangle.Extensions.Kestrel;
using Spangle.Extensions.Kestrel.DependencyInjection;
using Spangle.Extensions.Kestrel.Management;
using Spangle.Transport.HLS;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSpangle();
builder.WebHost.ConfigureSpangle();

var app = builder.Build();

var options = app.Services.GetRequiredService<IOptions<SpangleMediaServerOptions>>().Value;
var storage = app.Services.GetRequiredService<IHLSStorage>();
var registry = app.Services.GetRequiredService<HLSStreamRegistry>();
var viewerStats = app.Services.GetRequiredService<ViewerStatsRegistry>();
var hlsPathPrefix = options.Hls.RequestPath + "/";

// Control API for the console and for scripts; reachable only via the management port
app.MapSpangleManagement();

// Serve live playlists from memory, implementing LL-HLS blocking reload
// (?_HLS_msn=&_HLS_part=). Falls through to the storage/static serving below.
app.Use(async (ctx, next) =>
{
    PathString path = ctx.Request.Path;
    if (path.Value is { } p
        && p.StartsWith(hlsPathPrefix, StringComparison.OrdinalIgnoreCase)
        && p.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
    {
        // registry entries are keyed {stream}/{playlist}: demuxed CMAF sessions
        // publish several live playlists per stream (video.m3u8 / audio.m3u8)
        string rest = p[hlsPathPrefix.Length..];
        int slashAt = rest.IndexOf('/', StringComparison.Ordinal);
        string registryKey = rest;
        if (slashAt > 0 && rest.IndexOf('/', slashAt + 1) < 0 && registry.TryGet(registryKey, out var live))
        {
            string streamKey = rest[..slashAt];
            viewerStats.OnPlaylistRequest(streamKey);

            // _HLS_skip=YES|v2 requests a playlist delta update (EXT-X-SKIP)
            string? skipValue = ctx.Request.Query["_HLS_skip"];
            bool skip = "YES".Equals(skipValue, StringComparison.OrdinalIgnoreCase)
                        || "v2".Equals(skipValue, StringComparison.OrdinalIgnoreCase);

            string text;
            if (long.TryParse(ctx.Request.Query["_HLS_msn"], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out long msn))
            {
                // RFC 8216bis 6.2.5.2: a request beyond the next two segments is a
                // client error, not something to block 15 seconds on
                if (msn > live.CurrentMsn + 2)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                _ = int.TryParse(ctx.Request.Query["_HLS_part"], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int part);
                viewerStats.WaiterEntered(streamKey);
                try
                {
                    text = await live.WaitForAsync(msn, part, skip, TimeSpan.FromSeconds(15), ctx.RequestAborted).ConfigureAwait(false);
                }
                finally
                {
                    viewerStats.WaiterExited(streamKey);
                }
            }
            else
            {
                text = live.GetCurrent(skip);
            }

            if (text.Length > 0)
            {
                ctx.Response.ContentType = "application/vnd.apple.mpegurl";
                ctx.Response.Headers.CacheControl = "no-cache, no-store";
                await ctx.Response.WriteAsync(text).ConfigureAwait(false);
                return;
            }
        }
    }
    await next().ConfigureAwait(false);
});

// Inject timed metadata into a live session: {"name":"...","value":<string or JSON>}
if (options.Http.MetadataInjection)
{
    var metadataHub = app.Services.GetRequiredService<Spangle.Spinner.TimedMetadataHub>();
    app.MapPost("/api/streams/{streamKey}/metadata",
        async (string streamKey, HttpRequest request) =>
        {
            using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted).ConfigureAwait(false);
            if (!body.RootElement.TryGetProperty("name", out JsonElement nameElement)
                || nameElement.GetString() is not { Length: > 0 } name)
            {
                return Results.BadRequest("A non-empty `name` is required");
            }
            string value = body.RootElement.TryGetProperty("value", out JsonElement valueElement)
                ? valueElement.ValueKind == JsonValueKind.String ? valueElement.GetString()! : valueElement.GetRawText()
                : "";

            return metadataHub.TryInject(streamKey, name, value)
                ? Results.NoContent()
                : Results.NotFound($"No live session under `{streamKey}`");
        });
}

// Clock endpoint for DASH UTCTiming (LL players compute the live edge from it)
app.MapGet("/api/time", () =>
    Results.Text(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        "text/plain"));

// Web console (Blazor WASM) under /console, reachable only via the management port
if (options.Management.Enabled)
{
    int managementPort = options.Management.Port;
    app.Use(async (ctx, next) =>
    {
        if (ctx.Connection.LocalPort != managementPort
            && ctx.Request.Path.StartsWithSegments("/console", StringComparison.OrdinalIgnoreCase))
        {
            // the SPA and its framework files must not leak onto the delivery port
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await next().ConfigureAwait(false);
    });
    app.UseBlazorFrameworkFiles("/console");
    app.MapFallbackToFile("/console/{*path:nonfile}", "console/index.html");
}

app.UseDefaultFiles();
app.UseStaticFiles(); // wwwroot (test player) + the console's static web assets

if (storage is FileHLSStorage)
{
    // File storage: serve segments straight from disk (sendfile, ranges, archives)
    var hlsDirectory = Path.GetFullPath(options.Hls.OutputDirectory);
    Directory.CreateDirectory(hlsDirectory);
    var contentTypes = new FileExtensionContentTypeProvider();
    contentTypes.Mappings[".m3u8"] = "application/vnd.apple.mpegurl";
    contentTypes.Mappings[".ts"] = "video/mp2t";
    contentTypes.Mappings[".m4s"] = "video/iso.segment";
    contentTypes.Mappings[".mpd"] = "application/dash+xml";
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(hlsDirectory),
        RequestPath = options.Hls.RequestPath,
        ContentTypeProvider = contentTypes,
        OnPrepareResponse = static ctx =>
        {
            // Manifests change every segment; segments themselves are immutable
            if (ctx.File.Name.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                || ctx.File.Name.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Context.Response.Headers.CacheControl = "no-cache, no-store";
            }
        },
    });
}
else
{
    // Memory storage: serve segments, parts and init files from the storage itself
    app.Use(async (ctx, next) =>
    {
        PathString path = ctx.Request.Path;
        if (path.Value is { } p && p.StartsWith(hlsPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string rest = p[hlsPathPrefix.Length..];
            int slash = rest.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0 && rest.IndexOf('/', slash + 1) < 0)
            {
                string streamKey = rest[..slash];
                string name = rest[(slash + 1)..];
                if (storage.TryGetStream(streamKey, out var stream))
                {
                    if (name.Equals("playlist.m3u8", StringComparison.OrdinalIgnoreCase)
                        && stream.Playlist is { Length: > 0 } playlist)
                    {
                        ctx.Response.ContentType = "application/vnd.apple.mpegurl";
                        ctx.Response.Headers.CacheControl = "no-cache, no-store";
                        await ctx.Response.WriteAsync(playlist).ConfigureAwait(false);
                        return;
                    }

                    string extension = Path.GetExtension(name).ToLowerInvariant();
                    string contentType = extension switch
                    {
                        ".ts" => "video/mp2t",
                        ".m4s" => "video/iso.segment",
                        ".mp4" => "video/mp4",
                        ".mpd" => "application/dash+xml",
                        ".m3u8" => "application/vnd.apple.mpegurl",
                        _ => "application/octet-stream",
                    };

                    // LL-DASH: a segment still being written streams out chunk by
                    // chunk (chunked transfer) until the writer completes it
                    if (stream is ILiveBlobStreamStorage live && live.TryOpenLiveBlob(name, out var reader))
                    {
                        ctx.Response.ContentType = contentType;
                        while (await reader.ReadNextAsync(ctx.RequestAborted).ConfigureAwait(false) is { } chunk)
                        {
                            await ctx.Response.Body.WriteAsync(chunk, ctx.RequestAborted).ConfigureAwait(false);
                            await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
                        }
                        return;
                    }

                    if (stream.TryReadBlob(name, out ReadOnlyMemory<byte> blob))
                    {
                        ctx.Response.ContentType = contentType;
                        if (extension is ".mpd" or ".m3u8")
                        {
                            ctx.Response.Headers.CacheControl = "no-cache, no-store";
                        }
                        await ctx.Response.Body.WriteAsync(blob, ctx.RequestAborted).ConfigureAwait(false);
                        return;
                    }
                }
            }
        }
        await next().ConfigureAwait(false);
    });
}

app.Run();
