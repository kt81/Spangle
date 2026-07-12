using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Spangle.Transport.Rtsp;

/// <summary>
/// Client-side RTSP authentication: nothing until the server answers 401, then
/// Basic or Digest (RFC 7616 with MD5 — the algorithm RTSP servers actually speak;
/// IP cameras predate SHA-256 digests) on every subsequent request.
/// </summary>
[SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms",
    Justification = "HTTP/RTSP Digest authentication is defined over MD5 and cameras support nothing newer; this guards a media stream, not secrets")]
internal sealed class RtspAuthenticator(string? userName, string? password)
{
    private enum Scheme
    {
        None,
        Basic,
        Digest,
    }

    private Scheme _scheme;
    private string _realm = "";
    private string _nonce = "";
    private string? _opaque;
    private bool _qopAuth;
    private int _nonceCount;

    public bool HasCredentials => !string.IsNullOrEmpty(userName);

    /// <summary>
    /// Consumes a 401's WWW-Authenticate. Returns false when retrying cannot help
    /// (no credentials, unsupported scheme, or a repeated 401 with a stale nonce flag off).
    /// </summary>
    public bool TryAccept(string? wwwAuthenticate, bool firstAttempt)
    {
        if (!HasCredentials || string.IsNullOrEmpty(wwwAuthenticate))
        {
            return false;
        }
        if (!firstAttempt && !HasFlag(wwwAuthenticate, "stale", "true"))
        {
            return false; // the credentials themselves were rejected
        }

        if (wwwAuthenticate.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
        {
            _scheme = Scheme.Digest;
            _realm = Param(wwwAuthenticate, "realm") ?? "";
            _nonce = Param(wwwAuthenticate, "nonce") ?? "";
            _opaque = Param(wwwAuthenticate, "opaque");
            _qopAuth = Param(wwwAuthenticate, "qop")?.Split(',').Any(static q => q.Trim() is "auth") == true;
            _nonceCount = 0;
            string algorithm = Param(wwwAuthenticate, "algorithm") ?? "MD5";
            return algorithm.Equals("MD5", StringComparison.OrdinalIgnoreCase);
        }
        if (wwwAuthenticate.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            _scheme = Scheme.Basic;
            return true;
        }
        return false;
    }

    /// <summary>The Authorization header value for one request; null before any 401 arrived.</summary>
    public string? CreateAuthorization(string method, string uri)
    {
        switch (_scheme)
        {
            case Scheme.None:
                return null;

            case Scheme.Basic:
                string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}"));
                return $"Basic {basic}";

            case Scheme.Digest:
            default:
                string ha1 = Md5Hex($"{userName}:{_realm}:{password}");
                string ha2 = Md5Hex($"{method}:{uri}");
                var sb = new StringBuilder(256);
                sb.Append("Digest username=\"").Append(userName)
                    .Append("\", realm=\"").Append(_realm)
                    .Append("\", nonce=\"").Append(_nonce)
                    .Append("\", uri=\"").Append(uri).Append('"');
                if (_qopAuth)
                {
                    string nc = Interlocked.Increment(ref _nonceCount).ToString("x8", CultureInfo.InvariantCulture);
                    string cnonce = Guid.NewGuid().ToString("N")[..16];
                    string response = Md5Hex($"{ha1}:{_nonce}:{nc}:{cnonce}:auth:{ha2}");
                    sb.Append(", qop=auth, nc=").Append(nc)
                        .Append(", cnonce=\"").Append(cnonce)
                        .Append("\", response=\"").Append(response).Append('"');
                }
                else
                {
                    string response = Md5Hex($"{ha1}:{_nonce}:{ha2}");
                    sb.Append(", response=\"").Append(response).Append('"');
                }
                if (_opaque is not null)
                {
                    sb.Append(", opaque=\"").Append(_opaque).Append('"');
                }
                return sb.ToString();
        }
    }

    internal static string Md5Hex(string input) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(input)));

    /// <summary>Extracts one parameter from a challenge; quoted and unquoted forms.</summary>
    internal static string? Param(string challenge, string name)
    {
        int at = 0;
        while ((at = challenge.IndexOf(name, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int eq = at + name.Length;
            // must be a whole token followed by '='
            bool startsToken = at == 0 || challenge[at - 1] is ' ' or ',' or '\t';
            if (!startsToken || eq >= challenge.Length || challenge[eq] != '=')
            {
                at = eq;
                continue;
            }
            int valueStart = eq + 1;
            if (valueStart < challenge.Length && challenge[valueStart] == '"')
            {
                int close = challenge.IndexOf('"', valueStart + 1);
                return close > valueStart ? challenge[(valueStart + 1)..close] : null;
            }
            int end = challenge.IndexOfAny([',', ' '], valueStart);
            return end < 0 ? challenge[valueStart..] : challenge[valueStart..end];
        }
        return null;
    }

    private static bool HasFlag(string challenge, string name, string expected) =>
        Param(challenge, name)?.Equals(expected, StringComparison.OrdinalIgnoreCase) == true;
}
