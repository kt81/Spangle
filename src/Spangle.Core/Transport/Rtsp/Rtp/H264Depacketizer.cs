using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Transport.Rtsp.Rtp;

/// <summary>
/// RFC 6184 H.264 depacketization: single NAL units (types 1–23), STAP-A (24) and
/// FU-A (28). Access units close on the marker bit, with a timestamp change as the
/// fallback for peers that never set it. After a sequence gap the current unit is
/// damaged goods: it is dropped, and emission stays suppressed until the next IDR
/// so the decoder never sees references it does not have.
/// </summary>
internal sealed class H264Depacketizer(Action<NalAccessUnit> onAccessUnit)
{
    private static readonly ILogger<H264Depacketizer> s_logger = SpangleLogManager.GetLogger<H264Depacketizer>();

    private readonly NalAccessUnit _current = new();
    private readonly List<byte> _fuBuffer = new(16 * 1024);
    private bool _fuActive;
    private ushort _expectedSeq;
    private bool _hasSeq;
    private bool _damaged;
    private bool _waitKeyFrame = true;
    private bool _hasCurrentTs;

    public void Feed(in RtpPacket packet)
    {
        if (_hasSeq && packet.SequenceNumber != _expectedSeq)
        {
            s_logger.ZLogWarning($"RTP loss: expected seq {_expectedSeq}, got {packet.SequenceNumber}; waiting for the next IDR");
            _damaged = true;
            _fuActive = false;
        }
        _expectedSeq = (ushort)(packet.SequenceNumber + 1);
        _hasSeq = true;

        // timestamp change closes the previous access unit (marker-less peers)
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
        if (payload.Length == 0)
        {
            return;
        }

        var naluType = (byte)(payload[0] & 0x1F);
        switch (naluType)
        {
            case >= 1 and <= 23:
                AddNal(payload);
                break;

            case 24: // STAP-A: [hdr][len16 NALU]...
                ReadOnlySpan<byte> rest = payload[1..];
                while (rest.Length >= 2)
                {
                    int size = BinaryPrimitives.ReadUInt16BigEndian(rest);
                    if (rest.Length < 2 + size)
                    {
                        s_logger.ZLogWarning($"Truncated STAP-A; dropping the access unit");
                        _damaged = true;
                        break;
                    }
                    AddNal(rest.Slice(2, size));
                    rest = rest[(2 + size)..];
                }
                break;

            case 28: // FU-A: [indicator][fu header: S|E|R|type][fragment]
                if (payload.Length < 2)
                {
                    _damaged = true;
                    break;
                }
                bool start = (payload[1] & 0x80) != 0;
                bool end = (payload[1] & 0x40) != 0;
                if (start)
                {
                    _fuBuffer.Clear();
                    // reconstructed NAL header: F+NRI from the indicator, type from the FU header
                    _fuBuffer.Add((byte)((payload[0] & 0xE0) | (payload[1] & 0x1F)));
                    _fuActive = true;
                }
                else if (!_fuActive)
                {
                    // mid-fragment without a start (join after loss); already damaged
                    _damaged = true;
                    break;
                }
                _fuBuffer.AddRange(payload[2..]);
                if (end)
                {
                    _fuActive = false;
                    AddNal([.. _fuBuffer]);
                }
                break;

            default:
                s_logger.ZLogWarning($"Unsupported RTP H.264 packetization type {naluType}; ignored");
                break;
        }

        if (packet.Marker)
        {
            EmitCurrent();
        }
    }

    private void AddNal(ReadOnlySpan<byte> nal)
    {
        if (nal.Length == 0)
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
            bool hasIdr = _current.Nals.Any(static n => (n[0] & 0x1F) == 5);
            // parameter-set/SEI-only units pass: the adapter needs SPS/PPS even before the IDR
            bool configOnly = _current.Nals.TrueForAll(static n => (n[0] & 0x1F) is 6 or 7 or 8);
            if (!hasIdr && !configOnly)
            {
                _current.Nals.Clear();
                return;
            }
            if (hasIdr)
            {
                _waitKeyFrame = false;
            }
        }
        onAccessUnit(_current);
        _current.Nals.Clear();
    }
}
