using Spangle.Net.Moqt;
using Spangle.Net.Moqt.Wire;

namespace Spangle.Extensions.Moqt;

/// <summary>
/// Publishes encoded frames onto a MOQT track one object per frame, with a group per Group of
/// Pictures: a new group begins at each keyframe, and the frames that depend on it are the objects
/// within it. Audio frames carry no inter-frame dependency, so each begins its own group.
/// <para>
/// This is the object mapping LOC defines (draft-ietf-moq-loc-03 §2.2) — the payload is the codec's
/// elementary bitstream and the metadata rides in the object's Properties — but the mapping is the
/// container's business, not this type's: it takes whatever properties it is handed (see
/// <see cref="Loc03Properties"/>) and never reads them. Grouping by GoP is what makes a subscriber
/// able to join at a group boundary and decode.
/// </para>
/// <para>
/// The counterpart is <see cref="CmafMoqTrackBridge"/>, which carries whole CMAF segments as opaque
/// objects instead of individual frames.
/// </para>
/// </summary>
public sealed class MoqFrameTrack : IAsyncDisposable
{
    // MOQT publisher priority is 8-bit, lower is higher priority. Only meaningful per group.
    private const byte DefaultPriority = 128;

    private readonly MoqPublishedTrack _track;
    private MoqGroupWriter? _group;
    private ulong _nextGroupId;
    private ulong _nextObjectId;

    /// <summary>Creates a publisher for <paramref name="track"/>.</summary>
    public MoqFrameTrack(MoqPublishedTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        _track = track;
    }

    /// <summary>Completes with the assigned Track Alias once a subscriber has subscribed.</summary>
    public Task<ulong> WaitForSubscriberAsync() => _track.WaitForSubscriberAsync();

    /// <summary>
    /// Publishes one frame. <paramref name="startsGroup"/> marks a frame nothing later depends on
    /// resolving backwards — a video keyframe, or any audio frame — and begins a new group; other
    /// frames append to the current one. <paramref name="properties"/> is the container's per-frame
    /// metadata, and <paramref name="frame"/> the raw codec bitstream.
    /// </summary>
    public async ValueTask PublishFrameAsync(ReadOnlyMemory<byte> frame,
        IReadOnlyList<MoqKeyValuePair> properties, bool startsGroup,
        byte priority = DefaultPriority, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(properties);

        if (startsGroup || _group is null)
        {
            await CompleteGroupAsync(cancellationToken).ConfigureAwait(false);
            _group = await _track
                .BeginGroupAsync(_nextGroupId++, priority, hasExtensions: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            _nextObjectId = 0;
        }

        await _group.WriteObjectAsync(_nextObjectId++, frame, properties, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>FINs the current group's stream, if one is open.</summary>
    public async ValueTask CompleteGroupAsync(CancellationToken cancellationToken = default)
    {
        if (_group is null)
        {
            return;
        }

        MoqGroupWriter group = _group;
        _group = null;
        await group.CompleteAsync(cancellationToken).ConfigureAwait(false);
        await group.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await CompleteGroupAsync().ConfigureAwait(false);
}
