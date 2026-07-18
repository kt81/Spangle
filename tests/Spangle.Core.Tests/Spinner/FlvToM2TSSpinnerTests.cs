using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.InteropServices;
using Spangle.Codecs.AVC;
using Spangle.Containers.M2TS;
using Spangle.Spinner;

namespace Spangle.Tests.Spinner;

/// <summary>
/// <see cref="FlvToM2TSSpinner"/> muxes canonical MediaFrames into MPEG-2 TS. These pin the PES
/// timestamps it writes: decode time and the composition offset are 90 kHz ticks that flow through
/// to PTS/DTS verbatim, and a negative offset (PTS before DTS) is clamped so the PES stays valid.
/// </summary>
public class FlvToM2TSSpinnerTests
{
    private static readonly byte[] s_sps = [0x67, 0x64, 0x00, 0x1F, 0xAA, 0xBB];
    private static readonly byte[] s_pps = [0x68, 0xEE, 0x3C, 0x80];
    private static readonly byte[] s_idr = [0x65, 0x11, 0x22, 0x33, 0x44];

    [Fact]
    public async Task PositiveCompositionOffset_WritesPtsAndDtsVerbatim()
    {
        PesTiming pes = await MuxKeyframeAsync(timestamp: 90_000, compositionTime: 3_000);

        pes.HasDts.Should().BeTrue("a composition offset makes PTS differ from DTS");
        pes.Dts.Should().Be(90_000, "the 90 kHz decode time passes through to DTS unscaled");
        pes.Pts.Should().Be(93_000, "PTS is DTS plus the 3000-tick composition offset");
    }

    [Fact]
    public async Task NegativeCompositionOffset_ClampsPtsToDts()
    {
        // PTS would land 300 ticks before DTS, which no decoder accepts in a PES.
        PesTiming pes = await MuxKeyframeAsync(timestamp: 90_000, compositionTime: -300);

        pes.HasDts.Should().BeFalse("clamped PTS equals DTS, so only PTS is written");
        pes.Pts.Should().Be(90_000, "PTS is clamped up to DTS rather than emitted before it");
    }

    // =======================================================================

    private readonly record struct PesTiming(bool HasDts, long Pts, long Dts);

    /// <summary>Muxes one avcC config frame and one keyframe, returning the video PES timing.</summary>
    private static async Task<PesTiming> MuxKeyframeAsync(long timestamp, int compositionTime)
    {
        var dummy = new Pipe();
        var context = new FakeReceiverContext(dummy.Reader, dummy.Writer);
        var outPipe = new Pipe(new PipeOptions(useSynchronizationContext: false));
        var spinner = new FlvToM2TSSpinner(context, outPipe.Writer, CancellationToken.None);

        byte[] avcC = AvcCBuilder.Build(s_sps, s_pps);
        WriteFrame(spinner.Intake, MediaFrameFlags.Config, avcC, timestamp: 0, compositionTime: 0);
        WriteFrame(spinner.Intake, MediaFrameFlags.KeyFrame, LengthPrefixed(s_idr), timestamp, compositionTime);
        await spinner.Intake.FlushAsync();
        await spinner.Intake.CompleteAsync();

        await spinner.SpinAsync();

        ReadResult result = await outPipe.Reader.ReadAsync();
        byte[] ts = result.Buffer.ToArray();
        return ParseVideoPesTiming(ts);
    }

    private static void WriteFrame(PipeWriter intake, MediaFrameFlags flags, byte[] payload,
        long timestamp, int compositionTime)
    {
        MediaFrameHeader.Write(intake, MediaFrameKind.Video, flags, (uint)VideoCodec.H264,
            compositionTime, payload.Length, timestamp);
        intake.Write(payload);
    }

    private static byte[] LengthPrefixed(byte[] nalu)
    {
        var buff = new byte[4 + nalu.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buff, (uint)nalu.Length);
        nalu.CopyTo(buff.AsSpan(4));
        return buff;
    }

    /// <summary>Finds the first video PES (PID 0x100, payload_unit_start) and reads its PTS/DTS.</summary>
    private static PesTiming ParseVideoPesTiming(byte[] ts)
    {
        for (var o = 0; o + M2TSWriter.PacketSize <= ts.Length; o += M2TSWriter.PacketSize)
        {
            ushort pid = (ushort)(((ts[o + 1] & 0x1F) << 8) | ts[o + 2]);
            bool pusi = (ts[o + 1] & 0x40) != 0;
            if (pid != M2TSWriter.PidVideo || !pusi)
            {
                continue;
            }

            int payloadStart = o + 4;
            if ((ts[o + 3] & 0x20) != 0) // adaptation field present (carries the PCR on a keyframe)
            {
                payloadStart += 1 + ts[o + 4];
            }

            ReadOnlySpan<byte> pes = ts.AsSpan(payloadStart);
            pes[..3].ToArray().Should().Equal([0x00, 0x00, 0x01], "a PES packet_start_code_prefix");
            pes[3].Should().Be(M2TSWriter.StreamIdVideo);

            int ptsDtsFlags = pes[7] >> 6;
            long pts = (long)MemoryMarshal.AsRef<PESTimestamp>(pes.Slice(9, PESTimestamp.Size)).Value;
            if (ptsDtsFlags == 0b11)
            {
                long dts = (long)MemoryMarshal.AsRef<PESTimestamp>(pes.Slice(14, PESTimestamp.Size)).Value;
                return new PesTiming(HasDts: true, pts, dts);
            }
            return new PesTiming(HasDts: false, pts, Dts: 0);
        }

        throw new InvalidOperationException("no video PES found in the muxed TS");
    }

    private sealed class FakeReceiverContext(PipeReader reader, PipeWriter writer)
        : ReceiverContextBase<FakeReceiverContext>(reader, writer, CancellationToken.None)
    {
        public override string Id => "test";
        public override EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 0);
        public override bool IsCompleted => false;
        public override ValueTask BeginReceiveAsync(CancellationTokenSource readTimeoutSource) =>
            ValueTask.CompletedTask;
    }
}
