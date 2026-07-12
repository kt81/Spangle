using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Spangle.Extensions.Kestrel.Management;
using Spangle.Transport.HLS;

namespace Spangle.Extensions.Kestrel.DependencyInjection;

public static class SpangleServiceCollectionExtensions
{
    public static void AddSpangle(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<SpangleMediaServerOptions>()
            .BindConfiguration(SpangleMediaServerOptions.SectionPath)
            .ValidateDataAnnotations()
            .Validate(static options =>
                {
                    ManagementOptions management = options.Management;
                    if (!management.Enabled || !string.IsNullOrEmpty(management.Token))
                    {
                        return true;
                    }
                    // fail fast instead of silently exposing an unauthenticated control API
                    return IPAddress.TryParse(management.BindAddress, out IPAddress? address)
                           && IPAddress.IsLoopback(address);
                },
                "Management.Token is required when Management.BindAddress is not a loopback address")
            .Validate(static options =>
                {
                    // a TLS block without a certificate is a misconfiguration, not "plaintext please"
                    static bool Ok(TlsOptions tls) => !tls.Enabled || !string.IsNullOrEmpty(tls.CertificatePath);
                    return Ok(options.Rtmp.Tls) && Ok(options.Http.Tls) && Ok(options.Management.Tls);
                },
                "Tls.CertificatePath is required wherever Tls.Enabled is set")
            .ValidateOnStart();
        services.AddSingleton<RtmpConnectionHandler>();
        services.AddSingleton<HLSStreamRegistry>();
        services.AddSingleton<PublishSessionRegistry>();
        services.AddSingleton<Spangle.Spinner.TimedMetadataHub>();
        // apps replace this to implement their own publish policy (deny lists,
        // key validation, first-wins, ...); the built-in policy is allow-all +
        // last-wins, or an exact-match allowlist when Publish.AllowedStreamNames is set
        services.TryAddSingleton<IPublishAuthorizer>(static provider =>
        {
            PublishOptions publish =
                provider.GetRequiredService<IOptions<SpangleMediaServerOptions>>().Value.Publish;
            return publish.AllowedStreamNames.Count > 0
                ? new AllowListPublishAuthorizer(publish.AllowedStreamNames)
                : new DefaultPublishAuthorizer();
        });
        // Memory (default) serves the live window without touching disk;
        // File makes the output an on-disk archive as well
        services.TryAddSingleton<IHLSStorage>(static provider =>
        {
            HlsOptions hls = provider.GetRequiredService<IOptions<SpangleMediaServerOptions>>().Value.Hls;
            return hls.Storage.Equals("file", StringComparison.OrdinalIgnoreCase)
                ? new FileHLSStorage(hls.OutputDirectory)
                : new MemoryHLSStorage();
        });
        services.AddHostedService<SrtIngestService>();

        // Management surface: log capture, delivery counters, and the stats join
        services.AddSingleton<SpangleLogBuffer>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, SpangleLogBufferProvider>());
        // Information by default: per-chunk Trace logging would flush everything an
        // operator cares about out of the ring within seconds. Per-request ASP.NET
        // noise (one entry per served segment) is cut for the same reason. Override
        // with Logging:SpangleBuffer:LogLevel in configuration.
        services.AddLogging(static logging => logging
            .AddFilter<SpangleLogBufferProvider>(category: null, LogLevel.Information)
            .AddFilter<SpangleLogBufferProvider>("Microsoft.AspNetCore", LogLevel.Warning));
        services.AddSingleton<ViewerStatsRegistry>();
        services.AddSingleton<ManagementStatsService>();
    }
}
