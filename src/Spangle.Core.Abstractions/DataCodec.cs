namespace Spangle;

/// <summary>
/// Payload format of a <c>MediaFrameKind.Data</c> frame (timed metadata).
/// </summary>
public enum DataCodec : uint
{
    /// <summary>An AMF0 sequence: event name string followed by its arguments (RTMP data messages)</summary>
    Amf0 = 'a' << 24 | 'm' << 16 | 'f' << 8 | '0',

    /// <summary>A complete ID3v2 tag — the canonical timed-metadata form (HLS timed ID3 / emsg)</summary>
    Id3 = 'i' << 24 | 'd' << 16 | '3' << 8 | ' ',
}
