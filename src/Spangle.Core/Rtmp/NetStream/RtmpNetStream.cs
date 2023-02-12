using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Spangle.Interop;
using Spangle.Logging;
using Spangle.Rtmp.Extensions;
using Spangle.Rtmp.ProtocolControlMessage;
using Spangle.Util;
using ZLogger;
using static Spangle.Rtmp.Amf0.Amf0SequenceParser;

namespace Spangle.Rtmp.NetStream;

/// <summary>
/// The NetStream defines the channel through which the streaming audio, video,
/// and data messages can flow over the NetConnection that connects the client to the server.
/// A NetConnection object can support multiple NetStreams for multiple data streams.
/// </summary>
internal class RtmpNetStream
{
    public uint   Id { get; }
    public string Name { get; }

    private WeakReference<RtmpReceiverContext> _context;
    private RtmpReceiverContext Context {
        get
        {
            if (_context.TryGetTarget(out var ctx))
            {
                return ctx;
            }

            // not to reach
            throw new NullReferenceException("Context class is already finalized.");
        }
    }

    private readonly ILogger<RtmpNetStream> _logger;

    public RtmpNetStream(RtmpReceiverContext context, uint id, string name)
    {
        _context = new WeakReference<RtmpReceiverContext>(context);
        _logger = SpangleLogManager.GetLogger<RtmpNetStream>();
        Id = id;
        Name = name;
    }

    // The following commands can be sent on the NetStream by the client to the server:
    public static class Commands
    {
        public const string Play         = "play";
        public const string Play2        = "play2";
        public const string DeleteStream = "deleteStream";
        public const string CloseStream  = "closeStream";
        public const string ReceiveAudio = "receiveAudio";
        public const string ReceiveVideo = "receiveVideo";
        public const string Publish      = "publish";
        public const string Seek         = "seek";
        public const string Pause        = "pause";
    }

    public static class DataCommands
    {
        public const string SetDataFrame = "@setDataFrame";
    }

    [SuppressMessage("ReSharper", "MemberHidesStaticFromOuterClass")]
    public static class SetDataFrameEvents
    {
        public const string OnMetaData = "onMetaData";
    }

    private static class PublishingTypes
    {
        public const string Live = "live";
        // public const string Record = "record";
        // public const string Append = "append";
    }

    private static class OnMetaDataKeys
    {
        public const string Width           = "width";
        public const string Height          = "height";
        public const string VideoCodecId    = "videocodecid";
        public const string VideoDataRate   = "videodatarate";
        public const string FrameRate       = "framerate";
        public const string AudioCodecId    = "audiocodecid";
        public const string AudioDataRate   = "audiocdatarate";
        public const string AudioSampleRate = "audiosamplerate";
        public const string AudioSampleSize = "audiosamplesize";
        public const string AudioChannels   = "audiochannels";
        public const string Stereo          = "stereo";
        public const string Encoder         = "encoder";
    }

    /// <summary>
    /// The client sends the publish command to publish a named stream to the server. Using this name,
    /// any client can play this stream and receive the published audio, video, and data messages.
    /// </summary>
    public void OnPublish(
        double transactionId,
        AmfObject? commandObject,
        string publishingName,
        string publishingType
    )
    {
        var context = Context;
        if (publishingType != PublishingTypes.Live)
        {
            ThrowHelper.ThrowOverSpec(context);
        }

        if (Name != publishingName)
        {
            throw new InvalidDataException($"Inconsistent stream name: {Name} => {publishingName}");
        }
        context.PreparingStreamName = publishingName;
        uint csId = context.BasicHeader.ChunkStreamId;
        const uint publishingStreamId = Protocol.ControlStreamId + 1;

        _logger.ZLogTrace("Send UserControlMessage (4) StreamBegin (0) : 1");
        var streamBegin = UserControlMessage.Create(UserControlMessageEvents.StreamBegin,
            BigEndianUInt32.FromHost(publishingStreamId).AsSpan());
        RtmpWriter.Write(context, 0, MessageType.UserControl,
            csId, Protocol.ControlStreamId, ref streamBegin);

        var result = new OnStatusResult(OnStatusResult.Level.Status, OnStatusResult.Code.Play,
            $"Stream '{publishingName}' is now published.", publishingName);
        RtmpWriter.Write(context, 0, MessageType.CommandAmf0,
            csId, publishingStreamId, result);
    }

    public void OnSetDataFrame(
        ref ReadOnlySequence<byte> buff)
    {
        string eventName = ParseString(ref buff);
        switch (eventName)
        {
            case SetDataFrameEvents.OnMetaData:
                OnMetaData(ref buff);
                break;
            default:
#if DEBUG
                // Throws error in dev env
                throw new NotImplementedException($"The event name {eventName} is not implemented.");
#else
                s_logger.ZLogWarning("The event name {0} is not implemented.", eventName);
#endif
        }
    }

    private void OnMetaData(ref ReadOnlySequence<byte> buff)
    {
        object? obj = Parse(ref buff);
        if (obj is not AmfObject data)
        {
            throw new InvalidDataException(
                $"Unknown data type for the OnMetaData context: {obj?.GetType().ToString() ?? "null"}");
        }
        _logger.DumpObject(data);

    }
}
