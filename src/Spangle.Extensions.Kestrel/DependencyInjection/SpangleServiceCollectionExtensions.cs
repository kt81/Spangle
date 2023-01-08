namespace Spangle.Extensions.Kestrel.DependencyInjection;

public static class SpangleServiceCollectionExtensions
{
    public static void AddSpangle(this IServiceCollection services)
    {
        if (services == null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        services.AddOptions<SpangleMediaServerOptions>()
            .BindConfiguration(SpangleMediaServerOptions.SectionPath)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<RtmpConnectionHandler>();
    }

}
