namespace Spangle.Transport.Rtsp;

/// <summary>How a session keeps its RTSP session alive between media packets.</summary>
public enum RtspKeepAliveMethod
{
    /// <summary>GET_PARAMETER with the Session header (the RFC way; preferred)</summary>
    GetParameter,

    /// <summary>OPTIONS with the Session header (older servers that lack GET_PARAMETER)</summary>
    Options,
}

/// <summary>
/// A vendor dialect: the knobs that differ between camera firmwares, collected in
/// one table instead of if-chains in the control flow. Unknown names resolve to
/// the default dialect with a warning at the call site.
/// </summary>
public sealed class RtspDialect
{
    public required string Name { get; init; }

    /// <summary>Keepalive verb; the control flow falls back to OPTIONS automatically when the server's Public header lacks GET_PARAMETER.</summary>
    public RtspKeepAliveMethod KeepAlive { get; init; } = RtspKeepAliveMethod.GetParameter;

    /// <summary>Some firmwares reject PLAY with an explicit Range; this omits it.</summary>
    public bool OmitPlayRange { get; init; }

    public string UserAgent { get; init; } = "Spangle";

    /// <summary>Extra decoration of every outgoing request (vendor headers etc.).</summary>
    internal Action<RtspRequest>? DecorateRequest { get; init; }

    public static RtspDialect Default { get; } = new() { Name = "Default" };

    private static readonly Dictionary<string, RtspDialect> s_table = new(StringComparer.OrdinalIgnoreCase)
    {
        [Default.Name] = Default,
        // legacy firmwares (older Hikvision/Dahua lines): no GET_PARAMETER, picky about Range
        ["LegacyOptionsKeepalive"] = new RtspDialect
        {
            Name = "LegacyOptionsKeepalive",
            KeepAlive = RtspKeepAliveMethod.Options,
            OmitPlayRange = true,
        },
    };

    /// <summary>Resolves a configured dialect name; null/unknown fall back to the default.</summary>
    public static RtspDialect Resolve(string? name, out bool known)
    {
        if (string.IsNullOrEmpty(name))
        {
            known = true;
            return Default;
        }
        known = s_table.TryGetValue(name, out RtspDialect? dialect);
        return known ? dialect! : Default;
    }
}
