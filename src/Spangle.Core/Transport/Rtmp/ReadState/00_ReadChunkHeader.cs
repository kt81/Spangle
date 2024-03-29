﻿using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Spangle.IO;
using Spangle.Logging;
using Spangle.Transport.Rtmp.Chunk;
using Spangle.Transport.Rtmp.ProtocolControlMessage;
using ZLogger;

namespace Spangle.Transport.Rtmp.ReadState;

/// <summary>
/// ReadBasicHeader
/// See <see cref="BasicHeader" /> and <see cref="MessageHeader"/>
/// </summary>
internal abstract class ReadChunkHeader : IReadStateAction
{
    private static readonly ILogger<ReadChunkHeader> s_logger = SpangleLogManager.GetLogger<ReadChunkHeader>();

    public static async ValueTask Perform(RtmpReceiverContext context)
    {
        await ReadBasicHeader(context);
        await ReadMessageHeader(context);
        SetNextByMessageType(context);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static async ValueTask ReadBasicHeader(RtmpReceiverContext context)
    {
        PipeReader reader = context.RemoteReader;
        CancellationToken ct = context.CancellationToken;
        context.PreviousFormat = context.BasicHeader.Format;

        // Check the first byte
        (ReadOnlySequence<byte> firstBuff, _) = await reader.ReadExactAsync(1, ct);
        firstBuff.CopyTo(context.BasicHeader.AsSpan());
        var endPos = firstBuff.End;

        // Read the remaining buffer if needed
        int fullLen = context.BasicHeader.RequiredLength;
        if (fullLen > 1)
        {
            reader.AdvanceTo(firstBuff.Start); // reset reading
            (ReadOnlySequence<byte> fullBuff, _) = await reader.ReadExactAsync(fullLen, ct);
            fullBuff.CopyTo(context.BasicHeader.AsSpan());
            endPos = fullBuff.End;
        }

        reader.AdvanceTo(endPos);

        DumpBasicHeader(context);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static async ValueTask ReadMessageHeader(RtmpReceiverContext context)
    {
        PipeReader reader = context.RemoteReader;
        CancellationToken ct = context.CancellationToken;
        int len = context.BasicHeader.Format.GetMessageHeaderLength();

        if (len > 0)
        {
            (ReadOnlySequence<byte> buff, _) = await reader.ReadExactAsync(len, ct);
            buff.CopyTo(context.MessageHeader.AsSpan());
            reader.AdvanceTo(buff.End);

            if (context.MessageHeader.HasExtendedTimestamp)
            {
                // Requires reading of extended timestamp
                (buff, _) = await reader.ReadExactAsync(sizeof(uint), ct);
                buff.CopyTo(context.MessageHeader.ExtendedTimeStamp.AsSpan());
                reader.AdvanceTo(buff.End);
            }
        }

        // Compute or set timestamp
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
                throw new InvalidDataException($"Unrecognized message header format {context.BasicHeader.Format:@format}");
        }

        DumpMessageHeader(context);
    }

    // [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetNextByMessageType(RtmpReceiverContext context)
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

    [Conditional("DEBUG")]
    private static void DumpBasicHeader(RtmpReceiverContext context)
    {
        s_logger.ZLogTrace($"{context.BasicHeader.ToString()}");
    }

    [Conditional("DEBUG")]
    private static void DumpMessageHeader(RtmpReceiverContext context)
    {
        ref var m = ref context.MessageHeader;
        s_logger.ZLogTrace($$"""MessageHeader {ts:{{m.TimestampOrDeltaInterop}}, msgLen:{{m.Length}}, msgTypeId:{{m.TypeId}}, streamId:{{m.StreamId}}}""");
    }
}
