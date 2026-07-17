namespace Spangle;

/// <summary>
/// Creates an additional egress for each publish session — the hook through which an output that
/// is not the host's primary one (say, MOQT beside HLS) attaches itself to every stream. Register
/// implementations in DI; an ingest host asks each registered factory once per session and feeds
/// all returned egresses the same canonical MediaFrame stream.
/// </summary>
public interface IPublishEgressFactory
{
    /// <summary>
    /// Starts one egress for a session that is beginning, or returns null to sit this one out
    /// (disabled by configuration, or a policy that skips this stream). The returned egress's
    /// <see cref="IPublishEgress.SenderContext"/> is wired into the session's pipeline by the
    /// caller (which also sets its <see cref="ISenderContext.SourceInfo"/>); the implementation
    /// starts its sender over the context's intake before returning.
    /// </summary>
    IPublishEgress? Start(CancellationToken sessionToken);
}

/// <summary>
/// One running egress attached to a publish session: the sender context the pipeline writes into,
/// plus the lifetime of the sender draining it. Dispose after the session ends — it completes the
/// intake (a no-op when the pipeline already did) and waits for the sender to finish its tail.
/// </summary>
public interface IPublishEgress : IAsyncDisposable
{
    /// <summary>The context whose intake receives the session's MediaFrames.</summary>
    ISenderContext SenderContext { get; }
}
