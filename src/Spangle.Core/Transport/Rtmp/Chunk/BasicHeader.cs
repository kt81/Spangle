using System.Runtime.InteropServices;

namespace Spangle.Transport.Rtmp.Chunk;

/// <summary>
/// Chunk Basic Header
/// 1 ～ 3 bytes
/// </summary>
/*
  0 1 2 3 4 5 6 7
 +-+-+-+-+-+-+-+-+
 |fmt|   cs id   |
 +-+-+-+-+-+-+-+-+

 Chunk basic header 1

----

  0                   1
  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
 |fmt|     0     |   cs id - 64  |
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+

 Chunk basic header 2

----

  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
 |fmt|     1     |          cs id - 64           |
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+

 Chunk basic header 3
 */
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = MaxSize)]
internal unsafe struct BasicHeader
{
    public const  int  MaxSize = 3;
    private const byte MaxIdH1 = 0b0011_1111; // 63
    private const uint MaxIdH2 = 0xFF + MaxIdH1;

    private const byte FmtMask  = 0b1100_0000;
    private const byte FbIdMask = 0b0011_1111;

    private const byte H2Code = 0;
    private const byte H3Code = 1;

    private const byte MultiByteIdBias = 64;

    [FieldOffset(0)] private fixed byte _value[MaxSize];

    public MessageHeaderFormat Format
    {
        readonly get => (MessageHeaderFormat)(_value[0] >>> 6);
        // xx11_1111
        set => _value[0] = (byte)(((byte)value << 6) | (_value[0] & FbIdMask));
    }

    public uint ChunkStreamId
    {
        readonly get
        {
            var checkBits = (byte)(_value[0] & MaxIdH1);
            return checkBits switch
            {
                H2Code => (uint)(_value[1] + 64),
                H3Code => ((uint)_value[1] << 8) + _value[2] + 64,
                _      => checkBits,
            };
        }
        set
        {
            switch (value)
            {
                case <= MaxIdH1:
                    _value[0] = (byte)((byte)value | (_value[0] & FmtMask));
                    _value[1] = _value[2] = 0;
                    break;
                case <= MaxIdH2:
                    _value[0] = (byte)(_value[0] & FmtMask);
                    _value[1] = (byte)(value - MultiByteIdBias);
                    _value[2] = 0;
                    break;
                default:
                    _value[0] = (byte)((_value[0] & FmtMask) + 1);
                    value -= MultiByteIdBias;
                    _value[1] = (byte)(value >>> 8);
                    _value[2] = (byte)value;
                    break;
            }
        }
    }

    public readonly int RequiredLength => (_value[0] & FbIdMask) switch
    {
        H2Code => 2,
        H3Code => 3,
        _      => 1,
    };

    public readonly Span<byte> AsSpan()
    {
        fixed (void* p = &this)
        {
            return new Span<byte>(p, MaxSize);
        }
    }

    public void Dump()
    {

    }
    public override readonly string ToString()
    {
        return
            $$"""BasicHeader {fmt:{{Format}}, csId:{{ChunkStreamId}}} {{string.Join(' ', AsSpan().ToArray().Select(x => $"{x:X02}"))}}""";
    }
}
