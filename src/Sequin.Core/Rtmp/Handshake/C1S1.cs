using System.Runtime.InteropServices;
using Sequin.Interop;

namespace Sequin.Rtmp.Handshake;

/// <summary>
/// C1 / S1 Message
/// </summary>
/*
  0                   1                   2                   3
  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
 |                        time (4 bytes)                         |
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
 |                        zero (4 bytes)                         |
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
 |                        random bytes                           |
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
 |                        random bytes                           |
 |                           (cont)                              |
 |                            ....                               |
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+

                         C1 and S1 bits
 */
[StructLayout(LayoutKind.Sequential, Pack = 1)] 
internal unsafe struct C1S1
{
    public readonly BigEndianUInt32 Timestamp; 
    public readonly uint Empty;
    public fixed byte Random[IContract.RandomSectionLength];
    
    public Span<byte> RandomSpan => MemoryMarshal.CreateSpan(ref Random[0], IContract.RandomSectionLength);

    public C1S1(uint time)
    {
        Timestamp = BigEndianUInt32.FromHost(time);
        var rand = new Random();
        rand.NextBytes(RandomSpan);
    }
    
    public C1S1(uint time, byte[] random)
    {
        if (random.Length != IContract.RandomSectionLength)
        {
            throw new ArgumentOutOfRangeException(nameof(random));
        }
        Timestamp = BigEndianUInt32.FromHost(time);
        random.CopyTo(RandomSpan);
    }
}
