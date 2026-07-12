using System.Security.Cryptography;
using System.Text;

namespace Spangle.Extensions.Kestrel;

/// <summary>
/// Constant-time Bearer token comparison, shared by every token-gated endpoint
/// (the management surface and metadata injection).
/// </summary>
public static class TokenGate
{
    private const string Prefix = "Bearer ";

    /// <summary>Checks an Authorization header value against the expected token.</summary>
    public static bool Matches(string authorizationHeader, string expected)
    {
        ArgumentNullException.ThrowIfNull(authorizationHeader);
        ArgumentNullException.ThrowIfNull(expected);
        return authorizationHeader.StartsWith(Prefix, StringComparison.Ordinal)
               && FixedTimeEquals(authorizationHeader.AsSpan(Prefix.Length).Trim(), expected);
    }

    private static bool FixedTimeEquals(ReadOnlySpan<char> provided, string expected)
    {
        Span<byte> providedUtf8 = provided.Length <= 64
            ? stackalloc byte[256]
            : new byte[Encoding.UTF8.GetMaxByteCount(provided.Length)];
        int written = Encoding.UTF8.GetBytes(provided, providedUtf8);
        byte[] expectedUtf8 = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(providedUtf8[..written], expectedUtf8);
    }
}
