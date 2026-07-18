using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.InteropServices;
using Spangle.Codecs.Id3;
using Spangle.Containers.M2TS;
using Spangle.Spinner;

namespace Spangle.Tests.Containers.M2TS;

/// <summary>
/// Timed ID3 arriving in the source TS (stream_type 0x15 with an "ID3 " descriptor)
/// passes through verbatim as Data frames — SRT sources keep their metadata.
/// </summary>
public class Id3IngestTests
{
    [Fact]
    public async Task SourceId3PassesThroughAsDataFrames()
    {
        var dummy = new Pipe();
        var media = new Pipe(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));
        var context = new FakeReceiverContext(dummy.Reader, dummy.Writer) { MediaOutlet = media.Writer };

        byte[] id3 = Id3Tag.BuildTxxx("song", "Sparkle");

        var demuxer = new M2TSDemuxer();
        var adapter = new M2TSMediaFrameAdapter<FakeReceiverContext>(context);
        demuxer.ProcessPacket(WriterPatPacket(), adapter);
        demuxer.ProcessPacket(TsPacket(M2TSWriter.PidPmt, pusi: true, cc: 0, PmtPointerAndSection()), adapter);
        demuxer.ProcessPacket(TsPacket(0x0102, pusi: true, cc: 0, Pes(0xBD, pts90k: 90_000, id3)), adapter);
        demuxer.Flush(adapter);
        await media.Writer.CompleteAsync();

        var frames = await ReadAllFrames(media.Reader);
        // the ID3-only program maps no audio/video; only the data frame comes out
        frames.Should().ContainSingle();

        (MediaFrameHeader header, byte[] payload) = frames[0];
        header.Kind.Should().Be(MediaFrameKind.Data);
        header.DataCodec.Should().Be(DataCodec.Id3);
        header.Timestamp.Should().Be(90000L);
        payload.Should().Equal(id3, "the tag passes through untouched");
    }

    // =======================================================================

    private static byte[] WriterPatPacket()
    {
        var muxer = new M2TSWriter { VideoCodec = VideoCodec.H264 };
        var ts = new ArrayBufferWriter<byte>();
        muxer.WriteProgramTables(ts);
        return ts.WrittenSpan[..M2TSWriter.PacketSize].ToArray();
    }

    /// <summary>PMT: H.264 video (so the pipeline wiring has a trigger) + timed ID3.</summary>
    private static byte[] PmtPointerAndSection()
    {
        byte[] id3Descriptor =
        [
            0x26, 13, 0xFF, 0xFF, .. "ID3 "u8, 0xFF, .. "ID3 "u8, 0x00, 0x0F,
        ];
        int sectionLength = 9 + 5 + 5 + id3Descriptor.Length + 4;
        var s = new byte[1 + 3 + sectionLength];
        s[0] = 0x00; // pointer_field
        s[1] = 0x02;
        s[2] = (byte)(0xB0 | (sectionLength >> 8));
        s[3] = (byte)sectionLength;
        s[4] = 0x00; s[5] = 0x01;
        s[6] = 0xC3;
        s[7] = 0x00;
        s[8] = 0x00;
        s[9] = 0xE1; s[10] = 0x00;  // PCR PID = 0x0100
        s[11] = 0xF0; s[12] = 0x00;
        var pos = 13;
        s[pos++] = M2TSStreamType.H264;
        s[pos++] = 0xE1; s[pos++] = 0x00; // video PID 0x0100
        s[pos++] = 0xF0; s[pos++] = 0x00;
        s[pos++] = M2TSStreamType.PesMetadata;
        s[pos++] = 0xE1; s[pos++] = 0x02; // data PID 0x0102
        s[pos++] = (byte)(0xF0 | (id3Descriptor.Length >> 8));
        s[pos++] = (byte)id3Descriptor.Length;
        id3Descriptor.CopyTo(s, pos);
        return s;
    }

    private static byte[] Pes(byte streamId, ulong pts90k, byte[] payload)
    {
        var pes = new List<byte> { 0x00, 0x00, 0x01, streamId };
        int packetLength = 3 + 5 + payload.Length;
        pes.Add((byte)(packetLength >> 8));
        pes.Add((byte)packetLength);
        pes.Add(0x84);
        pes.Add(0x80); // PTS only
        pes.Add(0x05);
        pes.Add((byte)(0x21 | (byte)((pts90k >> 29) & 0x0E)));
        pes.Add((byte)(pts90k >> 22));
        pes.Add((byte)(0x01 | (pts90k >> 14)));
        pes.Add((byte)(pts90k >> 7));
        pes.Add((byte)(0x01 | (pts90k << 1)));
        pes.AddRange(payload);
        return pes.ToArray();
    }

    private static byte[] TsPacket(ushort pid, bool pusi, byte cc, ReadOnlySpan<byte> payload)
    {
        var pkt = new byte[M2TSWriter.PacketSize];
        pkt[0] = 0x47;
        pkt[1] = (byte)((pusi ? 0x40 : 0x00) | (pid >> 8));
        pkt[2] = unchecked((byte)pid);
        pkt[3] = (byte)(0x10 | (cc & 0x0F));
        payload.CopyTo(pkt.AsSpan(4));
        pkt.AsSpan(4 + payload.Length).Fill(0xFF);
        return pkt;
    }

    private static async Task<List<(MediaFrameHeader Header, byte[] Payload)>> ReadAllFrames(PipeReader reader)
    {
        ReadResult result = await reader.ReadAsync();
        while (!result.IsCompleted)
        {
            reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
            result = await reader.ReadAsync();
        }

        byte[] data = result.Buffer.ToArray();
        reader.AdvanceTo(result.Buffer.End);

        var frames = new List<(MediaFrameHeader, byte[])>();
        var pos = 0;
        while (pos < data.Length)
        {
            var header = MemoryMarshal.Read<MediaFrameHeader>(data.AsSpan(pos, MediaFrameHeader.Size));
            pos += MediaFrameHeader.Size;
            frames.Add((header, data.AsSpan(pos, header.Length).ToArray()));
            pos += header.Length;
        }
        return frames;
    }

    private sealed class FakeReceiverContext(PipeReader reader, PipeWriter writer)
        : ReceiverContextBase<FakeReceiverContext>(reader, writer, CancellationToken.None)
    {
        public override string Id => "test";
        public override EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 0);
        public override bool IsCompleted => false;
        public override ValueTask BeginReceiveAsync(CancellationTokenSource readTimeoutSource) =>
            ValueTask.CompletedTask;
    }
}
