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

// Serve the HLS output directory over HTTP
var options = app.Services.GetRequiredService<IOptions<SpangleMediaServerOptions>>().Value;
var hlsDirectory = Path.GetFullPath(options.Hls.OutputDirectory);
Directory.CreateDirectory(hlsDirectory);

var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".m3u8"] = "application/vnd.apple.mpegurl";
contentTypes.Mappings[".ts"] = "video/mp2t";
contentTypes.Mappings[".m4s"] = "video/iso.segment";

// Serve live playlists from memory, implementing LL-HLS blocking reload
// (?_HLS_msn=&_HLS_part=). Falls through to static files for ended streams.
var registry = app.Services.GetRequiredService<HLSStreamRegistry>();
var playlistPathPrefix = options.Hls.RequestPath + "/";
app.Use(async (ctx, next) =>
{
    PathString path = ctx.Request.Path;
    if (path.Value is { } p
        && p.StartsWith(playlistPathPrefix, StringComparison.OrdinalIgnoreCase)
        && p.EndsWith("/playlist.m3u8", StringComparison.OrdinalIgnoreCase))
    {
        string streamKey = p[playlistPathPrefix.Length..^"/playlist.m3u8".Length];
        if (!streamKey.Contains('/') && registry.TryGet(streamKey, out var live))
        {
            string text;
            if (long.TryParse(ctx.Request.Query["_HLS_msn"], out long msn))
            {
                _ = int.TryParse(ctx.Request.Query["_HLS_part"], out int part);
                text = await live.WaitForAsync(msn, part, TimeSpan.FromSeconds(15), ctx.RequestAborted);
            }
            else
            {
                text = live.Current;
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

app.Run();
