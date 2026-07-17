using System.Buffers;
using Spangle.Transport.Rtmp.ProtocolControlMessage;
using Spangle.Transport.Rtmp.ReadState;

namespace Spangle.Tests.Transport.Rtmp.ReadState;

/// <summary>
/// Tests for <see cref="ReadChunkHeader.TryReadChunk"/>: chunk parsing, message assembly,
/// timestamp semantics and interleaving. See also <seealso cref="MessageType"/>.
/// </summary>
public class ReadChunkHeaderTest
{
    private static readonly byte[] s_fmt0AudioHeader =
    {
        0x04,                            // Fmt0, csid 4
        0x00, 0x00, 0x2A,                // Timestamp = 42
        0x00, 0x00, 0x03,                // Message length = 3
        0x08,                            // Type: audio
        0x01, 0x00, 0x00, 0x00,          // Stream ID = 1 (LE)
    };

    [Fact]
    public void SingleChunkMessageCompletes()
    {
        var context = new TestContext().Context;
        var buffer = new ReadOnlySequence<byte>([.. s_fmt0AudioHeader, 0xAA, 0xBB, 0xCC]);

        ReadChunkHeader.TryReadChunk(context, ref buffer, out var completed).Should().BeTrue();

        completed.Should().NotBeNull();
        completed!.Timestamp.Should().Be(42u);
        completed.MessageLength.Should().Be(3u);
        completed.TypeId.Should().Be(MessageType.Audio);
        completed.MessageStreamId.Should().Be(1u);
        completed.Assembly.WrittenSpan.ToArray().Should().Equal(0xAA, 0xBB, 0xCC);
        buffer.Length.Should().Be(0);
    }

    [Fact]
    public void IncompleteChunkConsumesNothingAndMutatesNothing()
    {
        var context = new TestContext().Context;
        var state = context.GetChunkStreamState(4);

        // Header is complete but one payload byte is missing
        var buffer = new ReadOnlySequence<byte>([.. s_fmt0AudioHeader, 0xAA, 0xBB]);

        ReadChunkHeader.TryReadChunk(context, ref buffer, out var completed).Should().BeFalse();
        completed.Should().BeNull();
        buffer.Length.Should().Be(s_fmt0AudioHeader.Length + 2, "nothing must be consumed");
        state.Timestamp.Should().Be(0u, "state must not be mutated for a partial chunk");
        state.Remaining.Should().Be(0);
    }

    [Fact]
    public void Fmt1AccumulatesDelta()
    {
        var context = new TestContext().Context;
        var buffer = new ReadOnlySequence<byte>([
            .. s_fmt0AudioHeader, 0xAA, 0xBB, 0xCC,
            0x44,             // Fmt1, csid 4
            0x00, 0x00, 0x15, // Timestamp delta = 21
            0x00, 0x00, 0x01, // Message length = 1
            0x08,             // Type: audio
            0xDD,             // payload
        ]);

        ReadChunkHeader.TryReadChunk(context, ref buffer, out _).Should().BeTrue();
        ReadChunkHeader.TryReadChunk(context, ref buffer, out var completed).Should().BeTrue();

        completed.Should().NotBeNull();
        completed!.Timestamp.Should().Be(42u + 21u);
        completed.MessageStreamId.Should().Be(1u, "Fmt1 keeps the previous stream id");
        completed.Assembly.WrittenSpan.ToArray().Should().Equal(0xDD);
    }

    [Fact]
    public void Fmt3ContinuationCarriesNoTimestamp()
    {
        var context = new TestContext().Context;
        context.ChunkSize = 2; // force splitting: 3-byte message = 2 + 1

        var buffer = new ReadOnlySequence<byte>([
            .. s_fmt0AudioHeader, 0xAA, 0xBB, // first chunk (2 of 3 bytes)
            0xC4,                             // Fmt3 continuation, csid 4
            0xCC,                             // remaining byte
        ]);

        ReadChunkHeader.TryReadChunk(context, ref buffer, out var completed).Should().BeTrue();
        completed.Should().BeNull("the message is not complete yet");

        ReadChunkHeader.TryReadChunk(context, ref buffer, out completed).Should().BeTrue();
        completed.Should().NotBeNull();
        completed!.Timestamp.Should().Be(42u, "a continuation chunk must not change the timestamp");
        completed.Assembly.WrittenSpan.ToArray().Should().Equal(0xAA, 0xBB, 0xCC);
    }

    [Fact]
    public void Fmt3NewMessageReappliesDelta()
    {
        var context = new TestContext().Context;
        var buffer = new ReadOnlySequence<byte>([
            .. s_fmt0AudioHeader, 0xAA, 0xBB, 0xCC,
            0xC4,             // Fmt3 starting a NEW message (previous one completed)
            0xDD, 0xEE, 0xFF, // same length (3) as the previous message
        ]);

        ReadChunkHeader.TryReadChunk(context, ref buffer, out _).Should().BeTrue();
        ReadChunkHeader.TryReadChunk(context, ref buffer, out var completed).Should().BeTrue();

        completed.Should().NotBeNull();
        // Per spec, a Fmt3 chunk right after a Fmt0 chunk uses the Fmt0 timestamp as its delta
        completed!.Timestamp.Should().Be(84u);
        completed.Assembly.WrittenSpan.ToArray().Should().Equal(0xDD, 0xEE, 0xFF);
    }

    private static readonly byte[] s_fmt0ExtendedHeader =
    {
        0x04,                   // Fmt0, csid 4
        0xFF, 0xFF, 0xFF,       // Timestamp field = 0xFFFFFF: extended timestamp follows
        0x00, 0x00, 0x08,       // Message length = 8
        0x08,                   // Type: audio
        0x01, 0x00, 0x00, 0x00, // Stream ID = 1 (LE)
        0x01, 0x00, 0x00, 0x00, // Extended timestamp = 0x01000000 (~4.66 h in)
    };

    [Fact]
    public void Fmt3WithoutResentExtendedTimestampParses()
    {
        // The librtmp family does not resend the extended timestamp on Fmt3 chunks. Once the
        // stream clock passes 0xFFFFFF ms (4.66 hours in), assuming the resend misframed such
        // a client by four bytes and killed the session.
        var context = new TestContext().Context;
        context.ChunkSize = 4;

        var buffer = new ReadOnlySequence<byte>([
            .. s_fmt0ExtendedHeader, 0xA0, 0xA1, 0xA2, 0xA3, // first chunk (4 of 8 bytes)
            0xC4,                                            // Fmt3 continuation, no resend
            0xB0, 0xB1, 0xB2, 0xB3,                          // the payload continues directly
        ]);

        ReadChunkHeader.TryReadChunk(context, ref buffer, out var completed).Should().BeTrue();
        completed.Should().BeNull();
        ReadChunkHeader.TryReadChunk(context, ref buffer, out completed).Should().BeTrue();

        completed.Should().NotBeNull();
        completed!.Timestamp.Should().Be(0x0100_0000u);
        completed.Assembly.WrittenSpan.ToArray().Should()
            .Equal(0xA0, 0xA1, 0xA2, 0xA3, 0xB0, 0xB1, 0xB2, 0xB3);
        buffer.Length.Should().Be(0);
    }

    [Fact]
    public void Fmt3WithResentExtendedTimestampStillParses()
    {
        // The spec-shaped client resends the field on every Fmt3 chunk; the tolerant reader
        // must recognize the resend (it is byte-for-byte the last header's value) and consume
        // it, exactly as before.
        var context = new TestContext().Context;
        context.ChunkSize = 4;

        var buffer = new ReadOnlySequence<byte>([
            .. s_fmt0ExtendedHeader, 0xA0, 0xA1, 0xA2, 0xA3,
            0xC4,                   // Fmt3 continuation
            0x01, 0x00, 0x00, 0x00, // the resent extended timestamp
            0xB0, 0xB1, 0xB2, 0xB3,
        ]);

        ReadChunkHeader.TryReadChunk(context, ref buffer, out var completed).Should().BeTrue();
        completed.Should().BeNull();
        ReadChunkHeader.TryReadChunk(context, ref buffer, out completed).Should().BeTrue();

        completed.Should().NotBeNull();
        completed!.Timestamp.Should().Be(0x0100_0000u);
        completed.Assembly.WrittenSpan.ToArray().Should()
            .Equal(0xA0, 0xA1, 0xA2, 0xA3, 0xB0, 0xB1, 0xB2, 0xB3);
        buffer.Length.Should().Be(0);
    }

    [Fact]
    public void InterleavedChunkStreamsAssembleIndependently()
    {
        var context = new TestContext().Context;
        context.ChunkSize = 2;

        var buffer = new ReadOnlySequence<byte>([
            // csid 4: audio message of 3 bytes, first chunk
            .. s_fmt0AudioHeader, 0xA0, 0xA1,
            // csid 6: video message of 2 bytes, complete in one chunk (interleaved)
            0x06,                   // Fmt0, csid 6
            0x00, 0x00, 0x63,       // Timestamp = 99
            0x00, 0x00, 0x02,       // Message length = 2
            0x09,                   // Type: video
            0x01, 0x00, 0x00, 0x00, // Stream ID = 1
            0xB0, 0xB1,
            // csid 4: continuation
            0xC4, 0xA2,
        ]);

        ReadChunkHeader.TryReadChunk(context, ref buffer, out var completed).Should().BeTrue();
        completed.Should().BeNull();

        ReadChunkHeader.TryReadChunk(context, ref buffer, out completed).Should().BeTrue();
        completed.Should().NotBeNull();
        completed!.TypeId.Should().Be(MessageType.Video);
        completed.Timestamp.Should().Be(99u);
        completed.Assembly.WrittenSpan.ToArray().Should().Equal(0xB0, 0xB1);
        completed.Assembly.Clear();

        ReadChunkHeader.TryReadChunk(context, ref buffer, out completed).Should().BeTrue();
        completed.Should().NotBeNull();
        completed!.TypeId.Should().Be(MessageType.Audio);
        completed.Timestamp.Should().Be(42u);
        completed.Assembly.WrittenSpan.ToArray().Should().Equal(0xA0, 0xA1, 0xA2);
    }
}
