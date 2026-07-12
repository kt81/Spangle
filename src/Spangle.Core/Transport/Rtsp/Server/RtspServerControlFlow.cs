using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Spangle.Logging;
using Spangle.Transport.Rtsp.Rtp;
using Spangle.Transport.Rtsp.Sdp;
using ZLogger;

namespace Spangle.Transport.Rtsp.Server;

/// <summary>
/// The server side of the RTSP publish handshake (a client pushes with ffmpeg's
/// default rtsp muxer flow): OPTIONS → ANNOUNCE (the client's SDP) → SETUP per track
/// (TCP-interleaved) → RECORD, then interleaved RTP flows in. Mirrors the pull side's
/// numbered control flow, reactively: each request is answered here.
/// </summary>
internal sealed class RtspServerControlFlow(RtspMediaFrameAdapter<RtspPushReceiverContext> adapter)
{
    private static readonly ILogger<RtspServerControlFlow> s_logger =
        SpangleLogManager.GetLogger<RtspServerControlFlow>();

    private const string PublicMethods = "OPTIONS, DESCRIBE, ANNOUNCE, SETUP, RECORD, TEARDOWN, GET_PARAMETER";

    private readonly string _sessionId = Guid.NewGuid().ToString("N")[..12];
    private SdpSession? _sdp;
    private int _nextChannel;

    /// <summary>Interleaved channel → track role, filled in by SETUP (TCP transport).</summary>
    private readonly Dictionary<int, TrackChannel> _channels = new();

    /// <summary>Server-bound UDP sockets, filled in by SETUP (UDP transport).</summary>
    private readonly List<UdpServerTrack> _udpTracks = [];

    public IReadOnlyDictionary<int, TrackChannel> Channels => _channels;

    /// <summary>UDP sockets bound for the client to send RTP to; the receiver reads from them.</summary>
    public IReadOnlyList<UdpServerTrack> UdpTracks => _udpTracks;

    /// <summary>Set once RECORD is accepted and media is expected to flow.</summary>
    public bool Recording { get; private set; }

    /// <summary>Set on TEARDOWN so the receiver ends the session.</summary>
    public bool TornDown { get; private set; }

    /// <summary>The stream name derived from the ANNOUNCE URL path (the publish target).</summary>
    public string? StreamName { get; private set; }

    public ValueTask<RtspResponse> HandleAsync(RtspMessage request)
    {
        RtspResponse response = request.Method.ToUpperInvariant() switch
        {
            "OPTIONS" => Options(),
            "ANNOUNCE" => Announce(request),
            "SETUP" => Setup(request),
            "RECORD" => Record(),
            "TEARDOWN" => Teardown(),
            "GET_PARAMETER" or "SET_PARAMETER" => RtspResponse.Ok().With("Session", _sessionId),
            _ => RtspResponse.Status(405, "Method Not Allowed").With("Allow", PublicMethods),
        };
        return new ValueTask<RtspResponse>(response);
    }

    private static RtspResponse Options() => RtspResponse.Ok().With("Public", PublicMethods);

    private RtspResponse Announce(RtspMessage request)
    {
        StreamName = ExtractStreamName(request.Uri);
        if (request.Body.Length == 0)
        {
            return RtspResponse.Status(400, "Bad Request");
        }
        _sdp = SdpSession.Parse(request.Body);
        var wired = 0;
        foreach (SdpMedia media in _sdp.Media)
        {
            bool ok = media.Kind switch
            {
                SdpMediaKind.Video => adapter.SetupVideo(media),
                SdpMediaKind.Audio => adapter.SetupAudio(media),
                _ => false,
            };
            if (ok)
            {
                wired++;
            }
        }
        if (wired == 0)
        {
            s_logger.ZLogWarning($"ANNOUNCE for '{StreamName}' described no usable track");
            return RtspResponse.Status(415, "Unsupported Media Type");
        }
        s_logger.ZLogInformation($"RTSP ANNOUNCE '{StreamName}': {wired} track(s)");
        return RtspResponse.Ok().With("Session", _sessionId);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of the socket pair transfers to the UdpServerTrack in _udpTracks, which the receiver disposes when the session ends")]
    private RtspResponse Setup(RtspMessage request)
    {
        string? transport = request.Header("Transport");
        if (transport is null)
        {
            return RtspResponse.Status(461, "Unsupported Transport");
        }
        SdpMediaKind kind = MatchTrackKind(request.Uri);

        // TCP-interleaved: RTP rides the RTSP connection
        if (transport.Contains("interleaved=", StringComparison.OrdinalIgnoreCase))
        {
            (int rtp, int rtcp) = ParseInterleaved(transport);
            if (rtp < 0)
            {
                rtp = _nextChannel;
                rtcp = _nextChannel + 1;
            }
            _nextChannel = Math.Max(_nextChannel, rtcp + 1);
            _channels[rtp] = new TrackChannel(kind, rtp, rtcp);
            _channels[rtcp] = _channels[rtp];

            string echo = $"RTP/AVP/TCP;unicast;interleaved={rtp}-{rtcp};mode=record";
            s_logger.ZLogDebug($"RTSP SETUP {kind} on interleaved {rtp}-{rtcp}");
            return RtspResponse.Ok().With("Session", _sessionId).With("Transport", echo);
        }

        // UDP: bind our own server ports; the client sends RTP to them after RECORD
        if (transport.Contains("client_port=", StringComparison.OrdinalIgnoreCase))
        {
            (int clientRtp, int clientRtcp) = ParseClientPort(transport);
            UdpMediaSocketPair sockets = UdpMediaSocketPair.Bind(IPAddress.Any);
            _udpTracks.Add(new UdpServerTrack(kind, sockets));

            string echo = string.Create(CultureInfo.InvariantCulture,
                $"RTP/AVP;unicast;client_port={clientRtp}-{clientRtcp};server_port={sockets.RtpPort}-{sockets.RtcpPort};mode=record");
            s_logger.ZLogDebug($"RTSP SETUP {kind} on UDP server_port {sockets.RtpPort}-{sockets.RtcpPort}");
            return RtspResponse.Ok().With("Session", _sessionId).With("Transport", echo);
        }

        return RtspResponse.Status(461, "Unsupported Transport");
    }

    private static (int Rtp, int Rtcp) ParseClientPort(string transport)
    {
        foreach (string part in transport.Split(';', StringSplitOptions.TrimEntries))
        {
            if (!part.StartsWith("client_port=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string[] pair = part["client_port=".Length..].Split('-');
            if (pair.Length == 2
                && int.TryParse(pair[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rtp)
                && int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rtcp))
            {
                return (rtp, rtcp);
            }
        }
        return (0, 0);
    }

    private RtspResponse Record()
    {
        Recording = true;
        s_logger.ZLogInformation($"RTSP RECORD '{StreamName}': media flowing");
        return RtspResponse.Ok().With("Session", _sessionId);
    }

    private RtspResponse Teardown()
    {
        TornDown = true;
        return RtspResponse.Ok().With("Session", _sessionId);
    }

    /// <summary>Matches a SETUP URL's trailing control token back to an SDP track kind.</summary>
    private SdpMediaKind MatchTrackKind(string setupUri)
    {
        if (_sdp is not null)
        {
            foreach (SdpMedia media in _sdp.Media)
            {
                if (media.Control is { Length: > 0 } control
                    && setupUri.EndsWith(control, StringComparison.OrdinalIgnoreCase))
                {
                    return media.Kind;
                }
            }
            // single-track sessions: the only wired track
            if (adapter.HasVideo && !adapter.HasAudio)
            {
                return SdpMediaKind.Video;
            }
            if (adapter.HasAudio && !adapter.HasVideo)
            {
                return SdpMediaKind.Audio;
            }
        }
        return SdpMediaKind.Video;
    }

    private static (int Rtp, int Rtcp) ParseInterleaved(string transport)
    {
        foreach (string part in transport.Split(';', StringSplitOptions.TrimEntries))
        {
            if (!part.StartsWith("interleaved=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string[] pair = part["interleaved=".Length..].Split('-');
            if (pair.Length == 2
                && int.TryParse(pair[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rtp)
                && int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rtcp))
            {
                return (rtp, rtcp);
            }
        }
        return (-1, -1);
    }

    internal static string ExtractStreamName(string uri)
    {
        // The stream key is the last path segment, like an RTMP publish name:
        // rtsp://host:port/live/mystream -> mystream (predictable /hls/mystream/...).
        string path;
        if (Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed))
        {
            path = parsed.AbsolutePath;
        }
        else
        {
            int scheme = uri.IndexOf("://", StringComparison.Ordinal);
            string rest = scheme >= 0 ? uri[(scheme + 3)..] : uri;
            int slash = rest.IndexOf('/', StringComparison.Ordinal);
            path = slash >= 0 ? rest[slash..] : "";
        }
        string trimmed = path.Trim('/');
        if (trimmed.Length == 0)
        {
            return "stream";
        }
        int last = trimmed.LastIndexOf('/');
        return last >= 0 ? trimmed[(last + 1)..] : trimmed;
    }

    /// <summary>One SETUP-assigned track: which interleaved channels carry its RTP/RTCP.</summary>
    internal sealed record TrackChannel(SdpMediaKind Kind, int RtpChannel, int RtcpChannel);
}

/// <summary>One UDP-transported push track: the server sockets the client sends RTP/RTCP to.</summary>
internal sealed class UdpServerTrack(SdpMediaKind kind, UdpMediaSocketPair sockets) : IDisposable
{
    public SdpMediaKind Kind => kind;
    public UdpMediaSocketPair Sockets => sockets;
    public void Dispose() => sockets.Dispose();
}
