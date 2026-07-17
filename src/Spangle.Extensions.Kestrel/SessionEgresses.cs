namespace Spangle.Extensions.Kestrel;

/// <summary>
/// The additional egresses of one publish session, started from the registered
/// <see cref="IPublishEgressFactory"/> set and disposed after the session's primary sender has
/// drained. One instance per session; the ingest hosts all use it the same way, so the pattern
/// lives here once.
/// </summary>
internal sealed class SessionEgresses : IAsyncDisposable
{
    private readonly List<IPublishEgress> _egresses;

    private SessionEgresses(List<IPublishEgress> egresses) => _egresses = egresses;

    /// <summary>Asks every factory; a factory may decline (disabled, or policy for this stream).</summary>
    public static SessionEgresses Start(IEnumerable<IPublishEgressFactory> factories, CancellationToken sessionToken)
    {
        var egresses = new List<IPublishEgress>();
        foreach (IPublishEgressFactory factory in factories)
        {
            if (factory.Start(sessionToken) is { } egress)
            {
                egresses.Add(egress);
            }
        }

        return new SessionEgresses(egresses);
    }

    /// <summary>What to pass as a LiveContext's additional senders; null when there are none.</summary>
    public IReadOnlyList<ISenderContext>? Senders =>
        _egresses.Count == 0 ? null : [.. _egresses.Select(e => e.SenderContext)];

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (IPublishEgress egress in _egresses)
        {
            await egress.DisposeAsync().ConfigureAwait(false);
        }
    }
}
