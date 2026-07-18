using Microsoft.Extensions.Logging;
using Spangle.Logging;
using Spangle.Spinner;
using Spangle.Transport.HLS;
using ZLogger;

namespace Spangle;

/// <summary>
/// One live session: wires a receiver, the spinner chain and a sender together,
/// and owns the session's lifetime (authorization gate, shutdown, takeover kick).
/// </summary>
public sealed class LiveContext : IDisposable
{
    private static readonly ILogger<LiveContext> s_logger = SpangleLogManager.GetLogger<LiveContext>();

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

    /// <summary>
    /// Senders beyond the primary one, each fed the same MediaFrame stream through a fan-out —
    /// how a session serves HLS and an additional egress (say, MOQT) at once. The primary keeps
    /// its special roles (handover, takeover semantics); these only receive media.
    /// </summary>
    private readonly IReadOnlyList<ISenderContext> _additionalSenders;

    public LiveContext(IReceiverContext receiverContext, ISenderContext senderContext,
        IReadOnlyList<ISpinner>? mediaSpinners = null,
        PublishSessionRegistry? publishSessions = null, IPublishAuthorizer? publishAuthorizer = null,
        TimeSpan? audioOnlyFallback = null, IReadOnlyList<ISenderContext>? additionalSenders = null,
        CancellationToken cancellationToken = default)
    {
        ReceiverContext = receiverContext;
        SenderContext = senderContext;
        senderContext.SourceInfo = receiverContext;
        _additionalSenders = additionalSenders ?? [];
        foreach (ISenderContext additional in _additionalSenders)
        {
            additional.SourceInfo = receiverContext;
        }

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
                Transport.Rtsp.RtspReceiverContext => "RTSP",
                Transport.Rtsp.Server.RtspPushReceiverContext => "RTSP",
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

    /// <summary>
    /// A pipeline stage threw. It can no longer move media, so the session would otherwise stay
    /// alive but silent; end it (the tail drains, the host tears down or redials) with the cause
    /// made explicit rather than buried in a fire-and-forget log line. The exception itself is
    /// already logged by the spinner; this records the consequence.
    /// </summary>
    private void OnSpinnerFaulted(Exception e)
    {
        s_logger.ZLogError($"A pipeline stage failed ({e.Message}); ending the session.");
        Shutdown();
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
            spinner.BeginSpin(OnSpinnerFaulted);
            intake = spinner.Intake;
        }

        ReceiverContext.MediaOutlet = intake;
    }

    /// <summary>
    /// Determines the terminal stage of the MediaFrame chain and returns its intake. With one
    /// sender that is the sender's own terminal; with additional senders it is a fan-out copying
    /// the stream to every sender's terminal.
    /// </summary>
    private System.IO.Pipelines.PipeWriter DetermineTerminalIntake()
    {
        System.IO.Pipelines.PipeWriter primary = ResolveTerminalIntake(SenderContext);
        if (_additionalSenders.Count == 0)
        {
            return primary;
        }

        var outputs = new List<System.IO.Pipelines.PipeWriter>(1 + _additionalSenders.Count) { primary };
        outputs.AddRange(_additionalSenders.Select(ResolveTerminalIntake));
        return new MediaFrameFanOut(outputs, _cancellationToken).Intake;
    }

    /// <summary>The terminal stage for one sender, based on the receiver/sender pairing.</summary>
    private System.IO.Pipelines.PipeWriter ResolveTerminalIntake(ISenderContext sender)
    {
        if (sender is HLSSenderContext { SegmentFormat: not HLSSegmentFormat.Fmp4 })
        {
            // TS is the one output that does not speak MediaFrames, so it is the one that needs a
            // converting stage in front of it. Every receiver emits the same canonical MediaFrame
            // form, so the spinner is receiver-agnostic. Codec support is the spinner's own concern;
            // it rejects codecs that cannot be carried in its output container.
            var spinner = new FlvToM2TSSpinner(ReceiverContext, sender.Intake, _cancellationToken);
            spinner.BeginSpin(OnSpinnerFaulted);
            return spinner.Intake;
        }

        // Everything else — the CMAF sender, the MOQT sender — reads the canonical MediaFrame
        // stream and does its own muxing, so the chain ends at its intake.
        return sender.Intake;
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
                // Never wired (e.g. a denied publish): every sender is still waiting on
                // its intake and the host awaits them all, so complete each directly
                await SenderContext.Intake.CompleteAsync().ConfigureAwait(false);
                foreach (ISenderContext additional in _additionalSenders)
                {
                    await additional.Intake.CompleteAsync().ConfigureAwait(false);
                }
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
