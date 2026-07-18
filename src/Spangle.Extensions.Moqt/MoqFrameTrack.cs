using Spangle.Net.Moqt;
using Spangle.Net.Moqt.Wire;

namespace Spangle.Extensions.Moqt;

/// <summary>
/// How a track's objects are spread over MOQT streams. Both mappings put the same objects in the
/// same groups — a Group of Pictures is one MOQT Group either way, which is what MSF §4.1 requires
/// and what lets a subscriber join at a group boundary. They differ only in how many QUIC streams
/// carry it, and what that costs when a packet is lost.
/// </summary>
public enum MoqStreamMapping
{
    /// <summary>
    /// One subgroup stream per group: the keyframe opens it, the frames depending on it follow, and
    /// the next keyframe closes it. Cheap — one stream per GoP — and the group's end is known when
    /// the stream is opened, so the header's END_OF_GROUP bit can state it and a receiver never has
    /// to wait a timeout out to learn the group finished.
    /// <para>
    /// This is what every implementation does: moq-playa's browser broadcaster closes the stream at
    /// each keyframe and calls the shape "one-subgroup-per-GOP LOC video", its node publisher writes
    /// its objects onto one subgroup, and moxygen accepts it. The cost is head-of-line blocking: the
    /// frames of a GoP share a stream, so a lost packet in one holds up the ones behind it.
    /// </para>
    /// </summary>
    GroupPerStream,

    /// <summary>
    /// One stream per object, each frame its own subgroup — what MSF §6 requires in as many words
    /// ("each MOQT Object MUST be mapped to a new MOQT Stream"). A lost packet then delays only its
    /// own frame, which is the point of the rule.
    /// <para>
    /// Nothing else implements this, including the drafts' own reference publishers, so it is not
    /// the default — but it is honest about what the specification says and moq-playa's player reads
    /// it, which is the only claim about it we can actually check.
    /// </para>
    /// <para>
    /// It costs a stream per frame, and one more per group: the END_OF_GROUP bit is written with a
    /// stream's header, and a live publisher opening a frame's stream cannot yet know whether the
    /// GoP ends there — the next keyframe is what says so. So each group is closed by a final
    /// subgroup carrying nothing but an End of Group status object, sent the moment the group is
    /// known to be over.
    /// </para>
    /// </summary>
    ObjectPerStream,
}

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
/// Every group this publishes is closed twice over: with the subgroup header's END_OF_GROUP bit
/// where the mapping makes that knowable, and always with a zero-length End of Group object. Both,
/// because neither alone is enough. A receiver has no other way to tell a group that ended from one
/// whose remainder is late — it waits out a timeout before playing what came after — and the
/// reference relay <em>re-encodes subgroup headers and drops the bit</em>, so behind a relay the
/// object is the only signal that arrives. The bit still earns its place on a direct connection.
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
    private readonly MoqStreamMapping _mapping;
    private MoqGroupWriter? _group;
    private ulong _nextGroupId;
    private ulong _currentGroupId;
    private ulong _nextObjectId;
    private byte _groupPriority = DefaultPriority;
    private bool _groupOpen;

    /// <summary>
    /// Creates a publisher for <paramref name="track"/>. <paramref name="mapping"/> chooses how the
    /// group's objects are spread over streams; the default is what other implementations read and
    /// write.
    /// <para>
    /// <paramref name="firstGroupId"/> is where group numbering starts, and it matters across
    /// restarts, not within a session: a relay caches objects by group and object id, and a group id
    /// reused with different content is a cache collision it resolves by dropping the subscriber
    /// (MSF §6.1 — group ids must be unique and increasing for the life of the <em>track</em>, which
    /// outlives this process). A publisher that may restart under the same track name should pass
    /// the current wall-clock time in milliseconds, which is what the spec suggests and what
    /// <see cref="MoqSender"/> does.
    /// </para>
    /// </summary>
    public MoqFrameTrack(MoqPublishedTrack track, MoqStreamMapping mapping = MoqStreamMapping.GroupPerStream,
        ulong firstGroupId = 0)
    {
        ArgumentNullException.ThrowIfNull(track);
        _track = track;
        _mapping = mapping;
        _nextGroupId = firstGroupId;
    }

    /// <summary>Completes with the assigned Track Alias once a subscriber has subscribed.</summary>
    public Task<ulong> WaitForSubscriberAsync() => _track.WaitForSubscriberAsync();

    /// <summary>
    /// Whether a subscriber has arrived. <see cref="PublishFrameAsync"/> waits for one, so a live
    /// source asks this first and drops the frame instead — waiting would push back on the pipeline
    /// that is still producing frames in real time.
    /// </summary>
    public bool HasSubscriber => _track.HasSubscriber;

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

        if (startsGroup || !_groupOpen)
        {
            await CompleteGroupAsync(cancellationToken).ConfigureAwait(false);
            _currentGroupId = _nextGroupId++;
            _nextObjectId = 0;
            _groupPriority = priority;
            _groupOpen = true;

            if (_mapping == MoqStreamMapping.GroupPerStream)
            {
                // The group is this one subgroup, so it holds the group's largest object by
                // construction and the header can say so before a byte of it is written.
                _group = await _track.BeginGroupAsync(_currentGroupId, priority, hasProperties: true,
                    endOfGroup: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        ulong objectId = _nextObjectId++;
        if (_mapping == MoqStreamMapping.GroupPerStream)
        {
            await _group!.WriteObjectAsync(objectId, frame, properties, cancellationToken).ConfigureAwait(false);
            return;
        }

        // One object, one stream, one subgroup — the subgroup ID is the object's, which is what
        // keeps concurrent frames of a group distinguishable.
        MoqGroupWriter stream = await _track.BeginGroupAsync(_currentGroupId, priority, hasProperties: true,
            endOfGroup: false, subgroupId: objectId, cancellationToken: cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteObjectAsync(objectId, frame, properties, cancellationToken).ConfigureAwait(false);
            await stream.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Closes the current group, if one is open: the subgroup stream is FINed, and where the group's
    /// end was not already stated in a header it is stated now with an End of Group object. Call it
    /// when the track ends; a keyframe does it on its own.
    /// </summary>
    public async ValueTask CompleteGroupAsync(CancellationToken cancellationToken = default)
    {
        if (!_groupOpen)
        {
            return;
        }

        _groupOpen = false;

        if (_mapping == MoqStreamMapping.GroupPerStream)
        {
            MoqGroupWriter? group = _group;
            _group = null;
            if (group is not null)
            {
                // The group's own stream carries the marker; the header already said as much, but
                // the relay will have stripped that before any subscriber sees it.
                await group.WriteEndOfGroupAsync(_nextObjectId, cancellationToken).ConfigureAwait(false);
                await group.CompleteAsync(cancellationToken).ConfigureAwait(false);
                await group.DisposeAsync().ConfigureAwait(false);
            }

            return;
        }

        // The group's frames have each had their stream FINed already, and none of their headers
        // could claim to end the group. This last subgroup carries no media — only the news that
        // the group stopped at the object before it. One object, one stream, as §6 asks.
        MoqGroupWriter marker = await _track.BeginGroupAsync(_currentGroupId, _groupPriority,
            hasProperties: false, endOfGroup: true, subgroupId: _nextObjectId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using (marker.ConfigureAwait(false))
        {
            await marker.WriteEndOfGroupAsync(_nextObjectId, cancellationToken).ConfigureAwait(false);
            await marker.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Forgets the current group without the closing writes — for when its stream is already dead.
    /// A peer may reset any data stream (in draft-18 the stream, not the session, is the unit of
    /// delivery), and writing a farewell to a reset stream only throws again. The group it carried
    /// is lost; the track is not. The next <see cref="PublishFrameAsync"/> with
    /// <c>startsGroup: true</c> begins cleanly.
    /// </summary>
    public async ValueTask AbandonGroupAsync()
    {
        _groupOpen = false;
        MoqGroupWriter? group = _group;
        _group = null;
        if (group is not null)
        {
            try
            {
                await group.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // the stream is already dead, which is the premise of being here
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await CompleteGroupAsync().ConfigureAwait(false);
}
