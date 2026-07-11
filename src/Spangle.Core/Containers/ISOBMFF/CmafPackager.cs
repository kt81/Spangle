namespace Spangle.Containers.ISOBMFF;

internal readonly struct CmafVideoTrack
{
    /// <summary>Video track timescale (90kHz, matching PTS/DTS units)</summary>
    public const uint Timescale = 90000;

    public required VideoCodec Codec { get; init; }

    /// <summary>The codec configuration record verbatim (avcC / hvcC / av1C payload)</summary>
    public required byte[] ConfigRecord { get; init; }

    public required uint Width { get; init; }
    public required uint Height { get; init; }
}

internal readonly struct CmafAudioTrack
{
    public required AudioCodec Codec { get; init; }

    /// <summary>
    /// Codec configuration verbatim: the AudioSpecificConfig for AAC (goes into esds),
    /// the OpusHead identification header for Opus (converted into dOps).
    /// </summary>
    public required byte[] Config { get; init; }

    /// <summary>Sample rate; also used as the audio track timescale (48000 for Opus)</summary>
    public required uint SampleRate { get; init; }

    public required ushort ChannelCount { get; init; }
}

/// <summary>A timed-metadata event carried as an emsg box alongside a fragment.</summary>
internal readonly struct CmafEvent
{
    /// <summary>Presentation time on the media timeline, in milliseconds</summary>
    public required ulong TimeMs { get; init; }

    /// <summary>A complete ID3v2 tag</summary>
    public required ReadOnlyMemory<byte> Id3 { get; init; }
}

internal readonly struct CmafSample
{
    public required ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>Sample duration in the track timescale</summary>
    public required uint Duration { get; init; }

    /// <summary>PTS minus DTS in the track timescale (video B-frames); 0 otherwise</summary>
    public required int CompositionOffset { get; init; }

    public required bool IsSync { get; init; }
}

/// <summary>
/// Builds CMAF-style fragmented MP4: one init segment (ftyp+moov) and
/// media segments (styp+moof+mdat) with an optional video track and an optional
/// audio track (at least one must be present).
/// </summary>
internal sealed class CmafPackager(CmafVideoTrack? video, CmafAudioTrack? audio)
{
    public const uint VideoTrackId = 1;
    public const uint AudioTrackId = 2;

    private const uint SampleFlagsSync    = 0x02000000; // sample_depends_on = 2 (I-frame)
    private const uint SampleFlagsNonSync = 0x01010000; // sample_depends_on = 1 + non-sync

    private uint _sequenceNumber = 1;

    // Reused for every fragment; its buffer stabilizes at the largest fragment size
    private readonly BoxWriter _fragmentWriter = new();

    public byte[] BuildInitSegment()
    {
        var w = new BoxWriter();

        w.Begin("ftyp");
        w.WriteFourCc("iso6"); // major brand
        w.WriteUInt32(0);      // minor version
        w.WriteFourCc("iso6");
        w.WriteFourCc("cmfc");
        w.WriteFourCc("mp41");
        w.End();

        w.Begin("moov");
        WriteMvhd(w);
        if (video is not null)
        {
            WriteVideoTrak(w, video.Value);
        }
        if (audio is not null)
        {
            WriteAudioTrak(w, audio.Value);
        }
        w.Begin("mvex");
        if (video is not null)
        {
            WriteTrex(w, VideoTrackId);
        }
        if (audio is not null)
        {
            WriteTrex(w, AudioTrackId);
        }
        w.End(); // mvex
        w.End(); // moov

        return w.ToArray();
    }

    /// <summary>
    /// Builds one media segment into <paramref name="output"/>.
    /// Base times are in the respective track timescales.
    /// </summary>
    public void BuildFragment(
        ulong videoBaseTime, IReadOnlyList<CmafSample> videoSamples,
        ulong audioBaseTime, IReadOnlyList<CmafSample> audioSamples,
        Stream output, IReadOnlyList<CmafEvent>? events = null)
    {
        var w = _fragmentWriter;
        w.Reset();

        w.Begin("styp");
        w.WriteFourCc("msdh");
        w.WriteUInt32(0);
        w.WriteFourCc("msdh");
        w.WriteFourCc("msix");
        w.End();

        if (events is { Count: > 0 })
        {
            foreach (CmafEvent e in events)
            {
                WriteEmsg(w, e);
            }
        }

        long moofStart = w.Length;
        long videoOffsetPatch = -1;
        long audioOffsetPatch = -1;

        w.Begin("moof");

        w.BeginFull("mfhd", 0, 0);
        w.WriteUInt32(_sequenceNumber++);
        w.End();

        if (video is not null)
        {
            w.Begin("traf");
            WriteTfhd(w, VideoTrackId);
            WriteTfdt(w, videoBaseTime);
            // trun v1 (signed composition offsets): data-offset | duration | size | flags | cts
            w.BeginFull("trun", 1, 0x000F01);
            w.WriteUInt32((uint)videoSamples.Count);
            videoOffsetPatch = w.ReserveUInt32();
            foreach (var s in videoSamples)
            {
                w.WriteUInt32(s.Duration);
                w.WriteUInt32((uint)s.Data.Length);
                w.WriteUInt32(s.IsSync ? SampleFlagsSync : SampleFlagsNonSync);
                w.WriteInt32(s.CompositionOffset);
            }
            w.End(); // trun
            w.End(); // traf
        }

        if (audio is not null && audioSamples.Count > 0)
        {
            w.Begin("traf");
            WriteTfhd(w, AudioTrackId);
            WriteTfdt(w, audioBaseTime);
            // trun v0: data-offset | duration | size | flags
            w.BeginFull("trun", 0, 0x000701);
            w.WriteUInt32((uint)audioSamples.Count);
            audioOffsetPatch = w.ReserveUInt32();
            foreach (var s in audioSamples)
            {
                w.WriteUInt32(s.Duration);
                w.WriteUInt32((uint)s.Data.Length);
                w.WriteUInt32(SampleFlagsSync);
            }
            w.End(); // trun
            w.End(); // traf
        }

        w.End(); // moof

        long mdatStart = w.Length;
        w.Begin("mdat");
        long videoBytes = 0;
        foreach (var s in videoSamples)
        {
            w.WriteBytes(s.Data.Span);
            videoBytes += s.Data.Length;
        }
        foreach (var s in audioSamples)
        {
            w.WriteBytes(s.Data.Span);
        }
        w.End(); // mdat

        // data_offset is relative to the start of the moof box
        var mdatPayloadOffset = (uint)(mdatStart - moofStart + 8);
        if (videoOffsetPatch >= 0)
        {
            w.PatchUInt32(videoOffsetPatch, mdatPayloadOffset);
        }
        if (audioOffsetPatch >= 0)
        {
            w.PatchUInt32(audioOffsetPatch, (uint)(mdatPayloadOffset + videoBytes));
        }

        w.WriteTo(output);
    }

    // ---- moov internals ----

    private static void WriteMvhd(BoxWriter w)
    {
        w.BeginFull("mvhd", 0, 0);
        w.WriteUInt32(0);          // creation_time
        w.WriteUInt32(0);          // modification_time
        w.WriteUInt32(1000);       // timescale
        w.WriteUInt32(0);          // duration (unknown: fragmented)
        w.WriteUInt32(0x00010000); // rate 1.0
        w.WriteUInt16(0x0100);     // volume 1.0
        w.WriteZeros(2 + 4 * 2);   // reserved
        WriteUnityMatrix(w);
        w.WriteZeros(4 * 6);       // pre_defined
        w.WriteUInt32(3);          // next_track_ID
        w.End();
    }

    private static void WriteVideoTrak(BoxWriter w, CmafVideoTrack video)
    {
        w.Begin("trak");

        w.BeginFull("tkhd", 0, 3); // enabled + in movie
        w.WriteUInt32(0);          // creation_time
        w.WriteUInt32(0);          // modification_time
        w.WriteUInt32(VideoTrackId);
        w.WriteUInt32(0);          // reserved
        w.WriteUInt32(0);          // duration
        w.WriteZeros(4 * 2);       // reserved
        w.WriteUInt16(0);          // layer
        w.WriteUInt16(0);          // alternate_group
        w.WriteUInt16(0);          // volume (video)
        w.WriteUInt16(0);          // reserved
        WriteUnityMatrix(w);
        w.WriteUInt32(video.Width << 16);  // 16.16 fixed
        w.WriteUInt32(video.Height << 16);
        w.End();

        w.Begin("mdia");

        w.BeginFull("mdhd", 0, 0);
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteUInt32(CmafVideoTrack.Timescale);
        w.WriteUInt32(0);
        w.WriteUInt16(0x55C4); // language: und
        w.WriteUInt16(0);
        w.End();

        WriteHdlr(w, "vide", "SpangleVideo");

        w.Begin("minf");
        w.BeginFull("vmhd", 0, 1);
        w.WriteZeros(2 + 2 * 3); // graphicsmode + opcolor
        w.End();
        WriteDinf(w);
        w.Begin("stbl");
        w.BeginFull("stsd", 0, 0);
        w.WriteUInt32(1);
        WriteVideoSampleEntry(w, video);
        w.End(); // stsd
        WriteEmptySampleTables(w);
        w.End(); // stbl
        w.End(); // minf

        w.End(); // mdia
        w.End(); // trak
    }

    private static void WriteVideoSampleEntry(BoxWriter w, CmafVideoTrack video)
    {
        (string sampleEntry, string configBox) = video.Codec switch
        {
            VideoCodec.H264 => ("avc1", "avcC"),
            VideoCodec.H265 => ("hvc1", "hvcC"),
            VideoCodec.AV1  => ("av01", "av1C"),
            _ => throw new NotSupportedException($"No ISO-BMFF sample entry mapping for {video.Codec}"),
        };

        w.Begin(sampleEntry);
        w.WriteZeros(6);           // reserved
        w.WriteUInt16(1);          // data_reference_index
        w.WriteZeros(2 + 2 + 4 * 3); // pre_defined + reserved + pre_defined
        w.WriteUInt16((ushort)video.Width);
        w.WriteUInt16((ushort)video.Height);
        w.WriteUInt32(0x00480000); // horizresolution 72dpi
        w.WriteUInt32(0x00480000); // vertresolution
        w.WriteUInt32(0);          // reserved
        w.WriteUInt16(1);          // frame_count
        w.WriteZeros(32);          // compressorname
        w.WriteUInt16(0x0018);     // depth
        w.WriteInt16(-1);          // pre_defined

        w.Begin(configBox);
        w.WriteBytes(video.ConfigRecord);
        w.End();

        w.End();
    }

    private static void WriteAudioTrak(BoxWriter w, CmafAudioTrack audioTrack)
    {
        w.Begin("trak");

        w.BeginFull("tkhd", 0, 3);
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteUInt32(AudioTrackId);
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteZeros(4 * 2);
        w.WriteUInt16(0);
        w.WriteUInt16(0);
        w.WriteUInt16(0x0100); // volume 1.0
        w.WriteUInt16(0);
        WriteUnityMatrix(w);
        w.WriteUInt32(0); // width
        w.WriteUInt32(0); // height
        w.End();

        w.Begin("mdia");

        w.BeginFull("mdhd", 0, 0);
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteUInt32(audioTrack.SampleRate); // timescale = sample rate
        w.WriteUInt32(0);
        w.WriteUInt16(0x55C4);
        w.WriteUInt16(0);
        w.End();

        WriteHdlr(w, "soun", "SpangleAudio");

        w.Begin("minf");
        w.BeginFull("smhd", 0, 0);
        w.WriteZeros(2 + 2); // balance + reserved
        w.End();
        WriteDinf(w);
        w.Begin("stbl");
        w.BeginFull("stsd", 0, 0);
        w.WriteUInt32(1);
        WriteAudioSampleEntry(w, audioTrack);
        w.End(); // stsd
        WriteEmptySampleTables(w);
        w.End(); // stbl
        w.End(); // minf

        w.End(); // mdia
        w.End(); // trak
    }

    private static void WriteAudioSampleEntry(BoxWriter w, CmafAudioTrack audioTrack)
    {
        w.Begin(audioTrack.Codec == AudioCodec.Opus ? "Opus" : "mp4a");
        w.WriteZeros(6);      // reserved
        w.WriteUInt16(1);     // data_reference_index
        w.WriteZeros(4 * 2);  // reserved
        w.WriteUInt16(audioTrack.ChannelCount);
        w.WriteUInt16(16);    // samplesize
        w.WriteUInt16(0);     // pre_defined
        w.WriteUInt16(0);     // reserved
        w.WriteUInt32(audioTrack.SampleRate << 16); // 16.16 fixed

        if (audioTrack.Codec == AudioCodec.Opus)
        {
            WriteDOps(w, audioTrack.Config);
            w.End(); // Opus
            return;
        }

        // esds: MPEG-4 ES_Descriptor with the AudioSpecificConfig
        byte[] asc = audioTrack.Config;
        w.BeginFull("esds", 0, 0);
        int dsiLength = asc.Length;
        int dcdLength = 13 + 2 + dsiLength; // fixed part + DSI tag+len
        int esLength = 3 + 2 + dcdLength + 3; // ES fixed + DCD tag+len + SLConfig(tag+len+data)

        w.WriteUInt8(0x03); // ES_DescrTag
        w.WriteUInt8((byte)esLength);
        w.WriteUInt16((ushort)AudioTrackId); // ES_ID
        w.WriteUInt8(0);    // flags

        w.WriteUInt8(0x04); // DecoderConfigDescrTag
        w.WriteUInt8((byte)dcdLength);
        w.WriteUInt8(0x40); // objectTypeIndication: MPEG-4 Audio
        w.WriteUInt8(0x15); // streamType: audio, upStream 0, reserved 1
        w.WriteUInt8(0); w.WriteUInt16(0); // bufferSizeDB (24bit)
        w.WriteUInt32(0);   // maxBitrate
        w.WriteUInt32(0);   // avgBitrate

        w.WriteUInt8(0x05); // DecSpecificInfoTag
        w.WriteUInt8((byte)dsiLength);
        w.WriteBytes(asc);

        w.WriteUInt8(0x06); // SLConfigDescrTag
        w.WriteUInt8(1);
        w.WriteUInt8(0x02);
        w.End(); // esds

        w.End(); // mp4a
    }

    /// <summary>
    /// OpusSpecificBox ("Encapsulation of Opus in ISO BMFF" 4.3.2): the OpusHead
    /// fields with the multi-byte values flipped to big-endian, without the magic.
    /// </summary>
    private static void WriteDOps(BoxWriter w, ReadOnlySpan<byte> opusHead)
    {
        var info = Codecs.Opus.OpusPacket.ParseOpusHead(opusHead);
        w.Begin("dOps");
        w.WriteUInt8(0); // Version
        w.WriteUInt8(info.ChannelCount);
        w.WriteUInt16(info.PreSkip);
        w.WriteUInt32(info.InputSampleRate);
        w.WriteInt16(info.OutputGain);
        w.WriteUInt8(info.ChannelMappingFamily);
        if (info.ChannelMappingFamily != 0)
        {
            // StreamCount, CoupledCount and the channel mapping follow verbatim
            w.WriteBytes(opusHead[Codecs.Opus.OpusPacket.OpusHeadMinSize..]);
        }
        w.End();
    }

    private static void WriteHdlr(BoxWriter w, string handlerType, string name)
    {
        w.BeginFull("hdlr", 0, 0);
        w.WriteUInt32(0); // pre_defined
        w.WriteFourCc(handlerType);
        w.WriteZeros(4 * 3); // reserved
        foreach (char c in name)
        {
            w.WriteUInt8((byte)c);
        }
        w.WriteUInt8(0); // null terminator
        w.End();
    }

    private static void WriteDinf(BoxWriter w)
    {
        w.Begin("dinf");
        w.BeginFull("dref", 0, 0);
        w.WriteUInt32(1);
        w.BeginFull("url ", 0, 1); // self-contained
        w.End();
        w.End();
        w.End();
    }

    private static void WriteEmptySampleTables(BoxWriter w)
    {
        w.BeginFull("stts", 0, 0);
        w.WriteUInt32(0);
        w.End();
        w.BeginFull("stsc", 0, 0);
        w.WriteUInt32(0);
        w.End();
        w.BeginFull("stsz", 0, 0);
        w.WriteUInt32(0); // sample_size
        w.WriteUInt32(0); // sample_count
        w.End();
        w.BeginFull("stco", 0, 0);
        w.WriteUInt32(0);
        w.End();
    }

    private static void WriteUnityMatrix(BoxWriter w)
    {
        w.WriteUInt32(0x00010000); w.WriteUInt32(0); w.WriteUInt32(0);
        w.WriteUInt32(0); w.WriteUInt32(0x00010000); w.WriteUInt32(0);
        w.WriteUInt32(0); w.WriteUInt32(0); w.WriteUInt32(0x40000000);
    }

    /// <summary>
    /// DASH Event Message box (ISO 23009-1 5.10.3.3) v1, with the ID3-in-emsg scheme
    /// used by HLS/CMAF timed metadata ("https://aomedia.org/emsg/ID3").
    /// </summary>
    private void WriteEmsg(BoxWriter w, in CmafEvent e)
    {
        w.BeginFull("emsg", 1, 0);
        w.WriteUInt32(1000);      // timescale: milliseconds
        w.WriteUInt64(e.TimeMs);  // presentation_time
        w.WriteUInt32(0);         // event_duration: point event
        w.WriteUInt32(_emsgId++);
        WriteNulTerminated(w, "https://aomedia.org/emsg/ID3"); // scheme_id_uri
        WriteNulTerminated(w, "");                             // value
        w.WriteBytes(e.Id3.Span);                              // message_data: the ID3 tag
        w.End();
    }

    private uint _emsgId = 1;

    private static void WriteNulTerminated(BoxWriter w, string value)
    {
        foreach (char c in value)
        {
            w.WriteUInt8((byte)c);
        }
        w.WriteUInt8(0);
    }

    private static void WriteTfhd(BoxWriter w, uint trackId)
    {
        w.BeginFull("tfhd", 0, 0x020000); // default-base-is-moof
        w.WriteUInt32(trackId);
        w.End();
    }

    private static void WriteTfdt(BoxWriter w, ulong baseMediaDecodeTime)
    {
        w.BeginFull("tfdt", 1, 0);
        w.WriteUInt64(baseMediaDecodeTime);
        w.End();
    }

    private static void WriteTrex(BoxWriter w, uint trackId)
    {
        w.BeginFull("trex", 0, 0);
        w.WriteUInt32(trackId);
        w.WriteUInt32(1); // default_sample_description_index
        w.WriteUInt32(0); // default_sample_duration
        w.WriteUInt32(0); // default_sample_size
        w.WriteUInt32(0); // default_sample_flags
        w.End();
    }
}
