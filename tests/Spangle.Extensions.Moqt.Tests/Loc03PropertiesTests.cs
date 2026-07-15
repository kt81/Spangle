using System.Buffers;
using Spangle.Net.Moqt.Wire;

namespace Spangle.Extensions.Moqt.Tests;

/// <summary>
/// LOC properties (draft-ietf-moq-loc-03 §2.3). The container is thin enough that most of what can
/// go wrong is a registered ID or the even/odd parity that goes with it, so those are pinned
/// against the draft here — including on the wire, since a property that encodes to the wrong
/// bytes is the one failure a round trip through our own code would never show.
/// </summary>
public class Loc03PropertiesTests
{
    [Theory]
    // Every ID the draft registers, with the parity the draft assigns it. The parity is not a
    // convention we picked: it decides how the value is framed, so an ID with the wrong one
    // silently changes the shape of the frame that follows it.
    [InlineData(Loc03Properties.TimescaleId, 0x08UL, false)]
    [InlineData(Loc03Properties.VideoFrameMarkingId, 0x09UL, true)]
    [InlineData(Loc03Properties.TimestampId, 0x0AUL, false)]
    [InlineData(Loc03Properties.AudioLevelId, 0x0CUL, false)]
    [InlineData(Loc03Properties.VideoConfigId, 0x0DUL, true)]
    [InlineData(Loc03Properties.AudioConfigId, 0x0FUL, true)]
    public void RegisteredIds_MatchTheDraftAndItsParity(ulong actual, ulong expected, bool carriesBytes)
    {
        actual.Should().Be(expected);
        ((actual & 1) == 1).Should().Be(carriesBytes, "the ID's parity is what selects the value's shape");
    }

    [Fact]
    public void MediaTime_SendsTheTimescaleWithTheTimestamp()
    {
        IReadOnlyList<MoqKeyValuePair> properties = Loc03Properties.MediaTime(3_000, 90_000);

        properties.Select(p => p.Type).Should().Equal([Loc03Properties.TimescaleId, Loc03Properties.TimestampId],
            "IDs are delta-encoded on the wire, so they must not descend");
        properties[0].VarintValue.Should().Be(90_000UL);
        properties[1].VarintValue.Should().Be(3_000UL);
    }

    [Fact]
    public void MediaTime_RejectsAZeroTimescale()
    {
        // Zero units per second is not a slow clock, it is a divide by zero at the far end.
        Action act = () => Loc03Properties.MediaTime(0, timescale: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ATimestampWithoutATimescale_IsWallClock()
    {
        // The same varint means two different things depending on a property that is absent, so
        // this distinction is the one the reader has to get right (§2.3.1.1).
        Loc03Metadata wall = Loc03Metadata.Read([Loc03Properties.WallClockTime(1_700_000_000_000_000)]);
        wall.Timestamp.Should().Be(1_700_000_000_000_000UL);
        wall.Timescale.Should().BeNull();
        wall.IsWallClock.Should().BeTrue();

        Loc03Metadata media = Loc03Metadata.Read(Loc03Properties.MediaTime(3_000, 90_000));
        media.Timestamp.Should().Be(3_000UL);
        media.IsWallClock.Should().BeFalse("a Timescale is present, so the timestamp is media time");
    }

    [Fact]
    public void Read_PicksOutEveryKnownProperty()
    {
        byte[] avcC = [0x01, 0x64, 0x00, 0x1F];
        byte[] marking = [0x80];

        Loc03Metadata metadata = Loc03Metadata.Read(
        [
            Loc03Properties.Timescale(Loc03Properties.MicrosecondTimescale),
            Loc03Properties.VideoFrameMarking(marking),
            Loc03Properties.Timestamp(42),
            Loc03Properties.AudioLevel(0x7F),
            Loc03Properties.VideoConfig(avcC),
            Loc03Properties.AudioConfig([0x12, 0x10]),
        ]);

        metadata.Timescale.Should().Be(Loc03Properties.MicrosecondTimescale);
        metadata.Timestamp.Should().Be(42UL);
        metadata.AudioLevel.Should().Be((byte)0x7F);
        metadata.VideoConfig.ToArray().Should().Equal(avcC);
        metadata.AudioConfig.ToArray().Should().Equal([0x12, 0x10]);
        metadata.VideoFrameMarking.ToArray().Should().Equal(marking);
    }

    [Fact]
    public void Read_IgnoresPropertiesItDoesNotKnow()
    {
        // The registry is open and other specifications register into it (§2.3), so an unknown ID
        // is somebody else's property, not a malformed frame.
        Loc03Metadata metadata = Loc03Metadata.Read(
        [
            Loc03Properties.Timestamp(7),
            MoqKeyValuePair.Varint(0x40, 1),
            MoqKeyValuePair.FromBytes(0x41, [0xAB]),
        ]);

        metadata.Timestamp.Should().Be(7UL);
        metadata.VideoConfig.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Read_OfNothing_IsAllAbsent()
    {
        // Every LOC property is optional, so a frame with none is legal, not empty-by-accident.
        Loc03Metadata metadata = Loc03Metadata.Read([]);
        metadata.Timestamp.Should().BeNull();
        metadata.Timescale.Should().BeNull();
        metadata.AudioLevel.Should().BeNull();
        metadata.IsWallClock.Should().BeFalse("there is no timestamp to be wall-clock");
        metadata.VideoConfig.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Properties_EncodeToTheBytesTheDraftDescribes()
    {
        // vi64 is MOQT's own variable-length integer, and the draft says LOC values are vi64 — so a
        // property is exactly its delta-encoded ID followed by that value, with a length prefix
        // only when the ID is odd. Pinning the bytes keeps this honest against our own reader.
        var buffer = new ArrayBufferWriter<byte>();
        KeyValuePairCodec.WriteList(new MoqWriter(buffer),
            [.. Loc03Properties.MediaTime(3_000, 90_000), Loc03Properties.VideoConfig([0x01, 0x64])]);

        buffer.WrittenSpan.ToArray().Should().Equal(
        [
            0x08,             // Timescale, ID delta 0x08 - 0 = 0x08 (even: no length follows)
            0xC1, 0x5F, 0x90, // 90000 as a vi64: 3 bytes, so a 0b110xxxxx prefix over 0x015F90
            0x02,             // Timestamp, ID delta 0x0A - 0x08 = 0x02
            0x8B, 0xB8,       // 3000 as a vi64: 2 bytes, 0b10xxxxxx prefix
            0x03,             // Video Config, ID delta 0x0D - 0x0A = 0x03 (odd: a length follows)
            0x02,             // length 2
            0x01, 0x64,       // the extradata itself
        ]);
    }
}
