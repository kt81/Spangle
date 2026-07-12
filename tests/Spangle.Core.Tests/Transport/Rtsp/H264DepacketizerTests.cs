using System.Buffers.Binary;
using Spangle.Transport.Rtsp.Rtp;

namespace Spangle.Tests.Transport.Rtsp;

/// <summary>
/// <see cref="H264Depacketizer"/> reassembling RTP payloads into access units
/// (RFC 6184): single NAL units, FU-A fragmentation (type 28), STAP-A aggregation
/// (type 24), the "wait for the first IDR" suppression, and the sequence-gap drop.
/// </summary>
public class H264DepacketizerTests
{
    // NAL types (RFC 6184 / H.264 Table 7-1)
    private const byte NalNonIdrSlice = 1;
    private const byte NalIdrSlice = 5;
    private const byte NalSps = 7;
    private const byte NalPps = 8;

    private const byte FuAIndicator = 0x7C; // F=0, NRI=3, type=28
    private const byte StapAIndicator = 0x78; // F=0, NRI=3, type=24
    private const byte NalHeaderNri3 = 0x60; // F=0, NRI=3 prefix for a reconstructed header

    [Fact]
    public void SingleIdrNalWithMarkerEmitsOneAccessUnit()
    {
        List<Emitted> units = Collect(out H264Depacketizer d);
        byte[] idr = SingleNal(NalIdrSlice, [0x11, 0x22, 0x33, 0x44]);

        Feed(d, seq: 0, timestamp: 9000, marker: true, idr);

        units.Should().ContainSingle();
        units[0].RtpTimestamp.Should().Be(9000u);
        units[0].Nals.Should().ContainSingle();
        units[0].Nals[0].Should().Equal(idr);
    }

    [Fact]
    public void FuAFragmentsReassembleToTheOriginalNal()
    {
        List<Emitted> units = Collect(out H264Depacketizer d);
        // The original IDR NAL that was split across three RTP packets.
        byte[] originalNal = [(byte)(NalHeaderNri3 | NalIdrSlice), 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5];

        // FU header byte: S|E|R (top three bits) + the 5-bit NAL type.
        Feed(d, seq: 0, timestamp: 3000, marker: false, [FuAIndicator, (byte)(0x80 | NalIdrSlice), 0xA0, 0xA1]); // start
        Feed(d, seq: 1, timestamp: 3000, marker: false, [FuAIndicator, NalIdrSlice, 0xA2, 0xA3]);               // middle
        Feed(d, seq: 2, timestamp: 3000, marker: true, [FuAIndicator, (byte)(0x40 | NalIdrSlice), 0xA4, 0xA5]); // end

        units.Should().ContainSingle();
        units[0].Nals.Should().ContainSingle();
        // The reconstructed NAL header takes F/NRI from the indicator and the type from the FU header.
        units[0].Nals[0][0].Should().Be((byte)(NalHeaderNri3 | NalIdrSlice));
        units[0].Nals[0].Should().Equal(originalNal);
    }

    [Fact]
    public void StapACarriesSpsAndPpsAsSeparateNals()
    {
        List<Emitted> units = Collect(out H264Depacketizer d);
        byte[] sps = SingleNal(NalSps, [0x42, 0x00, 0x0A]);
        byte[] pps = SingleNal(NalPps, [0xCE, 0x38]);

        Feed(d, seq: 0, timestamp: 0, marker: true, StapA(sps, pps));

        // A parameter-set-only unit is allowed through even before the first IDR.
        units.Should().ContainSingle();
        units[0].Nals.Should().HaveCount(2);
        units[0].Nals[0].Should().Equal(sps);
        units[0].Nals[1].Should().Equal(pps);
    }

    [Fact]
    public void NonIdrBeforeAnyIdrIsSuppressedThenEverythingFromTheIdrFlows()
    {
        List<Emitted> units = Collect(out H264Depacketizer d);
        byte[] nonIdrFirst = SingleNal(NalNonIdrSlice, [0x01]);
        byte[] idr = SingleNal(NalIdrSlice, [0x02]);
        byte[] nonIdrAfter = SingleNal(NalNonIdrSlice, [0x03]);

        Feed(d, seq: 0, timestamp: 0, marker: true, nonIdrFirst);     // dropped: no IDR has been seen
        Feed(d, seq: 1, timestamp: 3000, marker: true, idr);          // the first key frame: emitted
        Feed(d, seq: 2, timestamp: 6000, marker: true, nonIdrAfter);  // now that we are keyed, emitted

        units.Should().HaveCount(2);
        NalType(units[0].Nals[0]).Should().Be(NalIdrSlice);
        NalType(units[1].Nals[0]).Should().Be(NalNonIdrSlice);
    }

    [Fact]
    public void ASequenceGapDropsTheInFlightUnitAndReWaitsForAnIdr()
    {
        List<Emitted> units = Collect(out H264Depacketizer d);

        Feed(d, seq: 0, timestamp: 0, marker: true, SingleNal(NalIdrSlice, [0xFF]));   // key frame -> emitted
        Feed(d, seq: 1, timestamp: 3000, marker: false, SingleNal(NalNonIdrSlice, [0x10])); // in flight
        // seq 2 is missing: the frame that was in flight is damaged and dropped.
        Feed(d, seq: 3, timestamp: 3000, marker: true, SingleNal(NalNonIdrSlice, [0x11]));
        Feed(d, seq: 4, timestamp: 6000, marker: true, SingleNal(NalNonIdrSlice, [0x12])); // still no IDR -> dropped
        Feed(d, seq: 5, timestamp: 9000, marker: true, SingleNal(NalIdrSlice, [0x13]));     // re-keyed -> emitted

        units.Should().HaveCount(2, "only the two IDR-anchored units survive the gap");
        NalType(units[0].Nals[0]).Should().Be(NalIdrSlice);
        NalType(units[1].Nals[0]).Should().Be(NalIdrSlice);
        units[1].RtpTimestamp.Should().Be(9000u);
    }

    [Fact]
    public void ATimestampChangeClosesTheAccessUnitWithoutAMarker()
    {
        List<Emitted> units = Collect(out H264Depacketizer d);

        Feed(d, seq: 0, timestamp: 0, marker: true, SingleNal(NalIdrSlice, [0x01])); // key frame first
        // Two consecutive packets with no marker but a changing timestamp: the second
        // packet's new timestamp closes the frame the first packet started.
        Feed(d, seq: 1, timestamp: 3000, marker: false, SingleNal(NalNonIdrSlice, [0x02]));
        Feed(d, seq: 2, timestamp: 6000, marker: false, SingleNal(NalNonIdrSlice, [0x03]));

        units.Should().HaveCount(2);
        units[0].RtpTimestamp.Should().Be(0u);
        units[1].RtpTimestamp.Should().Be(3000u, "the timestamp change flushed the frame with no marker");
        units[1].Nals[0].Should().Equal(SingleNal(NalNonIdrSlice, [0x02]));
    }

    // =======================================================================

    private sealed record Emitted(uint RtpTimestamp, IReadOnlyList<byte[]> Nals);

    private static List<Emitted> Collect(out H264Depacketizer depacketizer)
    {
        var units = new List<Emitted>();
        depacketizer = new H264Depacketizer(unit => units.Add(new Emitted(unit.RtpTimestamp, unit.Nals.ToList())));
        return units;
    }

    private static void Feed(H264Depacketizer d, ushort seq, uint timestamp, bool marker, byte[] payload)
    {
        var packet = new RtpPacket
        {
            PayloadType = 96,
            SequenceNumber = seq,
            Timestamp = timestamp,
            Ssrc = 0,
            Marker = marker,
            Payload = payload,
        };
        d.Feed(packet);
    }

    /// <summary>A single-NAL payload: the NAL header (F=0, NRI=3, type) followed by its body.</summary>
    private static byte[] SingleNal(byte nalType, byte[] body)
    {
        byte[] nal = new byte[1 + body.Length];
        nal[0] = (byte)(NalHeaderNri3 | nalType);
        body.CopyTo(nal.AsSpan(1));
        return nal;
    }

    /// <summary>A STAP-A payload: the aggregation header, then 16-bit-size-prefixed NALs.</summary>
    private static byte[] StapA(params byte[][] nals)
    {
        var length = 1;
        foreach (byte[] nal in nals)
        {
            length += 2 + nal.Length;
        }
        byte[] payload = new byte[length];
        payload[0] = StapAIndicator;
        var offset = 1;
        foreach (byte[] nal in nals)
        {
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset), (ushort)nal.Length);
            nal.CopyTo(payload.AsSpan(offset + 2));
            offset += 2 + nal.Length;
        }
        return payload;
    }

    private static byte NalType(byte[] nal) => (byte)(nal[0] & 0x1F);
}
