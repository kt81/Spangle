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
        if (!options.Management.Enabled)
        {
            return app;
        }

        int managementPort = options.Management.Port;
        app.Use(async (ctx, next) =>
        {
            if (ctx.Connection.LocalPort != managementPort
                && ctx.Request.Path.StartsWithSegments("/console", StringComparison.OrdinalIgnoreCase))
            {
                // the SPA and its framework files must not leak onto the delivery port
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next().ConfigureAwait(false);
        });
        app.UseBlazorFrameworkFiles("/console");
        app.MapFallbackToFile("/console/{*path:nonfile}", "console/index.html");

        return app;
    }
}
