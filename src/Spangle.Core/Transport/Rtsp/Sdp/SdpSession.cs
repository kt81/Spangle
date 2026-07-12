using System.Globalization;
using System.Text;

namespace Spangle.Transport.Rtsp.Sdp;

/// <summary>
/// The subset of a DESCRIBE answer this receiver acts on: media sections with
/// their payload type, encoding, clock rate, fmtp parameters and control URL.
/// Everything else in the SDP is deliberately ignored.
/// </summary>
internal sealed class SdpSession
{
    /// <summary>Session-level a=control (the aggregate control target), when present.</summary>
    public string? SessionControl { get; private set; }

    public IReadOnlyList<SdpMedia> Media => _media;
    private readonly List<SdpMedia> _media = [];

    public static SdpSession Parse(ReadOnlySpan<byte> body) => Parse(Encoding.UTF8.GetString(body));

    public static SdpSession Parse(string text)
    {
        var session = new SdpSession();
        SdpMedia? current = null;
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length < 2 || line[1] != '=')
            {
                continue;
            }
            string value = line[2..];
            switch (line[0])
            {
                case 'm':
                    current = ParseMediaLine(value);
                    if (current is not null)
                    {
                        session._media.Add(current);
                    }
                    break;

                case 'a' when current is null:
                    if (value.StartsWith("control:", StringComparison.OrdinalIgnoreCase))
                    {
                        session.SessionControl = value["control:".Length..].Trim();
                    }
                    break;

                case 'a':
                    ParseMediaAttribute(current, value);
                    break;
            }
        }
        return session;
    }

    private static SdpMedia? ParseMediaLine(string value)
    {
        // m=video 0 RTP/AVP 96
        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
        {
            return null;
        }
        var media = new SdpMedia { Kind = parts[0].ToUpperInvariant() switch
        {
            "VIDEO" => SdpMediaKind.Video,
            "AUDIO" => SdpMediaKind.Audio,
            _ => SdpMediaKind.Other,
        } };
        if (int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pt))
        {
            media.PayloadType = pt;
        }
        return media;
    }

    private static void ParseMediaAttribute(SdpMedia media, string value)
    {
        if (value.StartsWith("control:", StringComparison.OrdinalIgnoreCase))
        {
            media.Control = value["control:".Length..].Trim();
        }
        else if (value.StartsWith("rtpmap:", StringComparison.OrdinalIgnoreCase))
        {
            // rtpmap:96 H264/90000[/2]
            string[] parts = value["rtpmap:".Length..].Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pt)
                && pt == media.PayloadType)
            {
                string[] enc = parts[1].Split('/');
                media.Encoding = enc[0].ToUpperInvariant();
                if (enc.Length > 1 && uint.TryParse(enc[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint rate))
                {
                    media.ClockRate = rate;
                }
                if (enc.Length > 2 && int.TryParse(enc[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ch))
                {
                    media.Channels = ch;
                }
            }
        }
        else if (value.StartsWith("fmtp:", StringComparison.OrdinalIgnoreCase))
        {
            // fmtp:96 packetization-mode=1;sprop-parameter-sets=Z0IA...,aM4...
            string[] parts = value["fmtp:".Length..].Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pt)
                && pt == media.PayloadType)
            {
                foreach (string pair in parts[1].Split(';'))
                {
                    int eq = pair.IndexOf('=', StringComparison.Ordinal);
                    if (eq > 0)
                    {
                        media.Fmtp[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
                    }
                }
            }
        }
    }
}

internal enum SdpMediaKind
{
    Video,
    Audio,
    Other,
}

internal sealed class SdpMedia
{
    public SdpMediaKind Kind { get; init; }
    public int PayloadType { get; set; } = -1;
    public string Encoding { get; set; } = "";
    public uint ClockRate { get; set; }
    public int Channels { get; set; } = 1;
    public string? Control { get; set; }
    public Dictionary<string, string> Fmtp { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? FmtpValue(string key) => Fmtp.TryGetValue(key, out string? value) ? value : null;
}
