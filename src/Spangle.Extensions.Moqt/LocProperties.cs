using Spangle.Net.Moqt.Wire;

namespace Spangle.Extensions.Moqt;

/// <summary>
/// LOC — the Low Overhead Media Container (draft-ietf-moq-loc-03). It carries <em>one encoded frame
/// per MOQT object</em>: the object's payload is the codec's elementary bitstream verbatim (WebCodecs
/// calls it the chunk's "internal data" — no container framing at all, which is where the name comes
/// from), and the per-frame metadata rides in the object's Properties as Key-Value-Pairs.
/// <para>
/// So there is little code here, and that is the point: a LOC property <em>is</em> a MOQT
/// Key-Value-Pair (§2.3 spells out the same even/odd parity rule — an even ID carries a vi64 value,
/// an odd ID a length-prefixed byte string), and <c>vi64</c> is MOQT's own variable-length integer.
/// The wire machinery already exists; this type only supplies the registered IDs and keeps callers
/// on the right side of the parity rule.
/// </para>
/// <para>
/// <b>LOC does not name the codec.</b> There is no media-type property — the codec, and every other
/// per-track fact, comes from the MSF catalog (draft-ietf-moq-msf), which LOC layers under. A LOC
/// publisher without a catalog is telling a subscriber what the timestamps are but never what to
/// decode.
/// </para>
/// <para>
/// Properties are visible to relays. Where that matters, LOC hands them to MoQ Secure Objects to be
/// encrypted alongside the payload instead (§3.1.3); this type builds the relay-visible form.
/// </para>
/// </summary>
public static class LocProperties
{
    /// <summary>Timescale (§2.3.1.2) — Timestamp units per second. Even, so the value is a vi64.</summary>
    public const ulong TimescaleId = 0x08;

    /// <summary>Video Frame Marking (§2.3.2.2) — RFC 9626 flags. Odd, so the value is a byte string.</summary>
    public const ulong VideoFrameMarkingId = 0x09;

    /// <summary>Timestamp (§2.3.1.1) — the frame's time. Even, so the value is a vi64.</summary>
    public const ulong TimestampId = 0x0A;

    /// <summary>Audio Level (§2.3.3.2) — RFC 6464 level and voice activity. Even, so the value is a vi64.</summary>
    public const ulong AudioLevelId = 0x0C;

    /// <summary>Video Config (§2.3.2.1) — codec "extradata" (e.g. avcC). Odd, so the value is a byte string.</summary>
    public const ulong VideoConfigId = 0x0D;

    /// <summary>Audio Config (§2.3.3.1) — codec configuration. Odd, so the value is a byte string.</summary>
    public const ulong AudioConfigId = 0x0F;

    /// <summary>A timescale of microseconds, the unit the spec names first and WebCodecs works in.</summary>
    public const ulong MicrosecondTimescale = 1_000_000;

    /// <summary>A timescale of 90 kHz, the conventional video clock rate.</summary>
    public const ulong VideoClockTimescale = 90_000;

    /// <summary>
    /// A frame's time as <em>media time</em>: <paramref name="timestamp"/> counted in
    /// <paramref name="timescale"/> units per second, anchored wherever the application says.
    /// <para>
    /// Both properties are returned together because that is what makes them mean this: a Timestamp
    /// with no Timescale beside it is not media time at all but wall-clock microseconds
    /// (§2.3.1.1), so sending one without the other silently changes what the number is.
    /// </para>
    /// </summary>
    public static IReadOnlyList<MoqKeyValuePair> MediaTime(ulong timestamp, ulong timescale = MicrosecondTimescale)
    {
        if (timescale == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timescale), "A timescale is units per second and cannot be zero.");
        }

        return [Timescale(timescale), Timestamp(timestamp)];
    }

    /// <summary>
    /// A frame's time as wall-clock microseconds since the Unix epoch — the reading a Timestamp
    /// takes when no Timescale accompanies it (§2.3.1.1).
    /// </summary>
    public static MoqKeyValuePair WallClockTime(ulong microsecondsSinceEpoch) => Timestamp(microsecondsSinceEpoch);

    /// <summary>The Timestamp property alone. Prefer <see cref="MediaTime"/> or <see cref="WallClockTime"/>.</summary>
    public static MoqKeyValuePair Timestamp(ulong timestamp) => MoqKeyValuePair.Varint(TimestampId, timestamp);

    /// <summary>The Timescale property alone. Prefer <see cref="MediaTime"/>.</summary>
    public static MoqKeyValuePair Timescale(ulong unitsPerSecond) => MoqKeyValuePair.Varint(TimescaleId, unitsPerSecond);

    /// <summary>
    /// The decoder configuration, sent on the frames that need it (typically each keyframe). These
    /// are the codec's "extradata" bytes — an avcC record for H.264 — the same bytes WebCodecs takes
    /// as <c>VideoDecoderConfig.description</c>.
    /// </summary>
    public static MoqKeyValuePair VideoConfig(ReadOnlySpan<byte> extradata) =>
        MoqKeyValuePair.FromBytes(VideoConfigId, extradata);

    /// <summary>The audio decoder configuration (WebCodecs <c>AudioDecoderConfig.description</c>).</summary>
    public static MoqKeyValuePair AudioConfig(ReadOnlySpan<byte> description) =>
        MoqKeyValuePair.FromBytes(AudioConfigId, description);

    /// <summary>The RFC 6464 audio level and voice-activity flag, in the low 8 bits of a vi64.</summary>
    public static MoqKeyValuePair AudioLevel(byte levelAndVoiceActivity) =>
        MoqKeyValuePair.Varint(AudioLevelId, levelAndVoiceActivity);

    /// <summary>The RFC 9626 frame-marking flags, which let a relay drop or forward without decoding.</summary>
    public static MoqKeyValuePair VideoFrameMarking(ReadOnlySpan<byte> marking) =>
        MoqKeyValuePair.FromBytes(VideoFrameMarkingId, marking);
}

/// <summary>
/// The LOC properties this implementation knows, picked out of a MOQT object's Properties.
/// <para>
/// Every field is optional, because in LOC every property is (§2.3), and anything unrecognized is
/// left alone rather than rejected — the registry is open, and other specifications register their
/// own properties into it, so a frame carrying one is not malformed.
/// </para>
/// </summary>
public readonly struct LocMetadata
{
    /// <summary>The frame's time, in <see cref="Timescale"/> units — or microseconds, per <see cref="IsWallClock"/>.</summary>
    public ulong? Timestamp { get; private init; }

    /// <summary>Timestamp units per second. Absent means <see cref="Timestamp"/> is wall-clock.</summary>
    public ulong? Timescale { get; private init; }

    /// <summary>The video decoder configuration ("extradata", e.g. avcC); empty when absent.</summary>
    public ReadOnlyMemory<byte> VideoConfig { get; private init; }

    /// <summary>The audio decoder configuration; empty when absent.</summary>
    public ReadOnlyMemory<byte> AudioConfig { get; private init; }

    /// <summary>The RFC 6464 audio level and voice-activity flag.</summary>
    public byte? AudioLevel { get; private init; }

    /// <summary>The RFC 9626 frame-marking flags; empty when absent.</summary>
    public ReadOnlyMemory<byte> VideoFrameMarking { get; private init; }

    /// <summary>
    /// Whether <see cref="Timestamp"/> is wall-clock microseconds since the Unix epoch rather than
    /// media time — which is true exactly when no Timescale came with it (§2.3.1.1). The same
    /// number means two different things depending on a property that is not there, so this is
    /// worth asking rather than assuming.
    /// </summary>
    public bool IsWallClock => Timestamp is not null && Timescale is null;

    /// <summary>Picks the known LOC properties out of <paramref name="properties"/>.</summary>
    public static LocMetadata Read(IReadOnlyList<MoqKeyValuePair> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        var metadata = new LocMetadata();
        foreach (MoqKeyValuePair property in properties)
        {
            metadata = property.Type switch
            {
                LocProperties.TimestampId => metadata with { Timestamp = property.VarintValue },
                LocProperties.TimescaleId => metadata with { Timescale = property.VarintValue },
                LocProperties.AudioLevelId => metadata with { AudioLevel = (byte)property.VarintValue },
                LocProperties.VideoConfigId => metadata with { VideoConfig = property.Bytes.ToArray() },
                LocProperties.AudioConfigId => metadata with { AudioConfig = property.Bytes.ToArray() },
                LocProperties.VideoFrameMarkingId => metadata with { VideoFrameMarking = property.Bytes.ToArray() },
                _ => metadata,
            };
        }

        return metadata;
    }
}
