using Spangle.Rtmp.Chunk;
using Spangle.Rtmp.ReadState;

namespace Spangle.Tests.Rtmp.ReadState;

public class ReadBasicHeaderTest
{
    [Fact]
    public async Task TestType1()
    {
        byte[] expected = { 0b00_000011 }; // fmt=0, csId=3
        var context = (await TestContext.WithData(expected)).Context;
        await ReadBasicHeader.Perform(context);
        context.BasicHeader.Format.Should().Be(MessageHeaderFormat.Fmt0);
        context.BasicHeader.ChunkStreamId.Should().Be(3u);
    }

    [Fact]
    public async Task TestType1_Max()
    {
        byte[] expected = { 0xFF }; // fmt=3, csId=63
        var context = (await TestContext.WithData(expected)).Context;
        await ReadBasicHeader.Perform(context);
        context.BasicHeader.Format.Should().Be(MessageHeaderFormat.Fmt3);
        context.BasicHeader.ChunkStreamId.Should().Be(63u);
    }

    [Fact]
    public async Task TestType2()
    {
        byte[] expected = { 0b01_000000, 0x00 }; // fmt=1, csId=64
        var context = (await TestContext.WithData(expected)).Context;
        await ReadBasicHeader.Perform(context);
        context.BasicHeader.Format.Should().Be(MessageHeaderFormat.Fmt1);
        context.BasicHeader.ChunkStreamId.Should().Be(64u);
    }

    [Fact]
    public async Task TestType2_Max()
    {
        byte[] expected = { 0b10_000000, 0xFF }; // fmt=2, csId=255+64
        var context = (await TestContext.WithData(expected)).Context;
        await ReadBasicHeader.Perform(context);
        context.BasicHeader.Format.Should().Be(MessageHeaderFormat.Fmt2);
        context.BasicHeader.ChunkStreamId.Should().Be(319u);
    }

    [Fact]
    public async Task TestType3()
    {
        byte[] expected = { 0b01_000001, 0x00, 0x00 }; // fmt=1, csId=64
        var context = (await TestContext.WithData(expected)).Context;
        await ReadBasicHeader.Perform(context);
        context.BasicHeader.Format.Should().Be(MessageHeaderFormat.Fmt1);
        context.BasicHeader.ChunkStreamId.Should().Be(64u);
    }

    [Fact]
    public async Task TestType3_Max()
    {
        byte[] expected = { 0b11_000001, 0xFF, 0xFF }; // fmt=3, csId=65535+64
        var context = (await TestContext.WithData(expected)).Context;
        await ReadBasicHeader.Perform(context);
        context.BasicHeader.Format.Should().Be(MessageHeaderFormat.Fmt3);
        context.BasicHeader.ChunkStreamId.Should().Be(65599);
    }
}
