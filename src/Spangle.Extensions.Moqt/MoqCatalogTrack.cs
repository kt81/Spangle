using System.Text;
using Spangle.Net.Moqt;

namespace Spangle.Extensions.Moqt;

/// <summary>
/// Publishes an <see cref="MsfCatalog"/> on the MOQT track subscribers look for it on (MSF §5).
/// This is the track that makes a broadcast discoverable: a player is given a relay and a namespace,
/// subscribes to the catalog, and learns from it which tracks exist and how to decode them — so
/// nothing else a publisher offers is reachable until this track is live.
/// <para>
/// Each call to <see cref="PublishAsync"/> puts one complete catalog at Object ID 0 of a new group
/// on subgroup 0, which is what §5 requires of an independent catalog, and closes the group. Delta
/// updates (Object IDs ≥ 1 within a group) are not implemented; republishing the whole catalog is
/// conformant, and at the size of a catalog it costs little.
/// </para>
/// <para>
/// The counterpart on the wire is <see cref="MsfCatalog.Parse"/>. A relay forwards this payload
/// without reading it (§5), so what a subscriber parses is exactly what
/// <see cref="MsfCatalog.WriteTo"/> produced, draft spelling and all.
/// </para>
/// </summary>
public sealed class MoqCatalogTrack : IAsyncDisposable
{
    /// <summary>
    /// The Track Name a catalog must be published under (MSF §5), and it is case-sensitive: a
    /// subscriber asks for this exact name, having never been told anything else about the
    /// broadcast.
    /// </summary>
    public const string TrackName = "catalog";

    // MOQT publisher priority is 8-bit and lower is more urgent. The catalog outranks media
    // (MoqFrameTrack sends at 128) because a subscriber can decode nothing until it arrives, and
    // there is one small object of it per group.
    private const byte CatalogPriority = 64;

    private readonly MoqPublishedTrack _track;
    private ulong _nextGroupId;
    private MoqGroupWriter? _group;

    /// <summary>
    /// Creates a catalog publisher over <paramref name="track"/>, which must have been declared
    /// under <see cref="TrackName"/> — see <see cref="NameIn"/>.
    /// <para>
    /// <paramref name="firstGroupId"/> is where group numbering starts. Group IDs must be unique and
    /// increasing for the life of a track, across restarts and not just within one session (MSF
    /// §6.1), and this process cannot know what a previous one published. Zero is right when the
    /// track's history starts here; a publisher that may restart under the same track name should
    /// pass the current wall-clock time in milliseconds, which is the approach the spec names.
    /// </para>
    /// </summary>
    public MoqCatalogTrack(MoqPublishedTrack track, ulong firstGroupId = 0)
    {
        ArgumentNullException.ThrowIfNull(track);
        _track = track;
        _nextGroupId = firstGroupId;
    }

    /// <summary>The Full Track Name of the catalog track in <paramref name="namespace"/>.</summary>
    public static FullTrackName NameIn(TrackNamespace @namespace) =>
        new(@namespace, Encoding.UTF8.GetBytes(TrackName));

    /// <summary>Completes with the assigned Track Alias once a subscriber has subscribed.</summary>
    public Task<ulong> WaitForSubscriberAsync() => _track.WaitForSubscriberAsync();

    /// <summary>
    /// Publishes <paramref name="catalog"/> as a complete, independent catalog at the head of a new
    /// group. Blocks until a subscriber has subscribed to the track.
    /// </summary>
    public async ValueTask PublishAsync(MsfCatalog catalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        // Serialize before opening the stream: an invalid catalog throws here, and a group opened
        // and never written would leave a subscriber waiting on an empty stream.
        byte[] json = catalog.ToJsonUtf8();

        await CompleteGroupAsync(cancellationToken).ConfigureAwait(false);
        // A catalog group is this one object, so the group ends where the stream does and the
        // header says so — a subscriber that had to wait out a timeout to be sure would be waiting
        // before it knew a single track existed.
        _group = await _track
            .BeginGroupAsync(_nextGroupId++, CatalogPriority, hasProperties: false, endOfGroup: true,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await _group.WriteObjectAsync(0, json, cancellationToken: cancellationToken).ConfigureAwait(false);
        // Say it in the objects too, not only in the header: the reference relay re-encodes subgroup
        // headers and drops the END_OF_GROUP bit, so on the far side of one this is what is left of
        // "that was the whole catalog".
        await _group.WriteEndOfGroupAsync(1, cancellationToken).ConfigureAwait(false);
        await CompleteGroupAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops the current group so the next <see cref="PublishAsync"/> starts cleanly — for when the
    /// group is past saving (a subscriber may reset the stream carrying it). It ends the group's
    /// streams (each subscriber's is FINed by its own fan-out pump, one already dead simply dropped)
    /// without the closing End of Group object a clean publish writes.
    /// </summary>
    public async ValueTask AbandonGroupAsync()
    {
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
                // Ending a group whose streams are already gone is a no-op the pumps absorb; this
                // only guards the rare throw as a stream is aborted out from under the close.
            }
        }
    }

    private async ValueTask CompleteGroupAsync(CancellationToken cancellationToken)
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
    public async ValueTask DisposeAsync() => await CompleteGroupAsync(CancellationToken.None).ConfigureAwait(false);
}
