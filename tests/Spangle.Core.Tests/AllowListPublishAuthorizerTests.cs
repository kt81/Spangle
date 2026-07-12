using System.Net;

namespace Spangle.Tests;

public class AllowListPublishAuthorizerTests
{
    private static PublishRequest Request(string name, ExistingSessionInfo? existing = null) => new()
    {
        Protocol = "TEST",
        StreamName = name,
        StreamKey = StreamKeys.Sanitize(name),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 1),
        ExistingSession = existing,
    };

    [Fact]
    public async Task ListedNameIsAllowed()
    {
        var authorizer = new AllowListPublishAuthorizer(["live/x", "live/y"]);
        (await authorizer.AuthorizeAsync(Request("live/x"), CancellationToken.None))
            .Should().Be(PublishDecision.Allow);
    }

    [Fact]
    public async Task UnlistedNameIsDenied()
    {
        var authorizer = new AllowListPublishAuthorizer(["live/x"]);
        (await authorizer.AuthorizeAsync(Request("live/z"), CancellationToken.None))
            .Should().Be(PublishDecision.Deny);
    }

    [Fact]
    public async Task MatchIsExactAndCaseSensitive()
    {
        var authorizer = new AllowListPublishAuthorizer(["live/x"]);
        (await authorizer.AuthorizeAsync(Request("LIVE/X"), CancellationToken.None))
            .Should().Be(PublishDecision.Deny, "a stream name is a credential; matching must be exact");
    }

    [Fact]
    public async Task ContestedListedNameKeepsLastWinsTakeover()
    {
        var authorizer = new AllowListPublishAuthorizer(["live/x"]);
        var existing = new ExistingSessionInfo { Id = "A", StartedAt = DateTimeOffset.UtcNow };
        (await authorizer.AuthorizeAsync(Request("live/x", existing), CancellationToken.None))
            .Should().Be(PublishDecision.Takeover, "a reconnect by a valid key holder must not be blocked");
    }
}
