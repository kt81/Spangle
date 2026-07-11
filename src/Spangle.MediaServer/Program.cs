using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Spangle.Extensions.Kestrel;
using Spangle.Extensions.Kestrel.DependencyInjection;
using Spangle.Transport.HLS;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSpangle();
builder.WebHost.ConfigureSpangle();

var app = builder.Build();

var options = app.Services.GetRequiredService<IOptions<SpangleMediaServerOptions>>().Value;
var storage = app.Services.GetRequiredService<IHLSStorage>();
var registry = app.Services.GetRequiredService<HLSStreamRegistry>();
var hlsPathPrefix = options.Hls.RequestPath + "/";

// Serve live playlists from memory, implementing LL-HLS blocking reload
// (?_HLS_msn=&_HLS_part=). Falls through to the storage/static serving below.
app.Use(async (ctx, next) =>
{
    PathString path = ctx.Request.Path;
    if (path.Value is { } p
        && p.StartsWith(hlsPathPrefix, StringComparison.OrdinalIgnoreCase)
        && p.EndsWith("/playlist.m3u8", StringComparison.OrdinalIgnoreCase))
    {
        string streamKey = p[hlsPathPrefix.Length..^"/playlist.m3u8".Length];
        if (!streamKey.Contains('/') && registry.TryGet(streamKey, out var live))
        {
            // _HLS_skip=YES|v2 requests a playlist delta update (EXT-X-SKIP)
            string? skipValue = ctx.Request.Query["_HLS_skip"];
            bool skip = "YES".Equals(skipValue, StringComparison.OrdinalIgnoreCase)
                        || "v2".Equals(skipValue, StringComparison.OrdinalIgnoreCase);

            string text;
            if (long.TryParse(ctx.Request.Query["_HLS_msn"], out long msn))
            {
                // RFC 8216bis 6.2.5.2: a request beyond the next two segments is a
                // client error, not something to block 15 seconds on
                if (msn > live.CurrentMsn + 2)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                _ = int.TryParse(ctx.Request.Query["_HLS_part"], out int part);
                text = await live.WaitForAsync(msn, part, skip, TimeSpan.FromSeconds(15), ctx.RequestAborted);
            }
            else
            {
                text = live.GetCurrent(skip);
            }

            if (text.Length > 0)
            {
                ctx.Response.ContentType = "application/vnd.apple.mpegurl";
                ctx.Response.Headers.CacheControl = "no-cache, no-store";
                await ctx.Response.WriteAsync(text);
                return;
            }
        }
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles(); // wwwroot (test player)

if (storage is FileHLSStorage)
{
    // File storage: serve segments straight from disk (sendfile, ranges, archives)
    var hlsDirectory = Path.GetFullPath(options.Hls.OutputDirectory);
    Directory.CreateDirectory(hlsDirectory);
    var contentTypes = new FileExtensionContentTypeProvider();
    contentTypes.Mappings[".m3u8"] = "application/vnd.apple.mpegurl";
    contentTypes.Mappings[".ts"] = "video/mp2t";
    contentTypes.Mappings[".m4s"] = "video/iso.segment";
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(hlsDirectory),
        RequestPath = options.Hls.RequestPath,
        ContentTypeProvider = contentTypes,
        OnPrepareResponse = static ctx =>
        {
            // Playlists change every segment; segments themselves are immutable
            if (ctx.File.Name.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
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
            int slash = rest.IndexOf('/');
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
                        await ctx.Response.WriteAsync(playlist);
                        return;
                    }
                    if (stream.TryReadBlob(name, out ReadOnlyMemory<byte> blob))
                    {
                        ctx.Response.ContentType = Path.GetExtension(name).ToLowerInvariant() switch
                        {
                            ".ts" => "video/mp2t",
                            ".m4s" => "video/iso.segment",
                            ".mp4" => "video/mp4",
                            _ => "application/octet-stream",
                        };
                        await ctx.Response.Body.WriteAsync(blob, ctx.RequestAborted);
                        return;
                    }
                }
            }
        }
        await next();
    });
}

app.Run();
