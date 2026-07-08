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
}
