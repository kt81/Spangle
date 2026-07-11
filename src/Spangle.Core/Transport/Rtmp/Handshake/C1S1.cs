using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Spangle.Interop;

namespace Spangle.Transport.Rtmp.Handshake;

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
internal struct C1S1
{
    public readonly BigEndianUInt32 Timestamp;
    public readonly uint Empty;
    public HandshakeRandomBytes Random;

    [UnscopedRef]
    public Span<byte> RandomSpan => Random;

    [UnscopedRef]
    public readonly ReadOnlySpan<byte> RandomReadOnlySpan => Random;

    public C1S1(uint time)
    {
        Timestamp = BigEndianUInt32.FromHost(time);
        // The spec only asks for "any values"; the crypto RNG costs nothing here
        // (once per connection) and keeps the analyzer's security bar (CA5394).
        System.Security.Cryptography.RandomNumberGenerator.Fill(RandomSpan);
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

/// <summary>The 1528-byte random section shared by C1/S1 and C2/S2 (echo).</summary>
[InlineArray(IContract.RandomSectionLength)]
internal struct HandshakeRandomBytes
{
    private byte _element0;
}
