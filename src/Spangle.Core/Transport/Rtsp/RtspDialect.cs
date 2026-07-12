namespace Spangle.Transport.Rtsp;

/// <summary>
/// The request surface a dialect may decorate: set or replace headers. Keeps the wire
/// type internal while letting application dialects add vendor headers.
/// </summary>
public interface IRtspRequestBuilder
{
    /// <summary>Sets (or replaces) a request header.</summary>
    void SetHeader(string name, string value);
}

/// <summary>
/// A vendor dialect: the points where camera firmwares diverge from the RFC, expressed
/// as overridable hook methods rather than flags branched on in the control flow. The
/// base class is the standards-compliant behavior; a dialect subclasses it and overrides
/// only the hooks that firmware needs. New dialects are added by registering an
/// <see cref="RtspDialect"/> in DI (see <c>RtspDialectRegistry</c>) — the same
/// extensibility model as the publish authorizer.
/// </summary>
public abstract class RtspDialect
{
    /// <summary>The name selected by <c>Rtsp.Sources[].Dialect</c>; case-insensitive.</summary>
    public abstract string Name { get; }

    /// <summary>The User-Agent sent on every request.</summary>
    public virtual string UserAgent => "Spangle";

    /// <summary>
    /// The keepalive verb to send between media packets. The default prefers
    /// GET_PARAMETER when the server advertised it (OPTIONS otherwise); a firmware that
    /// lies about supporting GET_PARAMETER can force OPTIONS by overriding this.
    /// </summary>
    public virtual string KeepAliveMethod(bool serverSupportsGetParameter) =>
        serverSupportsGetParameter ? "GET_PARAMETER" : "OPTIONS";

    /// <summary>
    /// Decorates the PLAY request. The default asks for the whole stream with an explicit
    /// Range; a firmware that rejects Range on PLAY overrides this to leave it off.
    /// </summary>
    public virtual void ConfigurePlay(IRtspRequestBuilder play)
    {
        ArgumentNullException.ThrowIfNull(play);
        play.SetHeader("Range", "npt=0.000-");
    }

    /// <summary>
    /// Decorates every outgoing request — the escape hatch for vendor headers or auth
    /// quirks. The default adds nothing.
    /// </summary>
    public virtual void DecorateRequest(IRtspRequestBuilder request)
    {
    }

    /// <summary>
    /// Resolves a track's SETUP target from the base URL and the SDP control attributes.
    /// The default follows RFC 2326: an absolute control URL is used as-is, a relative one
    /// is appended to the base, and <c>*</c>/absent falls back to the base. Firmwares with
    /// non-standard control-URL schemes override this.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054:URI-like parameters should not be strings",
        Justification = "RTSP control targets are wire strings passed verbatim to the peer; Uri would drop credentials and vendor query quirks")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1055:URI-like return values should not be strings",
        Justification = "The resolved SETUP target is sent verbatim on the wire, not consumed as a Uri")]
    public virtual string ResolveControlUri(string baseUri, string? mediaControl, string? sessionControl)
    {
        string? control = mediaControl ?? sessionControl;
        if (string.IsNullOrEmpty(control) || control == "*")
        {
            return baseUri;
        }
        if (control.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            return control;
        }
        return baseUri.EndsWith('/') ? baseUri + control : $"{baseUri}/{control}";
    }

    /// <summary>The standards-compliant dialect; the fallback for an unset/unknown name.</summary>
    public static RtspDialect Default { get; } = new DefaultRtspDialect();
}

/// <summary>The RFC-compliant behavior with no vendor quirks.</summary>
public sealed class DefaultRtspDialect : RtspDialect
{
    public override string Name => "Default";
}

/// <summary>
/// Legacy firmwares (older Hikvision/Dahua lines and similar): no working GET_PARAMETER,
/// and PLAY is rejected when it carries an explicit Range.
/// </summary>
public sealed class LegacyOptionsKeepaliveRtspDialect : RtspDialect
{
    public override string Name => "LegacyOptionsKeepalive";

    public override string KeepAliveMethod(bool serverSupportsGetParameter) => "OPTIONS";

    public override void ConfigurePlay(IRtspRequestBuilder play)
    {
        // this firmware family rejects PLAY with a Range; leave it off
    }
}
