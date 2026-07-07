using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Spangle.Interop;
using Spangle.IO;
using Spangle.Logging;
using Spangle.Transport.Rtmp.Chunk;
using Spangle.Transport.Rtmp.ProtocolControlMessage;
using ZLogger;

namespace Spangle.Transport.Rtmp.ReadState;

/// <summary>
/// Reads one chunk (basic header + message header + payload chunk), assembles it into the
/// per-chunk-stream state, and dispatches the message once it is complete.
/// Chunks of different chunk streams may be interleaved.
/// </summary>
internal abstract class ReadChunkHeader
{
    private static readonly ILogger<ReadChunkHeader> s_logger = SpangleLogManager.GetLogger<ReadChunkHeader>();

    public static async ValueTask Perform(RtmpReceiverContext context)
    {
        PipeReader reader = context.RemoteReader;
        CancellationToken ct = context.CancellationToken;

        await ReadBasicHeader(context);
        var format = context.BasicHeader.Format;
        var state = context.GetChunkStreamState(context.BasicHeader.ChunkStreamId);

        await ReadMessageHeader(context, state, format);

        if (state.Remaining == 0)
        {
            // A new message begins with this chunk
            state.Remaining = (int)state.MessageLength;
            state.Assembly.Clear();
        }

        // Read one chunk of the payload
        int chunkLength = Math.Min(state.Remaining, (int)context.ChunkSize);
        if (chunkLength > 0)
        {
            (ReadOnlySequence<byte> buff, _) = await reader.ReadExactAsync(chunkLength, ct);
            buff.CopyTo(state.Assembly.GetSpan(chunkLength));
            state.Assembly.Advance(chunkLength);
            reader.AdvanceTo(buff.End);
            state.Remaining -= chunkLength;
        }

        if (state.Remaining > 0)
        {
            return; // wait for the next chunks (possibly interleaved with other chunk streams)
        }

        // Message is complete; expose it via the context for the handlers
        context.Timestamp = state.Timestamp;
        context.MessageHeader.SetFmt0(state.Timestamp, state.MessageLength, state.TypeId, state.MessageStreamId);

        var payload = new ReadOnlySequence<byte>(state.Assembly.WrittenMemory);
        await DispatchMessage(context, state, payload);
        state.Assembly.Clear();
    }

    private static async ValueTask DispatchMessage(RtmpReceiverContext context, ChunkStreamState state,
        ReadOnlySequence<byte> payload)
    {
        switch (state.TypeId)
        {
            case MessageType.SetChunkSize:
                SetChunkSize.Handle(context, payload);
                break;
            case MessageType.Abort:
            case MessageType.Acknowledgement:
            case MessageType.UserControl:
            case MessageType.WindowAcknowledgementSize:
            case MessageType.SetPeerBandwidth:
                s_logger.ZLogTrace($"Ignoring control message: {state.TypeId}");
                break;
            case MessageType.Audio:
                await Audio.Handle(context, payload);
                break;
            case MessageType.Video:
                await Video.Handle(context, payload);
                break;
            case MessageType.DataAmf0:
                DataAmf0.Handle(context, payload);
                break;
            case MessageType.CommandAmf0:
                await CommandAmf0.Handle(context, payload);
                break;
            default:
                throw new NotImplementedException(
                    $"The processor of [{state.TypeId}] is not implemented.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static async ValueTask ReadBasicHeader(RtmpReceiverContext context)
    {
        PipeReader reader = context.RemoteReader;
        CancellationToken ct = context.CancellationToken;

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
    internal static async ValueTask ReadMessageHeader(RtmpReceiverContext context, ChunkStreamState state,
        MessageHeaderFormat format)
    {
        PipeReader reader = context.RemoteReader;
        CancellationToken ct = context.CancellationToken;
        int len = format.GetMessageHeaderLength();

        var header = new MessageHeader();
        if (len > 0)
        {
            (ReadOnlySequence<byte> buff, _) = await reader.ReadExactAsync(len, ct);
            unsafe
            {
                buff.CopyTo(header.AsSpan());
            }
            reader.AdvanceTo(buff.End);

            state.HasExtendedTimestamp = header.HasExtendedTimestamp;
        }

        if (state.HasExtendedTimestamp)
        {
            // Fmt3 chunks also carry the extended timestamp if the last Fmt0-2 header had one
            (ReadOnlySequence<byte> buff, _) = await reader.ReadExactAsync(sizeof(uint), ct);
            unsafe
            {
                buff.CopyTo(header.ExtendedTimeStamp.AsSpan());
            }
            reader.AdvanceTo(buff.End);
        }

        switch (format)
        {
            case MessageHeaderFormat.Fmt0:
                state.Timestamp = header.TimestampOrDeltaInterop;
                // Per spec, a Fmt3 chunk that starts a new message right after a Fmt0 chunk
                // uses the Fmt0 timestamp as its delta
                state.TimestampDelta = header.TimestampOrDeltaInterop;
                state.MessageLength = header.Length.HostValue;
                state.TypeId = header.TypeId;
                state.MessageStreamId = header.StreamId;
                break;
            case MessageHeaderFormat.Fmt1:
                state.TimestampDelta = header.TimestampOrDeltaInterop;
                state.Timestamp += state.TimestampDelta;
                state.MessageLength = header.Length.HostValue;
                state.TypeId = header.TypeId;
                break;
            case MessageHeaderFormat.Fmt2:
                state.TimestampDelta = header.TimestampOrDeltaInterop;
                state.Timestamp += state.TimestampDelta;
                break;
            case MessageHeaderFormat.Fmt3:
                if (state.Remaining == 0)
                {
                    // Starts a new message: re-apply the previous delta
                    state.Timestamp += state.TimestampDelta;
                }
                // A continuation chunk carries no timestamp
                break;
            default:
                throw new InvalidDataException($"Unrecognized message header format {format}");
        }

        DumpMessageHeader(state, format);
    }

    [Conditional("DEBUG")]
    private static void DumpBasicHeader(RtmpReceiverContext context)
    {
        s_logger.ZLogTrace($"{context.BasicHeader.ToString()}");
    }

    [Conditional("DEBUG")]
    private static void DumpMessageHeader(ChunkStreamState state, MessageHeaderFormat format)
    {
        s_logger.ZLogTrace(
            $$"""ChunkStream {csid:{{state.ChunkStreamId}}, fmt:{{format}}, ts:{{state.Timestamp}}, msgLen:{{state.MessageLength}}, msgTypeId:{{state.TypeId}}, streamId:{{state.MessageStreamId}}, remaining:{{state.Remaining}}}""")
            ;
    }
}
