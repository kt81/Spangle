using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Text;
using Spangle.Codecs.Id3;
using Spangle.Containers.ISOBMFF;
using Spangle.Containers.M2TS;
using Spangle.Spinner;

namespace Spangle.Tests.Spinner;

/// <summary>
/// Timed metadata phase 1: AMF0 data events become ID3 TXXX tags (canonical form),
/// carried as stream_type 0x15 PES in TS and as ID3-in-emsg in CMAF.
/// </summary>
public class TimedMetadataTests
{
    // AMF0: string "onTextData", object { text: "hello" }
    private static readonly byte[] s_amfEvent =
    [
        0x02, 0x00, 0x0A, .. "onTextData"u8,
        0x03,
        0x00, 0x04, .. "text"u8,
        0x02, 0x00, 0x05, .. "hello"u8,
        0x00, 0x00, 0x09,
    ];

    [Fact]
    public void TxxxTagRoundTrips()
    {
        byte[] tag = Id3Tag.BuildTxxx("onTextData", """{"text":"hello"}""");

        Encoding.ASCII.GetString(tag, 0, 3).Should().Be("ID3");
        tag[3].Should().Be(4, "ID3v2.4");
        int tagSize = (tag[6] << 21) | (tag[7] << 14) | (tag[8] << 7) | tag[9];
        (10 + tagSize).Should().Be(tag.Length, "the sync-safe size covers everything after the header");

        Encoding.ASCII.GetString(tag, 10, 4).Should().Be("TXXX");
        tag[20].Should().Be(0x03, "UTF-8 encoding marker");
        Encoding.UTF8.GetString(tag, 21, tag.Length - 21).Should()
            .Be("onTextData\0" + """{"text":"hello"}""");
    }

    [Fact]
    public async Task SpinnerTurnsAmfEventsIntoId3AndPassesMediaThrough()
    {
        var outPipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));
        var spinner = new AmfDataToId3Spinner(outPipe.Writer, CancellationToken.None);
        spinner.BeginSpin();

        // a video frame followed by a data event
        byte[] video = [0x00, 0x00, 0x00, 0x02, 0x65, 0x11];
        MediaFrameHeader.Write(spinner.Intake, MediaFrameKind.Video, MediaFrameFlags.KeyFrame,
            (uint)VideoCodec.H264, 0, video.Length, 500);
        spinner.Intake.Write(video);
        MediaFrameHeader.Write(spinner.Intake, MediaFrameKind.Data, MediaFrameFlags.None,
            (uint)DataCodec.Amf0, 0, s_amfEvent.Length, 1234);
        spinner.Intake.Write(s_amfEvent);
        await spinner.Intake.FlushAsync();
        await spinner.Intake.CompleteAsync();

        var frames = await ReadAllFrames(outPipe.Reader);
        frames.Should().HaveCount(2);

        (MediaFrameHeader header, byte[] payload) = frames[0];
        header.Kind.Should().Be(MediaFrameKind.Video);
        header.Timestamp.Should().Be(500u);
        payload.Should().Equal(video, "media frames pass through unchanged");

        (header, payload) = frames[1];
        header.Kind.Should().Be(MediaFrameKind.Data);
        header.DataCodec.Should().Be(DataCodec.Id3);
        header.Timestamp.Should().Be(1234u, "the event keeps its media-timeline timestamp");
        string text = Encoding.UTF8.GetString(payload);
        text.Should().Contain("onTextData").And.Contain("""{"text":"hello"}""");
    }

    [Fact]
    public void PmtAnnouncesTheTimedId3Stream()
    {
        var muxer = new M2TSWriter { VideoCodec = VideoCodec.H264, HasAudio = true, HasTimedId3 = true };
        var ts = new ArrayBufferWriter<byte>();
        muxer.WriteProgramTables(ts);

        // packet 2 is the PMT; section starts after header(4) + pointer(1)
        ReadOnlySpan<byte> pmt = ts.WrittenSpan.Slice(M2TSWriter.PacketSize + 5);
        int sectionLength = ((pmt[1] & 0x0F) << 8) | pmt[2];
        sectionLength.Should().Be(9 + 5 + 5 + 5 + 15 + 4, "video + audio + ID3-with-descriptor");

        // the ID3 entry: stream_type 0x15 on PidData with the metadata_descriptor
        int dataEntry = 12 + 5 + 5;
        pmt[dataEntry].Should().Be(0x15);
        ushort pid = (ushort)(((pmt[dataEntry + 1] & 0x1F) << 8) | pmt[dataEntry + 2]);
        pid.Should().Be(M2TSWriter.PidData);
        pmt[dataEntry + 5].Should().Be(0x26, "metadata_descriptor tag");
        Encoding.ASCII.GetString(pmt.Slice(dataEntry + 9, 4)).Should().Be("ID3 ");
    }

    [Fact]
    public void EmsgBoxCarriesTheId3Tag()
    {
        var packager = new CmafPackager(
            new CmafVideoTrack { Codec = VideoCodec.H264, ConfigRecord = new byte[8], Width = 640, Height = 360 },
            audio: null);
        byte[] id3 = Id3Tag.BuildTxxx("onTextData", "x");

        using var stream = new MemoryStream();
        packager.BuildFragment(0,
            [new CmafSample { Data = new byte[4], Duration = 3000, CompositionOffset = 0, IsSync = true }],
            0, [], stream,
            [new CmafEvent { TimeMs = 1234, Id3 = id3 }]);
        byte[] fragment = stream.ToArray();

        int emsgAt = IndexOf(fragment, "emsg"u8);
        emsgAt.Should().BeGreaterThan(0);
        int moofAt = IndexOf(fragment, "moof"u8);
        emsgAt.Should().BeLessThan(moofAt, "emsg precedes the moof");
        IndexOf(fragment, "https://aomedia.org/emsg/ID3"u8).Should().BeGreaterThan(0);
        IndexOf(fragment, "TXXX"u8).Should().BeGreaterThan(0, "the ID3 tag rides in message_data");

        // presentation_time (64-bit, after version/flags + timescale)
        ReadOnlySpan<byte> emsg = fragment.AsSpan(emsgAt + 4);
        ulong presentationTime = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(emsg[8..]);
        presentationTime.Should().Be(1234u);
    }

    // =======================================================================

    private static int IndexOf(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern) => data.IndexOf(pattern);

    private static async Task<List<(MediaFrameHeader Header, byte[] Payload)>> ReadAllFrames(PipeReader reader)
    {
        ReadResult result = await reader.ReadAsync();
        while (!result.IsCompleted)
        {
            reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
            result = await reader.ReadAsync();
        }

        byte[] data = result.Buffer.ToArray();
        reader.AdvanceTo(result.Buffer.End);

        var frames = new List<(MediaFrameHeader, byte[])>();
        var pos = 0;
        while (pos < data.Length)
        {
            var header = MemoryMarshal.Read<MediaFrameHeader>(data.AsSpan(pos, MediaFrameHeader.Size));
            pos += MediaFrameHeader.Size;
            frames.Add((header, data.AsSpan(pos, header.Length).ToArray()));
            pos += header.Length;
        }
        return frames;
    }
}
