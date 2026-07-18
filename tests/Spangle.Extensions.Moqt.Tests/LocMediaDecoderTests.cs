using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using Spangle.Net.Moqt;
using Spangle.Net.Moqt.Wire;
using Spangle.Spinner;

namespace Spangle.Extensions.Moqt.Tests;

/// <summary>
/// The LOC → MediaFrame decoder (the ingest reverse of <see cref="MoqSender"/>'s egress mapping).
/// The objects here are built with the very property helpers the sender publishes with, so each
/// test is a round trip: what the egress writes onto the wire, the ingest must read back into the
/// canonical MediaFrame the rest of the pipeline consumes.
/// </summary>
public class LocMediaDecoderTests
{
    private readonly record struct Frame(
        MediaFrameKind Kind, MediaFrameFlags Flags, uint Codec, long Timestamp, byte[] Payload);

    [Fact]
    public void AVideoGoP_DecodesToAKeyframeThenDeltas_WithConfigFirst()
    {
        byte[] avcC = [0x01, 0x64, 0x00, 0x1F, 0xFF, 0xE1, 0x00, 0x04];
        byte[] key = Pattern(0x11, 96);
        byte[] delta1 = Pattern(0x22, 48);
        byte[] delta2 = Pattern(0x33, 32);
        const ulong timescale = 90_000;

        var decoder = new LocMediaDecoder(MediaFrameKind.Video, (uint)VideoCodec.H264, LocDraft.Draft03);

        // Group 0: a keyframe carrying its avcC, then two deltas — exactly what MoqFrameTrack sends.
        IReadOnlyList<Frame> frames = Decode(decoder,
            Obj(group: 0, id: 0, key, [.. Loc03Properties.MediaTime(0, timescale), Loc03Properties.VideoConfig(avcC)]),
            Obj(group: 0, id: 1, delta1, Loc03Properties.MediaTime(3_000, timescale)),
            Obj(group: 0, id: 2, delta2, Loc03Properties.MediaTime(6_000, timescale)));

        frames.Should().HaveCount(4, "one Config frame, then the keyframe and two deltas");

        frames[0].Flags.Should().Be(MediaFrameFlags.Config);
        frames[0].Codec.Should().Be((uint)VideoCodec.H264);
        frames[0].Payload.Should().Equal(avcC, "the avcC is recovered from the keyframe's Video Config property");

        frames[1].Flags.Should().Be(MediaFrameFlags.KeyFrame, "a new group is an IDR boundary");
        frames[1].Payload.Should().Equal(key, "the elementary bitstream passes through untouched");
        // The 90 kHz media time is the frame clock's own unit: 0, 3000, 6000 ticks pass through.
        frames[1].Timestamp.Should().Be(0L);
        frames[2].Flags.Should().Be(MediaFrameFlags.None);
        frames[2].Payload.Should().Equal(delta1);
        frames[2].Timestamp.Should().Be(3000L);
        frames[3].Payload.Should().Equal(delta2);
        frames[3].Timestamp.Should().Be(6000L);
    }

    [Fact]
    public void ASecondGroup_IsAnotherKeyframe()
    {
        var decoder = new LocMediaDecoder(MediaFrameKind.Video, (uint)VideoCodec.H264, LocDraft.Draft03);
        byte[] avcC = [0x01, 0x64, 0x00, 0x1F];

        IReadOnlyList<Frame> frames = Decode(decoder,
            Obj(group: 0, id: 0, Pattern(0x11, 16), [.. Loc03Properties.MediaTime(0), Loc03Properties.VideoConfig(avcC)]),
            Obj(group: 1, id: 0, Pattern(0x22, 16), [.. Loc03Properties.MediaTime(1_000_000), Loc03Properties.VideoConfig(avcC)]));

        // Config, keyframe (g0), keyframe (g1) — the config goes out once.
        frames.Select(f => f.Flags).Should().Equal(
            [MediaFrameFlags.Config, MediaFrameFlags.KeyFrame, MediaFrameFlags.KeyFrame]);
    }

    [Fact]
    public void ARegressedGroup_IsDroppedNotMistakenForAKeyframe()
    {
        var decoder = new LocMediaDecoder(MediaFrameKind.Video, (uint)VideoCodec.H264, LocDraft.Draft03);
        byte[] avcC = [0x01, 0x64, 0x00, 0x1F];

        IReadOnlyList<Frame> frames = Decode(decoder,
            Obj(group: 5, id: 0, Pattern(0x11, 16), [.. Loc03Properties.MediaTime(0), Loc03Properties.VideoConfig(avcC)]),
            Obj(group: 6, id: 0, Pattern(0x22, 16), [.. Loc03Properties.MediaTime(3_000), Loc03Properties.VideoConfig(avcC)]),
            // a late object from group 5 arrives after group 6 has already started
            Obj(group: 5, id: 1, Pattern(0x33, 16), Loc03Properties.MediaTime(1_000)));

        // Config, keyframe (g5), keyframe (g6): the stale group-5 delta is dropped, not emitted as a
        // fourth false-keyframe frame the way a plain != comparison would have marked it.
        frames.Select(f => f.Flags).Should().Equal(
            [MediaFrameFlags.Config, MediaFrameFlags.KeyFrame, MediaFrameFlags.KeyFrame]);
    }

    [Fact]
    public void Audio_TakesItsConfigFromTheCatalog_AndEveryFrameIsNormal()
    {
        // LOC has no audio config property, so the decoder is handed the AudioSpecificConfig the
        // catalog carried; without it there is nothing to build an AAC decoder from.
        byte[] asc = [0x12, 0x10];
        var decoder = new LocMediaDecoder(MediaFrameKind.Audio, (uint)AudioCodec.AAC, LocDraft.Draft01,
            initialConfig: asc);

        byte[] a0 = Pattern(0x40, 20);
        byte[] a1 = Pattern(0x50, 24);
        IReadOnlyList<Frame> frames = Decode(decoder,
            Obj(group: 0, id: 0, a0, [Loc01Properties.CaptureTimestamp(0)]),
            Obj(group: 1, id: 0, a1, [Loc01Properties.CaptureTimestamp(21_333)]));

        frames.Should().HaveCount(3);
        frames[0].Flags.Should().Be(MediaFrameFlags.Config);
        frames[0].Codec.Should().Be((uint)AudioCodec.AAC);
        frames[0].Payload.Should().Equal(asc);
        // Audio carries no keyframe flag even though each object is its own group.
        frames[1].Flags.Should().Be(MediaFrameFlags.None);
        frames[1].Payload.Should().Equal(a0);
        frames[2].Flags.Should().Be(MediaFrameFlags.None);
        frames[2].Payload.Should().Equal(a1);
        frames[2].Timestamp.Should().Be(1919L, "21,333 µs is 1919 ticks (21.33 ms) at 90 kHz");
    }

    [Fact]
    public void Loc01_ReadsMicrosecondTimestamps()
    {
        var decoder = new LocMediaDecoder(MediaFrameKind.Video, (uint)VideoCodec.H264, LocDraft.Draft01,
            initialConfig: new byte[] { 0x01 });

        IReadOnlyList<Frame> frames = Decode(decoder,
            Obj(group: 0, id: 0, Pattern(0x11, 8), [Loc01Properties.CaptureTimestamp(5_000_000)]));

        frames.Should().HaveCount(2); // config (from catalog) + keyframe
        frames[1].Timestamp.Should().Be(450_000L, "5,000,000 µs is 450,000 ticks (5 s) at 90 kHz");
    }

    [Fact]
    public void MediaBeforeAnyConfig_IsHeldUntilConfigArrives()
    {
        // A subscriber that joins mid-GoP gets delta frames before the keyframe; with no config yet
        // they cannot be decoded, so they are dropped rather than emitted headerless.
        var decoder = new LocMediaDecoder(MediaFrameKind.Video, (uint)VideoCodec.H264, LocDraft.Draft03);

        IReadOnlyList<Frame> frames = Decode(decoder,
            Obj(group: 0, id: 1, Pattern(0x22, 16), Loc03Properties.MediaTime(3_000)), // delta, no config
            Obj(group: 1, id: 0, Pattern(0x11, 16), [.. Loc03Properties.MediaTime(1_000_000), Loc03Properties.VideoConfig([0x01])]));

        frames.Select(f => f.Flags).Should().Equal([MediaFrameFlags.Config, MediaFrameFlags.KeyFrame],
            "nothing is emitted until the keyframe brings the config");
    }

    private static MoqObject Obj(ulong group, ulong id, byte[] payload, IReadOnlyList<MoqKeyValuePair> extensions) =>
        MoqObject.Normal(group, id, subgroupId: 0, publisherPriority: 128, payload, extensions);

    private static List<Frame> Decode(LocMediaDecoder decoder, params MoqObject[] objects)
    {
        var pipe = new Pipe();
        foreach (MoqObject moqObject in objects)
        {
            decoder.Decode(moqObject, pipe.Writer);
        }

        pipe.Writer.Complete();
        return ReadFrames(pipe.Reader);
    }

    private static List<Frame> ReadFrames(PipeReader reader)
    {
        var frames = new List<Frame>();
        if (!reader.TryRead(out ReadResult result))
        {
            return frames;
        }

        byte[] all = result.Buffer.ToArray();
        var offset = 0;
        while (offset + MediaFrameHeader.Size <= all.Length)
        {
            MediaFrameHeader header = MemoryMarshal.Read<MediaFrameHeader>(all.AsSpan(offset, MediaFrameHeader.Size));
            offset += MediaFrameHeader.Size;
            byte[] payload = all.AsSpan(offset, header.Length).ToArray();
            offset += header.Length;
            frames.Add(new Frame(header.Kind, header.Flags, header.Codec, header.Timestamp, payload));
        }

        reader.AdvanceTo(result.Buffer.End);
        return frames;
    }

    private static byte[] Pattern(byte seed, int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i);
        }

        return data;
    }
}
