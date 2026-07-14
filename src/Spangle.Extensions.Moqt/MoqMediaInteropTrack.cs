using Spangle.Net.Moqt;
using Spangle.Net.Moqt.Wire;

namespace Spangle.Extensions.Moqt;

/// <summary>
/// Publishes encoded frames onto a MOQT track using the draft-cenzano-moq-media-interop mapping:
/// one object per frame, metadata in the object's Extension Headers (see
/// <see cref="MoqMediaInterop"/>), and a group per Group of Pictures — a new group begins at each
/// keyframe, and the frames that depend on it are the objects within it. Audio frames, which carry
/// no inter-frame dependency, each begin their own group.
/// <para>
/// This is the media-mapping counterpart to <see cref="CmafMoqTrackBridge"/>: that one carries
/// whole CMAF segments as opaque objects, this one carries per-frame codec bitstream the way the
/// IETF interop tools expect.
/// </para>
/// </summary>
public sealed class MoqMediaInteropTrack : IAsyncDisposable
{
    // MOQT publisher priority is 8-bit, lower is higher priority. Only meaningful per group.
    private const byte DefaultPriority = 128;

    private readonly MoqPublishedTrack _track;
    private MoqGroupWriter? _group;
    private ulong _nextGroupId;
    private ulong _nextObjectId;

    /// <summary>Creates a publisher for <paramref name="track"/>.</summary>
    public MoqMediaInteropTrack(MoqPublishedTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        _track = track;
    }

    /// <summary>Completes with the assigned Track Alias once a subscriber has subscribed.</summary>
    public Task<ulong> WaitForSubscriberAsync() => _track.WaitForSubscriberAsync();

    /// <summary>
    /// Publishes one frame. <paramref name="startsGroup"/> marks a frame nothing later depends on
    /// resolving backwards — a video keyframe, or any audio frame — and begins a new group; other
    /// frames append to the current one. <paramref name="extensions"/> comes from
    /// <see cref="MoqMediaInterop"/>, and <paramref name="frame"/> is the raw codec bitstream.
    /// </summary>
    public async ValueTask PublishFrameAsync(ReadOnlyMemory<byte> frame,
        IReadOnlyList<MoqKeyValuePair> extensions, bool startsGroup,
        byte priority = DefaultPriority, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extensions);

        if (startsGroup || _group is null)
        {
            await CompleteGroupAsync(cancellationToken).ConfigureAwait(false);
            _group = await _track
                .BeginGroupAsync(_nextGroupId++, priority, hasExtensions: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            _nextObjectId = 0;
        }

        await _group.WriteObjectAsync(_nextObjectId++, frame, extensions, cancellationToken).ConfigureAwait(false);
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
