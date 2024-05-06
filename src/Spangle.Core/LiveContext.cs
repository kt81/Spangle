using System.Diagnostics;
using Spangle.Spinner;
using Spangle.Transport.HLS;
using Spangle.Transport.Rtmp;
using Spangle.Transport.SRT;

namespace Spangle;

/// <summary>
/// LiveContext is a context for whale live streaming.
/// </summary>
public sealed class LiveContext : IDisposable
{
    public IReceiverContext ReceiverContext { get; }
    public ISenderContext SenderContext { get; }

    private CancellationToken _cancellationToken;

    private LinkedList<ISpinner> VideoSpinnerChain { get; } = new();
    private LinkedList<ISpinner> AudioSpinnerChain { get; } = new();

    public LiveContext(IReceiverContext receiverContext, ISenderContext senderContext, CancellationToken cancellationToken = default)
    {
        ReceiverContext = receiverContext;
        SenderContext = senderContext;
        _cancellationToken = cancellationToken;
        receiverContext.VideoCodecSet += OnVideoCodecSet;
    }

    /// <summary>
    /// OnVideoCodecSet is an event handler for video codec set event.
    /// </summary>
    /// <param name="_"></param>
    private void OnVideoCodecSet(VideoCodec _)
    {
        AddVideoSpinner(DetermineVideoSpinner());
    }

    private void AddVideoSpinner(ISpinner spinner)
    {
        // TODO Insert video spinners to the chain based on configuration
        if (VideoSpinnerChain.Count == 0)
        {
            ReceiverContext.VideoOutlet = spinner.Intake;
        }
        else
        {
            VideoSpinnerChain.Last!.Value.Outlet = spinner.Intake;
        }
        VideoSpinnerChain.AddLast(spinner);
        spinner.BeginSpin();
    }

    /// <summary>
    /// DetermineVideoSpinner determines video spinner based on combination of intake and outlet video format.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    /// <exception cref="IndexOutOfRangeException"></exception>
    private ISpinner DetermineVideoSpinner()
    {
        if (ReceiverContext is RtmpReceiverContext rtmp && SenderContext is HLSSenderContext)
        {
            if (ReceiverContext.VideoCodec == VideoCodec.H264)
            {
                return new FlvAVCToM2TSSpinner(rtmp, SenderContext.VideoIntake, _cancellationToken);
            }
            throw new NotImplementedException();
        }

        throw new NotImplementedException();
    }

    private bool _disposed;

    private readonly CancellationTokenSource _lifetimeCancellationTokenSource = new();

    public async ValueTask StartAsync()
    {
        CancellationTokenSource readTimeoutSource = new CancellationTokenSource();
        CancellationTokenRegistration contextCancellationRegistration = default;
        CancellationTokenRegistration lifetimeCancellationRegistration = default;

        if (_cancellationToken.CanBeCanceled)
        {
            contextCancellationRegistration = _cancellationToken.UnsafeRegister(static state =>
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

        _cancellationToken = readTimeoutSource.Token;

        try
        {
            await ReceiverContext.BeginReceiveAsync(readTimeoutSource);
        }
        finally
        {
            // ReSharper disable MethodHasAsyncOverload
            contextCancellationRegistration.Dispose();
            lifetimeCancellationRegistration.Dispose();
            readTimeoutSource.Cancel();
            // ReSharper restore MethodHasAsyncOverload
            readTimeoutSource.Dispose();
        }
    }

    private void Dispose(bool disposing)
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

    ~LiveContext() => Dispose(false);
}
