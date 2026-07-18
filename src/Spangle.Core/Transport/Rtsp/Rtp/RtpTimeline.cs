namespace Spangle.Transport.Rtsp.Rtp;

/// <summary>
/// Maps one track's 32-bit RTP timestamps onto the session's 90 kHz tick timeline.
/// Alignment sources, best first:
/// <list type="number">
/// <item>PLAY's <c>RTP-Info: rtptime=…</c> — the timestamp of the Range start, so
/// every track's zero is the same instant (the standard answer)</item>
/// <item>an RTCP Sender Report seen before the track's first media packet — its
/// NTP wallclock aligns tracks through <see cref="RtspTimelineSync"/></item>
/// <item>the track's first packet as zero (each track free-runs; skew equals the
/// true inter-track start offset, typically small)</item>
/// </list>
/// Once frames were emitted the mapping never changes — a live timeline must not
/// jump under the segmenter.
/// </summary>
internal sealed class RtpTimeline(uint clockRate, RtspTimelineSync sync)
{
    private long _wraps;
    private uint _lastRaw;
    private bool _hasLast;

    private long   _baseExtended = -1;
    private double _baseSessionMs;

    private (ulong Ntp, uint Rtp)? _senderReport;
    private bool _emitted;

    /// <summary>PLAY's RTP-Info rtptime: this timestamp is session time zero.</summary>
    public void SetPlayBase(uint rtptime)
    {
        if (_baseExtended >= 0)
        {
            return;
        }
        _baseExtended = Extend(rtptime);
        _baseSessionMs = 0;
    }

    /// <summary>An RTCP Sender Report; only useful until the first media packet.</summary>
    public void OnSenderReport(in RtcpSenderReport report)
    {
        if (!_emitted && _baseExtended < 0)
        {
            _senderReport = (report.NtpTimestamp, report.RtpTimestamp);
        }
    }

    /// <summary>The session-timeline position of an RTP timestamp, in 90 kHz ticks.</summary>
    public long ToTicks90k(uint rtpTimestamp)
    {
        long extended = Extend(rtpTimestamp);
        if (_baseExtended < 0)
        {
            EstablishBase(extended, rtpTimestamp);
        }
        _emitted = true;
        // The session anchor is carried in milliseconds (it is derived from NTP wallclock); the RTP
        // delta converts straight to ticks, so a 90 kHz video track round-trips without rounding.
        double ticks = _baseSessionMs * 90.0 + (extended - _baseExtended) * 90000.0 / clockRate;
        return ticks <= 0 ? 0L : (long)ticks;
    }

    private void EstablishBase(long extended, uint rtpTimestamp)
    {
        if (_senderReport is { } sr)
        {
            // NTP wallclock of this packet, projected from the report
            long srExtended = Extend(sr.Rtp);
            double packetNtpMs = new RtcpSenderReport { Ssrc = 0, NtpTimestamp = sr.Ntp, RtpTimestamp = sr.Rtp }
                .NtpMilliseconds + (extended - srExtended) * 1000.0 / clockRate;
            _baseExtended = extended;
            _baseSessionMs = packetNtpMs - sync.EstablishNtpBase(packetNtpMs);
            return;
        }
        _ = rtpTimestamp;
        _baseExtended = extended;
        _baseSessionMs = 0;
    }

    private long Extend(uint raw)
    {
        if (_hasLast)
        {
            if (raw < _lastRaw && _lastRaw - raw > uint.MaxValue / 2)
            {
                _wraps++;
            }
            else if (raw > _lastRaw && raw - _lastRaw > uint.MaxValue / 2 && _wraps > 0)
            {
                _wraps--; // an out-of-order timestamp from just before a wrap
            }
        }
        _lastRaw = raw;
        _hasLast = true;
        return (_wraps << 32) | raw;
    }
}

/// <summary>
/// The session-wide anchor shared by every track: the first track to project an
/// NTP wallclock declares it the session's zero; the others align to it.
/// </summary>
internal sealed class RtspTimelineSync
{
    private double? _ntpBaseMs;

    public double EstablishNtpBase(double candidateMs)
    {
        _ntpBaseMs ??= candidateMs;
        return _ntpBaseMs.Value;
    }
}
