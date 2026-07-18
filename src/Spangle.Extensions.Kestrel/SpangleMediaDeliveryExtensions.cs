using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Spangle.Extensions.Kestrel.Management;
using Spangle.Spinner;
using Spangle.Transport.HLS;

namespace Spangle.Extensions.Kestrel;

/// <summary>
/// The delivery side of a Spangle media server as one call: live playlists with LL-HLS blocking
/// reload, segment serving for both storage modes, the DASH clock, and timed-metadata injection.
/// The host application keeps only what is its own — routing policy, static assets, the console.
/// </summary>
public static class SpangleMediaDeliveryExtensions
{
    /// <summary>
    /// Adds Spangle's media delivery to the pipeline: live playlist serving (with LL-HLS blocking
    /// reload and delta updates), the timed-metadata injection API (when enabled), the
    /// <c>/api/time</c> clock DASH players use for UTCTiming, and segment serving from whichever
    /// <see cref="IHLSStorage"/> is registered — static files for file storage, in-memory blobs
    /// (including chunked delivery of segments still being written) for memory storage.
    /// </summary>
    public static WebApplication UseSpangleMediaDelivery(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.Services.GetRequiredService<IOptions<SpangleMediaServerOptions>>().Value;
        var storage = app.Services.GetRequiredService<IHLSStorage>();
        var registry = app.Services.GetRequiredService<HLSStreamRegistry>();
        var viewerStats = app.Services.GetRequiredService<ViewerStatsRegistry>();
        var hlsPathPrefix = options.Hls.RequestPath + "/";

        // A browser player served from another origin cannot read the playlists or segments without
        // CORS; open it to the configured origins (or any, for "*"). No origins keeps it same-origin.
        IList<string> allowedOrigins = options.Http.AllowedOrigins;
        if (allowedOrigins.Count > 0)
        {
            app.UseCors(policy =>
            {
                if (allowedOrigins is ["*"])
                {
                    policy.AllowAnyOrigin();
                }
                else
                {
                    policy.WithOrigins([.. allowedOrigins]);
                }
                policy.AllowAnyHeader().AllowAnyMethod();
            });
        }

        // Serve live playlists from memory, implementing LL-HLS blocking reload
        // (?_HLS_msn=&_HLS_part=). Falls through to the storage/static serving below.
        app.Use(async (ctx, next) =>
        {
            if (TrySplitStreamPath(ctx.Request.Path, hlsPathPrefix, out string streamKey, out string name)
                && name.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                // registry entries are keyed {stream}/{playlist}: demuxed CMAF sessions
                // publish several live playlists per stream (video.m3u8 / audio.m3u8)
                if (registry.TryGet($"{streamKey}/{name}", out var live))
                {
                    viewerStats.OnPlaylistRequest(streamKey);

                    // _HLS_skip=YES|v2 requests a playlist delta update (EXT-X-SKIP)
                    string? skipValue = ctx.Request.Query["_HLS_skip"];
                    bool skip = "YES".Equals(skipValue, StringComparison.OrdinalIgnoreCase)
                                || "v2".Equals(skipValue, StringComparison.OrdinalIgnoreCase);

                    string text;
                    if (long.TryParse(ctx.Request.Query["_HLS_msn"], NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out long msn))
                    {
                        // RFC 8216bis 6.2.5.2: a request beyond the next two segments is a
                        // client error, not something to block 15 seconds on
                        if (msn > live.CurrentMsn + 2)
                        {
                            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                            return;
                        }
                        _ = int.TryParse(ctx.Request.Query["_HLS_part"], NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int part);
                        viewerStats.WaiterEntered(streamKey);
                        try
                        {
                            text = await live.WaitForAsync(msn, part, skip, TimeSpan.FromSeconds(15),
                                ctx.RequestAborted).ConfigureAwait(false);
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

        MapMetadataInjection(app, options);

        // Clock endpoint for DASH UTCTiming (LL players compute the live edge from it)
        app.MapGet("/api/time", () =>
            Results.Text(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                "text/plain"));

        if (storage is FileHLSStorage)
        {
            UseFileStorageDelivery(app, options);
        }
        else
        {
            UseMemoryStorageDelivery(app, storage, hlsPathPrefix);
        }

        return app;
    }

    /// <summary>Inject timed metadata into a live session: <c>{"name":"...","value":&lt;string or JSON&gt;}</c>.</summary>
    private static void MapMetadataInjection(WebApplication app, SpangleMediaServerOptions options)
    {
        if (!options.Http.MetadataInjection)
        {
            return;
        }

        var metadataHub = app.Services.GetRequiredService<TimedMetadataHub>();
        string? metadataToken = options.Http.MetadataInjectionToken;
        app.MapPost("/api/streams/{streamKey}/metadata",
            async (string streamKey, HttpRequest request) =>
            {
                if (!string.IsNullOrEmpty(metadataToken)
                    && !TokenGate.Matches(request.Headers.Authorization.ToString(), metadataToken))
                {
                    request.HttpContext.Response.Headers.WWWAuthenticate = "Bearer";
                    return Results.Unauthorized();
                }
                using var body = await JsonDocument.ParseAsync(request.Body,
                    cancellationToken: request.HttpContext.RequestAborted).ConfigureAwait(false);
                if (!body.RootElement.TryGetProperty("name", out JsonElement nameElement)
                    || nameElement.GetString() is not { Length: > 0 } name)
                {
                    return Results.BadRequest("A non-empty `name` is required");
                }
                string value = body.RootElement.TryGetProperty("value", out JsonElement valueElement)
                    ? valueElement.ValueKind == JsonValueKind.String
                        ? valueElement.GetString()!
                        : valueElement.GetRawText()
                    : "";

                return metadataHub.TryInject(streamKey, name, value)
                    ? Results.NoContent()
                    : Results.NotFound($"No live session under `{streamKey}`");
            });
    }

    /// <summary>File storage: serve segments straight from disk (sendfile, ranges, archives).</summary>
    private static void UseFileStorageDelivery(WebApplication app, SpangleMediaServerOptions options)
    {
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

    /// <summary>Memory storage: serve segments, parts and init files from the storage itself.</summary>
    private static void UseMemoryStorageDelivery(WebApplication app, IHLSStorage storage, string hlsPathPrefix)
    {
        app.Use(async (ctx, next) =>
        {
            if (TrySplitStreamPath(ctx.Request.Path, hlsPathPrefix, out string streamKey, out string name)
                && storage.TryGetStream(streamKey, out var stream))
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
                    // A completed blob has a known size: send Content-Length rather than falling back
                    // to chunked transfer, which players, proxies and caches all prefer.
                    ctx.Response.ContentLength = blob.Length;
                    if (extension is ".mpd" or ".m3u8")
                    {
                        ctx.Response.Headers.CacheControl = "no-cache, no-store";
                    }
                    await ctx.Response.Body.WriteAsync(blob, ctx.RequestAborted).ConfigureAwait(false);
                    return;
                }
            }
            await next().ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Splits a request path of the form <c>{prefix}{streamKey}/{name}</c> — the exactly-two-segment
    /// shape every delivery request under the HLS prefix takes (registry keys and storage names are
    /// both <c>{stream}/{file}</c>). Returns false for anything else.
    /// </summary>
    private static bool TrySplitStreamPath(PathString path, string prefix, out string streamKey, out string name)
    {
        streamKey = "";
        name = "";
        if (path.Value is not { } p || !p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string rest = p[prefix.Length..];
        int slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || rest.IndexOf('/', slash + 1) >= 0)
        {
            return false; // not exactly {streamKey}/{name}
        }

        streamKey = rest[..slash];
        name = rest[(slash + 1)..];
        return true;
    }
}
