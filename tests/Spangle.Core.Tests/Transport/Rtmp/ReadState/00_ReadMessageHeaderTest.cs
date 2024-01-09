using System.Buffers;
using Spangle.Transport.Rtmp.Chunk;
using Spangle.Transport.Rtmp.ProtocolControlMessage;
using Spangle.Transport.Rtmp.ReadState;

namespace Spangle.Tests.Transport.Rtmp.ReadState;

/// <summary>
/// Test for <see cref="ReadChunkHeader"/> and <see cref="MessageHeader"/>
/// See also <seealso cref="MessageType"/>
/// </summary>
public class ReadChunkHeaderTest
{
    [Fact]
    public async Task TestFmt0()
    {
        const uint msgLen = 189u;
        byte[] expected =
        {
            0x00, 0x00, 0x00,         // Timestamp
            0x00, 0x00, (byte)msgLen, // Message length
            0x14,                     // Message Type ID
            0x00, 0x00, 0x00, 0x00    // Stream ID (Little-Endian)
        };
        var context = (await TestContext.WithData(expected)).Context;

        // Assume it has already read the BasicHeader
        context.BasicHeader.Format = MessageHeaderFormat.Fmt0;
        context.BasicHeader.ChunkStreamId = 3;

        await ReadChunkHeader.ReadMessageHeader(context);
        context.MessageHeader.TimestampOrDelta.HostValue.Should().Be(0u);
        context.MessageHeader.Length.HostValue.Should().Be(msgLen);
        context.MessageHeader.TypeId.Should().Be(MessageType.CommandAmf0);
        context.MessageHeader.StreamId.Should().Be(0u);
    }

    [Fact]
    public async Task TestFmt1()
    {
        const uint msgLen = 47u;
        byte[] expected =
        {
            0x00, 0x00, 0x00,         // Timestamp
            0x00, 0x00, (byte)msgLen, // Message length
            0x14,                     // Message Type ID
        };
        var context = (await TestContext.WithData(expected)).Context;

        context.BasicHeader.Format = MessageHeaderFormat.Fmt1;
        context.BasicHeader.ChunkStreamId = 3;

        await ReadChunkHeader.ReadMessageHeader(context);
        context.MessageHeader.TimestampOrDelta.HostValue.Should().Be(0u);
        context.MessageHeader.Length.HostValue.Should().Be(msgLen);
        context.MessageHeader.TypeId.Should().Be(MessageType.CommandAmf0);
        context.MessageHeader.StreamId.Should().Be(0u);
    }

    [Fact]
    public async Task TestStepFmt0ToFmt3()
    {
        const byte msgType = 0x08; // audio
        const uint streamId = 1;
        var testCtx = new TestContext();
        var context = testCtx.Context;
        var receivePipe = testCtx.ReceivePipe;
        // var sendPipe = testCtx.SendPipe;
        var ct = testCtx.CTokenSrc.Token;

        // Fmt 0 --------------
        uint msgLen = 47;
        byte[] fmt0 =
        {
            0x00, 0x00, 0x00,                // Timestamp
            0x00, 0x00, (byte)msgLen,        // Message length
            msgType,                         // Message Type ID
            (byte)streamId, 0x00, 0x00, 0x00 // Stream ID (Little-Endian)
        };
        context.BasicHeader.Format = MessageHeaderFormat.Fmt0;
        context.BasicHeader.ChunkStreamId = 4;

        receivePipe.Writer.Write(fmt0);
        await receivePipe.Writer.FlushAsync(ct);

        await ReadChunkHeader.ReadMessageHeader(context);
        context.MessageHeader.TimestampOrDelta.HostValue.Should().Be(0u);
        context.MessageHeader.Length.HostValue.Should().Be(msgLen);
        context.MessageHeader.TypeId.Should().Be(MessageType.Audio);
        context.MessageHeader.StreamId.Should().Be(streamId);

        // Fmt 1 --------------
        msgLen = 8;
        uint tsDelta = 0;
        byte[] fmt1 =
        {
            0x00, 0x00, 0x00,         // Timestamp Delta
            0x00, 0x00, (byte)msgLen, // Message length
            msgType,                  // Message Type ID
        };
        context.BasicHeader.Format = MessageHeaderFormat.Fmt1;
        context.BasicHeader.ChunkStreamId = 4;
        context.PreviousFormat = MessageHeaderFormat.Fmt0;

        receivePipe.Writer.Write(fmt1);
        await receivePipe.Writer.FlushAsync(ct);

        await ReadChunkHeader.ReadMessageHeader(context);
        context.MessageHeader.TimestampOrDelta.HostValue.Should().Be(tsDelta);
        context.MessageHeader.Length.HostValue.Should().Be(msgLen);
        context.MessageHeader.TypeId.Should().Be(MessageType.Audio);
        context.MessageHeader.StreamId.Should().Be(streamId);

        // Fmt 2 --------------
        // msgLen = 8;
        tsDelta = 21;
        byte[] fmt2 =
        {
            0x00, 0x00, (byte)tsDelta, // Timestamp Delta
        };
        context.BasicHeader.Format = MessageHeaderFormat.Fmt2;
        context.BasicHeader.ChunkStreamId = 4;
        context.PreviousFormat = MessageHeaderFormat.Fmt1;

        receivePipe.Writer.Write(fmt2);
        await receivePipe.Writer.FlushAsync(ct);

        await ReadChunkHeader.ReadMessageHeader(context);
        context.MessageHeader.TimestampOrDelta.HostValue.Should().Be(tsDelta);
        context.MessageHeader.Length.HostValue.Should().Be(msgLen);
        context.MessageHeader.TypeId.Should().Be(MessageType.Audio);
        context.MessageHeader.StreamId.Should().Be(streamId);
        context.Timestamp.Should().Be(tsDelta);

        // Fmt 3 --------------
        // msgLen = 8;
        // tsDelta = 21;
        byte[] fmt3 = Array.Empty<byte>();
        context.BasicHeader.Format = MessageHeaderFormat.Fmt3;
        context.BasicHeader.ChunkStreamId = 4;
        context.PreviousFormat = MessageHeaderFormat.Fmt2;

        // does not affect anything
        receivePipe.Writer.Write(fmt3);
        await receivePipe.Writer.FlushAsync(ct);

        await ReadChunkHeader.ReadMessageHeader(context);
        context.MessageHeader.TimestampOrDelta.HostValue.Should().Be(tsDelta);
        context.MessageHeader.Length.HostValue.Should().Be(msgLen);
        context.MessageHeader.TypeId.Should().Be(MessageType.Audio);
        context.MessageHeader.StreamId.Should().Be(streamId);
        context.Timestamp.Should().Be(tsDelta * 2);
    }

    [Fact]
    public async Task TestDirectFmt0ToFmt3()
    {
        const byte msgType = 0x08; // audio
        const uint streamId = 1;
        var testCtx = new TestContext();
        var context = testCtx.Context;
        var receivePipe = testCtx.ReceivePipe;
        // var sendPipe = testCtx.SendPipe;
        var ct = testCtx.CTokenSrc.Token;

        // Fmt 0 --------------
        uint msgLen = 47;
        byte[] fmt0 =
        {
            0x00, 0x00, 0xFF,                // Timestamp
            0x00, 0x00, (byte)msgLen,        // Message length
            msgType,                         // Message Type ID
            (byte)streamId, 0x00, 0x00, 0x00 // Stream ID (Little-Endian)
        };
        context.BasicHeader.Format = MessageHeaderFormat.Fmt0;
        context.BasicHeader.ChunkStreamId = 4;

        receivePipe.Writer.Write(fmt0);
        await receivePipe.Writer.FlushAsync(ct);

        await ReadChunkHeader.ReadMessageHeader(context);
        context.MessageHeader.TimestampOrDelta.HostValue.Should().Be(0xFFu);
        context.MessageHeader.Length.HostValue.Should().Be(msgLen);
        context.MessageHeader.TypeId.Should().Be(MessageType.Audio);
        context.MessageHeader.StreamId.Should().Be(streamId);
        context.Timestamp.Should().Be(0xFFu);

        // Fmt 3 --------------
        // msgLen = 47;
        byte[] fmt3 = Array.Empty<byte>();
        context.BasicHeader.Format = MessageHeaderFormat.Fmt3;
        context.BasicHeader.ChunkStreamId = 4;
        context.PreviousFormat = MessageHeaderFormat.Fmt0;

        // does not affect anything
        receivePipe.Writer.Write(fmt3);
        await receivePipe.Writer.FlushAsync(ct);

        await ReadChunkHeader.ReadMessageHeader(context);
        context.MessageHeader.TimestampOrDelta.HostValue.Should().Be(0u);
        context.MessageHeader.Length.HostValue.Should().Be(msgLen);
        context.MessageHeader.TypeId.Should().Be(MessageType.Audio);
        context.MessageHeader.StreamId.Should().Be(streamId);
        context.Timestamp.Should().Be(0xFFu, "Direct Fmt0 to Fmt3 transition must not change calculated timestamp.");
    }
    // TODO test extended timestamp
}
