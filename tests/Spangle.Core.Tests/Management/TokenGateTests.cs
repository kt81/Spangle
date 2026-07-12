using Spangle.Extensions.Kestrel;

namespace Spangle.Tests.Management;

public class TokenGateTests
{
    [Fact]
    public void CorrectTokenMatches()
    {
        TokenGate.Matches("Bearer sekrit", "sekrit").Should().BeTrue();
    }

    [Fact]
    public void WrongTokenIsRejected()
    {
        TokenGate.Matches("Bearer wrong", "sekrit").Should().BeFalse();
    }

    [Fact]
    public void MissingSchemeIsRejected()
    {
        TokenGate.Matches("sekrit", "sekrit").Should().BeFalse();
    }

    [Fact]
    public void SchemeIsCaseSensitive()
    {
        TokenGate.Matches("bearer sekrit", "sekrit").Should().BeFalse();
    }

    [Fact]
    public void EmptyHeaderIsRejected()
    {
        TokenGate.Matches("", "sekrit").Should().BeFalse();
    }

    [Fact]
    public void SurroundingWhitespaceAroundTheTokenIsTolerated()
    {
        TokenGate.Matches("Bearer   sekrit  ", "sekrit").Should().BeTrue();
    }

    [Fact]
    public void PrefixOfTheTokenIsRejected()
    {
        TokenGate.Matches("Bearer sekri", "sekrit").Should().BeFalse();
        TokenGate.Matches("Bearer sekrit2", "sekrit").Should().BeFalse();
    }

    [Fact]
    public void LongTokensTakeTheHeapPathAndStillMatch()
    {
        string token = new('x', 300); // beyond the stackalloc fast path
        TokenGate.Matches($"Bearer {token}", token).Should().BeTrue();
        TokenGate.Matches($"Bearer {token}y", token).Should().BeFalse();
    }
}
