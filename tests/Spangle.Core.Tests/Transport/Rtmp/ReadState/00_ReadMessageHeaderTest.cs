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
        var state = context.GetChunkStreamState(3);

        await ReadChunkHeader.ReadMessageHeader(context, state, MessageHeaderFormat.Fmt0);
        state.Timestamp.Should().Be(0u);
        state.MessageLength.Should().Be(msgLen);
        state.TypeId.Should().Be(MessageType.CommandAmf0);
        state.MessageStreamId.Should().Be(0u);
    }

    [Fact]
    public async Task TestFmt1()
    {
        const uint msgLen = 47u;
        byte[] expected =
        {
            0x00, 0x00, 0x00,         // Timestamp Delta
            0x00, 0x00, (byte)msgLen, // Message length
            0x14,                     // Message Type ID
        };
        var context = (await TestContext.WithData(expected)).Context;
        var state = context.GetChunkStreamState(3);

        await ReadChunkHeader.ReadMessageHeader(context, state, MessageHeaderFormat.Fmt1);
        state.Timestamp.Should().Be(0u);
        state.MessageLength.Should().Be(msgLen);
        state.TypeId.Should().Be(MessageType.CommandAmf0);
        state.MessageStreamId.Should().Be(0u);
    }

    [Fact]
    public async Task TestStepFmt0ToFmt3()
    {
        const byte msgType = 0x08; // audio
        const uint streamId = 1;
        var testCtx = new TestContext();
        var context = testCtx.Context;
        var receivePipe = testCtx.ReceivePipe;
        var ct = testCtx.CTokenSrc.Token;
        var state = context.GetChunkStreamState(4);

        // Fmt 0 --------------
        uint msgLen = 47;
        byte[] fmt0 =
        {
            0x00, 0x00, 0x00,                // Timestamp
            0x00, 0x00, (byte)msgLen,        // Message length
            msgType,                         // Message Type ID
            (byte)streamId, 0x00, 0x00, 0x00 // Stream ID (Little-Endian)
        };
        receivePipe.Writer.Write(fmt0);
        await receivePipe.Writer.FlushAsync(ct);

        await ReadChunkHeader.ReadMessageHeader(context, state, MessageHeaderFormat.Fmt0);
        state.Timestamp.Should().Be(0u);
        state.MessageLength.Should().Be(msgLen);
        state.TypeId.Should().Be(MessageType.Audio);
        state.MessageStreamId.Should().Be(streamId);

        // Fmt 1 --------------
        msgLen = 8;
        byte[] fmt1 =
        {
            0x00, 0x00, 0x00,         // Timestamp Delta
            0x00, 0x00, (byte)msgLen, // Message length
            msgType,                  // Message Type ID
        };
        receivePipe.Writer.Write(fmt1);
        await receivePipe.Writer.FlushAsync(ct);

        await ReadChunkHeader.ReadMessageHeader(context, state, MessageHeaderFormat.Fmt1);
        state.Timestamp.Should().Be(0u);
        state.MessageLength.Should().Be(msgLen);
        state.TypeId.Should().Be(MessageType.Audio);
        state.MessageStreamId.Should().Be(streamId);

        // Fmt 2 --------------
        // msgLen = 8;
        uint tsDelta = 21;
        byte[] fmt2 =
        {
            0x00, 0x00, (byte)tsDelta, // Timestamp Delta
        };
        receivePipe.Writer.Write(fmt2);
        await receivePipe.Writer.FlushAsync(ct);

        await ReadChunkHeader.ReadMessageHeader(context, state, MessageHeaderFormat.Fmt2);
        state.Timestamp.Should().Be(tsDelta);
        state.MessageLength.Should().Be(msgLen);
        state.TypeId.Should().Be(MessageType.Audio);
        state.MessageStreamId.Should().Be(streamId);

        // Fmt 3 starting a new message: re-applies the last delta --------------
        await ReadChunkHeader.ReadMessageHeader(context, state, MessageHeaderFormat.Fmt3);
        state.Timestamp.Should().Be(tsDelta * 2);
        state.MessageLength.Should().Be(msgLen);
        state.TypeId.Should().Be(MessageType.Audio);
        state.MessageStreamId.Should().Be(streamId);
    }

    [Fact]
    public async Task TestFmt3Continuation()
    {
        const byte msgType = 0x08;
        var testCtx = new TestContext();
        var context = testCtx.Context;
        var receivePipe = testCtx.ReceivePipe;
        var ct = testCtx.CTokenSrc.Token;
        var state = context.GetChunkStreamState(4);

        byte[] fmt0 =
        {
            0x00, 0x00, 0xFF,       // Timestamp
            0x00, 0x00, 0x2F,       // Message length
            msgType,                // Message Type ID
            0x01, 0x00, 0x00, 0x00  // Stream ID
        };
        receivePipe.Writer.Write(fmt0);
        await receivePipe.Writer.FlushAsync(ct);

        await ReadChunkHeader.ReadMessageHeader(context, state, MessageHeaderFormat.Fmt0);
        state.Timestamp.Should().Be(0xFFu);

        // A continuation chunk of the current message must not change the timestamp
        state.Remaining = 10;
        await ReadChunkHeader.ReadMessageHeader(context, state, MessageHeaderFormat.Fmt3);
        state.Timestamp.Should().Be(0xFFu, "a continuation chunk carries no timestamp");
    }

    [Fact]
    public async Task TestDirectFmt0ToFmt3NewMessage()
    {
        const byte msgType = 0x08;
        var testCtx = new TestContext();
        var context = testCtx.Context;
        var receivePipe = testCtx.ReceivePipe;
        var ct = testCtx.CTokenSrc.Token;
        var state = context.GetChunkStreamState(4);

        byte[] fmt0 =
        {
            0x00, 0x00, 0xFF,       // Timestamp
            0x00, 0x00, 0x2F,       // Message length
            msgType,                // Message Type ID
            0x01, 0x00, 0x00, 0x00  // Stream ID
        };
        receivePipe.Writer.Write(fmt0);
        await receivePipe.Writer.FlushAsync(ct);

        await ReadChunkHeader.ReadMessageHeader(context, state, MessageHeaderFormat.Fmt0);
        state.Timestamp.Should().Be(0xFFu);

        // A Fmt3 chunk that starts a new message right after a Fmt0 chunk
        // uses the Fmt0 timestamp as its delta (RTMP spec 5.3.1.2.4)
        state.Remaining = 0;
        await ReadChunkHeader.ReadMessageHeader(context, state, MessageHeaderFormat.Fmt3);
        state.Timestamp.Should().Be(0x1FEu);
    }
    // TODO test extended timestamp
}
