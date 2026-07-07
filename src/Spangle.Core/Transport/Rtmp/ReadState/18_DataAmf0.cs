using System.Buffers;
using Spangle.Transport.Rtmp.NetStream;
using static Spangle.Transport.Rtmp.Amf0.Amf0SequenceParser;

namespace Spangle.Transport.Rtmp.ReadState;

internal abstract class DataAmf0
{
    public static void Handle(RtmpReceiverContext context, ReadOnlySequence<byte> payload)
    {
        // Parse command
        string command = ParseString(ref payload);

        // Dispatch RPC
        switch (command)
        {
            case RtmpNetStream.DataCommands.SetDataFrame:
                context.GetStreamOrError().OnSetDataFrame(ref payload);
                break;
            default:
                throw new NotImplementedException($"The command `{command}` is not implemented.");
        }
    }
}
