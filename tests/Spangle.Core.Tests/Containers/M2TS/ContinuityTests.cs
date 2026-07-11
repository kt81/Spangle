using System.Buffers;
using Spangle.Containers.M2TS;

namespace Spangle.Tests.Containers.M2TS;

/// <summary>
/// "A lost packet drops the frame under assembly, not the stream" — including when
/// the loss hits the tail of a frame and the next thing seen is a PUSI packet:
/// the truncated access unit must not be emitted.
/// </summary>
public class ContinuityTests
{
    [Fact]
    public void TruncatedFrameIsDroppedWhenLossPrecedesThePusi()
    {
        byte[] ts = MuxTwoFrames(out int firstPesPacketCount);
        firstPesPacketCount.Should().BeGreaterThan(2, "the first PES must span 3+ packets for this test");

        // Drop a middle packet of the first PES (its tail never completes)
        var sink = new RecordingSink();
        var demuxer = new M2TSDemuxer();
        int dropIndex = 2 + firstPesPacketCount - 1; // the last packet of PES 1 (packets 0/1 are PAT/PMT)
        for (var i = 0; i * M2TSWriter.PacketSize < ts.Length; i++)
        {
            if (i == dropIndex)
            {
                continue;
            }
            demuxer.ProcessPacket(ts.AsSpan(i * M2TSWriter.PacketSize, M2TSWriter.PacketSize), sink);
        }
        demuxer.Flush(sink);

        sink.Payloads.Should().HaveCount(1, "the truncated first frame must be dropped");
        sink.Payloads[0].Length.Should().Be(SecondAuLength);
    }

    [Fact]
    public void IntactStreamEmitsBothFrames()
    {
        byte[] ts = MuxTwoFrames(out _);

        var sink = new RecordingSink();
        var demuxer = new M2TSDemuxer();
        for (var i = 0; i * M2TSWriter.PacketSize < ts.Length; i++)
        {
            demuxer.ProcessPacket(ts.AsSpan(i * M2TSWriter.PacketSize, M2TSWriter.PacketSize), sink);
        }
        demuxer.Flush(sink);

        sink.Payloads.Should().HaveCount(2);
    }

    // =======================================================================

    private const int SecondAuLength = 4 + 3; // start code + tiny NALU

    private static byte[] MuxTwoFrames(out int firstPesPacketCount)
    {
        // First AU big enough to span several TS packets
        var big = new byte[600];
        big[3] = 0x01;
        big[4] = 0x65;
        var muxer = new M2TSWriter { VideoCodec = VideoCodec.H264 };
        var buff = new ArrayBufferWriter<byte>();
        muxer.WriteProgramTables(buff);
        int before = buff.WrittenCount;
        muxer.WritePes(buff, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo, big,
            pts: 90_000, dts: null, randomAccess: true, withPcr: true);
        firstPesPacketCount = (buff.WrittenCount - before) / M2TSWriter.PacketSize;
        muxer.WritePes(buff, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo,
            [0x00, 0x00, 0x00, 0x01, 0x41, 0x55, 0x66],
            pts: 93_600, dts: null, randomAccess: false, withPcr: false);
        return buff.WrittenSpan.ToArray();
    }

    private sealed class RecordingSink : IM2TSDemuxerSink
    {
        public List<byte[]> Payloads { get; } = [];

        public void OnProgramMapped(byte videoStreamType, ushort videoPid, byte audioStreamType, ushort audioPid,
            byte opusChannels)
        {
        }

        public void OnPes(byte streamType, ulong? pts90k, ulong? dts90k, ReadOnlySpan<byte> es) =>
            Payloads.Add(es.ToArray());
    }
}
