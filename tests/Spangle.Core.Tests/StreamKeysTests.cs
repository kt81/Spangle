namespace Spangle.Tests;

public class StreamKeysTests
{
    [Fact]
    public void SafeNamesMapToThemselves()
    {
        StreamKeys.Sanitize("live_test-1").Should().Be("live_test-1");
        StreamKeys.Sanitize("Abc09").Should().Be("Abc09");
    }

    [Fact]
    public void AlteredNamesCannotCollideWithLiterals()
    {
        // With last-wins takeover as the default, "a/b" colliding into "a_b"
        // would let one publisher hijack another stream's output.
        string slashed = StreamKeys.Sanitize("a/b");
        slashed.Should().NotBe(StreamKeys.Sanitize("a_b"));
        slashed.Should().StartWith("a_b-");
    }

    [Fact]
    public void DifferentUnsafeNamesGetDifferentKeys()
    {
        StreamKeys.Sanitize("a/b").Should().NotBe(StreamKeys.Sanitize("a?b"));
    }

    [Fact]
    public void SanitizeIsDeterministic()
    {
        StreamKeys.Sanitize("live/test").Should().Be(StreamKeys.Sanitize("live/test"));
    }

    [Fact]
    public void HugeNamesAreBoundedAndSafe()
    {
        // AMF strings can be 64KB; the key (and the stack usage) must stay bounded
        var huge = new string('x', 200_000) + "/tail";
        string key = StreamKeys.Sanitize(huge);
        key.Length.Should().BeLessThanOrEqualTo(64 + 1 + 8);
        key.Should().NotBe(StreamKeys.Sanitize(new string('x', 200_000) + "?tail"),
            "the hash covers the part beyond the truncation too");
    }

    [Fact]
    public void EmptyAndNullFallBack()
    {
        StreamKeys.Sanitize(null).Should().Be("stream");
        StreamKeys.Sanitize("  ").Should().Be("stream");
    }
}
