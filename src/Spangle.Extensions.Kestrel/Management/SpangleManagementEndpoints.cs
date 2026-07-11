using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Spangle.Spinner;

namespace Spangle.Extensions.Kestrel.Management;

/// <summary>Static facts about this server process, served by GET /api/manage/server.</summary>
public sealed record ServerInfoDto
{
    public required string Version { get; init; }
    public required string Runtime { get; init; }
    public required string Os { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required double UptimeSeconds { get; init; }
    public required long WorkingSetBytes { get; init; }
    public required long GcHeapBytes { get; init; }
    public required bool RtmpEnabled { get; init; }
    public required int RtmpPort { get; init; }
    public required bool SrtEnabled { get; init; }
    public required int SrtPort { get; init; }
    public required int HttpPort { get; init; }
    public required string SegmentFormat { get; init; }
    public required string Storage { get; init; }
    public required bool LowLatency { get; init; }
    public required bool TsPassthrough { get; init; }
}

/// <summary>
/// The management/control API: <c>/api/manage/*</c>, reachable only through the
/// management port (404 elsewhere) and guarded by the configured Bearer token.
/// The web console is a client of exactly this surface — everything the console
/// shows or does is equally scriptable with curl.
/// </summary>
public static class SpangleManagementEndpoints
{
    public static IEndpointRouteBuilder MapSpangleManagement(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/manage");
        group.AddEndpointFilter(static async (ctx, next) =>
        {
            IResult? rejection = Gate(ctx.HttpContext);
            return rejection ?? await next(ctx).ConfigureAwait(false);
        });

        group.MapGet("/streams", static (ManagementStatsService stats) => Results.Json(stats.Collect()));

        // one `stats` event per second while the connection lives
        group.MapGet("/streams/live", static (ManagementStatsService stats, CancellationToken ct) =>
            Results.ServerSentEvents(StatsFeedAsync(stats, ct), eventType: "stats"));

        group.MapDelete("/streams/{streamKey}", static (string streamKey, PublishSessionRegistry sessions) =>
            sessions.TryKick(streamKey) ? Results.NoContent() : Results.NotFound());

        group.MapPost("/streams/{streamKey}/metadata",
            static async (string streamKey, HttpRequest request, TimedMetadataHub hub) =>
            {
                using var body = await JsonDocument.ParseAsync(request.Body,
                    cancellationToken: request.HttpContext.RequestAborted).ConfigureAwait(false);
                if (!body.RootElement.TryGetProperty("name", out JsonElement nameElement)
                    || nameElement.GetString() is not { Length: > 0 } name)
                {
                    return Results.BadRequest("A non-empty `name` is required");
                }
                string value = body.RootElement.TryGetProperty("value", out JsonElement valueElement)
                    ? valueElement.ValueKind == JsonValueKind.String ? valueElement.GetString()! : valueElement.GetRawText()
                    : "";
                return hub.TryInject(streamKey, name, value)
                    ? Results.NoContent()
                    : Results.NotFound($"No live session under `{streamKey}`");
            });

        group.MapGet("/logs", static (SpangleLogBuffer buffer, int? take, string? minLevel) =>
        {
            LogLevel level = Enum.TryParse(minLevel, ignoreCase: true, out LogLevel parsed)
                ? parsed
                : LogLevel.Trace;
            return Results.Json(buffer.Snapshot(Math.Clamp(take ?? 500, 1, 2048), level));
        });

        group.MapGet("/logs/live", static (SpangleLogBuffer buffer, CancellationToken ct) =>
            Results.ServerSentEvents(buffer.SubscribeAsync(ct), eventType: "log"));

        group.MapGet("/server", static (IOptions<SpangleMediaServerOptions> options) =>
            Results.Json(CollectServerInfo(options.Value)));

        return app;
    }

    /// <summary>Port isolation and token auth; null means the request may proceed.</summary>
    private static IResult? Gate(HttpContext ctx)
    {
        ManagementOptions opt =
            ctx.RequestServices.GetRequiredService<IOptions<SpangleMediaServerOptions>>().Value.Management;
        if (!opt.Enabled || ctx.Connection.LocalPort != opt.Port)
        {
            // management routes must not exist on the delivery port
            return Results.NotFound();
        }
        if (string.IsNullOrEmpty(opt.Token))
        {
            return null; // loopback-only bind is enforced by options validation
        }

        const string prefix = "Bearer ";
        string auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith(prefix, StringComparison.Ordinal)
            && FixedTimeEquals(auth.AsSpan(prefix.Length).Trim(), opt.Token))
        {
            return null;
        }
        ctx.Response.Headers.WWWAuthenticate = "Bearer";
        return Results.Unauthorized();
    }

    private static bool FixedTimeEquals(ReadOnlySpan<char> provided, string expected)
    {
        Span<byte> providedUtf8 = provided.Length <= 64 ? stackalloc byte[256] : new byte[provided.Length * 4];
        int written = Encoding.UTF8.GetBytes(provided, providedUtf8);
        byte[] expectedUtf8 = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(providedUtf8[..written], expectedUtf8);
    }

    private static async IAsyncEnumerable<IReadOnlyList<StreamStatsDto>> StatsFeedAsync(
        ManagementStatsService stats, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return stats.Collect();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    yield break;
                }
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            yield return stats.Collect();
        }
    }

    private static ServerInfoDto CollectServerInfo(SpangleMediaServerOptions options)
    {
        using var process = Process.GetCurrentProcess();
        var startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime());
        string version = Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        return new ServerInfoDto
        {
            Version = version,
            Runtime = RuntimeInformation.FrameworkDescription,
            Os = RuntimeInformation.OSDescription,
            StartedAt = startedAt,
            UptimeSeconds = (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
            WorkingSetBytes = process.WorkingSet64,
            GcHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
            RtmpEnabled = options.Rtmp.Enabled,
            RtmpPort = options.Rtmp.Port,
            SrtEnabled = options.Srt.Enabled,
            SrtPort = options.Srt.Port,
            HttpPort = options.Http.Port,
            SegmentFormat = options.Hls.SegmentFormat,
            Storage = options.Hls.Storage,
            LowLatency = options.Hls.LowLatency,
            TsPassthrough = options.Hls.TsPassthrough,
        };
    }
}
