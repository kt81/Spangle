using Spangle.Net.Moqt.Wire;

namespace Spangle.Extensions.Moqt.Tests;

/// <summary>
/// The draft-cenzano-moq-media-interop packaging: which Extension Headers a frame carries, their
/// key parity (even = varint, odd = byte string), and the packed metadata blob's field order.
/// </summary>
public class MoqMediaInteropTests
{
    [Theory]
    [InlineData("live/", false, "live/video0")]
    [InlineData("live/", true, "live/audio0")]
    [InlineData("20260714152057", false, "20260714152057video0")]
    public void TrackName_AppendsTheMediaSuffix(string prefix, bool isAudio, string expected)
    {
        MoqMediaInterop.TrackName(prefix, isAudio).Should().Be(expected);
    }

    [Fact]
    public void VideoH264_CarriesMediaTypeAndPackedMetadata()
    {
        IReadOnlyList<MoqKeyValuePair> headers = MoqMediaInterop.VideoH264AvccExtensions(
            seqId: 7, pts: 90_000, dts: 87_000, timebase: 90_000, duration: 3_000, wallclock: 1_700_000_000_000);

        headers.Should().HaveCount(2, "no extradata was supplied");

        // MEDIA_TYPE is an even key, so it carries a bare varint.
        headers[0].Type.Should().Be(0x0AUL);
        headers[0].IsBytes.Should().BeFalse();
        headers[0].VarintValue.Should().Be(MoqMediaInterop.MediaTypeVideoH264Avcc);

        // The metadata is an odd key, so a byte string: seqId, pts, dts, timebase, duration, wallclock.
        headers[1].Type.Should().Be(0x15UL);
        headers[1].IsBytes.Should().BeTrue();
        MoqMediaInterop.UnpackVarints(headers[1].Bytes, 6)
            .Should().Equal(7UL, 90_000UL, 87_000UL, 90_000UL, 3_000UL, 1_700_000_000_000UL);
    }

    [Fact]
    public void VideoH264_CarriesAvcCExtradataWhenSupplied()
    {
        byte[] avcC = [0x01, 0x64, 0x00, 0x1F, 0xFF, 0xE1, 0x00, 0x04];
        IReadOnlyList<MoqKeyValuePair> headers = MoqMediaInterop.VideoH264AvccExtensions(
            seqId: 0, pts: 0, dts: 0, timebase: 90_000, duration: 3_000, wallclock: 1, avcCExtradata: avcC);

        headers.Should().HaveCount(3);
        headers[2].Type.Should().Be(0x0DUL);
        headers[2].Bytes.ToArray().Should().Equal(avcC, "the avcC record rides verbatim as extradata");
    }

    [Fact]
    public void AudioAacLc_CarriesMediaTypeAndPackedMetadata()
    {
        IReadOnlyList<MoqKeyValuePair> headers = MoqMediaInterop.AudioAacLcExtensions(
            seqId: 3, pts: 1_024, timebase: 44_100, sampleFreq: 44_100, numChannels: 2, duration: 1_024,
            wallclock: 1_700_000_000_000);

        headers[0].Type.Should().Be(0x0AUL);
        headers[0].VarintValue.Should().Be(MoqMediaInterop.MediaTypeAudioAacLc);

        headers[1].Type.Should().Be(0x13UL);
        MoqMediaInterop.UnpackVarints(headers[1].Bytes, 7)
            .Should().Equal(3UL, 1_024UL, 44_100UL, 44_100UL, 2UL, 1_024UL, 1_700_000_000_000UL);
    }

    [Fact]
    public void AudioOpus_UsesItsOwnMediaTypeAndHeader()
    {
        IReadOnlyList<MoqKeyValuePair> headers = MoqMediaInterop.AudioOpusExtensions(
            seqId: 1, pts: 960, timebase: 48_000, sampleFreq: 48_000, numChannels: 2, duration: 960, wallclock: 5);

        headers[0].VarintValue.Should().Be(MoqMediaInterop.MediaTypeAudioOpus);
        headers[1].Type.Should().Be(0x0FUL);
        MoqMediaInterop.UnpackVarints(headers[1].Bytes, 7)
            .Should().Equal(1UL, 960UL, 48_000UL, 48_000UL, 2UL, 960UL, 5UL);
    }
}
