using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Spangle.Logging;
using ZLogger;

namespace Spangle;

/// <summary>
/// The default policy: allow everyone; when the name is contested, the newest session
/// wins (last-wins). A zombie session blocking a publisher's reconnect is a worse
/// failure than a takeover by someone who holds the same key.
/// </summary>
public sealed class DefaultPublishAuthorizer : IPublishAuthorizer
{
    public ValueTask<PublishDecision> AuthorizeAsync(PublishRequest request, CancellationToken ct) =>
        new(request.ExistingSession is null ? PublishDecision.Allow : PublishDecision.Takeover);
}

/// <summary>
/// The <see cref="IPublishGate"/> for one session: binds the shared registry and
/// authorizer to this session's identity and kick action.
/// </summary>
internal sealed class PublishGate(
    PublishSessionRegistry registry,
    IPublishAuthorizer authorizer,
    string protocol,
    string sessionId,
    EndPoint remoteEndPoint,
    Action<bool> kick,
    IReceiverContext? receiver = null) : IPublishGate
{
    private string? _openedName;

    public async ValueTask<bool> TryOpenAsync(string streamName, CancellationToken ct)
    {
        bool opened = await registry.TryOpenAsync(authorizer, protocol, streamName, remoteEndPoint,
            sessionId, kick, receiver, ct).ConfigureAwait(false);
        if (opened)
        {
            _openedName = streamName;
        }
        return opened;
    }

    public void Release()
    {
        if (_openedName is { } name)
        {
            _openedName = null;
            registry.Release(name, sessionId);
        }
    }
}

/// <summary>
/// Tracks which session publishes under which stream key, applies
/// <see cref="IPublishAuthorizer"/> decisions, and executes takeovers.
/// One instance per server.
/// </summary>
public sealed class PublishSessionRegistry
{
    private static readonly ILogger<PublishSessionRegistry> s_logger =
        SpangleLogManager.GetLogger<PublishSessionRegistry>();

    private static readonly TimeSpan s_takeoverTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    private sealed class Session(
        string id, string streamName, string protocol, EndPoint remoteEndPoint, Action<bool> kick,
        IReceiverContext? receiver)
    {
        public string Id { get; } = id;
        public string StreamName { get; } = streamName;
        public string Protocol { get; } = protocol;
        public EndPoint RemoteEndPoint { get; } = remoteEndPoint;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        /// <summary>Ends the session; true = handover (the successor continues the playlist)</summary>
        public Action<bool> Kick { get; } = kick;

        /// <summary>Weakly held: monitoring must never keep a dead session alive</summary>
        public WeakReference<IReceiverContext>? Receiver { get; } =
            receiver is null ? null : new WeakReference<IReceiverContext>(receiver);

        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal async ValueTask<bool> TryOpenAsync(IPublishAuthorizer authorizer, string protocol, string streamName,
        EndPoint remoteEndPoint, string sessionId, Action<bool> kick, IReceiverContext? receiver, CancellationToken ct)
    {
        string key = StreamKeys.Sanitize(streamName);
        var self = new Session(sessionId, streamName, protocol, remoteEndPoint, kick, receiver);

        // a kicked session needs a moment to unregister; retry a few times
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Session? existing = _sessions.TryGetValue(key, out Session? cur) ? cur : null;
            var request = new PublishRequest
            {
                Protocol = protocol,
                StreamName = streamName,
                StreamKey = key,
                RemoteEndPoint = remoteEndPoint,
                ExistingSession = existing is null
                    ? null
                    : new ExistingSessionInfo { Id = existing.Id, StartedAt = existing.StartedAt },
            };

            PublishDecision decision = await authorizer.AuthorizeAsync(request, ct).ConfigureAwait(false);
            switch (decision)
            {
                case PublishDecision.Deny:
                    s_logger.ZLogInformation($"Publish denied: {protocol} '{streamName}' from {remoteEndPoint}");
                    return false;

                case PublishDecision.Allow when existing is null:
                    if (_sessions.TryAdd(key, self))
                    {
                        return true;
                    }
                    continue; // lost a race; re-evaluate

                case PublishDecision.Allow:
                    s_logger.ZLogWarning($"Authorizer returned Allow for the contested name '{key}'; treating as Takeover");
                    goto case PublishDecision.Takeover;

                case PublishDecision.Takeover when existing is not null:
                    s_logger.ZLogInformation($"Takeover of '{key}': {existing.Id} -> {sessionId}");
                    existing.Kick(true);
                    try
                    {
                        await existing.Ended.Task.WaitAsync(s_takeoverTimeout, ct).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        s_logger.ZLogWarning($"The previous session of '{key}' did not end within {s_takeoverTimeout}; taking the slot anyway");
                        _sessions.TryRemove(new KeyValuePair<string, Session>(key, existing));
                    }
                    continue;

                case PublishDecision.Takeover:
                    // nothing to take over; same as Allow
                    if (_sessions.TryAdd(key, self))
                    {
                        return true;
                    }
                    continue;

                default:
                    // the decision comes from an authorizer implementation, not from a caller argument
                    throw new InvalidOperationException($"Unknown PublishDecision: {decision}");
            }
        }

        s_logger.ZLogWarning($"Publish of '{key}' gave up after repeated takeover races");
        return false;
    }

    internal void Release(string streamName, string sessionId)
    {
        string key = StreamKeys.Sanitize(streamName);
        if (_sessions.TryGetValue(key, out Session? session) && session.Id == sessionId)
        {
            _sessions.TryRemove(new KeyValuePair<string, Session>(key, session));
            session.Ended.TrySetResult();
        }
    }

    /// <summary>
    /// Snapshots every live publish session for monitoring. Codec and byte counters
    /// come from the session's receiver context when it is still alive.
    /// </summary>
    public IReadOnlyList<PublishSessionInfo> ListSessions()
    {
        var list = new List<PublishSessionInfo>(_sessions.Count);
        foreach ((string key, Session session) in _sessions)
        {
            IReceiverContext? receiver = null;
            session.Receiver?.TryGetTarget(out receiver);
            list.Add(new PublishSessionInfo
            {
                StreamKey = key,
                StreamName = session.StreamName,
                SessionId = session.Id,
                Protocol = session.Protocol,
                RemoteEndPoint = session.RemoteEndPoint.ToString() ?? "",
                StartedAt = session.StartedAt,
                VideoCodec = receiver?.VideoCodec,
                AudioCodec = receiver?.AudioCodec,
                VideoWidth = receiver?.VideoWidth ?? 0,
                VideoHeight = receiver?.VideoHeight ?? 0,
                IsAudioOnly = receiver?.IsAudioOnly ?? false,
                BytesReceived = receiver?.BytesReceived ?? 0,
            });
        }
        return list;
    }

    /// <summary>
    /// Ends the session publishing under <paramref name="streamKey"/> (already-sanitized
    /// key as listed by <see cref="ListSessions"/>). The output is finalized normally
    /// (ENDLIST written) — this is an operator stop, not a takeover.
    /// </summary>
    public bool TryKick(string streamKey)
    {
        if (!_sessions.TryGetValue(streamKey, out Session? session))
        {
            return false;
        }
        s_logger.ZLogInformation($"Operator kick of '{streamKey}' ({session.Protocol} session {session.Id})");
        session.Kick(false);
        return true;
    }
}
