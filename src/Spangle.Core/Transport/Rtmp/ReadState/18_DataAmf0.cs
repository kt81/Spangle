using System.IO.Pipelines;
using Spangle.Transport.Rtmp.NetStream;
using static Spangle.Transport.Rtmp.Amf0.Amf0SequenceParser;

namespace Spangle.Transport.Rtmp.ReadState;

internal abstract class DataAmf0 : IReadStateAction
{
    public static async ValueTask Perform(RtmpReceiverContext context)
    {
        PipeReader reader = context.Reader;
        var (buff, disposeHandle) = await ReadHelper.ReadMessageBody(context);

        using (disposeHandle)
        {
            // Parse command
            string command = ParseString(ref buff);

            // Dispatch RPC
            switch (command)
            {
                case RtmpNetStream.DataCommands.SetDataFrame:
                    context.GetStreamOrError().OnSetDataFrame(ref buff);
                    break;
                default:
                    throw new NotImplementedException($"The command `{command}` is not implemented.");
            }
        }

        if (!disposeHandle.IsOriginalBufferConsumed)
        {
            reader.AdvanceTo(buff.End);
        }
        // await context.Writer.FlushAsync(context.CancellationToken);

        context.SetNext<ReadChunkHeader>();
    }
}
