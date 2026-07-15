using System.IO.Pipelines;
using System.Net;
using Spangle.Net.Moqt;

namespace Spangle.Extensions.Moqt;

/// <summary>Which draft of LOC's per-frame metadata to write.</summary>
public enum LocDraft
{
    /// <summary>
    /// <b>draft-ietf-moq-loc-01</b> — what every LOC implementation reads today (moq-playa's
    /// <c>@moqt/loc</c> and moq5 are both -01 only), so it is the default. A relay re-encodes
    /// properties per draft, so speaking -01 over draft-18 transport is exactly what a -01
    /// publisher looks like from a subscriber's side. See <see cref="Loc01Properties"/>.
    /// </summary>
    Draft01,

    /// <summary>
    /// <b>draft-ietf-moq-loc-03</b> — current, and nothing reads it yet. See
    /// <see cref="Loc03Properties"/>.
    /// </summary>
    Draft03,
}

/// <summary>
/// Where a <see cref="MoqSender"/> publishes and what it calls things.
/// </summary>
public sealed record MoqSenderOptions
{
    /// <summary>The relay to dial over raw QUIC.</summary>
    public required IPEndPoint Relay { get; init; }

    /// <summary>The Track Namespace to announce and publish under.</summary>
    public required string Namespace { get; init; }

    /// <summary>
    /// The relay's endpoint path, sent as the PATH setup option. Over raw QUIC there is no URL to
    /// carry it, so this is how a relay that serves several endpoints knows which one is meant.
    /// </summary>
    public string Path { get; init; } = "/moq";

    /// <summary>Prepended to every track name, so several streams can share one namespace.</summary>
    public string TrackNamePrefix { get; init; } = "";

    /// <summary>The SNI host name offered to the relay; defaults to its address when null.</summary>
    public string? TargetHost { get; init; }

    /// <summary>
    /// Accept the relay's certificate without validating it. For a development relay with a
    /// self-signed certificate; off by default, because a publisher that does not check who it is
    /// handing the stream to is not something to arrive at by accident.
    /// </summary>
    public bool AllowUntrustedRelayCertificate { get; init; }

    /// <summary>How the objects of a group are spread over streams.</summary>
    public MoqStreamMapping StreamMapping { get; init; } = MoqStreamMapping.GroupPerStream;

    /// <summary>Which LOC draft the per-frame metadata is written in.</summary>
    public LocDraft Loc { get; init; } = LocDraft.Draft01;

    /// <summary>Which MSF draft the catalog is written in.</summary>
    public MsfDraft CatalogDraft { get; init; } = MsfDraft.Draft00;

    /// <summary>
    /// How often the catalog is republished, so a subscriber that arrives late still learns what is
    /// on offer without waiting for the track list to change.
    /// </summary>
    public TimeSpan CatalogInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How often to PING an idle connection. A publisher with no subscribers sends nothing, and a
    /// silent QUIC connection is closed — taking the announced namespace with it, after which
    /// subscribers are told the track does not exist. Raising the idle timeout instead does not
    /// work: the effective one is the lesser of the two peers'.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// The sender side of a Spangle stream published to MOQT. Like every Spangle sender it owns a pipe
/// whose intake the pipeline writes canonical MediaFrames to; <see cref="MoqSender"/> reads them.
/// </summary>
public sealed class MoqSenderContext : ISenderContext<MoqSenderContext>
{
    /// <summary>The token the host's session lifetime is bound to.</summary>
    public readonly CancellationToken CancellationToken;

    /// <summary>Creates a context that publishes per <paramref name="options"/>.</summary>
    public MoqSenderContext(MoqSenderOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        CancellationToken = cancellationToken;
        var pipe = new Pipe(new PipeOptions(useSynchronizationContext: false));
        Intake = pipe.Writer;
        IntakeReader = pipe.Reader;
    }

    /// <summary>Where and how to publish.</summary>
    public MoqSenderOptions Options { get; }

    /// <inheritdoc />
    public PipeWriter Intake { get; }

    /// <summary>The reading end of the same pipe, which the sender drains.</summary>
    public PipeReader IntakeReader { get; }

    /// <inheritdoc />
    public IReceiverContext? SourceInfo { get; set; }

    /// <summary>
    /// The Track Namespace, as MOQT wants it. One field: relays match namespaces by prefix, and a
    /// single opaque field is the simplest thing that cannot be split in a way we did not intend.
    /// </summary>
    internal TrackNamespace TrackNamespace => Spangle.Net.Moqt.TrackNamespace.FromStrings(Options.Namespace);
}
