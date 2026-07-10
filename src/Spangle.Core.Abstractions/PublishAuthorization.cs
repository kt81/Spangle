using System.Net;

namespace Spangle;

/// <summary>
/// The outcome of a publish authorization.
/// </summary>
public enum PublishDecision
{
    /// <summary>Let the publisher in (only meaningful while the stream name is free).</summary>
    Allow,

    /// <summary>Reject the publisher (protocol-appropriate rejection, e.g. NetStream.Publish.BadName).</summary>
    Deny,

    /// <summary>
    /// Kick the session currently publishing under the same name and let this one take
    /// over. The output continues under the same name (media sequence continues, with an
    /// EXT-X-DISCONTINUITY marker).
    /// </summary>
    Takeover,
}

/// <summary>The session currently publishing under the requested name, if any.</summary>
public sealed class ExistingSessionInfo
{
    public required string Id { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public TimeSpan Age => DateTimeOffset.UtcNow - StartedAt;
}

/// <summary>Everything known about a publish attempt at authorization time.</summary>
public sealed class PublishRequest
{
    /// <summary>Ingest protocol, e.g. "RTMP" or "SRT".</summary>
    public required string Protocol { get; init; }

    /// <summary>The raw name the publisher presented (RTMP publish name / SRT streamid).</summary>
    public required string StreamName { get; init; }

    /// <summary>The sanitized routing key (directory name, registry key).</summary>
    public required string StreamKey { get; init; }

    public required EndPoint RemoteEndPoint { get; init; }

    /// <summary>Non-null when another session is already publishing under the same key.</summary>
    public ExistingSessionInfo? ExistingSession { get; init; }
}

/// <summary>
/// First-class publish authorization: registered in the host's DI and consulted at each
/// protocol's natural rejection point (RTMP publish command; SRT accept). When no
/// authorizer is registered, the default policy applies: allow everyone, and the newest
/// session under a contested name wins (last-wins), because a zombie session blocking a
/// publisher's reconnect is a worse failure than a takeover by a holder of the same key.
/// </summary>
public interface IPublishAuthorizer
{
    ValueTask<PublishDecision> AuthorizeAsync(PublishRequest request, CancellationToken ct);
}

/// <summary>
/// The per-session view of publish authorization, wired by the pipeline and called by
/// the receiver at its protocol's natural rejection point (RTMP publish command;
/// SRT stream start). Null when no session registry is configured (everything allowed).
/// </summary>
public interface IPublishGate
{
    /// <summary>
    /// Authorizes this session to publish under the given name. Returns false when the
    /// publisher must be rejected. On a takeover decision, the previous session is shut
    /// down (its output is handed over, not finalized) before this returns true.
    /// </summary>
    ValueTask<bool> TryOpenAsync(string streamName, CancellationToken ct);
}

/// <summary>
/// Turns arbitrary stream names into safe routing keys (directory / registry names).
/// Shared by output routing and the publish session registry so they cannot disagree.
/// </summary>
public static class StreamKeys
{
    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "stream";
        }
        Span<char> buff = stackalloc char[name.Length];
        for (var i = 0; i < name.Length; i++)
        {
            char c = name[i];
            buff[i] = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_';
        }
        return new string(buff);
    }
}
