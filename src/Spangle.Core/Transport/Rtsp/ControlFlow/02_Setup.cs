using System.Globalization;
using System.Net;
using Spangle.Transport.Rtsp.Rtp;
using Spangle.Transport.Rtsp.Sdp;
using ZLogger;

namespace Spangle.Transport.Rtsp.ControlFlow;

internal sealed partial class RtspControlFlow
{
    /// <summary>
    /// SETUP: for each usable track, requests TCP-interleaved transport and records
    /// the RTP/RTCP channel pair. The first response also establishes the RTSP
    /// session id (carried on every later request) and the keepalive interval.
    /// </summary>
    private async ValueTask SetupTracksAsync(CancellationToken ct)
    {
        var nextChannel = 0;
        foreach (SdpMedia media in _sdp!.Media)
        {
            if (media.Kind is not (SdpMediaKind.Video or SdpMediaKind.Audio))
            {
                continue;
            }
            // one track's config may be unsupported (e.g. an exotic codec); skip it
            // rather than failing the whole session, as long as something remains
            bool wired = media.Kind == SdpMediaKind.Video
                ? adapter.SetupVideo(media)
                : adapter.SetupAudio(media);
            if (!wired)
            {
                continue;
            }

            if (transport == RtspTransportMode.Udp)
            {
                await SetupUdpTrackAsync(media, ct).ConfigureAwait(false);
            }
            else
            {
                await SetupInterleavedTrackAsync(media, nextChannel, ct).ConfigureAwait(false);
                nextChannel += 2;
            }
        }

        if (_channels.Count == 0 && _udpBindings.Count == 0)
        {
            throw new RtspProtocolException("No track could be set up (all codecs unsupported)");
        }
    }

    private async ValueTask SetupInterleavedTrackAsync(SdpMedia media, int firstChannel, CancellationToken ct)
    {
        int rtpChannel = firstChannel;
        int rtcpChannel = firstChannel + 1;

        string header = string.Create(CultureInfo.InvariantCulture,
            $"RTP/AVP/TCP;unicast;interleaved={rtpChannel}-{rtcpChannel}");
        RtspMessage response = await SendAsync("SETUP", ControlUri(media),
            request => request.Headers["Transport"] = header, ct).ConfigureAwait(false);

        AcceptSession(response);
        (int actualRtp, int actualRtcp) = ParseInterleaved(response.Header("Transport"), rtpChannel, rtcpChannel);
        _channels[actualRtp] = new TrackChannel(media.Kind, actualRtp, actualRtcp);
        _channels[actualRtcp] = _channels[actualRtp];
        s_logger.ZLogDebug($"RTSP SETUP {media.Kind} on interleaved channels {actualRtp}-{actualRtcp}");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of the socket pair transfers to the UdpTrackBinding, which the receiver disposes when the session ends; on the failure path it is disposed here")]
    private async ValueTask SetupUdpTrackAsync(SdpMedia media, CancellationToken ct)
    {
        // bind our own RTP/RTCP ports and ask the server to stream to them
        var sockets = UdpMediaSocketPair.Bind(IPAddress.Any);
        try
        {
            string header = string.Create(CultureInfo.InvariantCulture,
                $"RTP/AVP;unicast;client_port={sockets.RtpPort}-{sockets.RtcpPort}");
            RtspMessage response = await SendAsync("SETUP", ControlUri(media),
                request => request.Headers["Transport"] = header, ct).ConfigureAwait(false);

            AcceptSession(response);
            IPAddress serverIp = ResolveServerAddress(response.Header("Transport"));
            (int serverRtp, int serverRtcp) = ParseServerPort(response.Header("Transport"), sockets.RtpPort);
            _udpBindings.Add(new UdpTrackBinding(media.Kind, sockets,
                new IPEndPoint(serverIp, serverRtp), new IPEndPoint(serverIp, serverRtcp)));
            s_logger.ZLogDebug($"RTSP SETUP {media.Kind} on UDP client_port {sockets.RtpPort}-{sockets.RtcpPort}, server {serverIp}:{serverRtp}");
        }
        catch
        {
            sockets.Dispose();
            throw;
        }
    }

    /// <summary>The server's RTP source: the Transport <c>source=</c> if present, else the RTSP host.</summary>
    private IPAddress ResolveServerAddress(string? transportHeader)
    {
        if (transportHeader is not null)
        {
            foreach (string part in transportHeader.Split(';', StringSplitOptions.TrimEntries))
            {
                if (part.StartsWith("source=", StringComparison.OrdinalIgnoreCase)
                    && IPAddress.TryParse(part["source=".Length..], out IPAddress? source))
                {
                    return source;
                }
            }
        }
        if (Uri.TryCreate(baseUri, UriKind.Absolute, out Uri? uri)
            && IPAddress.TryParse(uri.Host, out IPAddress? host))
        {
            return host;
        }
        return IPAddress.Loopback;
    }

    private static (int Rtp, int Rtcp) ParseServerPort(string? transportHeader, int fallbackBase)
    {
        if (transportHeader is not null)
        {
            foreach (string part in transportHeader.Split(';', StringSplitOptions.TrimEntries))
            {
                if (!part.StartsWith("server_port=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string[] pair = part["server_port=".Length..].Split('-');
                if (pair.Length == 2
                    && int.TryParse(pair[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rtp)
                    && int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rtcp))
                {
                    return (rtp, rtcp);
                }
            }
        }
        // no server_port advertised: NAT-punch to the same ports we bound (best effort)
        return (fallbackBase, fallbackBase + 1);
    }

    private void AcceptSession(RtspMessage response)
    {
        if (response.Header("Session") is not { } sessionHeader)
        {
            return;
        }
        // Session: <id>[;timeout=<seconds>]
        string[] parts = sessionHeader.Split(';', StringSplitOptions.TrimEntries);
        _sessionId ??= parts[0];
        foreach (string part in parts.AsSpan(1))
        {
            if (part.StartsWith("timeout=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(part["timeout=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int timeout)
                && timeout > 0)
            {
                // refresh at half the server's timeout, clamped to a sane band
                KeepAliveInterval = TimeSpan.FromSeconds(Math.Clamp(timeout / 2.0, 5, 60));
            }
        }
    }

    private static (int Rtp, int Rtcp) ParseInterleaved(string? transport, int fallbackRtp, int fallbackRtcp)
    {
        // the server echoes interleaved=a-b, and is free to renumber
        if (transport is not null)
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
        }
        return (fallbackRtp, fallbackRtcp);
    }
}
