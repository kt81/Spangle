using System.Buffers;
using Spangle.Interop;
using Spangle.Tests.Transport.Rtmp.ReadState;
using Spangle.Transport.Rtmp;
using Spangle.Transport.Rtmp.ProtocolControlMessage;

namespace Spangle.Tests.Transport.Rtmp;

public class RtmpWriterTests
{
    /// <summary>
    /// Timestamps ≥ 0xFFFFFF move to the 4-byte extended timestamp field. The writer
    /// once advanced by the whole GetSpan (not 4 bytes), splicing kilobytes of
    /// uninitialized memory into the send stream.
    /// </summary>
    [Fact]
    public async Task ExtendedTimestampIsExactlyFourBytes()
    {
        var tc = new TestContext();
        var payload = BigEndianUInt32.FromHost(0xAABBCCDD);
        const uint timestamp = 0x01234567;

        int written = RtmpWriter.Write(tc.Context, timestamp, MessageType.UserControl,
            chunkStreamId: 2, streamId: 0, ref payload);
        await tc.Context.RemoteWriter.FlushAsync();
        await tc.Context.RemoteWriter.CompleteAsync();

        var result = await tc.SendPipe.Reader.ReadAsync();
        byte[] bytes = result.Buffer.ToArray();

        // basic header (1) + Fmt0 message header (11) + extended timestamp (4) + payload (4)
        bytes.Length.Should().Be(20, "no uninitialized bytes may leak into the stream");
        written.Should().Be(20);

        bytes[0].Should().Be(0x02, "Fmt0, chunk stream id 2");
        bytes[1..4].Should().Equal([0xFF, 0xFF, 0xFF], "the 24-bit field holds the escape value");
        bytes[12..16].Should().Equal([0x01, 0x23, 0x45, 0x67], "the extended timestamp carries the value");
        bytes[16..20].Should().Equal([0xAA, 0xBB, 0xCC, 0xDD]);
    }
}
