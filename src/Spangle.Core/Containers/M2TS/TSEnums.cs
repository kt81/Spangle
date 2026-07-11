namespace Spangle.Containers.M2TS;

/// <summary>
/// ISO/IEC 13818-1 stream_type values used by this application,
/// shared between the muxer and the demuxer.
/// </summary>
internal static class M2TSStreamType
{
    public const byte H264    = 0x1B;
    public const byte H265    = 0x24;
    public const byte AdtsAac = 0x0F;

    /// <summary>
    /// PES packets containing private data — the codec is identified by descriptors;
    /// Opus rides here with a registration descriptor "Opus".
    /// </summary>
    public const byte PrivatePes = 0x06;

    /// <summary>Metadata carried in PES packets — timed ID3 for HLS.</summary>
    public const byte PesMetadata = 0x15;
}
