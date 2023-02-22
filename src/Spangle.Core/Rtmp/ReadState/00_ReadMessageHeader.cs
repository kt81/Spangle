using System.Buffers;
using System.IO.Pipelines;
using Spangle.IO;
using Spangle.Rtmp.Chunk;
using Spangle.Rtmp.ProtocolControlMessage;

namespace Spangle.Rtmp.ReadState;

/// <summary>
///     ReadMessageHeader
///     <see cref="MessageHeader" />
/// </summary>
internal abstract class ReadMessageHeader : IReadStateAction
{
    public static async ValueTask Perform(RtmpReceiverContext context)
    {
        PipeReader reader = context.Reader;
        CancellationToken ct = context.CancellationToken;
        int len = context.BasicHeader.Format.GetMessageHeaderLength();

        if (len > 0)
        {
            (ReadOnlySequence<byte> buff, _) = await reader.ReadExactlyAsync(len, ct);
            buff.CopyTo(context.MessageHeader.AsSpan());
            reader.AdvanceTo(buff.End);

            if (context.MessageHeader.HasExtendedTimestamp)
            {
                // Requires reading of extended timestamp
                (buff, _) = await reader.ReadExactlyAsync(sizeof(uint), ct);
                buff.CopyTo(context.MessageHeader.ExtendedTimeStamp.AsSpan());
                reader.AdvanceTo(buff.End);
            }
        }

        switch (context.BasicHeader.Format)
        {
            case MessageHeaderFormat.Fmt0:
                context.Timestamp = context.MessageHeader.TimestampOrDeltaInterop;
                break;
            case MessageHeaderFormat.Fmt1:
            case MessageHeaderFormat.Fmt2:
                context.Timestamp += context.MessageHeader.TimestampOrDeltaInterop;
                break;
            case MessageHeaderFormat.Fmt3:
                if (context.PreviousFormat == MessageHeaderFormat.Fmt0)
                {
                    // Set delta to zero for following Fmt3 chunks
                    context.MessageHeader.TimestampOrDeltaInterop = 0;
                }
                else
                {
                    context.Timestamp += context.MessageHeader.TimestampOrDeltaInterop;
                }
                break;
            default:
                throw new InvalidDataException($"Unrecognized message header format {context.BasicHeader.Format}");
        }

        DispatchNextByMessageType(context);
    }

    private static void DispatchNextByMessageType(RtmpReceiverContext context)
    {
        switch (context.MessageHeader.TypeId)
        {
            case MessageType.SetChunkSize:
                context.SetNext<SetChunkSize>();
                break;
            case MessageType.Audio:
                context.SetNext<Audio>();
                break;
            case MessageType.Video:
                context.SetNext<Video>();
                break;
            case MessageType.DataAmf0:
                context.SetNext<DataAmf0>();
                break;
            case MessageType.CommandAmf0:
                context.SetNext<CommandAmf0>();
                break;
            default:
                throw new NotImplementedException(
                    $"The processor of [{context.MessageHeader.TypeId}] is not implemented.");
        }
    }
}
