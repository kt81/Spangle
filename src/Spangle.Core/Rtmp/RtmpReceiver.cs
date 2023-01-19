using Microsoft.Extensions.Logging;
using Spangle.Rtmp.Handshake;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Rtmp;

public sealed class RtmpReceiver : IReceiver<RtmpReceiverContext>, IDisposable
{
    private static readonly ILogger<RtmpReceiver>   s_logger;
    private readonly        CancellationTokenSource _lifetimeCancellationTokenSource = new();

    static RtmpReceiver()
    {
        s_logger = SpangleLogManager.GetLogger<RtmpReceiver>();
    }

    public async ValueTask StartAsync(RtmpReceiverContext context)
    {
        var contextCancellation = context.CancellationToken;
        CancellationTokenSource readTimeoutSource = new CancellationTokenSource();
        CancellationTokenRegistration contextCancellationRegistration = default;
        CancellationTokenRegistration lifetimeCancellationRegistration = default;

        if (contextCancellation.CanBeCanceled)
        {
            contextCancellationRegistration = contextCancellation.UnsafeRegister(static state =>
            {
                ((CancellationTokenSource)state!).Cancel();
            }, readTimeoutSource);
        }
        if (_lifetimeCancellationTokenSource.Token.CanBeCanceled)
        {
            lifetimeCancellationRegistration = _lifetimeCancellationTokenSource.Token.UnsafeRegister(static state =>
            {
                ((CancellationTokenSource)state!).Cancel();
            }, readTimeoutSource);
        }

        context.CancellationToken = readTimeoutSource.Token;

        try
        {
            await BeginReadAsyncImpl(context, readTimeoutSource);
        }
        finally
        {
            // ReSharper disable MethodHasAsyncOverload
            contextCancellationRegistration.Dispose();
            lifetimeCancellationRegistration.Dispose();
            // ReSharper restore MethodHasAsyncOverload
            readTimeoutSource.Cancel();
            readTimeoutSource.Dispose();
        }

        s_logger.ZLogDebug("Begin to read chunk");
    }

    private static async ValueTask BeginReadAsyncImpl(RtmpReceiverContext context, CancellationTokenSource readTimeoutSource)
    {
        s_logger.ZLogDebug("Begin to handshake");
        await HandshakeHandler.DoHandshakeAsync(context);
        s_logger.ZLogDebug("Handshake done");
        context.ConnectionState = ReceivingState.WaitingConnect;

        while (!context.IsCompleted)
        {
            if (context.Timeout > 0)
            {
                readTimeoutSource.CancelAfter(context.Timeout);
                await context.MoveNext(context);
                readTimeoutSource.TryReset();
            }
            else
            {
                await context.MoveNext(context);
            }
        }
    }

    public void Dispose()
    {
        _lifetimeCancellationTokenSource.Cancel();
        _lifetimeCancellationTokenSource.Dispose();
    }
}
