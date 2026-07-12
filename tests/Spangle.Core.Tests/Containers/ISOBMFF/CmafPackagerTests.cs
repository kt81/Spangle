using System.Buffers.Binary;
using System.Text;
using Spangle.Containers.ISOBMFF;

namespace Spangle.Tests.Containers.ISOBMFF;

/// <summary>
/// CmafPackager builds fragmented MP4: one init segment (ftyp+moov) and media
/// segments (styp+moof+mdat). These tests parse the emitted box structure back
/// out of the raw bytes and verify layout, sample entries, trun bookkeeping and
/// the moof-relative data offsets.
/// </summary>
public class CmafPackagerTests
{
    private static readonly byte[] s_avcConfig = [0x01, 0x64, 0x00, 0x1F, 0xFF, 0xE1, 0x00, 0x04];
    private static readonly byte[] s_aacConfig = [0x12, 0x10]; // AAC-LC, 44.1 kHz, stereo

    private static CmafPackager CreateH264AacPackager() => new(
        new CmafVideoTrack { Codec = VideoCodec.H264, ConfigRecord = s_avcConfig, Width = 640, Height = 360 },
        new CmafAudioTrack { Codec = AudioCodec.AAC, Config = s_aacConfig, SampleRate = 44100, ChannelCount = 2 });

    // ---- init segment ----

    [Fact]
    public void InitSegmentForH264AndAacHasExpectedBoxTree()
    {
        byte[] init = CreateH264AacPackager().BuildInitSegment();

        List<Box> top = ParseBoxes(init, 0, init.Length);
        top.Select(b => b.Type).Should().Equal("ftyp", "moov");

        // ftyp: major brand iso6 with the CMAF structural brand
        Box ftyp = top[0];
        FourCcAt(init, ftyp.PayloadStart).Should().Be("iso6");
        Encoding.ASCII.GetString(init, ftyp.PayloadStart, ftyp.PayloadEnd - ftyp.PayloadStart)
            .Should().Contain("cmfc");

        List<Box> moov = ParseBoxes(init, top[1].PayloadStart, top[1].PayloadEnd);
        moov.Select(b => b.Type).Should().Equal("mvhd", "trak", "trak", "mvex");

        // mvex declares both tracks, in track-id order
        List<Box> mvex = ParseBoxes(init, moov[3].PayloadStart, moov[3].PayloadEnd);
        mvex.Select(b => b.Type).Should().Equal("trex", "trex");
        ReadUInt32(init, mvex[0].PayloadStart + 4).Should().Be(CmafPackager.VideoTrackId);
        ReadUInt32(init, mvex[1].PayloadStart + 4).Should().Be(CmafPackager.AudioTrackId);
    }

    [Fact]
    public void InitSegmentCarriesAvc1SampleEntryWithVerbatimAvcC()
    {
        byte[] init = CreateH264AacPackager().BuildInitSegment();
        Box videoTrak = TrakBoxes(init)[0];

        Box mdhd = Descend(init, videoTrak, "mdia")[0];
        mdhd.Type.Should().Be("mdhd");
        ReadUInt32(init, mdhd.PayloadStart + 12).Should().Be(90000u, "video uses the 90 kHz PTS timescale");

        Box stsd = StsdOf(init, videoTrak);
        ReadUInt32(init, stsd.PayloadStart + 4).Should().Be(1u, "one sample entry");

        List<Box> entries = ParseBoxes(init, stsd.PayloadStart + 8, stsd.PayloadEnd);
        entries.Should().ContainSingle().Which.Type.Should().Be("avc1");
        Box avc1 = entries[0];

        // VisualSampleEntry: width/height after 6 reserved + 2 dri + 16 pre_defined/reserved
        ReadUInt16(init, avc1.PayloadStart + 24).Should().Be(640);
        ReadUInt16(init, avc1.PayloadStart + 26).Should().Be(360);

        // the codec config box follows the 78-byte fixed VisualSampleEntry fields
        List<Box> configBoxes = ParseBoxes(init, avc1.PayloadStart + 78, avc1.PayloadEnd);
        configBoxes.Should().ContainSingle().Which.Type.Should().Be("avcC");
        init.AsSpan(configBoxes[0].PayloadStart, configBoxes[0].PayloadEnd - configBoxes[0].PayloadStart)
            .ToArray().Should().Equal(s_avcConfig, "the avcC record is carried verbatim");
    }

    [Fact]
    public void InitSegmentCarriesMp4aSampleEntryWithEsdsHoldingTheAsc()
    {
        byte[] init = CreateH264AacPackager().BuildInitSegment();
        Box audioTrak = TrakBoxes(init)[1];

        Box mdhd = Descend(init, audioTrak, "mdia")[0];
        ReadUInt32(init, mdhd.PayloadStart + 12).Should().Be(44100u, "the audio timescale is the sample rate");

        Box stsd = StsdOf(init, audioTrak);
        List<Box> entries = ParseBoxes(init, stsd.PayloadStart + 8, stsd.PayloadEnd);
        entries.Should().ContainSingle().Which.Type.Should().Be("mp4a");
        Box mp4a = entries[0];

        // AudioSampleEntry: channelcount after 6 reserved + 2 dri + 8 reserved
        ReadUInt16(init, mp4a.PayloadStart + 16).Should().Be(2);
        ReadUInt32(init, mp4a.PayloadStart + 24).Should().Be(44100u << 16, "samplerate is 16.16 fixed");

        // esds follows the 28-byte fixed AudioSampleEntry fields
        List<Box> esds = ParseBoxes(init, mp4a.PayloadStart + 28, mp4a.PayloadEnd);
        esds.Should().ContainSingle().Which.Type.Should().Be("esds");

        // DecSpecificInfo (tag 0x05) carries the AudioSpecificConfig verbatim
        ReadOnlySpan<byte> dsi = [0x05, (byte)s_aacConfig.Length, .. s_aacConfig];
        init.AsSpan(esds[0].PayloadStart, esds[0].PayloadEnd - esds[0].PayloadStart)
            .IndexOf(dsi).Should().BeGreaterThan(0);
    }

    [Fact]
    public void VideoOnlyInitSegmentHasOneTrackAndOneTrex()
    {
        var packager = new CmafPackager(
            new CmafVideoTrack { Codec = VideoCodec.H264, ConfigRecord = s_avcConfig, Width = 640, Height = 360 },
            audio: null);
        byte[] init = packager.BuildInitSegment();

        List<Box> top = ParseBoxes(init, 0, init.Length);
        List<Box> moov = ParseBoxes(init, top[1].PayloadStart, top[1].PayloadEnd);
        moov.Select(b => b.Type).Should().Equal("mvhd", "trak", "mvex");
        List<Box> mvex = ParseBoxes(init, moov[2].PayloadStart, moov[2].PayloadEnd);
        mvex.Should().ContainSingle();
        ReadUInt32(init, mvex[0].PayloadStart + 4).Should().Be(CmafPackager.VideoTrackId);
    }

    [Fact]
    public void UnmappedVideoCodecThrowsNotSupported()
    {
        var packager = new CmafPackager(
            new CmafVideoTrack { Codec = VideoCodec.VP9, ConfigRecord = [], Width = 640, Height = 360 },
            audio: null);

        Action act = () => packager.BuildInitSegment();
        act.Should().Throw<NotSupportedException>().WithMessage("*VP9*");
    }

    // ---- media segments ----

    [Fact]
    public void FragmentEmitsStypMoofMdatInOrderWithCorrectTruns()
    {
        CmafPackager packager = CreateH264AacPackager();

        CmafSample[] videoSamples =
        [
            MakeSample([0xA0, 0xA1, 0xA2, 0xA3], duration: 3000, compositionOffset: 3000, isSync: true),
            MakeSample([0xB0, 0xB1], duration: 3000, compositionOffset: -1500, isSync: false),
        ];
        CmafSample[] audioSamples = [MakeSample([0xC0, 0xC1, 0xC2], duration: 1024, compositionOffset: 0, isSync: true)];

        using var stream = new MemoryStream();
        packager.BuildFragment(90_000, videoSamples, 44_100, audioSamples, stream);
        byte[] fragment = stream.ToArray();

        List<Box> top = ParseBoxes(fragment, 0, fragment.Length);
        top.Select(b => b.Type).Should().Equal("styp", "moof", "mdat");
        Box moof = top[1];
        Box mdat = top[2];

        List<Box> moofChildren = ParseBoxes(fragment, moof.PayloadStart, moof.PayloadEnd);
        moofChildren.Select(b => b.Type).Should().Equal("mfhd", "traf", "traf");
        ReadUInt32(fragment, moofChildren[0].PayloadStart + 4).Should().Be(1u, "the first fragment is sequence 1");

        // video traf: tfhd/tfdt/trun with per-sample fields and moof-relative data offset
        List<Box> videoTraf = ParseBoxes(fragment, moofChildren[1].PayloadStart, moofChildren[1].PayloadEnd);
        videoTraf.Select(b => b.Type).Should().Equal("tfhd", "tfdt", "trun");
        ReadUInt32(fragment, videoTraf[0].PayloadStart + 4).Should().Be(CmafPackager.VideoTrackId);
        ReadUInt64(fragment, videoTraf[1].PayloadStart + 4).Should().Be(90_000u, "tfdt carries the video base time");

        int trun = videoTraf[2].PayloadStart;
        ReadUInt32(fragment, trun).Should().Be(0x01_000F01u, "trun v1 with data-offset|duration|size|flags|cts");
        ReadUInt32(fragment, trun + 4).Should().Be(2u, "sample count");
        int moofStart = moof.PayloadStart - 8;
        ReadUInt32(fragment, trun + 8).Should().Be((uint)(mdat.PayloadStart - moofStart),
            "data_offset points at the mdat payload, relative to the moof start");
        ReadUInt32(fragment, trun + 12).Should().Be(3000u);
        ReadUInt32(fragment, trun + 16).Should().Be(4u, "size of the first sample");
        ReadUInt32(fragment, trun + 20).Should().Be(0x02000000u, "keyframe sample flags");
        ReadUInt32(fragment, trun + 24).Should().Be(3000u, "composition offset of the first sample");
        ReadUInt32(fragment, trun + 32).Should().Be(2u, "size of the second sample");
        ReadUInt32(fragment, trun + 36).Should().Be(0x01010000u, "non-sync sample flags");
        BinaryPrimitives.ReadInt32BigEndian(fragment.AsSpan(trun + 40)).Should().Be(-1500,
            "trun v1 composition offsets are signed");

        // audio traf: its data offset skips the video bytes
        List<Box> audioTraf = ParseBoxes(fragment, moofChildren[2].PayloadStart, moofChildren[2].PayloadEnd);
        audioTraf.Select(b => b.Type).Should().Equal("tfhd", "tfdt", "trun");
        ReadUInt32(fragment, audioTraf[0].PayloadStart + 4).Should().Be(CmafPackager.AudioTrackId);
        ReadUInt64(fragment, audioTraf[1].PayloadStart + 4).Should().Be(44_100u);
        int audioTrun = audioTraf[2].PayloadStart;
        ReadUInt32(fragment, audioTrun).Should().Be(0x00_000701u, "trun v0 with data-offset|duration|size|flags");
        ReadUInt32(fragment, audioTrun + 8).Should().Be((uint)(mdat.PayloadStart - moofStart + 6),
            "the audio payload starts after the 4+2 video bytes");

        // mdat holds video samples then audio samples, verbatim
        fragment.AsSpan(mdat.PayloadStart, mdat.PayloadEnd - mdat.PayloadStart).ToArray()
            .Should().Equal([0xA0, 0xA1, 0xA2, 0xA3, 0xB0, 0xB1, 0xC0, 0xC1, 0xC2]);
    }

    [Fact]
    public void SequenceNumbersIncrementAndBaseTimesAdvanceAcrossFragments()
    {
        CmafPackager packager = CreateH264AacPackager();
        CmafSample[] video = [MakeSample([0x01], duration: 3000, compositionOffset: 0, isSync: true)];
        CmafSample[] audio = [MakeSample([0x02], duration: 1024, compositionOffset: 0, isSync: true)];

        (uint Sequence, ulong VideoTfdt, ulong AudioTfdt) BuildAndInspect(ulong videoBase, ulong audioBase)
        {
            using var stream = new MemoryStream();
            packager.BuildFragment(videoBase, video, audioBase, audio, stream);
            byte[] fragment = stream.ToArray();

            List<Box> top = ParseBoxes(fragment, 0, fragment.Length);
            List<Box> moof = ParseBoxes(fragment, top[1].PayloadStart, top[1].PayloadEnd);
            List<Box> videoTraf = ParseBoxes(fragment, moof[1].PayloadStart, moof[1].PayloadEnd);
            List<Box> audioTraf = ParseBoxes(fragment, moof[2].PayloadStart, moof[2].PayloadEnd);
            return (ReadUInt32(fragment, moof[0].PayloadStart + 4),
                ReadUInt64(fragment, videoTraf[1].PayloadStart + 4),
                ReadUInt64(fragment, audioTraf[1].PayloadStart + 4));
        }

        BuildAndInspect(0, 0).Should().Be((1u, 0ul, 0ul));
        BuildAndInspect(180_000, 88_200).Should().Be((2u, 180_000ul, 88_200ul));
        BuildAndInspect(360_000, 176_400).Should().Be((3u, 360_000ul, 176_400ul));
    }

    [Fact]
    public void AudioTrafIsOmittedWhenNoAudioSamplesArrive()
    {
        CmafPackager packager = CreateH264AacPackager();

        using var stream = new MemoryStream();
        packager.BuildFragment(0, [MakeSample([0x01], 3000, 0, isSync: true)], 0, [], stream);
        byte[] fragment = stream.ToArray();

        List<Box> top = ParseBoxes(fragment, 0, fragment.Length);
        List<Box> moof = ParseBoxes(fragment, top[1].PayloadStart, top[1].PayloadEnd);
        moof.Select(b => b.Type).Should().Equal("mfhd", "traf");
        List<Box> traf = ParseBoxes(fragment, moof[1].PayloadStart, moof[1].PayloadEnd);
        ReadUInt32(fragment, traf[0].PayloadStart + 4).Should().Be(CmafPackager.VideoTrackId);
    }

    [Fact]
    public void AudioOnlyFragmentHasASingleAudioTraf()
    {
        var packager = new CmafPackager(video: null,
            new CmafAudioTrack { Codec = AudioCodec.AAC, Config = s_aacConfig, SampleRate = 44100, ChannelCount = 2 });

        using var stream = new MemoryStream();
        packager.BuildFragment(0, [], 22_050, [MakeSample([0x0A, 0x0B], 1024, 0, isSync: true)], stream);
        byte[] fragment = stream.ToArray();

        List<Box> top = ParseBoxes(fragment, 0, fragment.Length);
        top.Select(b => b.Type).Should().Equal("styp", "moof", "mdat");
        List<Box> moof = ParseBoxes(fragment, top[1].PayloadStart, top[1].PayloadEnd);
        moof.Select(b => b.Type).Should().Equal("mfhd", "traf");
        List<Box> traf = ParseBoxes(fragment, moof[1].PayloadStart, moof[1].PayloadEnd);
        ReadUInt32(fragment, traf[0].PayloadStart + 4).Should().Be(CmafPackager.AudioTrackId);
        ReadUInt64(fragment, traf[1].PayloadStart + 4).Should().Be(22_050u);
        fragment.AsSpan(top[2].PayloadStart, top[2].PayloadEnd - top[2].PayloadStart).ToArray()
            .Should().Equal([0x0A, 0x0B]);
    }

    // =======================================================================

    private readonly record struct Box(string Type, int PayloadStart, int PayloadEnd);

    /// <summary>Parses a run of ISO-BMFF boxes; they must tile [start, end) exactly.</summary>
    private static List<Box> ParseBoxes(ReadOnlySpan<byte> data, int start, int end)
    {
        var boxes = new List<Box>();
        int pos = start;
        while (pos < end)
        {
            (pos + 8).Should().BeLessThanOrEqualTo(end, "a box header must fit in the remaining range");
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
            size.Should().BeGreaterThanOrEqualTo(8, "compact (32-bit, typed) box headers are expected");
            (pos + size).Should().BeLessThanOrEqualTo(end, "a box must not overrun its container");
            boxes.Add(new Box(FourCcAt(data, pos + 4), pos + 8, pos + size));
            pos += size;
        }
        return boxes;
    }

    private static string FourCcAt(ReadOnlySpan<byte> data, int offset) =>
        Encoding.ASCII.GetString(data.Slice(offset, 4));

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);

    private static ulong ReadUInt64(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt64BigEndian(data[offset..]);

    private static List<Box> TrakBoxes(byte[] init)
    {
        List<Box> top = ParseBoxes(init, 0, init.Length);
        return ParseBoxes(init, top[1].PayloadStart, top[1].PayloadEnd)
            .Where(b => b.Type == "trak").ToList();
    }

    /// <summary>The children of the first <paramref name="childType"/> child of <paramref name="parent"/>.</summary>
    private static List<Box> Descend(byte[] data, Box parent, string childType)
    {
        Box child = ParseBoxes(data, parent.PayloadStart, parent.PayloadEnd).First(b => b.Type == childType);
        return ParseBoxes(data, child.PayloadStart, child.PayloadEnd);
    }

    private static Box StsdOf(byte[] init, Box trak)
    {
        List<Box> mdia = Descend(init, trak, "mdia");
        List<Box> stbl = Descend(init, mdia.First(b => b.Type == "minf"), "stbl");
        return stbl.First(b => b.Type == "stsd");
    }

    private static CmafSample MakeSample(byte[] data, uint duration, int compositionOffset, bool isSync) => new()
    {
        Data = data,
        Duration = duration,
        CompositionOffset = compositionOffset,
        IsSync = isSync,
    };
}
