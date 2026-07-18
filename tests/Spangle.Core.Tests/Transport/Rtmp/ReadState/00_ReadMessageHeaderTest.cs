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

    // =======================================================================
    // The 64-bit timeline: RTMP's 32-bit millisecond field is unwrapped to 90 kHz ticks so a
    // long-running stream neither loses precision nor resets when the field wraps (~49.7 days).

    [Fact]
    public void TimestampTicksScaleMillisecondsTo90kHz()
    {
        var context = new TestContext().Context;

        context.SetTimestamp(0);
        context.TimestampTicks.Should().Be(0);

        context.SetTimestamp(1000);
        context.TimestampTicks.Should().Be(90_000, "1000 ms is 90,000 ticks at 90 kHz");
    }

    [Fact]
    public void TimestampTicksUnwrapAcrossThe32BitWrap()
    {
        var context = new TestContext().Context;

        // Just before the 32-bit millisecond field wraps (~49.7 days in).
        context.SetTimestamp(0xFFFF_FF00);
        long beforeWrap = context.TimestampTicks;
        beforeWrap.Should().Be(0xFFFF_FF00L * 90);

        // It wraps to a small value; the timeline must keep climbing, not reset to ~0.
        context.SetTimestamp(0x0000_0100);
        long afterWrap = context.TimestampTicks;
        afterWrap.Should().Be(((1L << 32) | 0x0000_0100) * 90);
        afterWrap.Should().BeGreaterThan(beforeWrap, "a wrap must not turn the clock backwards");
        (afterWrap - beforeWrap).Should().Be(0x0200L * 90, "the true elapsed time is 0x200 ms");
    }

    [Fact]
    public void AnInterleavedTimestampFromJustBeforeTheWrapStaysInTheOldEpoch()
    {
        var context = new TestContext().Context;

        // Video crosses the wrap first...
        context.SetTimestamp(0xFFFF_FF00);
        context.SetTimestamp(0x0000_0100); // wrapped: a new epoch

        // ...then a late audio message still carries a pre-wrap value. It belongs to the old epoch,
        // not to a point 49.7 days in the future.
        context.SetTimestamp(0xFFFF_FF80);
        context.TimestampTicks.Should().Be(0xFFFF_FF80L * 90, "the out-of-order value stays in the old epoch");
    }

    [Fact]
    public void NormalInterleaveDoesNotLookLikeAWrap()
    {
        var context = new TestContext().Context;

        // Audio at 1000 ms, then a video frame a little behind it (990 ms) — ordinary interleave,
        // a tiny dip nowhere near half the 32-bit range.
        context.SetTimestamp(1000);
        context.SetTimestamp(990);
        context.TimestampTicks.Should().Be(990L * 90, "a small backward step is interleave, not a wrap");
    }
}
