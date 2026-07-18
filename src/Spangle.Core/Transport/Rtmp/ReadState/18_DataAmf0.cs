using System.Buffers;
using Microsoft.Extensions.Logging;
using Spangle.Logging;
using Spangle.Spinner;
using Spangle.Transport.Rtmp.NetStream;
using ZLogger;
using static Spangle.Transport.Rtmp.Amf0.Amf0SequenceParser;

namespace Spangle.Transport.Rtmp.ReadState;

/// <summary>
/// Dispatches AMF0 data messages: <c>onMetaData</c> (bare or wrapped in
/// <c>@setDataFrame</c>) feeds the stream info, every other event becomes a timed
/// <see cref="MediaFrameKind.Data"/> frame on the media timeline — the raw material
/// for timed metadata (a spinner turns it into ID3 downstream).
/// </summary>
internal abstract class DataAmf0
{
    private static readonly ILogger<DataAmf0> s_logger = SpangleLogManager.GetLogger<DataAmf0>();

    public static void Handle(RtmpReceiverContext context, ReadOnlySequence<byte> payload)
    {
        try
        {
            HandleCore(context, payload);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
            // Data events are ancillary; one the parser cannot decode (an exotic AMF0 type,
            // a malformed payload) is dropped rather than allowed to end the session.
            s_logger.ZLogWarning($"Undecodable AMF0 data message dropped: {e.Message}");
        }
    }

    private static void HandleCore(RtmpReceiverContext context, ReadOnlySequence<byte> payload)
    {
        ReadOnlySequence<byte> rest = payload;
        string command = ParseString(ref rest);
        if (command == RtmpNetStream.DataCommands.SetDataFrame)
        {
            // @setDataFrame is just an envelope; the enclosed event is the message
            payload = rest;
            command = ParseString(ref rest);
        }

        if (command == RtmpNetStream.SetDataFrameEvents.OnMetaData)
        {
            context.GetStreamOrError().OnMetaData(ref rest);
            return;
        }

        // Any other data event (onTextData, onCuePoint, vendor events...):
        // forward it verbatim (event name + arguments) as an AMF0 data frame
        if (context.MediaOutlet is null)
        {
            s_logger.ZLogDebug($"Data event `{command}` arrived before the media outlet is ready; dropped");
            return;
        }

        // RTMP speaks milliseconds; the canonical frame clock is 90 kHz.
        MediaFrameHeader.Write(context.MediaOutlet,
            MediaFrameKind.Data, MediaFrameFlags.None, (uint)DataCodec.Amf0, 0,
            (int)payload.Length, (long)context.Timestamp * 90);
        var buff = context.MediaOutlet.GetSpan((int)payload.Length);
        payload.CopyTo(buff);
        context.MediaOutlet.Advance((int)payload.Length);
        // No flush here: data events ride along with the next media frame's flush
        s_logger.ZLogDebug($"Data event `{command}` forwarded ({payload.Length} bytes)");
    }
}
