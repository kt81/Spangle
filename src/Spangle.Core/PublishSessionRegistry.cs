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
    Action kick) : IPublishGate
{
    private string? _openedName;

    public async ValueTask<bool> TryOpenAsync(string streamName, CancellationToken ct)
    {
        bool opened = await registry.TryOpenAsync(authorizer, protocol, streamName, remoteEndPoint,
            sessionId, kick, ct).ConfigureAwait(false);
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

    private sealed class Session(string id, Action kick)
    {
        public string Id { get; } = id;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public Action Kick { get; } = kick;
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal async ValueTask<bool> TryOpenAsync(IPublishAuthorizer authorizer, string protocol, string streamName,
        EndPoint remoteEndPoint, string sessionId, Action kick, CancellationToken ct)
    {
        string key = StreamKeys.Sanitize(streamName);
        var self = new Session(sessionId, kick);

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
                    existing.Kick();
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
}
