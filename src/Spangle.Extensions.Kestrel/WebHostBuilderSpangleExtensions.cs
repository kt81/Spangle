using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Spangle.Logging;

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
        if (opt.Value.Rtmp.Enabled)
        {
            options.ListenAnyIP(opt.Value.Rtmp.Port,
                listenOptions =>
                {
                    // RTMPS: the TLS middleware wraps the raw connection before the handler sees it
                    ApplyTls(listenOptions, opt.Value.Rtmp.Tls);
                    listenOptions.UseConnectionHandler<RtmpConnectionHandler>();
                });
        }
        // Explicit Listen* calls override URL-based configuration, so HTTP must be explicit too
        options.ListenAnyIP(opt.Value.Http.Port, listenOptions => ApplyTls(listenOptions, opt.Value.Http.Tls));
        // RTSP push server: its own listen port, accepting inbound RECORD sessions
        RtspListenOptions rtspListen = opt.Value.Rtsp.Listen;
        if (opt.Value.Rtsp.Enabled && rtspListen.Enabled)
        {
            options.ListenAnyIP(rtspListen.Port, listenOptions =>
            {
                ApplyTls(listenOptions, rtspListen.Tls);
                listenOptions.UseConnectionHandler<RtspConnectionHandler>();
            });
        }
        // The management surface (console + control API) never shares the delivery port
        ManagementOptions management = opt.Value.Management;
        if (management.Enabled)
        {
            options.Listen(System.Net.IPAddress.Parse(management.BindAddress), management.Port,
                listenOptions => ApplyTls(listenOptions, management.Tls));
        }
        var loggerFactory = options.ApplicationServices.GetService<ILoggerFactory>();
        if (loggerFactory != null)
        {
            SpangleLogManager.SetLoggerFactory(loggerFactory);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The certificate must stay alive as long as the listener - the process lifetime")]
    private static void ApplyTls(ListenOptions listenOptions, TlsOptions tls)
    {
        if (!tls.Enabled)
        {
            return;
        }
        listenOptions.UseHttps(tls.LoadCertificate());
    }
}
