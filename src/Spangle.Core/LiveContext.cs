using Spangle.Spinner;
using Spangle.Transport.HLS;

namespace Spangle;

/// <summary>
/// One live session: wires a receiver, the spinner chain and a sender together,
/// and owns the session's lifetime (authorization gate, shutdown, takeover kick).
/// </summary>
public sealed class LiveContext : IDisposable
{
    public IReceiverContext ReceiverContext { get; }
    public ISenderContext SenderContext { get; }

    private CancellationToken _cancellationToken;

    /// <summary>
    /// User-provided MediaFrame-to-MediaFrame transforms, inserted between the receiver
    /// and the terminal stage in order. This is the plugin point of the pipeline:
    /// e.g. a spinner that turns AMF data events into timed-metadata frames.
    /// </summary>
    private readonly IReadOnlyList<ISpinner> _mediaSpinners;

    private readonly PublishGate? _publishGate;

    /// <summary>
    /// When set, a session whose audio codec is known but whose video codec has not
    /// appeared within this delay is treated as audio-only and wired on the audio
    /// track. RTMP needs this: the protocol has no way to declare "no video is
    /// coming" (TS programs declare it in the PMT, so SRT never waits).
    /// </summary>
    private readonly TimeSpan? _audioOnlyFallback;

    public LiveContext(IReceiverContext receiverContext, ISenderContext senderContext,
        IReadOnlyList<ISpinner>? mediaSpinners = null,
        PublishSessionRegistry? publishSessions = null, IPublishAuthorizer? publishAuthorizer = null,
        TimeSpan? audioOnlyFallback = null, CancellationToken cancellationToken = default)
    {
        ReceiverContext = receiverContext;
        SenderContext = senderContext;
        senderContext.SourceInfo = receiverContext;
        _cancellationToken = cancellationToken;
        _mediaSpinners = mediaSpinners ?? [];
        _audioOnlyFallback = audioOnlyFallback;
        receiverContext.VideoCodecSet += OnVideoCodecSet;
        receiverContext.AudioCodecSet += OnAudioCodecSet;

        if (publishSessions is not null)
        {
            string protocol = receiverContext switch
            {
                Transport.Rtmp.RtmpReceiverContext => "RTMP",
                Transport.SRT.SRTReceiverContext => "SRT",
                _ => receiverContext.GetType().Name,
            };
            _publishGate = new PublishGate(publishSessions,
                publishAuthorizer ?? new DefaultPublishAuthorizer(),
                protocol, receiverContext.Id, receiverContext.EndPoint,
                kick: Shutdown, receiver: receiverContext);
            receiverContext.PublishGate = _publishGate;
        }
    }

    /// <summary>
    /// Ends this session from outside the receive loop. With <paramref name="handover"/>,
    /// the HLS output is not finalized (no ENDLIST): the successor session continues the
    /// same playlist with an EXT-X-DISCONTINUITY.
    /// </summary>
    public void Shutdown(bool handover = false)
    {
        if (_disposed)
        {
            return;
        }
        if (handover && SenderContext is HLSSenderContext hls)
        {
            hls.EndBehavior = HLSEndBehavior.Handover;
        }
        _lifetimeCancellationTokenSource.Cancel();
    }

    private readonly Lock _wireLock = new();
    private bool _pipelineWired;
    private bool _wiringClosed;
    private bool _audioOnlyFallbackScheduled;

    private void OnVideoCodecSet(VideoCodec _) => WirePipeline();

    /// <summary>
    /// An audio-only source never raises <see cref="IReceiverContext.VideoCodecSet"/>,
    /// so the audio codec is the wiring trigger — but only when the source has
    /// declared itself audio-only; otherwise the video codec event stays authoritative
    /// (audio usually arrives first and the output must not start without video).
    /// When the source cannot declare it (RTMP), an optional fallback timer treats
    /// "audio present, no video in sight" as audio-only after a grace period.
    /// </summary>
    private void OnAudioCodecSet(AudioCodec codec)
    {
        if (ReceiverContext.IsAudioOnly)
        {
            WirePipeline();
            return;
        }
        if (_audioOnlyFallback is { } delay && !_pipelineWired && !_audioOnlyFallbackScheduled)
        {
            _audioOnlyFallbackScheduled = true;
            _ = RunAudioOnlyFallbackAsync(delay);
        }
    }

    private async Task RunAudioOnlyFallbackAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_wireLock)
        {
            if (_pipelineWired || _wiringClosed || ReceiverContext.VideoCodec is not null)
            {
                return;
            }
            // declare before wiring: the segmenters key their cut logic on this flag
            ReceiverContext.IsAudioOnly = true;
        }
        WirePipeline();
    }

    /// <summary>
    /// Wires the pipeline once the leading codec is known:
    /// receiver → [media spinners...] → terminal (a format-converting spinner, or
    /// the sender itself when it consumes MediaFrames directly).
    /// </summary>
    private void WirePipeline()
    {
        lock (_wireLock)
        {
            if (_pipelineWired || _wiringClosed)
            {
                return;
            }
            _pipelineWired = true;
        }

        var intake = DetermineTerminalIntake();

        // Wire the interceptor chain back to front
        for (int i = _mediaSpinners.Count - 1; i >= 0; i--)
        {
            var spinner = _mediaSpinners[i];
            spinner.Outlet = intake;
            spinner.BeginSpin();
            intake = spinner.Intake;
        }

        ReceiverContext.MediaOutlet = intake;
    }

    /// <summary>
    /// Determines the terminal stage of the MediaFrame chain based on the
    /// receiver/sender pairing, and returns its intake.
    /// </summary>
    private System.IO.Pipelines.PipeWriter DetermineTerminalIntake()
    {
        if (SenderContext is HLSSenderContext { SegmentFormat: HLSSegmentFormat.Fmp4 })
        {
            // The CMAF sender muxes MediaFrames itself; no converting spinner is needed
            return SenderContext.Intake;
        }

        if (SenderContext is HLSSenderContext)
        {
            // Every receiver emits the same canonical MediaFrame form, so the
            // TS-converting spinner is receiver-agnostic. Codec support is the
            // spinner's own concern; it rejects codecs that cannot be carried
            // in its output container.
            var spinner = new FlvToM2TSSpinner(ReceiverContext, SenderContext.Intake, _cancellationToken);
            spinner.BeginSpin();
            return spinner.Intake;
        }

        throw new NotSupportedException(
            $"No terminal stage is available for {ReceiverContext.GetType().Name} -> {SenderContext.GetType().Name}");
    }

    private bool _disposed;

    private readonly CancellationTokenSource _lifetimeCancellationTokenSource = new();
    private CancellationTokenSource? _readTimeoutSource;

    public async ValueTask StartAsync()
    {
        CancellationTokenSource readTimeoutSource = _readTimeoutSource = new CancellationTokenSource();
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
        // The receiver reads with this token, so host shutdown, a read timeout, and a
        // takeover kick (Shutdown) all interrupt a blocked read instead of waiting for
        // the peer to send more data.
        ReceiverContext.CancellationToken = readTimeoutSource.Token;

        try
        {
            await ReceiverContext.BeginReceiveAsync(readTimeoutSource).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // orderly abort: host shutdown, read timeout, or a takeover kick
        }
        finally
        {
            // No wiring may happen after this point (e.g. the audio-only fallback
            // timer firing into a dead session)
            lock (_wireLock)
            {
                _wiringClosed = true;
            }

            // Signal downstream (spinner -> sender) that no more media will come
            if (ReceiverContext.MediaOutlet is not null)
            {
                await ReceiverContext.MediaOutlet.CompleteAsync().ConfigureAwait(false);
            }
            else
            {
                // Never wired (e.g. a denied publish): the sender is still waiting on
                // its intake and the host awaits the sender, so complete it directly
                await SenderContext.Intake.CompleteAsync().ConfigureAwait(false);
            }

            await contextCancellationRegistration.DisposeAsync().ConfigureAwait(false);
            await lifetimeCancellationRegistration.DisposeAsync().ConfigureAwait(false);
            // No Cancel() here: the spinner chain must be left running to drain the
            // frames the receiver already emitted, or the tail of the stream is lost.
            // The CTS is disposed with the context, after the host awaited the sender.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _lifetimeCancellationTokenSource.Cancel();
        _lifetimeCancellationTokenSource.Dispose();
        _readTimeoutSource?.Dispose();
        // the hosts dispose after the sender finished, so the successor session
        // (waiting in its gate) only proceeds once any handover state is stashed
        _publishGate?.Release();
        _disposed = true;
    }
}
