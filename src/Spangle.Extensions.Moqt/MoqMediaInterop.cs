using System.Buffers;
using Spangle.Net.Moqt.Wire;

namespace Spangle.Extensions.Moqt;

/// <summary>
/// draft-cenzano-moq-media-interop packaging (the "MoQ MI" mapping the IETF interop tools use).
/// Unlike a container mapping, it carries <em>one encoded frame per MOQT object</em>: the payload
/// is the raw codec bitstream and the per-frame metadata rides in the object's Extension Headers.
/// This type builds those headers; grouping and delivery are <see cref="MoqMediaInteropTrack"/>'s
/// job.
/// <para>
/// Header types follow the MOQT Key-Value-Pair parity rule — an even type carries a varint, an odd
/// type a length-prefixed byte string — so MEDIA_TYPE is even and the packed metadata blobs and
/// codec extradata are odd.
/// </para>
/// <para>
/// <b>Known ambiguity.</b> The metadata blob is a run of varints, but the draft does not pin which
/// varint. This implementation uses the MOQT varint, matching the draft-18 transport it rides on.
/// The reference browser tool (moq-encoder-player) is draft-16 and so packs the blob with the QUIC
/// RFC 9000 varint — the two are incompatible, exactly as the two drafts are. The blob is opaque to
/// MOQT (a relay forwards it untouched), so no peer can validate this choice for us; it is the one
/// part of this mapping that is spec-reasoning rather than verified interop.
/// </para>
/// </summary>
public static class MoqMediaInterop
{
    // Extension header types (draft-cenzano-moq-media-interop).
    private const ulong MediaTypeHeader = 0x0A;              // even -> varint
    private const ulong VideoH264AvccMetadataHeader = 0x15;  // odd  -> bytes
    private const ulong VideoH264AvccExtradataHeader = 0x0D; // odd  -> bytes
    private const ulong AudioOpusMetadataHeader = 0x0F;      // odd  -> bytes
    private const ulong AudioAacLcMetadataHeader = 0x13;     // odd  -> bytes

    /// <summary>MEDIA_TYPE value for H.264 carried in AVCC (length-prefixed NALUs).</summary>
    public const ulong MediaTypeVideoH264Avcc = 0x00;

    /// <summary>MEDIA_TYPE value for an Opus bitstream.</summary>
    public const ulong MediaTypeAudioOpus = 0x01;

    /// <summary>MEDIA_TYPE value for AAC-LC (MPEG-4).</summary>
    public const ulong MediaTypeAudioAacLc = 0x03;

    /// <summary>
    /// The track name for a media type: the mapping appends a fixed suffix to the publisher's
    /// prefix, giving e.g. "live/video0" and "live/audio0".
    /// </summary>
    public static string TrackName(string prefix, bool isAudio)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return prefix + (isAudio ? "audio0" : "video0");
    }

    /// <summary>
    /// The Extension Headers for one H.264 frame: the media type, the packed metadata
    /// (<c>seqId, pts, dts, timebase, duration, wallclock</c>), and — when supplied, typically on a
    /// keyframe — the avcC decoder configuration record as extradata. The object's payload is the
    /// AVCC frame itself.
    /// </summary>
    public static IReadOnlyList<MoqKeyValuePair> VideoH264AvccExtensions(ulong seqId, ulong pts, ulong dts,
        ulong timebase, ulong duration, ulong wallclock, ReadOnlyMemory<byte> avcCExtradata = default)
    {
        var headers = new List<MoqKeyValuePair>(3)
        {
            MoqKeyValuePair.Varint(MediaTypeHeader, MediaTypeVideoH264Avcc),
            MoqKeyValuePair.FromBytes(VideoH264AvccMetadataHeader,
                PackVarints(seqId, pts, dts, timebase, duration, wallclock)),
        };

        if (!avcCExtradata.IsEmpty)
        {
            headers.Add(MoqKeyValuePair.FromBytes(VideoH264AvccExtradataHeader, avcCExtradata.Span));
        }

        return headers;
    }

    /// <summary>
    /// The Extension Headers for one AAC-LC frame: the media type and the packed metadata
    /// (<c>seqId, pts, timebase, sampleFreq, numChannels, duration, wallclock</c>). The object's
    /// payload is the raw AAC frame.
    /// </summary>
    public static IReadOnlyList<MoqKeyValuePair> AudioAacLcExtensions(ulong seqId, ulong pts, ulong timebase,
        ulong sampleFreq, ulong numChannels, ulong duration, ulong wallclock) =>
    [
        MoqKeyValuePair.Varint(MediaTypeHeader, MediaTypeAudioAacLc),
        MoqKeyValuePair.FromBytes(AudioAacLcMetadataHeader,
            PackVarints(seqId, pts, timebase, sampleFreq, numChannels, duration, wallclock)),
    ];

    /// <summary>
    /// The Extension Headers for one Opus frame — the same shape as
    /// <see cref="AudioAacLcExtensions"/> under a different media type and header.
    /// </summary>
    public static IReadOnlyList<MoqKeyValuePair> AudioOpusExtensions(ulong seqId, ulong pts, ulong timebase,
        ulong sampleFreq, ulong numChannels, ulong duration, ulong wallclock) =>
    [
        MoqKeyValuePair.Varint(MediaTypeHeader, MediaTypeAudioOpus),
        MoqKeyValuePair.FromBytes(AudioOpusMetadataHeader,
            PackVarints(seqId, pts, timebase, sampleFreq, numChannels, duration, wallclock)),
    ];

    /// <summary>Reads back a packed metadata blob — the inverse of the packing above.</summary>
    public static ulong[] UnpackVarints(ReadOnlySpan<byte> blob, int count)
    {
        var reader = new MoqReader(blob);
        var values = new ulong[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = reader.ReadVarInt();
        }

        return values;
    }

    private static byte[] PackVarints(params ulong[] values)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MoqWriter(buffer);
        foreach (ulong value in values)
        {
            writer.WriteVarInt(value);
        }

        return buffer.WrittenSpan.ToArray();
    }
}
