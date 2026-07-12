using System.Text;
using Spangle.Transport.Rtsp;

namespace Spangle.Tests.Transport.Rtsp;

/// <summary>
/// <see cref="RtspAuthenticator"/> answering a 401 challenge: HTTP Basic, and HTTP
/// Digest with MD5 (RFC 7616 / RFC 2617) both without and with qop=auth. The Digest
/// responses are checked against vectors computed independently with
/// <see cref="RtspAuthenticator.Md5Hex"/>.
/// </summary>
public class RtspAuthTests
{
    private const string User = "admin";
    private const string Password = "s3cret";
    private const string Uri = "rtsp://camera/stream";
    private const string Method = "DESCRIBE";

    [Fact]
    public void BasicHeaderIsBase64OfUserColonPassword()
    {
        var auth = new RtspAuthenticator(User, Password);

        auth.TryAccept("Basic realm=\"cam\"", firstAttempt: true).Should().BeTrue();
        string? header = auth.CreateAuthorization(Method, Uri);

        string expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));
        header.Should().Be(expected);
    }

    [Fact]
    public void DigestWithoutQopMatchesTheHandComputedResponse()
    {
        const string realm = "RTSP Server";
        const string nonce = "0a4f113bXYZ";
        var auth = new RtspAuthenticator(User, Password);

        auth.TryAccept($"Digest realm=\"{realm}\", nonce=\"{nonce}\"", firstAttempt: true).Should().BeTrue();
        string? header = auth.CreateAuthorization(Method, Uri);
        header.Should().NotBeNull();

        // RFC 7616 §3.4: HA1=MD5(user:realm:pass), HA2=MD5(method:uri), response=MD5(HA1:nonce:HA2).
        string ha1 = RtspAuthenticator.Md5Hex($"{User}:{realm}:{Password}");
        string ha2 = RtspAuthenticator.Md5Hex($"{Method}:{Uri}");
        string expectedResponse = RtspAuthenticator.Md5Hex($"{ha1}:{nonce}:{ha2}");

        RtspAuthenticator.Param(header!, "realm").Should().Be(realm);
        RtspAuthenticator.Param(header!, "nonce").Should().Be(nonce);
        RtspAuthenticator.Param(header!, "uri").Should().Be(Uri);
        RtspAuthenticator.Param(header!, "response").Should().Be(expectedResponse);
        header.Should().StartWith("Digest ");
    }

    [Fact]
    public void DigestWithQopAuthAddsNcCnonceAndMatchesTheQopResponse()
    {
        const string realm = "cam";
        const string nonce = "abcdef0123456789";
        var auth = new RtspAuthenticator(User, Password);

        auth.TryAccept($"Digest realm=\"{realm}\", nonce=\"{nonce}\", qop=\"auth\"", firstAttempt: true)
            .Should().BeTrue();
        string? header = auth.CreateAuthorization(Method, Uri);
        header.Should().NotBeNull();

        string? nc = RtspAuthenticator.Param(header!, "nc");
        string? cnonce = RtspAuthenticator.Param(header!, "cnonce");
        nc.Should().Be("00000001", "the nonce count starts at one");
        cnonce.Should().NotBeNullOrEmpty();
        RtspAuthenticator.Param(header!, "qop").Should().Be("auth");

        // RFC 7616 §3.4.1 qop=auth: response = MD5(HA1:nonce:nc:cnonce:qop:HA2).
        string ha1 = RtspAuthenticator.Md5Hex($"{User}:{realm}:{Password}");
        string ha2 = RtspAuthenticator.Md5Hex($"{Method}:{Uri}");
        string expectedResponse = RtspAuthenticator.Md5Hex($"{ha1}:{nonce}:{nc}:{cnonce}:auth:{ha2}");

        RtspAuthenticator.Param(header!, "response").Should().Be(expectedResponse);
    }

    [Fact]
    public void WithoutCredentialsNothingIsAcceptedAndNoHeaderIsProduced()
    {
        var auth = new RtspAuthenticator(null, null);

        auth.HasCredentials.Should().BeFalse();
        auth.TryAccept("Digest realm=\"cam\", nonce=\"n\"", firstAttempt: true).Should().BeFalse();
        auth.CreateAuthorization(Method, Uri).Should().BeNull();
    }
}
