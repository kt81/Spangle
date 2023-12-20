namespace Spangle;

public abstract class ReceiverBase<TReceiver, TReceiverContext> : IReceiver<TReceiverContext>, IDisposable
    where TReceiver : ReceiverBase<TReceiver, TReceiverContext>
    where TReceiverContext : IReceiverContext
{
    private bool _disposed;

    private readonly CancellationTokenSource _lifetimeCancellationTokenSource = new();

    protected abstract ValueTask BeginReadAsync(TReceiverContext context, CancellationTokenSource readTimeoutSource);

    public async ValueTask StartAsync(TReceiverContext context)
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
            await BeginReadAsync(context, readTimeoutSource);
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
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }
        if (disposing)
        {
            _lifetimeCancellationTokenSource.Cancel();
            _lifetimeCancellationTokenSource.Dispose();
        }

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~ReceiverBase() => Dispose(false);
}
