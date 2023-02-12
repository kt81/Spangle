using System.Buffers;
using System.IO.Pipelines;
using Spangle.IO;
using Spangle.Rtmp.NetConnection;
using Spangle.Rtmp.NetStream;
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
        AmfObject? commandObject = Parse(ref buff) as AmfObject;

        // Dispatch RPC
        switch (command)
        {
            // NetConnection Commands
            // -------------------------------------------------------------------
            case NetConnectionHandler.Commands.Connect:
                // null is not allowed for connect
                ArgumentNullException.ThrowIfNull(commandObject);
                IReadStateAction.EnsureValidProtocolControlMessage(context);
                AmfObject? optionalArguments = null;
                if (0 < buff.Length)
                {
                    optionalArguments = ParseObject(ref buff);
                }

                NetConnectionHandler.OnConnect(context, transactionId, commandObject, optionalArguments);
                context.ConnectionState = ReceivingState.WaitingFCPublish;
                break;
            case NetConnectionHandler.Commands.ReleaseStream:
                IReadStateAction.EnsureValidProtocolControlMessage(context);
                NetConnectionHandler.OnReleaseStream(context, transactionId, commandObject, ParseString(ref buff));
                break;
            case NetConnectionHandler.Commands.FCPublish:
                IReadStateAction.EnsureValidProtocolControlMessage(context);
                NetConnectionHandler.OnFCPublish(context, transactionId, commandObject, ParseString(ref buff));
                context.ConnectionState = ReceivingState.WaitingPublish;
                break;
            case NetConnectionHandler.Commands.CreateStream:
                NetConnectionHandler.OnCreateStream(context, transactionId, commandObject);
                break;

            // NetStream Commands
            // -------------------------------------------------------------------
            case RtmpNetStream.Commands.Publish:
                string streamName = ParseString(ref buff);
                var stream = context.NetStream;
                stream.OnPublish(transactionId, commandObject,
                    streamName, ParseString(ref buff));
                break;
            default:
                throw new NotImplementedException($"The command `{command}` is not implemented.");
        }

        reader.AdvanceTo(buff.End);
        await context.Writer.FlushAsync(ct);

        context.SetNext<ReadBasicHeader>();
    }
}
