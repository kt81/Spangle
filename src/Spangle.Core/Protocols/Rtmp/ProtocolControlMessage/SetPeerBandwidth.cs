using System.Runtime.InteropServices;
using Spangle.Interop;

namespace Spangle.Protocols.Rtmp.ProtocolControlMessage;

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 5)]
internal struct SetPeerBandwidth
{
    public BigEndianUInt32    AcknowledgementWindowSize;
    public BandwidthLimitType LimitType;
}

internal enum BandwidthLimitType : byte
{
    Hard    = 0,
    Soft    = 1,
    Dynamic = 2,
}
