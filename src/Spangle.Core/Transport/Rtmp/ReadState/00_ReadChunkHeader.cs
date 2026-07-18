using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Spangle.Logging;
using Spangle.Transport.Rtmp.Chunk;
using Spangle.Transport.Rtmp.ProtocolControlMessage;
using ZLogger;

namespace Spangle.Transport.Rtmp.ReadState;

/// <summary>
/// The receive core: reads once from the pipe, then synchronously parses as many complete
/// chunks as the buffer holds (assembling them into the per-chunk-stream state), dispatching
/// each completed message. Only one read and one advance happen per iteration, so the
/// per-chunk cost is plain buffer parsing with no pipe round-trips.
/// Chunks of different chunk streams may be interleaved.
/// </summary>
internal abstract class ReadChunkHeader
{
    private static readonly ILogger<ReadChunkHeader> s_logger = SpangleLogManager.GetLogger<ReadChunkHeader>();

    public static async ValueTask PerformAsync(RtmpReceiverContext context)
    {
        PipeReader reader = context.RemoteReader;
        ReadResult result = await reader.ReadAsync(context.CancellationToken).ConfigureAwait(false);
        var buffer = result.Buffer;

        while (TryReadChunk(context, ref buffer, out var completed))
        {
            if (completed is null)
            {
                continue; // mid-message chunk; keep parsing
            }

            // Expose the completed message via the context for the handlers
            context.SetTimestamp(completed.Timestamp);
            context.MessageHeader.SetFmt0(completed.Timestamp, completed.MessageLength,
                completed.TypeId, completed.MessageStreamId);
            context.BasicHeader.ChunkStreamId = completed.ChunkStreamId;

            // The assembly buffer is our own copy, so dispatching while the read buffer
            // is still rented is safe: handlers never touch RemoteReader.
            var payload = new ReadOnlySequence<byte>(completed.Assembly.WrittenMemory);
            await DispatchMessageAsync(context, completed, payload).ConfigureAwait(false);
            // ResetWrittenCount, not Clear: Clear would memset the written area on
            // every message, a pointless cost on the hot path
            completed.Assembly.ResetWrittenCount();

            if (context.IsCompleted)
            {
                break;
            }
        }

        context.AddBytesReceived(result.Buffer.Length - buffer.Length);
        reader.AdvanceTo(buffer.Start, buffer.End);

        if (result.IsCompleted && !context.IsCompleted)
        {
            // The peer closed the connection without deleteStream
            s_logger.ZLogInformation($"Connection closed by peer");
            context.ConnectionState = ReceivingState.Terminated;
        }
    }

    /// <summary>
    /// Tries to parse one chunk (basic header + message header + payload chunk) from the
    /// front of <paramref name="buffer"/>. Consumes it from the buffer and updates the
    /// chunk stream state only when the whole chunk is available; otherwise leaves
    /// everything untouched and returns false.
    /// </summary>
    /// <param name="context">Connection context</param>
    /// <param name="buffer">The unconsumed read buffer; advanced past the chunk on success</param>
    /// <param name="completedMessage">Set when this chunk completed a message</param>
    internal static bool TryReadChunk(RtmpReceiverContext context, ref ReadOnlySequence<byte> buffer,
        out ChunkStreamState? completedMessage)
    {
        completedMessage = null;

        // ---- Basic header (1-3 bytes) ----
        if (buffer.Length < 1)
        {
            return false;
        }
        var bh = context.BasicHeader.AsSpan();
        buffer.Slice(0, 1).CopyTo(bh);
        int bhLen = context.BasicHeader.RequiredLength;
        if (buffer.Length < bhLen)
        {
            return false;
        }
        buffer.Slice(0, bhLen).CopyTo(bh);

        var format = context.BasicHeader.Format;
        var state = context.GetChunkStreamState(context.BasicHeader.ChunkStreamId);

        // ---- Message header (0/3/7/11 bytes) + extended timestamp ----
        int mhLen = format.GetMessageHeaderLength();
        var header = new MessageHeader();
        if (buffer.Length < bhLen + mhLen)
        {
            return false;
        }
        if (mhLen > 0)
        {
            buffer.Slice(bhLen, mhLen).CopyTo(header.AsSpan());
        }

        // Fmt3 chunks carry the extended timestamp if the last Fmt0-2 header on this
        // chunk stream had one
        bool hasExtendedTimestamp = mhLen > 0 ? header.HasExtendedTimestamp : state.HasExtendedTimestamp;
        int extLen = hasExtendedTimestamp ? sizeof(uint) : 0;
        if (extLen > 0)
        {
            if (buffer.Length < bhLen + mhLen + extLen)
            {
                return false;
            }
            buffer.Slice(bhLen + mhLen, extLen).CopyTo(header.ExtendedTimeStamp.AsSpan());

            // On a Fmt3 chunk the field, when present, is a byte-for-byte resend of the last
            // header's value — and the librtmp family famously never resends it. Once the
            // timestamp passes 0xFFFFFF (4.66 hours in), assuming either way misframes such a
            // client by four bytes and kills the session, so do what nginx-rtmp does: peek,
            // and only consume the field when it is the resend. (A payload whose first four
            // bytes happen to equal the value is misread — a 2^-32-per-chunk trade every
            // tolerant reader makes.)
            if (mhLen == 0 && header.ExtendedTimeStamp.HostValue != state.LastExtendedTimestamp)
            {
                extLen = 0; // not a resend: those four bytes are payload
            }
        }

        // ---- Payload availability ----
        bool startsNewMessage = state.Remaining == 0;
        uint messageLength = format is MessageHeaderFormat.Fmt0 or MessageHeaderFormat.Fmt1
            ? header.Length.HostValue
            : state.MessageLength;
        if (startsNewMessage && messageLength > context.MaxMessageSize)
        {
            // the length field allows up to 16MB per message and the assembly buffers
            // never shrink, so unbounded lengths are a memory-exhaustion vector
            throw new InvalidDataException(
                $"Message length {messageLength} exceeds the limit of {context.MaxMessageSize}");
        }
        int remaining = startsNewMessage ? (int)messageLength : state.Remaining;
        int chunkLength = Math.Min(remaining, (int)context.ChunkSize);
        int totalLength = bhLen + mhLen + extLen + chunkLength;
        if (buffer.Length < totalLength)
        {
            return false;
        }

        // ---- The whole chunk is available; commit to the chunk stream state ----
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
                if (startsNewMessage)
                {
                    // Starts a new message: re-apply the previous delta
                    state.Timestamp += state.TimestampDelta;
                }
                // A continuation chunk carries no timestamp
                break;
            default:
                throw new InvalidDataException($"Unrecognized message header format {format}");
        }
        if (mhLen > 0)
        {
            state.HasExtendedTimestamp = header.HasExtendedTimestamp;
            if (header.HasExtendedTimestamp)
            {
                state.LastExtendedTimestamp = header.ExtendedTimeStamp.HostValue;
            }
        }

        if (startsNewMessage)
        {
            state.Remaining = (int)state.MessageLength;
            state.Assembly.ResetWrittenCount();
        }
        if (chunkLength > 0)
        {
            buffer.Slice(bhLen + mhLen + extLen, chunkLength).CopyTo(state.Assembly.GetSpan(chunkLength));
            state.Assembly.Advance(chunkLength);
            state.Remaining -= chunkLength;
        }

        buffer = buffer.Slice(totalLength);
        DumpChunk(state, format);

        if (state.Remaining == 0)
        {
            completedMessage = state;
        }
        return true;
    }

    private static async ValueTask DispatchMessageAsync(RtmpReceiverContext context, ChunkStreamState state,
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
                await Audio.HandleAsync(context, payload).ConfigureAwait(false);
                break;
            case MessageType.Video:
                await Video.HandleAsync(context, payload).ConfigureAwait(false);
                break;
            case MessageType.DataAmf0:
                DataAmf0.Handle(context, payload);
                break;
            case MessageType.CommandAmf0:
                await CommandAmf0.HandleAsync(context, payload).ConfigureAwait(false);
                break;
            default:
                throw new NotSupportedException(
                    $"The processor of [{state.TypeId}] is not supported.");
        }
    }

    [Conditional("DEBUG")]
    private static void DumpChunk(ChunkStreamState state, MessageHeaderFormat format)
    {
        s_logger.ZLogTrace(
            $$"""Chunk {csid:{{state.ChunkStreamId}}, fmt:{{format}}, ts:{{state.Timestamp}}, msgLen:{{state.MessageLength}}, msgTypeId:{{state.TypeId}}, streamId:{{state.MessageStreamId}}, remaining:{{state.Remaining}}}""");
    }
}
