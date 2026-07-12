using Spangle.Transport.Rtsp;

namespace Spangle.Tests.Transport.Rtsp;

public class RtspDialectTests
{
    /// <summary>Captures the header decisions a dialect hook makes, without the wire type.</summary>
    private sealed class HeaderSink : IRtspRequestBuilder
    {
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public void SetHeader(string name, string value) => Headers[name] = value;
    }

    // ---- Default dialect: RFC behavior ----

    [Fact]
    public void DefaultPrefersGetParameterWhenTheServerSupportsIt()
    {
        RtspDialect.Default.KeepAliveMethod(serverSupportsGetParameter: true).Should().Be("GET_PARAMETER");
    }

    [Fact]
    public void DefaultFallsBackToOptionsWhenGetParameterIsUnavailable()
    {
        RtspDialect.Default.KeepAliveMethod(serverSupportsGetParameter: false).Should().Be("OPTIONS");
    }

    [Fact]
    public void DefaultPlayCarriesAnExplicitRange()
    {
        var sink = new HeaderSink();
        RtspDialect.Default.ConfigurePlay(sink);
        sink.Headers.Should().ContainKey("Range").WhoseValue.Should().Be("npt=0.000-");
    }

    [Fact]
    public void DefaultDecorateAddsNothing()
    {
        var sink = new HeaderSink();
        RtspDialect.Default.DecorateRequest(sink);
        sink.Headers.Should().BeEmpty();
    }

    [Theory]
    [InlineData("rtsp://h/live", "trackID=0", null, "rtsp://h/live/trackID=0")]     // relative, appended
    [InlineData("rtsp://h/live/", "trackID=0", null, "rtsp://h/live/trackID=0")]    // trailing slash honored
    [InlineData("rtsp://h/live", "rtsp://h/other/track", null, "rtsp://h/other/track")] // absolute used as-is
    [InlineData("rtsp://h/live", null, "sess", "rtsp://h/live/sess")]               // session-level control
    [InlineData("rtsp://h/live", "*", null, "rtsp://h/live")]                       // aggregate control
    [InlineData("rtsp://h/live", null, null, "rtsp://h/live")]                      // no control -> base
    public void DefaultResolvesControlUriPerRfc(string basePath, string? media, string? session, string expected)
    {
        RtspDialect.Default.ResolveControlUri(basePath, media, session).Should().Be(expected);
    }

    // ---- Legacy dialect: the vendor overrides ----

    [Fact]
    public void LegacyForcesOptionsEvenWhenGetParameterIsAdvertised()
    {
        var legacy = new LegacyOptionsKeepaliveRtspDialect();
        legacy.KeepAliveMethod(serverSupportsGetParameter: true).Should().Be("OPTIONS");
    }

    [Fact]
    public void LegacyPlayOmitsTheRange()
    {
        var sink = new HeaderSink();
        new LegacyOptionsKeepaliveRtspDialect().ConfigurePlay(sink);
        sink.Headers.Should().NotContainKey("Range");
    }

    // ---- A custom dialect is just a subclass overriding the hooks it needs ----

    private sealed class VendorHeaderDialect : RtspDialect
    {
        public override string Name => "VendorHeader";
        public override void DecorateRequest(IRtspRequestBuilder request) => request.SetHeader("X-Vendor", "1");
    }

    [Fact]
    public void ACustomDialectOverridesOnlyWhatItNeeds()
    {
        var dialect = new VendorHeaderDialect();
        var sink = new HeaderSink();
        dialect.DecorateRequest(sink);
        sink.Headers.Should().ContainKey("X-Vendor");
        // hooks it did not override keep the RFC defaults
        dialect.KeepAliveMethod(serverSupportsGetParameter: true).Should().Be("GET_PARAMETER");
        dialect.ResolveControlUri("rtsp://h/live", "trackID=0", null).Should().Be("rtsp://h/live/trackID=0");
    }
}

public class RtspDialectRegistryTests
{
    private sealed class CustomDialect(string name) : RtspDialect
    {
        public override string Name { get; } = name;
    }

    [Fact]
    public void BuiltInsAreAlwaysResolvable()
    {
        var registry = new RtspDialectRegistry([]);
        registry.Resolve("Default", out bool knownDefault).Should().BeOfType<DefaultRtspDialect>();
        knownDefault.Should().BeTrue();
        registry.Resolve("LegacyOptionsKeepalive", out bool knownLegacy)
            .Should().BeOfType<LegacyOptionsKeepaliveRtspDialect>();
        knownLegacy.Should().BeTrue();
    }

    [Fact]
    public void ResolutionIsCaseInsensitive()
    {
        var registry = new RtspDialectRegistry([]);
        registry.Resolve("legacyoptionskeepalive", out bool known)
            .Should().BeOfType<LegacyOptionsKeepaliveRtspDialect>();
        known.Should().BeTrue();
    }

    [Fact]
    public void NullOrEmptyResolvesToDefaultAndIsKnown()
    {
        var registry = new RtspDialectRegistry([]);
        registry.Resolve(null, out bool known).Should().BeSameAs(RtspDialect.Default);
        known.Should().BeTrue();
    }

    [Fact]
    public void UnknownFallsBackToDefaultButIsFlaggedUnknown()
    {
        var registry = new RtspDialectRegistry([]);
        registry.Resolve("NoSuchCamera", out bool known).Should().BeSameAs(RtspDialect.Default);
        known.Should().BeFalse();
    }

    [Fact]
    public void ARegisteredDialectIsResolvedByName()
    {
        var registry = new RtspDialectRegistry([new CustomDialect("MyCam")]);
        registry.Resolve("MyCam", out bool known).Should().BeOfType<CustomDialect>();
        known.Should().BeTrue();
    }

    [Fact]
    public void ARegisteredDialectOverridesABuiltInOfTheSameName()
    {
        var custom = new CustomDialect("Default");
        var registry = new RtspDialectRegistry([custom]);
        registry.Resolve("Default", out _).Should().BeSameAs(custom);
    }
}
