using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Spangle.Rtmp.Logging;

namespace Spangle.Extensions.Kestrel;

public static class WebHostBuilderSpangleExtensions
{
    public static IWebHostBuilder ConfigureSpangle(this IWebHostBuilder hostBuilder) =>
        hostBuilder.UseKestrel(options => options.ConfigureSpangle())
            .ConfigureSpangleConfiguration();

    public static IWebHostBuilder ConfigureSpangleConfiguration(this IWebHostBuilder hostBuilder)
    {
        return hostBuilder.ConfigureAppConfiguration((context, confBuilder) =>
        {
            var env = context.HostingEnvironment;
            confBuilder
                .AddYamlFile("spanglesettings.yaml", optional: false, reloadOnChange: true)
                .AddYamlFile($"spanglesettings.{env.EnvironmentName}.yaml", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables("SMS_");
        });
    }

    public static void ConfigureSpangle(this KestrelServerOptions options)
    {
        var opt = options.ApplicationServices.GetRequiredService<IOptions<SpangleMediaServerOptions>>();
        options.ListenAnyIP(opt.Value.Rtmp.Port,
            listenOptions => { listenOptions.UseConnectionHandler<RtmpConnectionHandler>(); });
        var loggerFactory = options.ApplicationServices.GetService<ILoggerFactory>();
        if (loggerFactory != null)
        {
            SpangleLogManager.SetLoggerFactory(loggerFactory);
        }
    }
}
