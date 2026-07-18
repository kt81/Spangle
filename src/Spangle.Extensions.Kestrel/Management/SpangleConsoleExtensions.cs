using Microsoft.Extensions.Options;

namespace Spangle.Extensions.Kestrel.Management;

/// <summary>
/// Hosts the web console (a Blazor WASM app) under <c>/console</c>, reachable only via the
/// management port — the console is an operator surface, and it must not leak onto the port that
/// serves media to the public.
/// </summary>
public static class SpangleConsoleExtensions
{
    /// <summary>
    /// Serves the console when management is enabled: the port gate, the Blazor framework files,
    /// and the SPA fallback. The host still serves its own static assets (the console's files ride
    /// its static web assets), so call <c>UseStaticFiles</c> after this as usual.
    /// </summary>
    public static WebApplication UseSpangleConsole(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.Services.GetRequiredService<IOptions<SpangleMediaServerOptions>>().Value;
        int managementPort = options.Management.Port;

        // The port gate goes in whether or not the console is served. The console's files ride the
        // host's static web assets, which UseStaticFiles serves — so even with management disabled
        // (no management port at all), a /console request on the delivery port would otherwise be
        // answered from those assets. 404 it on every non-management port.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Connection.LocalPort != managementPort
                && ctx.Request.Path.StartsWithSegments("/console", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next().ConfigureAwait(false);
        });

        // Serving the console (Blazor framework files + SPA fallback) is only for when it is enabled.
        if (options.Management.Enabled)
        {
            app.UseBlazorFrameworkFiles("/console");
            app.MapFallbackToFile("/console/{*path:nonfile}", "console/index.html");
        }

        return app;
    }
}
