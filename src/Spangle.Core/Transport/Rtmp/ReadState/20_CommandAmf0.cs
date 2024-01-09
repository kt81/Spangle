using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using Spangle.Transport.Rtmp.NetConnection;
using Spangle.Transport.Rtmp.NetStream;
using static Spangle.Transport.Rtmp.Amf0.Amf0SequenceParser;

namespace Spangle.Transport.Rtmp.ReadState;

internal abstract class CommandAmf0 : IReadStateAction
{
    public static async ValueTask Perform(RtmpReceiverContext context)
    {
        PipeReader reader = context.Reader;
        CancellationToken ct = context.CancellationToken;

        var (buff, disposeHandle) = await ReadHelper.ReadMessageBody(context);

        using (disposeHandle)
        {
            DispatchRpc(context, ref buff);

            if (!disposeHandle.IsOriginalBufferConsumed)
            {
                reader.AdvanceTo(buff.End);
            }
        }

        await context.Writer.FlushAsync(ct);

        context.SetNext<ReadChunkHeader>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DispatchRpc(RtmpReceiverContext context, ref ReadOnlySequence<byte> buff)
    {
        // Parse command
        string command = ParseString(ref buff); // If too long, still leave it to an error to occur
        double transactionId = ParseNumber(ref buff);
        // dictionary or null
        AmfObject? commandObject = Parse(ref buff) as AmfObject;
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
            case NetConnectionHandler.Commands.FCUnpublish:
                NetConnectionHandler.OnFCUnpublish(context, transactionId, commandObject, ParseString(ref buff));
                context.ConnectionState = ReceivingState.WaitingPublish;
                break;
            case NetConnectionHandler.Commands.CreateStream:
                NetConnectionHandler.OnCreateStream(context, transactionId, commandObject);
                break;

            // NetStream Commands
            // -------------------------------------------------------------------
            case RtmpNetStream.Commands.Publish:
                context.GetStreamOrError().OnPublish(transactionId, commandObject,
                    ParseString(ref buff), ParseString(ref buff));
                break;
            case RtmpNetStream.Commands.DeleteStream:
                context.GetStreamOrError().OnDeleteStream(transactionId, commandObject,
                    ParseNumber(ref buff));
                break;
            default:
                throw new NotImplementedException($"The command `{command}` is not implemented.");
        }
    }
}
