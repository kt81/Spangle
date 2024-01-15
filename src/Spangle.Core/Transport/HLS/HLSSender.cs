using System.Buffers;
using Microsoft.Extensions.Logging;
using Spangle.Interop;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Transport.HLS;

public class HLSSender : ISender<HLSSenderContext>, IDisposable
{
    private static readonly ILogger<HLSSender> s_logger = SpangleLogManager.GetLogger<HLSSender>();

    public void Dispose()
    {
        // TODO release managed resources here
    }

    public async ValueTask StartAsync(HLSSenderContext context)
    {
        var ct = context.CancellationToken;
        while (!ct.IsCancellationRequested)
        {
            // Debug
            var result = await context.VideoReader.ReadAsync(ct);
            BufferMarshal.DumpHex(result.Buffer.ToArray(), str => s_logger.ZLogDebug($"{str}"));
            context.VideoReader.AdvanceTo(result.Buffer.End);
        }
        // TODO implement this
    }
}
