using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Spangle.Containers.Flv;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Protocols.Rtmp.ReadState;

internal abstract class Audio : IReadStateAction
{
    private static readonly ILogger<Audio> s_logger = SpangleLogManager.GetLogger<Audio>();

    public static async ValueTask Perform(RtmpReceiverContext context)
    {
        PipeReader reader = context.Reader;
        CancellationToken ct = context.CancellationToken;

        var enumerator = ReadHelper.ReadChunkedMessageBody(context).GetAsyncEnumerator(ct);
        await enumerator.MoveNextAsync();
        var buff = enumerator.Current;

        // Parse control
        var control = new FlvAudioControl(buff.FirstSpan[0]);
        s_logger.ZLogDebug(control.ToString());

        reader.AdvanceTo(buff.End);

        // Continue reading
        while (await enumerator.MoveNextAsync())
        {
            buff = enumerator.Current;
            reader.AdvanceTo(buff.End);
        }

        context.SetNext<ReadChunkHeader>();
    }
}
