using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.InteropServices;
using Spangle.Containers.M2TS;
using Spangle.Spinner;

namespace Spangle.Tests.Containers.M2TS;

/// <summary>
/// Audio-only TS programs: the PMT maps only an AAC stream (PCR on the audio PID),
/// and the adapter must declare the source audio-only so the pipeline wires without
/// a video codec ever appearing.
/// </summary>
public class AudioOnlyIngestTests
{
    private static readonly byte[] s_aac = [0xDE, 0xAD, 0xBE, 0xEF];

    [Fact]
    public void AudioOnlyPmtHasNoVideoAndMovesPcrToAudio()
    {
        var muxer = new M2TSWriter { HasVideo = false, HasAudio = true };
        var ts = new ArrayBufferWriter<byte>();
        muxer.WriteProgramTables(ts);

        // packet 2 is the PMT; the section starts after header(4) + pointer_field(1)
        ReadOnlySpan<byte> pmt = ts.WrittenSpan.Slice(M2TSWriter.PacketSize + 5);
        int sectionLength = ((pmt[1] & 0x0F) << 8) | pmt[2];
        sectionLength.Should().Be(9 + 5 + 4, "exactly one ES entry (audio)");

        ushort pcrPid = (ushort)(((pmt[8] & 0x1F) << 8) | pmt[9]);
        pcrPid.Should().Be(M2TSWriter.PidAudio, "no video track exists to carry the PCR");

        pmt[12].Should().Be(0x0F, "the single ES entry is ADTS AAC");
    }

    [Fact]
    public async Task RoundTripAudioOnlyThroughM2TSWriter()
    {
        var muxer = new M2TSWriter { HasVideo = false, HasAudio = true };
        var ts = new ArrayBufferWriter<byte>();
        muxer.WriteProgramTables(ts);
        muxer.WritePes(ts, M2TSWriter.PidAudio, M2TSWriter.StreamIdAudio,
            Adts(profile: 1, freqIndex: 3 /* 48kHz */, channels: 2, s_aac), pts: 90_000, dts: null,
            randomAccess: true, withPcr: true);

        var dummy = new Pipe();
        var media = new Pipe(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));
        var context = new FakeReceiverContext(dummy.Reader, dummy.Writer) { MediaOutlet = media.Writer };

        var demuxer = new M2TSDemuxer();
        var adapter = new M2TSMediaFrameAdapter<FakeReceiverContext>(context);
        ReadOnlyMemory<byte> written = ts.WrittenMemory;
        for (var i = 0; i < written.Length; i += M2TSWriter.PacketSize)
        {
            demuxer.ProcessPacket(written.Span.Slice(i, M2TSWriter.PacketSize), adapter);
        }
        demuxer.Flush(adapter);
        await media.Writer.CompleteAsync();

        context.IsAudioOnly.Should().BeTrue("the PMT maps no video stream");
        context.AudioCodec.Should().Be(AudioCodec.AAC);
        context.VideoCodec.Should().BeNull();

        var frames = await ReadAllFrames(media.Reader);
        frames.Should().HaveCount(2);

        (MediaFrameHeader header, byte[] payload) = frames[0];
        header.Kind.Should().Be(MediaFrameKind.Audio);
        header.IsConfig.Should().BeTrue();
        payload.Should().Equal([0x11, 0x90], "AAC-LC 48kHz stereo AudioSpecificConfig");

        (header, payload) = frames[1];
        header.Kind.Should().Be(MediaFrameKind.Audio);
        header.Timestamp.Should().Be(1000u);
        payload.Should().Equal(s_aac, "the ADTS header is stripped");
    }

    // =======================================================================

    private static byte[] Adts(byte profile, byte freqIndex, byte channels, byte[] payload)
    {
        int frameLength = 7 + payload.Length;
        var b = new byte[frameLength];
        b[0] = 0xFF;
        b[1] = 0xF1;
        b[2] = (byte)((profile << 6) | (freqIndex << 2) | ((channels >> 2) & 0x01));
        b[3] = (byte)(((channels & 0x03) << 6) | ((frameLength >> 11) & 0x03));
        b[4] = (byte)((frameLength >> 3) & 0xFF);
        b[5] = (byte)(((frameLength & 0x07) << 5) | 0x1F);
        b[6] = 0xFC;
        payload.CopyTo(b.AsSpan(7));
        return b;
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
