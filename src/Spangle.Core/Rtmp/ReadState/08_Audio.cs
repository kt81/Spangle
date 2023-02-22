using System.Buffers;
using System.IO.Pipelines;
using Spangle.Containers.Flv;
using Spangle.Interop;
using Spangle.IO;
using Spangle.Rtmp.NetStream;
using static Spangle.Rtmp.Amf0.Amf0SequenceParser;

namespace Spangle.Rtmp.ReadState;

internal abstract class Audio : IReadStateAction
{
    public static async ValueTask Perform(RtmpReceiverContext context)
    {
        PipeReader reader = context.Reader;
        CancellationToken ct = context.CancellationToken;
        (ReadOnlySequence<byte> buff, _) =
            await reader.ReadExactlyAsync((int)context.MessageHeader.Length.HostValue, ct);

        // Parse control
        var control = new FlvAudioControl(buff.FirstSpan[0]);
        
        reader.AdvanceTo(buff.End);

        context.SetNext<ReadBasicHeader>();
    }
}
