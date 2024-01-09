using System.Runtime.InteropServices;
using Spangle.Interop;

namespace Spangle.Transport.Rtmp.Handshake;

/// <summary>
/// C2 / S2 Message
/// </summary>
/*
  0                   1                   2                   3
  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
 |                        time (4 bytes)                         |
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
 |                       time2 (4 bytes)                         |
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
 |                         random echo                           |
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
 |                         random echo                           |
 |                            (cont)                             |
 |                             ....                              |
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+

                          C2 and S2 bits
 */
[StructLayout(LayoutKind.Sequential, Pack = 1)] 
internal unsafe struct C2S2
{
    /// <summary>
    /// The timestamp sent by the peer in S1 (for C2) or C1 (for S2)
    /// </summary>
    public BigEndianUInt32 Timestamp; 
    /// <summary>
    /// The timestamp at which the previous packet(s1 or c1) sent by the peer was read
    /// </summary>
    public BigEndianUInt32 Timestamp2; 
    /// <summary>
    /// the random data field sent by the peer in S1 (for C2) or S2 (for C1)
    /// </summary>
    public fixed byte RandomEcho[IContract.RandomSectionLength];

    public Span<byte> RandomEchoSpan => 
        MemoryMarshal.CreateSpan(ref RandomEcho[0], IContract.RandomSectionLength);
    
    public C2S2(in C1S1 recvPeerMessage, uint time)
    {
        Timestamp = recvPeerMessage.Timestamp;
        Timestamp2 = BigEndianUInt32.FromHost(time);
        recvPeerMessage.RandomSpan.CopyTo(RandomEchoSpan);
    }
}
