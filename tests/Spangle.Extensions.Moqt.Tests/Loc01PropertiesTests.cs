using System.Buffers;
using Spangle.Net.Moqt.Wire;

namespace Spangle.Extensions.Moqt.Tests;

/// <summary>
/// LOC-01 header extensions (draft-ietf-moq-loc-01 §2.3) — the version every implementation we can
/// test against actually speaks. The container is thin, so what can go wrong is an ID or the parity
/// that comes with it; both are pinned here against the draft and against moq5's constants.
/// </summary>
public class Loc01PropertiesTests
{
    [Theory]
    // The draft's IDs — and, independently, the constants in moq5's C implementation
    // (openmoq/moq5, media/loc/src/loc.c), which is the only third party that can tell us we read
    // the draft the same way it did:
    //   #define LOC01_CAPTURE_TIMESTAMP   0x02u
    //   #define LOC01_VIDEO_FRAME_MARKING 0x04u
    //   #define LOC01_AUDIO_LEVEL         0x06u
    //   #define LOC01_VIDEO_CONFIG        0x0du
    [InlineData(Loc01Properties.CaptureTimestampId, 0x02UL, false)]
    [InlineData(Loc01Properties.VideoFrameMarkingId, 0x04UL, false)]
    [InlineData(Loc01Properties.AudioLevelId, 0x06UL, false)]
    [InlineData(Loc01Properties.VideoConfigId, 0x0DUL, true)]
    public void RegisteredIds_MatchTheDraftAndMoq5(ulong actual, ulong expected, bool carriesBytes)
    {
        actual.Should().Be(expected);
        ((actual & 1) == 1).Should().Be(carriesBytes, "the ID's parity is what selects the value's shape");
    }

    [Fact]
    public void CaptureTimestamp_IsAlwaysWallClock()
    {
        // -01 has no timescale, so there is no second reading to get wrong: the number is
        // microseconds since the Unix epoch and nothing else (§2.3.1.1).
        MoqKeyValuePair property = Loc01Properties.CaptureTimestamp(1_700_000_000_000_000);
        property.Type.Should().Be(Loc01Properties.CaptureTimestampId);
        property.IsBytes.Should().BeFalse();
        property.VarintValue.Should().Be(1_700_000_000_000_000UL);
    }

    [Fact]
    public void Read_PicksOutEveryKnownExtension()
    {
        byte[] avcC = [0x01, 0x64, 0x00, 0x1F];

        Loc01Metadata metadata = Loc01Metadata.Read(
        [
            Loc01Properties.CaptureTimestamp(1_700_000_000_000_000),
            Loc01Properties.VideoFrameMarking(0x18),
            Loc01Properties.AudioLevel(0x7F),
            Loc01Properties.VideoConfig(avcC),
        ]);

        metadata.CaptureTimestamp.Should().Be(1_700_000_000_000_000UL);
        metadata.VideoFrameMarking.Should().Be(0x18UL);
        metadata.AudioLevel.Should().Be((byte)0x7F);
        metadata.VideoConfig.ToArray().Should().Equal(avcC);
    }

    [Fact]
    public void Read_IgnoresExtensionsItDoesNotKnow()
    {
        // The registry is open and other specifications register into it (§2.3), so an unknown ID
        // is somebody else's extension, not a malformed frame.
        Loc01Metadata metadata = Loc01Metadata.Read(
            [Loc01Properties.CaptureTimestamp(5), MoqKeyValuePair.Varint(0x40, 1)]);

        metadata.CaptureTimestamp.Should().Be(5UL);
        metadata.VideoConfig.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Extensions_EncodeToTheBytesTheDraftDescribes()
    {
        var buffer = new ArrayBufferWriter<byte>();
        KeyValuePairCodec.WriteList(new MoqWriter(buffer),
            [Loc01Properties.CaptureTimestamp(1_000), Loc01Properties.VideoConfig([0x01, 0x64])]);

        buffer.WrittenSpan.ToArray().Should().Equal(
        [
            0x02,       // Capture Timestamp, ID delta 2 - 0 = 2 (even: no length follows)
            0x83, 0xE8, // 1000 as a varint: 2 bytes, so a 0b10xxxxxx prefix over 0x03E8
            0x0B,       // Video Config, ID delta 13 - 2 = 11 (odd: a length follows)
            0x02,       // length 2
            0x01, 0x64, // the extradata itself
        ]);
    }

    [Fact]
    public void TheTwoDraftsDoNotInteroperate()
    {
        // Kept as an executable fact because it is the whole reason these are two implementations
        // and not one with a version switch. A publisher has to know which draft its subscriber
        // speaks; there is no encoding that satisfies both.
        Loc01Properties.CaptureTimestampId.Should().NotBe(Loc03Properties.TimestampId);
        Loc01Properties.VideoFrameMarkingId.Should().NotBe(Loc03Properties.VideoFrameMarkingId);
        Loc01Properties.AudioLevelId.Should().NotBe(Loc03Properties.AudioLevelId);

        // Frame marking did not just move — it changed which side of the parity rule it sits on,
        // so the value's shape differs too, not only its ID.
        Loc01Properties.VideoFrameMarking(0x18).IsBytes.Should().BeFalse("-01 carries it as a varint");
        Loc03Properties.VideoFrameMarking([0x18]).IsBytes.Should().BeTrue("-03 carries it as a byte string");

        // Video Config is the one extension that came through the revisions untouched.
        Loc01Properties.VideoConfigId.Should().Be(Loc03Properties.VideoConfigId);
    }
}
