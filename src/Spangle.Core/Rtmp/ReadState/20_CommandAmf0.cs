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

        // Parse command
        string command = ParseString(ref buff);
        double transactionId = ParseNumber(ref buff);
        // dictionary or null
        IReadOnlyDictionary<string, object?>? commandObject = Parse(ref buff) as IReadOnlyDictionary<string, object?>;

        // Dispatch RPC
        switch (command)
        {
            case NetConnection.NetConnection.Commands.Connect:
                // null is not allowed for connect
                ArgumentNullException.ThrowIfNull(commandObject);
                IReadStateAction.EnsureValidProtocolControlMessage(context);
                IReadOnlyDictionary<string, object?>? optionalArguments = null;
                if (0 < buff.Length)
                {
                    optionalArguments = ParseObject(ref buff);
                }
                NetConnection.NetConnection.Connect(context, transactionId, commandObject, optionalArguments);
                context.ConnectionState = ReceivingState.WaitingFCPublish;
                break;
            case NetConnection.NetConnection.Commands.ReleaseStream:
                IReadStateAction.EnsureValidProtocolControlMessage(context);
                NetConnection.NetConnection.ReleaseStream(context, transactionId, commandObject, ParseString(ref buff));
                break;
            case NetConnection.NetConnection.Commands.FCPublish:
                IReadStateAction.EnsureValidProtocolControlMessage(context);
                NetConnection.NetConnection.FCPublish(context, transactionId, commandObject, ParseString(ref buff));
                context.ConnectionState = ReceivingState.WaitingPublish;
                break;
            case NetConnection.NetConnection.Commands.CreateStream:
                NetConnection.NetConnection.CreateStream(context, transactionId, commandObject);
                break;
            default:
                throw new NotImplementedException($"NetConnection.{command} is not implemented.");
        }

        reader.AdvanceTo(buff.End);
        await context.Writer.FlushAsync(ct);

        context.SetNext<ReadBasicHeader>();
    }
}
