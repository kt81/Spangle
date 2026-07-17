namespace Spangle;

/// <summary>
/// A pull ingest source — a stream Spangle dials out to and republishes, the mirror of
/// <see cref="IPublishEgressFactory"/>'s push egress. A provider (registered in DI) enumerates its
/// sources; the host runs each on a reconnect loop, and for every connection wires the returned
/// receiver into a session with the host's own output (HLS/CMAF). This is the seam a MOQT ingest
/// plugs into without the host referencing the MOQT stack, the same way egress does.
/// </summary>
public interface IIngestSourceProvider
{
    /// <summary>The sources to pull. Read once at host start; each is then run on its own loop.</summary>
    IReadOnlyList<IIngestSource> Sources { get; }
}

/// <summary>
/// One pull source: a name for output routing and a dial that establishes a fresh connection each
/// time the host (re)connects it. The dial is called again after a disconnect, so it must build a
/// new connection rather than reuse a dead one.
/// </summary>
public interface IIngestSource
{
    /// <summary>The stream key this source republishes under (e.g. the HLS path segment).</summary>
    string Name { get; }

    /// <summary>How long the host waits before redialling after this source disconnects or fails.</summary>
    TimeSpan ReconnectDelay { get; }

    /// <summary>
    /// Dials the source and returns a live connection whose <see cref="IIngestConnection.Receiver"/>
    /// the host runs. Called once per connection attempt; a throw is treated as a failed attempt and
    /// retried after <see cref="ReconnectDelay"/>.
    /// </summary>
    ValueTask<IIngestConnection> ConnectAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One established pull connection: the receiver the host drives, plus ownership of the transport
/// underneath it. Disposed after the session ends — before the host redials.
/// </summary>
public interface IIngestConnection : IAsyncDisposable
{
    /// <summary>The receiver producing this connection's MediaFrames.</summary>
    IReceiverContext Receiver { get; }
}
