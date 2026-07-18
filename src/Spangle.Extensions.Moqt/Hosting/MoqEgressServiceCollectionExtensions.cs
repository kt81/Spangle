using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Spangle.Extensions.Moqt.Hosting;

/// <summary>
/// MOQT egress as host configuration — the <c>Spangle:Moq</c> section. Enabled, every publish
/// session additionally announces itself to the relay and publishes its frames as LOC objects with
/// an MSF catalog beside them, without displacing whatever the host's primary output is.
/// </summary>
public sealed class MoqEgressOptions
{
    /// <summary>Where these options live in the host configuration.</summary>
    public const string SectionPath = "Spangle:Moq";

    /// <summary>Whether sessions publish to MOQT at all. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>The relay to publish to, as <c>address:port</c> (raw QUIC).</summary>
    public string Relay { get; set; } = "";

    /// <summary>
    /// Prefix fields for each session's namespace, <c>/</c>-separated: with root <c>live</c>,
    /// stream key <c>test</c> becomes namespace <c>live/test</c>. Empty means the stream key is
    /// the whole namespace. One namespace per stream is what keeps the fixed track names
    /// (<c>video0</c>, <c>audio0</c>, <c>catalog</c>) from colliding across streams.
    /// </summary>
    public string? NamespaceRoot { get; set; }

    /// <summary>The relay's endpoint path, conveyed as the PATH setup option.</summary>
    public string Path { get; set; } = "/moq";

    /// <summary>The SNI host name offered to the relay; defaults to its address.</summary>
    public string? TargetHost { get; set; }

    /// <summary>
    /// Accept the relay's certificate without validating it — for a development relay with a
    /// self-signed certificate, never for one that matters.
    /// </summary>
    public bool AllowUntrustedRelayCertificate { get; set; }

    /// <summary>
    /// Pull sources: relays and namespaces to dial and republish as HLS. Independent of egress —
    /// a deployment may ingest, publish, or both. Each source's TLS defaults to this section's
    /// <see cref="Path"/> / <see cref="TargetHost"/> / <see cref="AllowUntrustedRelayCertificate"/>.
    /// </summary>
    public IList<MoqIngestSourceOptions> Ingest { get; } = [];
}

/// <summary>One MOQT pull source: which relay, which namespace, republished under which name.</summary>
public sealed class MoqIngestSourceOptions
{
    /// <summary>The stream key this source republishes under (its HLS path segment).</summary>
    public string Name { get; set; } = "";

    /// <summary>The relay to dial, as <c>address:port</c> (raw QUIC).</summary>
    public string Relay { get; set; } = "";

    /// <summary>The namespace to pull, <c>/</c>-separated into fields.</summary>
    public string Namespace { get; set; } = "";

    /// <summary>The LOC draft the source publishes in. Every implementation today writes -01.</summary>
    public LocDraft Loc { get; set; } = LocDraft.Draft01;

    /// <summary>How long to wait before redialling after a disconnect.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The watchdog: with no media object for this long, the pull is torn down and redialled.
    /// A pull source has nobody on the other end to notice it silently stalling — keep-alives
    /// hold a dead-quiet session "healthy" forever, so the absence of objects is itself the
    /// failure signal. Zero disables it.
    /// </summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often to PING an otherwise silent connection. A subscriber waiting on a slow or
    /// paused publisher is silent by design, and QUIC's effective idle timeout is the smaller
    /// of the two peers' values — the same reasoning the egress side documents.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>Registers MOQT egress into a Spangle host.</summary>
public static class MoqEgressServiceCollectionExtensions
{
    /// <summary>
    /// Adds the MOQT egress factory, bound to <c>Spangle:Moq</c>. The ingest hosts pick every
    /// registered <see cref="IPublishEgressFactory"/> up per session; with <c>Enabled: false</c>
    /// (the default) the factory declines each one, so registering this is safe unconditionally.
    /// </summary>
    public static IServiceCollection AddSpangleMoqEgress(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<MoqEgressOptions>()
            .BindConfiguration(MoqEgressOptions.SectionPath)
            .Validate(static options => !options.Enabled || IPEndPoint.TryParse(options.Relay, out _),
                "Spangle:Moq:Relay must be an address:port endpoint when Spangle:Moq:Enabled is set")
            .Validate(static options => options.Ingest.All(source => IPEndPoint.TryParse(source.Relay, out _)),
                "Every Spangle:Moq:Ingest source needs an address:port Relay");
        services.AddSingleton<IPublishEgressFactory, MoqPublishEgressFactory>();
        services.AddSingleton<IIngestSourceProvider, MoqIngestSourceProvider>();
        return services;
    }
}

/// <summary>Turns the <c>Spangle:Moq:Ingest</c> configuration into pull sources for the host.</summary>
internal sealed class MoqIngestSourceProvider : IIngestSourceProvider
{
    private readonly MoqEgressOptions _options;

    public MoqIngestSourceProvider(IOptions<MoqEgressOptions> options) => _options = options.Value;

    public IReadOnlyList<IIngestSource> Sources =>
        [.. _options.Ingest.Select(source => new MoqIngestSource(source, _options))];
}

/// <summary>
/// The <see cref="IPublishEgressFactory"/> that attaches a <see cref="MoqSender"/> to each publish
/// session, per <see cref="MoqEgressOptions"/>.
/// </summary>
internal sealed class MoqPublishEgressFactory : IPublishEgressFactory
{
    private readonly IOptions<MoqEgressOptions> _options;

    public MoqPublishEgressFactory(IOptions<MoqEgressOptions> options) => _options = options;

    /// <inheritdoc />
    public IPublishEgress? Start(CancellationToken sessionToken)
    {
        MoqEgressOptions options = _options.Value;
        if (!options.Enabled)
        {
            return null;
        }

        var context = new MoqSenderContext(new MoqSenderOptions
        {
            Relay = ParseEndPoint(options.Relay),
            NamespaceRoot = options.NamespaceRoot,
            Path = options.Path,
            TargetHost = options.TargetHost,
            AllowUntrustedRelayCertificate = options.AllowUntrustedRelayCertificate,
        }, sessionToken);
        var sender = new MoqSender();

        // The sender blocks on its intake long before the session has produced anything, so it
        // runs beside the session; the egress handle is how the host awaits its tail.
        Task running = Task.Run(() => sender.StartAsync(context).AsTask(), CancellationToken.None);
        return new MoqPublishEgress(context, sender, running);
    }

    private static IPEndPoint ParseEndPoint(string relay)
    {
        // Validated at options level; parse defensively anyway so a misconfiguration reads as one.
        if (!IPEndPoint.TryParse(relay, out IPEndPoint? endPoint))
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture,
                    $"'{relay}' is not an address:port endpoint (Spangle:Moq:Relay)."));
        }

        return endPoint;
    }
}

/// <summary>One session's running MOQT egress.</summary>
internal sealed class MoqPublishEgress : IPublishEgress
{
    private readonly MoqSenderContext _context;
    private readonly MoqSender _sender;
    private readonly Task _running;

    internal MoqPublishEgress(MoqSenderContext context, MoqSender sender, Task running)
    {
        _context = context;
        _sender = sender;
        _running = running;
    }

    /// <inheritdoc />
    public ISenderContext SenderContext => _context;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Usually a no-op — the pipeline completed the intake when the session ended — but a
        // session that never wired must still release the sender waiting on it.
        await _context.Intake.CompleteAsync().ConfigureAwait(false);
        try
        {
            // our own Task.Run from Start; awaiting it here is not the foreign-task hazard
#pragma warning disable VSTHRD003
            await _running.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
        catch (Exception)
        {
            // the sender logged its own failure; disposal is not the place to rethrow it
        }

        await _sender.DisposeAsync().ConfigureAwait(false);
    }
}
