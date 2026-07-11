using System.Buffers.Binary;
using Spangle.Codecs.Opus;
using Spangle.Containers.ISOBMFF;

namespace Spangle.Tests.Codecs;

public class OpusTests
{
    [Theory]
    [InlineData(new byte[] { 0xF8, 0x00 }, 960u)]        // config 31 (CELT FB 20ms), 1 frame
    [InlineData(new byte[] { 0x80, 0x00 }, 120u)]        // config 16 (CELT NB 2.5ms), 1 frame
    [InlineData(new byte[] { 0x58, 0x00 }, 2880u)]       // config 11 (SILK WB 60ms), 1 frame
    [InlineData(new byte[] { 0x60, 0x00 }, 480u)]        // config 12 (Hybrid SWB 10ms), 1 frame
    [InlineData(new byte[] { 0xF9, 0x00 }, 1920u)]       // code 1: two 20ms frames
    [InlineData(new byte[] { 0xFB, 0x03, 0x00 }, 2880u)] // code 3: count byte = 3 frames
    public void TocDurationsAreComputed(byte[] packet, uint expectedSamples)
    {
        OpusPacket.GetSampleCount(packet).Should().Be(expectedSamples);
    }

    [Fact]
    public void OpusHeadRoundTrips()
    {
        byte[] head = OpusPacket.BuildOpusHead(2);
        head.Length.Should().Be(19);
        OpusPacket.IsOpusHead(head).Should().BeTrue();

        OpusPacket.OpusHeadInfo info = OpusPacket.ParseOpusHead(head);
        info.ChannelCount.Should().Be(2);
        info.PreSkip.Should().Be(3840);
        info.InputSampleRate.Should().Be(48000u);
        info.OutputGain.Should().Be(0);
        info.ChannelMappingFamily.Should().Be(0);
    }

    [Fact]
    public void MultichannelOpusHeadUsesMappingFamilyOne()
    {
        byte[] head = OpusPacket.BuildOpusHead(6);
        head.Length.Should().Be(19 + 2 + 6);
        OpusPacket.OpusHeadInfo info = OpusPacket.ParseOpusHead(head);
        info.ChannelCount.Should().Be(6);
        info.ChannelMappingFamily.Should().Be(1);
        head[19].Should().Be(4, "stream count for 5.1");
        head[20].Should().Be(2, "coupled count for 5.1");
    }

    [Fact]
    public void InitSegmentCarriesOpusSampleEntryAndDOps()
    {
        var packager = new CmafPackager(video: null, new CmafAudioTrack
        {
            Codec = AudioCodec.Opus,
            Config = OpusPacket.BuildOpusHead(2),
            SampleRate = OpusPacket.SampleRate,
            ChannelCount = 2,
        });
        byte[] init = packager.BuildInitSegment();

        int opusAt = IndexOfFourCc(init, "Opus");
        opusAt.Should().BeGreaterThan(0, "the sample entry box must be 'Opus'");
        int dopsAt = IndexOfFourCc(init, "dOps");
        dopsAt.Should().BeGreaterThan(opusAt, "dOps lives inside the sample entry");

        // dOps payload: Version(0), ChannelCount, PreSkip(BE), InputSampleRate(BE)
        ReadOnlySpan<byte> dops = init.AsSpan(dopsAt + 4);
        dops[0].Should().Be(0);
        dops[1].Should().Be(2);
        BinaryPrimitives.ReadUInt16BigEndian(dops[2..]).Should().Be(3840, "pre-skip is byte-swapped to BE");
        BinaryPrimitives.ReadUInt32BigEndian(dops[4..]).Should().Be(48000u);
    }

    private static int IndexOfFourCc(ReadOnlySpan<byte> data, string fourCc)
    {
        Span<byte> pattern = stackalloc byte[4];
        for (var i = 0; i < 4; i++)
        {
            pattern[i] = (byte)fourCc[i];
        }
        return data.IndexOf(pattern);
    }
}
