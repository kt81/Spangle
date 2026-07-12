using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Transport.Rtsp.Rtp;

/// <summary>
/// RFC 7798 H.265 depacketization: single NAL units, aggregation packets (48) and
/// fragmentation units (49). Same access-unit and loss policy as the H.264
/// depacketizer, with HEVC's 2-byte NAL header and IRAP range (16–23).
/// </summary>
internal sealed class H265Depacketizer(Action<NalAccessUnit> onAccessUnit)
{
    private static readonly ILogger<H265Depacketizer> s_logger = SpangleLogManager.GetLogger<H265Depacketizer>();

    private readonly NalAccessUnit _current = new();
    private readonly List<byte> _fuBuffer = new(16 * 1024);
    private bool _fuActive;
    private ushort _expectedSeq;
    private bool _hasSeq;
    private bool _damaged;
    private bool _waitKeyFrame = true;
    private bool _hasCurrentTs;

    private static byte NalType(ReadOnlySpan<byte> nal) => (byte)((nal[0] >> 1) & 0x3F);

    public void Feed(in RtpPacket packet)
    {
        if (_hasSeq && packet.SequenceNumber != _expectedSeq)
        {
            s_logger.ZLogWarning($"RTP loss: expected seq {_expectedSeq}, got {packet.SequenceNumber}; waiting for the next IRAP");
            _damaged = true;
            _fuActive = false;
        }
        _expectedSeq = (ushort)(packet.SequenceNumber + 1);
        _hasSeq = true;

        if (_hasCurrentTs && packet.Timestamp != _current.RtpTimestamp)
        {
            EmitCurrent();
        }
        if (!_hasCurrentTs)
        {
            _current.Reset(packet.Timestamp);
            _hasCurrentTs = true;
        }

        ReadOnlySpan<byte> payload = packet.Payload;
        if (payload.Length < 2)
        {
            return;
        }

        byte payloadUnitType = NalType(payload);
        switch (payloadUnitType)
        {
            case 48: // AP: [2-byte payload hdr][len16 NALU]...
                ReadOnlySpan<byte> rest = payload[2..];
                while (rest.Length >= 2)
                {
                    int size = BinaryPrimitives.ReadUInt16BigEndian(rest);
                    if (rest.Length < 2 + size)
                    {
                        s_logger.ZLogWarning($"Truncated HEVC AP; dropping the access unit");
                        _damaged = true;
                        break;
                    }
                    AddNal(rest.Slice(2, size));
                    rest = rest[(2 + size)..];
                }
                break;

            case 49: // FU: [2-byte payload hdr][fu header: S|E|type][fragment]
                if (payload.Length < 3)
                {
                    _damaged = true;
                    break;
                }
                bool start = (payload[2] & 0x80) != 0;
                bool end = (payload[2] & 0x40) != 0;
                if (start)
                {
                    _fuBuffer.Clear();
                    // reconstructed 2-byte NAL header: type from the FU header, layer/TID from the payload header
                    var fuType = (byte)(payload[2] & 0x3F);
                    _fuBuffer.Add((byte)((payload[0] & 0x81) | (fuType << 1)));
                    _fuBuffer.Add(payload[1]);
                    _fuActive = true;
                }
                else if (!_fuActive)
                {
                    _damaged = true;
                    break;
                }
                _fuBuffer.AddRange(payload[3..]);
                if (end)
                {
                    _fuActive = false;
                    AddNal([.. _fuBuffer]);
                }
                break;

            case 50: // PACI and anything newer
                s_logger.ZLogWarning($"Unsupported RTP H.265 packetization type {payloadUnitType}; ignored");
                break;

            default: // a plain NAL unit
                AddNal(payload);
                break;
        }

        if (packet.Marker)
        {
            EmitCurrent();
        }
    }

    private void AddNal(ReadOnlySpan<byte> nal)
    {
        if (nal.Length < 2)
        {
            return;
        }
        _current.Nals.Add(nal.ToArray());
    }

    private void EmitCurrent()
    {
        _hasCurrentTs = false;
        if (_current.Nals.Count == 0)
        {
            _damaged = false;
            return;
        }
        if (_damaged)
        {
            _damaged = false;
            _waitKeyFrame = true;
            _current.Nals.Clear();
            return;
        }
        if (_waitKeyFrame)
        {
            bool hasIrap = _current.Nals.Any(static n => NalType(n) is >= 16 and <= 23);
            // VPS/SPS/PPS/SEI-only units pass: the adapter needs them before the IRAP
            bool configOnly = _current.Nals.TrueForAll(static n => NalType(n) is 32 or 33 or 34 or 39 or 40);
            if (!hasIrap && !configOnly)
            {
                _current.Nals.Clear();
                return;
            }
            if (hasIrap)
            {
                _waitKeyFrame = false;
            }
        }
        onAccessUnit(_current);
        _current.Nals.Clear();
    }
}
