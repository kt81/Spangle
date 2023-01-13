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
    }
}
