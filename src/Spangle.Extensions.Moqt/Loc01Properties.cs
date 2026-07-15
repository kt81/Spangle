using Spangle.Net.Moqt.Wire;

namespace Spangle.Extensions.Moqt;

/// <summary>
/// LOC Header Extensions — the Low Overhead Media Container, <b>draft-ietf-moq-loc-01</b> (§2.3).
/// The object mapping is the same idea as later drafts (§2.2: the payload is the codec's elementary
/// bitstream, the metadata rides in the object's header extensions), but the metadata itself is not
/// the same, so this is a separate implementation from <see cref="Loc03Properties"/> rather than a
/// version switch inside one — see that type for what moved.
/// <para>
/// Why an old draft is here at all: -03 is current and what we publish, but every implementation we
/// can check ourselves against — moq5, moq-playa — still speaks -01, and each binds it to the
/// draft-14/16 KVP wire format it was written against. We speak only draft-18, so on the face of it
/// those are unreachable.
/// </para>
/// <para>
/// They are not, because a relay is the version adapter. It parses each object's properties and
/// re-encodes them for whatever draft the subscriber negotiated, carrying the IDs and values across
/// untouched — we watched a draft-16 browser's properties arrive at our draft-18 subscriber with
/// both intact. So LOC-01 IDs over draft-18 framing is not a chimera; it is exactly what a
/// draft-16 LOC-01 publisher looks like from where we stand. That is what this type reads and
/// writes, and it is why supporting draft-16 ourselves would buy nothing.
/// </para>
/// <para>
/// Four extensions, all optional. There is no Timescale: a Capture Timestamp is always wall-clock
/// microseconds. There is no Audio Config either, and no public/private split — everything here is
/// visible to relays.
/// </para>
/// </summary>
public static class Loc01Properties
{
    /// <summary>Capture Timestamp (§2.3.1.1) — ID 2. Even, so the value is a varint.</summary>
    public const ulong CaptureTimestampId = 2;

    /// <summary>Video Frame Marking (§2.3.2.2) — ID 4. Even, so the value is a varint.</summary>
    public const ulong VideoFrameMarkingId = 4;

    /// <summary>Audio Level (§2.3.3.1) — ID 6. Even, so the value is a varint.</summary>
    public const ulong AudioLevelId = 6;

    /// <summary>Video Config (§2.3.2.1) — ID 13. Odd, so the value is a length-prefixed byte string.</summary>
    public const ulong VideoConfigId = 13;

    /// <summary>
    /// When the frame was captured: wall-clock microseconds since the Unix epoch. That is the only
    /// reading this version has — there is no timescale to make it mean anything else.
    /// </summary>
    public static MoqKeyValuePair CaptureTimestamp(ulong microsecondsSinceEpoch) =>
        MoqKeyValuePair.Varint(CaptureTimestampId, microsecondsSinceEpoch);

    /// <summary>
    /// The decoder configuration ("extradata", an avcC record for H.264), sent on the frames that
    /// need it — typically each keyframe. The one extension whose ID survived into -03 unchanged.
    /// </summary>
    public static MoqKeyValuePair VideoConfig(ReadOnlySpan<byte> extradata) =>
        MoqKeyValuePair.FromBytes(VideoConfigId, extradata);

    /// <summary>
    /// The RFC 9626 frame-marking flags, in the low bits of a varint. -03 moved these into a
    /// length-prefixed byte string, so this signature is the shape difference made visible.
    /// </summary>
    public static MoqKeyValuePair VideoFrameMarking(ulong marking) =>
        MoqKeyValuePair.Varint(VideoFrameMarkingId, marking);

    /// <summary>The RFC 6464 audio level and voice-activity flag, in the low 8 bits of a varint.</summary>
    public static MoqKeyValuePair AudioLevel(byte levelAndVoiceActivity) =>
        MoqKeyValuePair.Varint(AudioLevelId, levelAndVoiceActivity);
}

/// <summary>
/// The draft-ietf-moq-loc-01 header extensions this implementation knows, picked out of a MOQT
/// object's extensions. Every field is optional because every extension is (§2.3), and an
/// unrecognized ID belongs to some other specification registered in the same registry, so it is
/// left alone rather than rejected.
/// </summary>
public readonly struct Loc01Metadata
{
    /// <summary>Wall-clock microseconds since the Unix epoch at capture. Always that; -01 has no timescale.</summary>
    public ulong? CaptureTimestamp { get; private init; }

    /// <summary>The RFC 9626 frame-marking flags, as the varint they are in this version.</summary>
    public ulong? VideoFrameMarking { get; private init; }

    /// <summary>The RFC 6464 audio level and voice-activity flag.</summary>
    public byte? AudioLevel { get; private init; }

    /// <summary>The video decoder configuration ("extradata"); empty when absent.</summary>
    public ReadOnlyMemory<byte> VideoConfig { get; private init; }

    /// <summary>Picks the known LOC-01 extensions out of <paramref name="extensions"/>.</summary>
    public static Loc01Metadata Read(IReadOnlyList<MoqKeyValuePair> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);

        var metadata = new Loc01Metadata();
        foreach (MoqKeyValuePair extension in extensions)
        {
            metadata = extension.Type switch
            {
                Loc01Properties.CaptureTimestampId => metadata with { CaptureTimestamp = extension.VarintValue },
                Loc01Properties.VideoFrameMarkingId => metadata with { VideoFrameMarking = extension.VarintValue },
                Loc01Properties.AudioLevelId => metadata with { AudioLevel = (byte)extension.VarintValue },
                Loc01Properties.VideoConfigId => metadata with { VideoConfig = extension.Bytes.ToArray() },
                _ => metadata,
            };
        }

        return metadata;
    }
}
