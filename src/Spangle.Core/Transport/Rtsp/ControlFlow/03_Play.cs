using System.Globalization;
using Spangle.Transport.Rtsp.Sdp;
using ZLogger;

namespace Spangle.Transport.Rtsp.ControlFlow;

internal sealed partial class RtspControlFlow
{
    /// <summary>
    /// PLAY: starts the flow. The RTP-Info response header carries each track's
    /// starting rtptime, which anchors that track's timeline so audio and video
    /// share one zero. Some firmwares reject an explicit Range (dialect-controlled).
    /// </summary>
    private async ValueTask PlayAsync(CancellationToken ct)
    {
        RtspMessage response = await SendAsync("PLAY", baseUri, dialect.ConfigurePlay, ct).ConfigureAwait(false);

        ApplyRtpInfo(response.Header("RTP-Info"));
        s_logger.ZLogInformation($"RTSP PLAY ok; media flowing");
    }

    /// <summary>
    /// RTP-Info: url=...;seq=...;rtptime=... per track, comma separated. The rtptime
    /// of each is that track's session-time zero.
    /// </summary>
    private void ApplyRtpInfo(string? rtpInfo)
    {
        if (string.IsNullOrEmpty(rtpInfo))
        {
            return; // no anchor; timelines fall back to first-packet / RTCP alignment
        }

        foreach (string entry in rtpInfo.Split(',', StringSplitOptions.TrimEntries))
        {
            string? url = null;
            uint? rtptime = null;
            foreach (string field in entry.Split(';', StringSplitOptions.TrimEntries))
            {
                if (field.StartsWith("url=", StringComparison.OrdinalIgnoreCase))
                {
                    url = field["url=".Length..];
                }
                else if (field.StartsWith("rtptime=", StringComparison.OrdinalIgnoreCase)
                         && uint.TryParse(field["rtptime=".Length..], NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out uint value))
                {
                    rtptime = value;
                }
            }
            if (rtptime is not { } t)
            {
                continue;
            }

            // match the url back to a track kind through the SDP control URL; when a
            // single track exists (or the url is absent) apply it to what we have
            SdpMediaKind kind = ResolveRtpInfoKind(url);
            if (kind == SdpMediaKind.Video)
            {
                adapter.SetVideoPlayBase(t);
            }
            else if (kind == SdpMediaKind.Audio)
            {
                adapter.SetAudioPlayBase(t);
            }
        }
    }

    private SdpMediaKind ResolveRtpInfoKind(string? url)
    {
        if (url is not null && _sdp is not null)
        {
            foreach (SdpMedia media in _sdp.Media)
            {
                if (media.Control is { } control && url.EndsWith(control, StringComparison.OrdinalIgnoreCase))
                {
                    return media.Kind;
                }
            }
        }
        // fall back: if only one media kind is wired, it must be that one
        bool hasVideo = adapter.HasVideo;
        bool hasAudio = adapter.HasAudio;
        if (hasVideo && !hasAudio)
        {
            return SdpMediaKind.Video;
        }
        if (hasAudio && !hasVideo)
        {
            return SdpMediaKind.Audio;
        }
        return SdpMediaKind.Other;
    }
}
