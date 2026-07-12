using Microsoft.Extensions.Logging;
using Spangle.Logging;
using Spangle.Transport.Rtsp.Sdp;
using ZLogger;

namespace Spangle.Transport.Rtsp.ControlFlow;

/// <summary>
/// The RTSP client handshake, one method per exchange in numbered files, mirroring
/// the RTMP receiver's numbered ReadState flow:
/// <c>00_Options</c> → <c>01_Describe</c> → <c>02_Setup</c> (per track) → <c>03_Play</c>.
/// After <see cref="RunAsync"/> returns, media is arriving on the interleaved
/// channels this flow assigned, and the caller pumps the connection's read loop.
/// </summary>
internal sealed partial class RtspControlFlow(
    RtspConnection connection,
    string baseUri,
    RtspAuthenticator authenticator,
    RtspDialect dialect,
    RtspMediaFrameAdapter<RtspReceiverContext> adapter)
{
    private static readonly ILogger<RtspControlFlow> s_logger = SpangleLogManager.GetLogger<RtspControlFlow>();

    private SdpSession? _sdp;
    private string? _sessionId;
    private string? _serverPublicMethods;

    /// <summary>Interleaved channel → track role, filled in by SETUP.</summary>
    private readonly Dictionary<int, TrackChannel> _channels = new();

    /// <summary>The negotiated RTSP session id (for keepalive/TEARDOWN).</summary>
    public string? SessionId => _sessionId;

    /// <summary>Session keepalive interval; from the server's Session timeout, halved, clamped.</summary>
    public TimeSpan KeepAliveInterval { get; private set; } = TimeSpan.FromSeconds(30);

    public IReadOnlyDictionary<int, TrackChannel> Channels => _channels;

    /// <summary>Runs OPTIONS → DESCRIBE → SETUP(s) → PLAY. Throws on an unrecoverable failure.</summary>
    public async ValueTask RunAsync(CancellationToken ct)
    {
        await OptionsAsync(ct).ConfigureAwait(false);
        await DescribeAsync(ct).ConfigureAwait(false);
        await SetupTracksAsync(ct).ConfigureAwait(false);
        await PlayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Sends a request, applying auth and retrying once on a 401 challenge.</summary>
    private async ValueTask<RtspMessage> SendAsync(string method, string uri,
        Action<RtspRequest>? decorate, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var request = new RtspRequest(method, uri);
            request.Headers["User-Agent"] = dialect.UserAgent;
            if (_sessionId is not null)
            {
                request.Headers["Session"] = _sessionId;
            }
            if (authenticator.CreateAuthorization(method, uri) is { } auth)
            {
                request.Headers["Authorization"] = auth;
            }
            decorate?.Invoke(request);
            dialect.DecorateRequest?.Invoke(request);

            RtspMessage response = await connection.ExchangeAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == 401 && authenticator.TryAccept(response.Header("WWW-Authenticate"), attempt == 0))
            {
                s_logger.ZLogDebug($"{method} challenged; retrying with credentials");
                continue;
            }
            if (!response.IsSuccess)
            {
                throw new RtspProtocolException($"{method} {uri} failed: {response.StatusCode} {response.ReasonPhrase}");
            }
            return response;
        }
        throw new RtspProtocolException($"{method} {uri} failed: authentication rejected");
    }

    private string ControlUri(SdpMedia media)
    {
        string? control = media.Control ?? _sdp?.SessionControl;
        if (string.IsNullOrEmpty(control) || control == "*")
        {
            return baseUri;
        }
        if (control.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            return control; // absolute control URL
        }
        // relative: append to the base, honoring a trailing slash
        return baseUri.EndsWith('/') ? baseUri + control : $"{baseUri}/{control}";
    }

    /// <summary>One SETUP-assigned track: which interleaved channels carry its RTP/RTCP and what it is.</summary>
    internal sealed record TrackChannel(SdpMediaKind Kind, int RtpChannel, int RtcpChannel);
}
