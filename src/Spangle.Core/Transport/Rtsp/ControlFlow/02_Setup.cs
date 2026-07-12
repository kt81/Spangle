using System.Globalization;
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

            int rtpChannel = nextChannel;
            int rtcpChannel = nextChannel + 1;
            nextChannel += 2;

            string transport = string.Create(CultureInfo.InvariantCulture,
                $"RTP/AVP/TCP;unicast;interleaved={rtpChannel}-{rtcpChannel}");
            RtspMessage response = await SendAsync("SETUP", ControlUri(media),
                request => request.Headers["Transport"] = transport, ct).ConfigureAwait(false);

            AcceptSession(response);
            (int actualRtp, int actualRtcp) = ParseInterleaved(response.Header("Transport"), rtpChannel, rtcpChannel);
            _channels[actualRtp] = new TrackChannel(media.Kind, actualRtp, actualRtcp);
            _channels[actualRtcp] = _channels[actualRtp];
            s_logger.ZLogDebug($"RTSP SETUP {media.Kind} on interleaved channels {actualRtp}-{actualRtcp}");
        }

        if (_channels.Count == 0)
        {
            throw new RtspProtocolException("No track could be set up (all codecs unsupported)");
        }
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
