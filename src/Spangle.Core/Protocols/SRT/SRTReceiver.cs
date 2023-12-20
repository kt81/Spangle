using System.Buffers;
using Microsoft.Extensions.Logging;
using Spangle.Containers.M2TS;
using Spangle.Interop;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Protocols.SRT;

public sealed class SRTReceiver : ReceiverBase<SRTReceiver, SRTReceiverContext>
{
    private static readonly ILogger<SRTReceiver> s_logger;

    private bool _disposed;

    static SRTReceiver()
    {
        s_logger = SpangleLogManager.GetLogger<SRTReceiver>();
    }

    protected override async ValueTask BeginReadAsync(SRTReceiverContext context,
        CancellationTokenSource readTimeoutSource)
    {
        while (!context.IsCompleted)
        {
            var result = await context.Reader.ReadAsync(readTimeoutSource.Token);
            s_logger.ZLogDebug($"Data received");
            var buff = result.Buffer;
            ParseTSHeader(ref buff);

            // ref var packet = ref BufferMarshal.AsRefOrCopy<TSPacket>(result.Buffer);
            //BufferMarshal.DumpHex(result.Buffer.ToArray(), s_logger.ZLogDebug);
            context.Reader.AdvanceTo(result.Buffer.End);
            // if (context.Timeout > 0)
            // {
            //     readTimeoutSource.CancelAfter(context.Timeout);
            //     await context.MoveNext(context);
            //     readTimeoutSource.TryReset();
            // }
            // else
            // {
            //     await context.MoveNext(context);
            // }

        }

        s_logger.ZLogInformation($"SRT connection closed");
    }

    private void ParseTSHeader(ref ReadOnlySequence<byte> buff)
    {
        ref readonly var ts = ref BufferMarshal.AsRefOrCopy<TSPacket>(buff);
        ref readonly var header = ref ts.Header;
        s_logger.ZLogDebug($"""
TS Header ---
SyncByte: 0x{header.SyncByte:X}
TransportErrorIndicator: {header.TransportError}
PayloadUnitStartIndicator: {header.PayloadUnitStart}
TransportPriority: {header.TransportPriority}
PID: {header.PID}
AdaptationFieldControl: {header.AdaptationFieldControl}
ContinuityCounter: 0x{header.ContinuityCounter:X}
""");
        if (header.AdaptationFieldControl.HasAdaptation())
        {
            ref readonly var adaptation = ref ts.AdaptationFields;
            s_logger.ZLogDebug($"""
TS Adaptation Header ---
AdaptationFieldLength: {adaptation.AdaptationFieldLength}
DiscontinuityIndicator: {adaptation.DiscontinuityIndicator}
RandomAccessIndicator: {adaptation.RandomAccessIndicator}
ElementaryStreamPriorityIndicator: {adaptation.ESPriorityIndicator}
PCRFlag: {adaptation.HasPCR}
OPCRFlag: {adaptation.HasOPCR}
SplicingPointFlag: {adaptation.HasSplicingPoint}
TransportPrivateDataFlag: {adaptation.HasTransportPrivateData}
AdaptationFieldExtensionFlag: {adaptation.HasAdaptationFieldExtension}
""");
            if (adaptation.HasPCR)
            {
                ref readonly var pcr = ref ts.PCR;
                s_logger.ZLogDebug($"""
PCR ---
BasePCR: {pcr.BasePCR}
ExtensionPCR: {pcr.ExtensionPCR}
""");
            }
            if (adaptation.HasOPCR)
            {
                ref readonly var opcr = ref ts.OPCR;
                s_logger.ZLogDebug($"""
OPCR ---
OriginalBasePCR: {opcr.BasePCR}
OriginalExtensionPCR: {opcr.ExtensionPCR}
""");
            }
            if (adaptation.HasSplicingPoint)
            {
                s_logger.ZLogDebug($"""
SplicingPoint Data ---
SpliceCountdown: {ts.SpliceCountdown}
""");
            }

        }


    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed) // DO NOT invert to "early return"
        {
            if (disposing)
            {
                // place holder
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }

    ~SRTReceiver() => Dispose(false);

}
