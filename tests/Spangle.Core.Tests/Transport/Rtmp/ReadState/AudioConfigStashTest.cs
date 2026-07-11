using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using Spangle.Spinner;
using Spangle.Transport.Rtmp.ReadState;

namespace Spangle.Tests.Transport.Rtmp.ReadState;

/// <summary>
/// The pipeline wires on the video codec, so an AAC sequence header can arrive while
/// the media outlet is still null (metadata-less, audio-first encoders). Dropping it
/// used to mute the whole session; it must be stashed and replayed.
/// </summary>
public class AudioConfigStashTest
{
    private static readonly byte[] s_asc = [0x11, 0x90];
    private static readonly byte[] s_aac = [0xDE, 0xAD, 0xBE, 0xEF];

    [Fact]
    public async Task SequenceHeaderBeforeWiringIsStashedAndReplayed()
    {
        var tc = new TestContext();
        var context = tc.Context;
        context.MediaOutlet.Should().BeNull("precondition: the pipeline is not wired yet");

        // AAC sequence header while unwired: must be kept, not dropped
        await Audio.Handle(context, new ReadOnlySequence<byte>([0xAF, 0x00, .. s_asc]));
        context.PendingAudioConfig.Should().Equal(s_asc);
        context.AudioCodec.Should().Be(AudioCodec.AAC, "the codec is known regardless of wiring");

        // the pipeline gets wired (normally by the video codec event)
        var media = new Pipe(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));
        context.MediaOutlet = media.Writer;

        // the next audio frame must be preceded by the replayed config
        await Audio.Handle(context, new ReadOnlySequence<byte>([0xAF, 0x01, .. s_aac]));
        await media.Writer.CompleteAsync();

        var frames = await ReadAllFrames(media.Reader);
        frames.Should().HaveCount(2);

        (MediaFrameHeader header, byte[] payload) = frames[0];
        header.Kind.Should().Be(MediaFrameKind.Audio);
        header.IsConfig.Should().BeTrue("the stashed sequence header comes first");
        payload.Should().Equal(s_asc);

        (header, payload) = frames[1];
        header.IsConfig.Should().BeFalse();
        payload.Should().Equal(s_aac);

        context.PendingAudioConfig.Should().BeNull("the stash is consumed once");
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
