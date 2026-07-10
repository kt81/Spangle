using Microsoft.Extensions.DependencyInjection.Extensions;
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
        // apps replace this to implement their own publish policy (deny lists,
        // key validation, first-wins, ...); the default is allow-all + last-wins
        services.TryAddSingleton<IPublishAuthorizer, DefaultPublishAuthorizer>();
        services.AddHostedService<SrtIngestService>();
    }
}
