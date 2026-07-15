using System.Text;
using System.Text.Json;

namespace Spangle.Extensions.Moqt.Tests;

/// <summary>
/// The MSF catalog (draft-ietf-moq-msf §5). A relay never parses this document, so unlike every
/// other thing we put on the wire there is no reference implementation in the path to catch a
/// mistake in it — the first parser to see it is the player's. That makes the rules the drafts state
/// worth pinning here: the version's JSON type, which is all that separates the two drafts a
/// consumer might expect, and the MUSTs that make a catalog parse at all.
/// </summary>
public class MsfCatalogTests
{
    private static MsfCatalog VideoAndAudio(MsfDraft draft) => new()
    {
        Draft = draft,
        GeneratedAt = 1_746_104_606_044,
        Tracks =
        [
            new MsfTrack
            {
                Name = "video0",
                Packaging = MsfPackaging.Loc,
                IsLive = true,
                Role = MsfTrackRole.Video,
                RenderGroup = 1,
                TargetLatency = 2_000,
                Codec = "avc1.64001f",
                Width = 640,
                Height = 360,
                Framerate = 30,
                Timescale = 90_000,
            },
            new MsfTrack
            {
                Name = "audio0",
                Packaging = MsfPackaging.Loc,
                IsLive = true,
                Role = MsfTrackRole.Audio,
                RenderGroup = 1,
                TargetLatency = 2_000,
                Codec = "opus",
                SampleRate = 48_000,
                ChannelConfig = "2",
            },
        ],
    };

    [Fact]
    public void Draft00_WritesTheVersionAsANumber()
    {
        // The whole of what separates the drafts, and the reason the choice is exposed at all: a
        // -00 parser type-checks this field and throws on a string.
        JsonElement root = Root(VideoAndAudio(MsfDraft.Draft00));

        root.GetProperty("version").ValueKind.Should().Be(JsonValueKind.Number);
        root.GetProperty("version").GetInt32().Should().Be(1);
    }

    [Fact]
    public void Draft01_WritesTheVersionAsAString()
    {
        JsonElement root = Root(VideoAndAudio(MsfDraft.Draft01));

        root.GetProperty("version").ValueKind.Should().Be(JsonValueKind.String, "MSF-01 §5.1.1 restates it as a String");
        root.GetProperty("version").GetString().Should().Be("1");
    }

    [Fact]
    public void ACatalog_CarriesWhatAPlayerNeedsToDecodeTheTrack()
    {
        JsonElement track = Root(VideoAndAudio(MsfDraft.Draft00)).GetProperty("tracks")[0];

        track.GetProperty("name").GetString().Should().Be("video0");
        track.GetProperty("packaging").GetString().Should().Be("loc");
        track.GetProperty("isLive").GetBoolean().Should().BeTrue();
        // LOC has no media type of its own, so this field is the only place the codec is stated.
        track.GetProperty("codec").GetString().Should().Be("avc1.64001f");
        track.GetProperty("width").GetInt32().Should().Be(640);
        track.GetProperty("height").GetInt32().Should().Be(360);
    }

    [Fact]
    public void UnsetFields_AreAbsentRatherThanNull()
    {
        // A consumer reads "field is missing" as "not declared"; a null is a value of the wrong
        // type, and a parser that type-checks (they do) rejects the track.
        JsonElement audio = Root(VideoAndAudio(MsfDraft.Draft00)).GetProperty("tracks")[1];

        audio.TryGetProperty("width", out _).Should().BeFalse("an audio track declares no width");
        audio.TryGetProperty("framerate", out _).Should().BeFalse();
        audio.TryGetProperty("label", out _).Should().BeFalse();
    }

    [Fact]
    public void IsComplete_IsWrittenOnlyWhenTheBroadcastIsOver()
    {
        Root(VideoAndAudio(MsfDraft.Draft00)).TryGetProperty("isComplete", out _)
            .Should().BeFalse("§5.1.3 forbids publishing the field as false");

        MsfCatalog ended = VideoAndAudio(MsfDraft.Draft00) with { IsComplete = true };
        Root(ended).GetProperty("isComplete").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData(MsfDraft.Draft00)]
    [InlineData(MsfDraft.Draft01)]
    public void ACatalog_RoundTrips(MsfDraft draft)
    {
        MsfCatalog original = VideoAndAudio(draft);

        MsfCatalog parsed = MsfCatalog.Parse(original.ToJsonUtf8());

        // The draft is not carried in the model, it is read back off the version field — which is
        // how a subscriber learns which document it is holding.
        parsed.Draft.Should().Be(draft);
        parsed.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void ATrackWithoutANamespace_InheritsTheCatalogTracks()
    {
        // §5.2.2. The usual case: the tracks sit beside the catalog that lists them, so the
        // namespace is stated once, on the subscription.
        MsfCatalog catalog = MsfCatalog.Parse(VideoAndAudio(MsfDraft.Draft00).ToJsonUtf8(), catalogNamespace: "vc");

        catalog.Tracks.Should().AllSatisfy(track => track.Namespace.Should().Be("vc"));
    }

    [Fact]
    public void ATracksOwnNamespace_OverridesTheInheritedOne()
    {
        MsfCatalog original = VideoAndAudio(MsfDraft.Draft00) with
        {
            Tracks = [new MsfTrack { Name = "video0", Packaging = MsfPackaging.Loc, IsLive = true, Namespace = "other" }],
        };

        MsfCatalog parsed = MsfCatalog.Parse(original.ToJsonUtf8(), catalogNamespace: "vc");

        parsed.Tracks[0].Namespace.Should().Be("other");
    }

    [Fact]
    public void TwoTracksWithOneNameInOneNamespace_AreRejected()
    {
        // §5.2.3. The name is how a subscriber asks for the track, so a duplicate makes one of the
        // two unaddressable.
        MsfCatalog clashing = VideoAndAudio(MsfDraft.Draft00) with
        {
            Tracks =
            [
                new MsfTrack { Name = "video0", Packaging = MsfPackaging.Loc, IsLive = true },
                new MsfTrack { Name = "video0", Packaging = MsfPackaging.Loc, IsLive = true },
            ],
        };

        Action act = () => clashing.ToJsonUtf8();

        act.Should().Throw<InvalidOperationException>().WithMessage("*appears twice*");
    }

    [Fact]
    public void ATargetLatencyOnATrackThatIsNotLive_IsRejected()
    {
        MsfCatalog invalid = new()
        {
            Tracks = [new MsfTrack { Name = "vod", Packaging = MsfPackaging.Loc, IsLive = false, TargetLatency = 2_000 }],
        };

        Action act = () => invalid.ToJsonUtf8();

        act.Should().Throw<InvalidOperationException>().WithMessage("*target latency*");
    }

    [Fact]
    public void ADurationOnALiveTrack_IsRejected()
    {
        MsfCatalog invalid = new()
        {
            Tracks = [new MsfTrack { Name = "live", Packaging = MsfPackaging.Loc, IsLive = true, TrackDuration = 30_000 }],
        };

        Action act = () => invalid.ToJsonUtf8();

        act.Should().Throw<InvalidOperationException>().WithMessage("*duration*");
    }

    [Fact]
    public void ACatalogWithNoTracks_IsRejected()
    {
        MsfCatalog empty = new() { Tracks = [] };

        Action act = () => empty.ToJsonUtf8();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AVersionWeDoNotUnderstand_IsNotParsed()
    {
        // §5.1.1: a subscriber MUST NOT attempt a version it does not know — the fields are not
        // promised to mean the same thing.
        byte[] future = Encoding.UTF8.GetBytes("""{"version":2,"tracks":[]}""");

        Action act = () => MsfCatalog.Parse(future);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ADeltaUpdate_IsRejectedRatherThanReadAsACatalog()
    {
        // Delta updates are not implemented; reading one as if it were complete would silently
        // drop every track it does not mention.
        byte[] delta = Encoding.UTF8.GetBytes(
            """{"version":1,"deltaUpdate":[{"op":"add","tracks":[]}],"tracks":[]}""");

        Action act = () => MsfCatalog.Parse(delta);

        act.Should().Throw<InvalidDataException>().WithMessage("*delta*");
    }

    [Fact]
    public void ATrackMissingARequiredField_IsRejected()
    {
        byte[] noPackaging = Encoding.UTF8.GetBytes("""{"version":1,"tracks":[{"name":"v","isLive":true}]}""");

        Action act = () => MsfCatalog.Parse(noPackaging);

        act.Should().Throw<InvalidDataException>().WithMessage("*packaging*");
    }

    [Fact]
    public void UnknownFields_AreIgnored()
    {
        // §5: a producer may add fields, and a parser must ignore what it does not know. Being
        // strict here would make us break against publishers that are within their rights.
        byte[] extended = Encoding.UTF8.GetBytes(
            """{"version":1,"vendorThing":42,"tracks":[{"name":"v","packaging":"loc","isLive":true,"somethingElse":"x"}]}""");

        MsfCatalog catalog = MsfCatalog.Parse(extended);

        catalog.Tracks.Should().ContainSingle().Which.Name.Should().Be("v");
    }

    private static JsonElement Root(MsfCatalog catalog) => JsonDocument.Parse(catalog.ToJsonUtf8()).RootElement;
}
