using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Spangle.Containers.Flv;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Protocols.Rtmp.ReadState;

internal abstract class Video : IReadStateAction
{
    private static readonly ILogger<Video> s_logger = SpangleLogManager.GetLogger<Video>();

    public static async ValueTask Perform(RtmpReceiverContext context)
    {
        PipeReader reader = context.Reader;
        CancellationToken ct = context.CancellationToken;

        await using var enumerator = ReadHelper.ReadChunkedMessageBody(context).GetAsyncEnumerator(ct);
        await enumerator.MoveNextAsync();
        var buff = enumerator.Current;

        // Parse control
        var c = new FlvVideoControl(buff.FirstSpan[0]);
        s_logger.ZLogDebug($$"""FlvVideoControl {frameType:{{c.FrameType}}, codec:{{c.Codec}}}""");

        reader.AdvanceTo(buff.End);

        // Read all chunks
        while (await enumerator.MoveNextAsync())
        {
            buff = enumerator.Current;
            reader.AdvanceTo(buff.End);
        }

        // switch (control.Codec.ToInternal())
        // {
        //     case VideoCodec.H264:
        // }
        // buff.ToArray().DumpHex(s_logger.ZLogDebug);
        // Advance to last buffer's End position

        context.SetNext<ReadChunkHeader>();
    }
}
