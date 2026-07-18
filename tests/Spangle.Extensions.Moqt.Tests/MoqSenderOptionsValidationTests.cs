using System.Net;

namespace Spangle.Extensions.Moqt.Tests;

/// <summary>
/// The sender rejects option combinations it cannot honor. MSF-01's initDataList — where a Draft01
/// catalog would carry decoder configuration — is not implemented, so a Draft01 catalog cannot
/// describe an audio track (LOC has no audio config property either). The context refuses the draft
/// up front rather than publish a catalog that would leave audio silently undecodable.
/// </summary>
public class MoqSenderOptionsValidationTests
{
    private static MoqSenderOptions With(MsfDraft catalogDraft) => new()
    {
        Relay = new IPEndPoint(IPAddress.Loopback, 4433),
        CatalogDraft = catalogDraft,
    };

    [Fact]
    public void CatalogDraft01_IsRejected()
    {
        Action act = () => _ = new MoqSenderContext(With(MsfDraft.Draft01));
        act.Should().Throw<ArgumentException>().WithMessage("*Draft01*");
    }

    [Fact]
    public void CatalogDraft00_IsAccepted()
    {
        Action act = () => _ = new MoqSenderContext(With(MsfDraft.Draft00));
        act.Should().NotThrow();
    }
}
