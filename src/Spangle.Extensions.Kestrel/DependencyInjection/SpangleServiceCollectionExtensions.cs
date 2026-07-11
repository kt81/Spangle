using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
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
            .ValidateOnStart();
        services.AddSingleton<RtmpConnectionHandler>();
        services.AddSingleton<HLSStreamRegistry>();
        services.AddSingleton<PublishSessionRegistry>();
        services.AddSingleton<Spangle.Spinner.TimedMetadataHub>();
        // apps replace this to implement their own publish policy (deny lists,
        // key validation, first-wins, ...); the default is allow-all + last-wins
        services.TryAddSingleton<IPublishAuthorizer, DefaultPublishAuthorizer>();
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
    }
}
