using Spangle.Net.Moqt;

namespace Spangle.Extensions.Moqt;

/// <summary>
/// Bridges one Spangle CMAF track onto a MOQT published track: the init segment and each media
/// segment become MOQT objects the publisher streams to subscribers. This is the whole of what
/// the Spangle side writes onto the MOQT types — the media mapping the Moqt package deliberately
/// leaves out (its README: "media mapping belongs in the Spangle core, which writes only the
/// bridge").
/// <para>
/// Mapping (first cut): the init segment is group 0 / object 0, and media segment N is group N /
/// object 0 carrying the whole fMP4 segment. One CMAF track (video or audio) maps to one MOQT
/// track, so a full A/V stream drives two bridges (e.g. "video0" and "audio0"). This cut only has
/// to round-trip Spangle's own fragments, and it does.
/// </para>
/// <para>
/// <b>Not yet CMSF.</b> The standard home for this shape is CMSF (draft-ietf-moq-cmsf-01, "CMAF
/// compliant MOQT Streaming Format"), and two of its rules this does not follow yet:
/// the initialization header belongs in the MSF catalog as an <c>initDataList</c> entry (§3.1),
/// not on the wire as group 0; and a group MUST begin at a stream access point of type 1 or 2 and
/// end on a CMAF Fragment boundary (§3.4), which segment-per-group only satisfies by accident of
/// how Spangle happens to cut segments. Both wait on an MSF catalog, which CMSF extends and which
/// nothing here implements — see <see cref="LocProperties"/> for the same gap on the frame-per-
/// object side.
/// </para>
/// </summary>
public sealed class CmafMoqTrackBridge
{
    // MOQT publisher priority is 8-bit, lower = higher priority. The init must arrive before any
    // media, so it goes at the top; media segments share a middle priority.
    private const byte InitPriority = 0;
    private const byte MediaPriority = 128;

    private readonly MoqPublishedTrack _track;
    private ulong _nextGroup;
    private bool _initPublished;

    /// <summary>Creates a bridge that publishes onto <paramref name="track"/>.</summary>
    public CmafMoqTrackBridge(MoqPublishedTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        _track = track;
    }

    /// <summary>Completes with the assigned Track Alias once a subscriber has subscribed.</summary>
    public Task<ulong> WaitForSubscriberAsync() => _track.WaitForSubscriberAsync();

    /// <summary>
    /// Publishes the track's init segment (ftyp+moov) as the first group. Call once, before any
    /// media segment.
    /// </summary>
    public async ValueTask PublishInitAsync(ReadOnlyMemory<byte> initSegment,
        CancellationToken cancellationToken = default)
    {
        if (_initPublished)
        {
            throw new InvalidOperationException("The init segment has already been published.");
        }

        _initPublished = true;
        await PublishGroupAsync(initSegment, InitPriority, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Publishes one CMAF media segment (styp+moof+mdat) as the next group.</summary>
    public async ValueTask PublishSegmentAsync(ReadOnlyMemory<byte> segment,
        CancellationToken cancellationToken = default)
    {
        if (!_initPublished)
        {
            throw new InvalidOperationException("Publish the init segment before any media segment.");
        }

        await PublishGroupAsync(segment, MediaPriority, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PublishGroupAsync(ReadOnlyMemory<byte> payload, byte priority,
        CancellationToken cancellationToken)
    {
        MoqGroupWriter group = await _track.BeginGroupAsync(_nextGroup++, priority, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await using (group.ConfigureAwait(false))
        {
            await group.WriteObjectAsync(0, payload, cancellationToken: cancellationToken).ConfigureAwait(false);
            await group.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
