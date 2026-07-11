using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using Spangle.Codecs.Opus;
using Spangle.Spinner;
using Spangle.Transport.Rtmp.ReadState;

namespace Spangle.Tests.Transport.Rtmp.ReadState;

/// <summary>
/// enhanced-RTMP v2 audio envelope: control byte (SoundFormat 9 + AudioPacketType)
/// followed by a FourCC. Opus sequence start carries the OpusHead verbatim.
/// </summary>
public class EnhancedAudioTest
{
    private static readonly byte[] s_fourCc = [(byte)'O', (byte)'p', (byte)'u', (byte)'s'];
    private static readonly byte[] s_packet = [0xF8, 0x11, 0x22, 0x33];

    [Fact]
    public async Task OpusEnvelopeProducesConfigAndFrames()
    {
        var tc = new TestContext();
        var context = tc.Context;
        var media = new Pipe(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));
        context.MediaOutlet = media.Writer;

        byte[] opusHead = OpusPacket.BuildOpusHead(2);
        await Audio.HandleAsync(context, new ReadOnlySequence<byte>([0x90, .. s_fourCc, .. opusHead]));
        await Audio.HandleAsync(context, new ReadOnlySequence<byte>([0x94, .. s_fourCc, 0x01, 0x02, 0, 0, 0, 3]));
        await Audio.HandleAsync(context, new ReadOnlySequence<byte>([0x91, .. s_fourCc, .. s_packet]));
        await media.Writer.CompleteAsync();

        context.AudioCodec.Should().Be(AudioCodec.Opus);

        var frames = await ReadAllFrames(media.Reader);
        frames.Should().HaveCount(2, "the multichannel-config packet is a hint, not media");

        (MediaFrameHeader header, byte[] payload) = frames[0];
        header.Kind.Should().Be(MediaFrameKind.Audio);
        header.IsConfig.Should().BeTrue();
        header.AudioCodec.Should().Be(AudioCodec.Opus);
        payload.Should().Equal(opusHead, "the OpusHead passes through verbatim");

        (header, payload) = frames[1];
        header.IsConfig.Should().BeFalse();
        header.AudioCodec.Should().Be(AudioCodec.Opus);
        payload.Should().Equal(s_packet);
    }

    [Fact]
    public async Task OpusConfigBeforeWiringIsStashedWithItsCodec()
    {
        var tc = new TestContext();
        var context = tc.Context;
        context.MediaOutlet.Should().BeNull();

        byte[] opusHead = OpusPacket.BuildOpusHead(1);
        await Audio.HandleAsync(context, new ReadOnlySequence<byte>([0x90, .. s_fourCc, .. opusHead]));
        context.PendingAudioConfig.Should().Equal(opusHead);
        context.PendingAudioConfigCodec.Should().Be(AudioCodec.Opus);

        var media = new Pipe(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));
        context.MediaOutlet = media.Writer;
        await Audio.HandleAsync(context, new ReadOnlySequence<byte>([0x91, .. s_fourCc, .. s_packet]));
        await media.Writer.CompleteAsync();

        var frames = await ReadAllFrames(media.Reader);
        frames.Should().HaveCount(2);
        frames[0].Header.IsConfig.Should().BeTrue();
        frames[0].Header.AudioCodec.Should().Be(AudioCodec.Opus);
        frames[0].Payload.Should().Equal(opusHead);
    }

    // =======================================================================

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
}
