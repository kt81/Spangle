using Spangle.Transport.Rtmp.Chunk;

namespace Spangle.Tests.Transport.Rtmp.ReadState;

/// <summary>
/// Tests for the 1-3 byte chunk basic header encodings of <see cref="BasicHeader"/>.
/// </summary>
public class ReadBasicHeaderTest
{
    private static BasicHeader Parse(params byte[] bytes)
    {
        var header = new BasicHeader();
        bytes.CopyTo(header.AsSpan());
        return header;
    }

    [Fact]
    public void TestType1()
    {
        var header = Parse(0b00_000011); // fmt=0, csId=3
        header.Format.Should().Be(MessageHeaderFormat.Fmt0);
        header.ChunkStreamId.Should().Be(3u);
        header.RequiredLength.Should().Be(1);
    }

    [Fact]
    public void TestType1_Max()
    {
        var header = Parse(0xFF); // fmt=3, csId=63
        header.Format.Should().Be(MessageHeaderFormat.Fmt3);
        header.ChunkStreamId.Should().Be(63u);
        header.RequiredLength.Should().Be(1);
    }

    [Fact]
    public void TestType2()
    {
        var header = Parse(0b01_000000, 0x00); // fmt=1, csId=64
        header.Format.Should().Be(MessageHeaderFormat.Fmt1);
        header.ChunkStreamId.Should().Be(64u);
        header.RequiredLength.Should().Be(2);
    }

    [Fact]
    public void TestType2_Max()
    {
        var header = Parse(0b10_000000, 0xFF); // fmt=2, csId=255+64
        header.Format.Should().Be(MessageHeaderFormat.Fmt2);
        header.ChunkStreamId.Should().Be(319u);
        header.RequiredLength.Should().Be(2);
    }

    [Fact]
    public void TestType3()
    {
        var header = Parse(0b01_000001, 0x00, 0x00); // fmt=1, csId=64
        header.Format.Should().Be(MessageHeaderFormat.Fmt1);
        header.ChunkStreamId.Should().Be(64u);
        header.RequiredLength.Should().Be(3);
    }

    [Fact]
    public void TestType3_Max()
    {
        var header = Parse(0b11_000001, 0xFF, 0xFF); // fmt=3, csId=65535+64
        header.Format.Should().Be(MessageHeaderFormat.Fmt3);
        header.ChunkStreamId.Should().Be(65599u);
        header.RequiredLength.Should().Be(3);
    }

    // The 3-byte form is little-endian: csid = (3rd byte) * 256 + (2nd byte) + 64. The vectors
    // above are byte-order symmetric (00 00 / FF FF), so only an asymmetric vector can tell a
    // conformant decoder from one whose encoder and decoder share the same byte swap — the same
    // round-trip blindness the MoQ varint suite guards against with the spec's worked examples.
    [Fact]
    public void TestType3_ByteOrder()
    {
        var header = Parse(0b00_000001, 0x05, 0x01); // fmt=0, csId = 1*256 + 5 + 64
        header.ChunkStreamId.Should().Be(325u);
    }

    [Fact]
    public void TestType3_ByteOrder_Encode()
    {
        var header = new BasicHeader { Format = MessageHeaderFormat.Fmt0, ChunkStreamId = 325u };
        header.AsSpan().ToArray().Should().Equal(0b00_000001, 0x05, 0x01);
    }

    [Fact]
    public void TestType2_UpperBound_Encode()
    {
        // 319 is the last id that fits the 2-byte form; 320 is the first that needs the 3-byte form
        var atLimit = new BasicHeader { ChunkStreamId = 319u };
        atLimit.RequiredLength.Should().Be(2);
        atLimit.ChunkStreamId.Should().Be(319u);

        var overLimit = new BasicHeader { ChunkStreamId = 320u };
        overLimit.RequiredLength.Should().Be(3);
        overLimit.ChunkStreamId.Should().Be(320u);
    }
}
