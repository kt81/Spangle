using System.Buffers;

namespace Spangle.Protocols.Rtmp.Amf0;

/// <summary>
/// AMF0 Serializable
/// </summary>
public interface IAmf0Serializable
{
    int WriteBytes(IBufferWriter<byte> writer);
}
