using System.Net;

namespace Spangle.Tests;

public class PublishSessionRegistryTests
{
    private static readonly EndPoint s_ep = new IPEndPoint(IPAddress.Loopback, 1);

    [Fact]
    public async Task DefaultPolicyTakesOverTheContestedName()
    {
        var registry = new PublishSessionRegistry();
        var authorizer = new DefaultPublishAuthorizer();

        var kickedA = false;
        PublishGate? gateA = null;
        gateA = new PublishGate(registry, authorizer, "TEST", "A", s_ep, kick: () =>
        {
            kickedA = true;
            // in the real flow the kicked session shuts down and releases via Dispose
            gateA!.Release();
        });

        (await gateA.TryOpenAsync("live/x", CancellationToken.None)).Should().BeTrue();

        var gateB = new PublishGate(registry, authorizer, "TEST", "B", s_ep, kick: () => { });
        (await gateB.TryOpenAsync("live/x", CancellationToken.None))
            .Should().BeTrue("the newest session wins by default");
        kickedA.Should().BeTrue("the previous session must have been kicked");
    }

    [Fact]
    public async Task CustomFirstWinsPolicyDeniesTheNewcomer()
    {
        var registry = new PublishSessionRegistry();
        var firstWins = new FirstWinsAuthorizer();

        var gateA = new PublishGate(registry, firstWins, "TEST", "A", s_ep, kick: () => { });
        (await gateA.TryOpenAsync("live/x", CancellationToken.None)).Should().BeTrue();

        var gateB = new PublishGate(registry, firstWins, "TEST", "B", s_ep, kick: () => { });
        (await gateB.TryOpenAsync("live/x", CancellationToken.None))
            .Should().BeFalse("the name is taken and the policy is first-wins");

        // after the holder releases, the name is free again
        gateA.Release();
        var gateC = new PublishGate(registry, firstWins, "TEST", "C", s_ep, kick: () => { });
        (await gateC.TryOpenAsync("live/x", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task DifferentNamesDoNotInterfere()
    {
        var registry = new PublishSessionRegistry();
        var authorizer = new DefaultPublishAuthorizer();

        var gateA = new PublishGate(registry, authorizer, "TEST", "A", s_ep,
            kick: () => throw new InvalidOperationException("must not be kicked"));
        var gateB = new PublishGate(registry, authorizer, "TEST", "B", s_ep, kick: () => { });

        (await gateA.TryOpenAsync("live/a", CancellationToken.None)).Should().BeTrue();
        (await gateB.TryOpenAsync("live/b", CancellationToken.None)).Should().BeTrue();
    }

    private sealed class FirstWinsAuthorizer : IPublishAuthorizer
    {
        public ValueTask<PublishDecision> AuthorizeAsync(PublishRequest request, CancellationToken ct) =>
            new(request.ExistingSession is null ? PublishDecision.Allow : PublishDecision.Deny);
    }
}
