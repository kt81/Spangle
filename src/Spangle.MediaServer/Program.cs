using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Spangle.Extensions.Kestrel;
using Spangle.Extensions.Kestrel.DependencyInjection;

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
