using System.Collections.Frozen;

namespace Spangle;

/// <summary>
/// Allows only the configured stream names, matched exactly against the raw name the
/// publisher presented (RTMP publish name / SRT streamid). Contested names keep the
/// last-wins takeover policy of <see cref="DefaultPublishAuthorizer"/>, so a reconnect
/// by a holder of a valid name still just works.
/// </summary>
public sealed class AllowListPublishAuthorizer(IEnumerable<string> allowedStreamNames) : IPublishAuthorizer
{
    private readonly FrozenSet<string> _allowed = allowedStreamNames.ToFrozenSet(StringComparer.Ordinal);

    public ValueTask<PublishDecision> AuthorizeAsync(PublishRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ValueTask<PublishDecision>(
            !_allowed.Contains(request.StreamName)
                ? PublishDecision.Deny
                : request.ExistingSession is null
                    ? PublishDecision.Allow
                    : PublishDecision.Takeover);
    }
}
