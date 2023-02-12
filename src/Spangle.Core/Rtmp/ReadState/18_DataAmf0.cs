using System.Buffers;
using System.IO.Pipelines;
using Spangle.IO;
using Spangle.Rtmp.NetStream;
using static Spangle.Rtmp.Amf0.Amf0SequenceParser;

namespace Spangle.Rtmp.ReadState;

internal abstract class DataAmf0 : IReadStateAction
{
    public static async ValueTask Perform(RtmpReceiverContext context)
    {
        PipeReader reader = context.Reader;
        CancellationToken ct = context.CancellationToken;
        (ReadOnlySequence<byte> buff, _) =
            await reader.ReadExactlyAsync((int)context.MessageHeader.Length.HostValue, ct);

        // Parse command
        string command = ParseString(ref buff);

        // Dispatch RPC
        switch (command)
        {
            case RtmpNetStream.DataCommands.SetDataFrame:
                context.NetStream.OnSetDataFrame(ref buff);
                break;
            default:
                throw new NotImplementedException($"The command `{command}` is not implemented.");
        }

        reader.AdvanceTo(buff.End);
        await context.Writer.FlushAsync(ct);

        context.SetNext<ReadBasicHeader>();
    }
}
