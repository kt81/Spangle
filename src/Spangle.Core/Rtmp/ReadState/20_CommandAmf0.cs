using System.Buffers;
using System.IO.Pipelines;
using Spangle.IO;
using static Spangle.Rtmp.Amf0.Amf0SequenceParser;

namespace Spangle.Rtmp.ReadState;

internal abstract class CommandAmf0 : IReadStateAction
{
    public static async ValueTask Perform(RtmpReceiverContext context)
    {
        PipeReader reader = context.Reader;
        CancellationToken ct = context.CancellationToken;
        (ReadOnlySequence<byte> buff, _) =
            await reader.ReadExactlyAsync((int)context.MessageHeader.Length.HostValue, ct);
        IReadStateAction.EnsureValidProtocolControlMessage(context);

        // Parse command
        string command = ParseString(ref buff);
        double transactionId = ParseNumber(ref buff);
        IReadOnlyDictionary<string, object> commandObject = ParseObject(ref buff);
        IReadOnlyDictionary<string, object>? optionalArguments = null;
        if (0 < buff.Length)
        {
            optionalArguments = ParseObject(ref buff);
        }

        reader.AdvanceTo(buff.End);

        // Dispatch RPC
        switch (command)
        {
            case NetConnection.Commands.Connect:
                NetConnection.Connect(context, transactionId, commandObject, optionalArguments);
                break;
            default:
                throw new NotImplementedException($"NetConnection.{command} is not implemented.");
        }

        await context.Writer.FlushAsync(ct);

        context.SetNext<ReadBasicHeader>();
    }
}
