using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Spangle.Containers.M2TS;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Transport.HLS;

/// <summary>
/// HLS sender for MPEG-2 TS segments fed by a TS source (SRT ingest): the source
/// packets are re-segmented as-is instead of being demuxed to MediaFrames and muxed
/// back, which halves the container work on the SRT→TS-HLS path.
/// </summary>
public class TSPassthroughHLSSender : ISender<HLSSenderContext>, IDisposable
{
    private static readonly ILogger<TSPassthroughHLSSender> s_logger =
        SpangleLogManager.GetLogger<TSPassthroughHLSSender>();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public async ValueTask StartAsync(HLSSenderContext context)
    {
        var ct = context.CancellationToken;
        var reader = context.IntakeReader;
        TSPassthroughSegmenter? segmenter = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(ct);

                if (segmenter is null && result.Buffer.Length > 0)
                {
                    string key = context.ResolveStreamKey();
                    HLSPlaylistHandover? resume = context.Registry?.TakeHandover(key);
                    Action<string, long, int>? onUpdated =
                        context.Registry is { } registry ? registry.GetOrAdd(key).Publish : null;
                    segmenter = new TSPassthroughSegmenter(context.ResolveStreamStorage(),
                        context.TargetSegmentDuration, resume, onUpdated);
                    s_logger.ZLogInformation($"HLS(TS passthrough) output for {key} to {context.StorageDescription}");
                }

                var consumed = segmenter is null
                    ? result.Buffer.Start
                    : ProcessBuffer(segmenter, result.Buffer);
                reader.AdvanceTo(consumed, result.Buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            if (segmenter is not null)
            {
                if (context.EndBehavior == HLSEndBehavior.Handover && context.Registry is { } registry)
                {
                    registry.StashHandover(context.ResolveStreamKey(), segmenter.ExportHandover());
                    s_logger.ZLogInformation($"HLS(TS passthrough) stream handed over");
                }
                else
                {
                    segmenter.Complete();
                    context.Registry?.Remove(context.ResolveStreamKey());
                    s_logger.ZLogInformation($"HLS(TS passthrough) stream completed");
                }
            }
        }
    }

    private static SequencePosition ProcessBuffer(TSPassthroughSegmenter segmenter, in ReadOnlySequence<byte> buffer)
    {
        var buff = buffer;
        Span<byte> copyBuff = stackalloc byte[M2TSWriter.PacketSize];
        while (buff.Length >= M2TSWriter.PacketSize)
        {
            var packetSeq = buff.Slice(0, M2TSWriter.PacketSize);
            if (packetSeq.IsSingleSegment)
            {
                segmenter.ProcessPacket(packetSeq.FirstSpan);
            }
            else
            {
                packetSeq.CopyTo(copyBuff);
                segmenter.ProcessPacket(copyBuff);
            }
            buff = buff.Slice(M2TSWriter.PacketSize);
        }
        return buff.Start;
    }
}

/// <summary>
/// Cuts a foreign TS packet stream into HLS segments without remuxing.
/// <para>
/// Unlike <see cref="HLSSegmenter"/> (which segments our own muxer's output, where a
/// PAT boundary means a keyframe), a foreign muxer repeats PAT/PMT on a timer, so the
/// cut points come from the stream itself: a video PES start flagged random-access
/// (or any audio PES start when the program has no video). Each new segment starts
/// with the latest cached PAT+PMT so it is independently decodable; the PSI PIDs get
/// locally-owned continuity counters since we drop and inject table packets.
/// </para>
/// </summary>
internal sealed class TSPassthroughSegmenter : IM2TSDemuxerSink
{
    private static readonly ILogger<TSPassthroughSegmenter> s_logger =
        SpangleLogManager.GetLogger<TSPassthroughSegmenter>();

    private const ulong PtsMask = (1UL << 33) - 1;

    /// <summary>A segment is force-cut past this multiple of the target duration.</summary>
    private const double ForcedCutFactor = 4;

    private readonly IHLSStreamStorage _storage;
    private readonly double _targetDuration;
    private readonly ulong _maxSegment90k;
    private readonly HLSPlaylist _playlist;

    /// <summary>PSI-only parse of the source to learn the program layout.</summary>
    private readonly M2TSDemuxer _psi = new() { PsiOnly = true };

    private ushort _videoPid;
    private ushort _audioPid;
    private bool   _programMapped;

    // Latest PAT/PMT packets, re-injected at every segment start
    private readonly byte[] _patPacket = new byte[M2TSWriter.PacketSize];
    private readonly byte[] _pmtPacket = new byte[M2TSWriter.PacketSize];
    private bool _patCached;
    private bool _pmtCached;
    private byte _ccPat;
    private byte _ccPmt;

    private readonly MemoryStream _current = new();

    private bool  _hasSegmentStart;
    private ulong _segmentStart90k;
    private ulong _last90k;

    // Sources without random_access_indicator flags (they exist) would otherwise
    // produce no output at all, or one endlessly growing segment when the flags
    // stop mid-stream; past ForcedCutFactor x target we cut at plain PES starts.
    private bool  _noRaiFallback;
    private bool  _forcedCutWarned;
    private bool  _waitStart90kSet;
    private ulong _waitStart90k;

    public TSPassthroughSegmenter(IHLSStreamStorage storage, double targetDuration, HLSPlaylistHandover? resume = null,
        Action<string, long, int>? onUpdated = null)
    {
        _storage = storage;
        _targetDuration = targetDuration;
        _maxSegment90k = (ulong)(targetDuration * ForcedCutFactor * 90000.0);
        _playlist = new HLSPlaylist(storage, onUpdated: onUpdated, resume: resume);
    }

    public void ProcessPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != M2TSWriter.PacketSize || packet[0] != 0x47)
        {
            throw new InvalidDataException("Broken TS packet stream");
        }

        _psi.ProcessPacket(packet, this);

        ref readonly var header = ref MemoryMarshal.AsRef<TSHeader>(packet[..TSHeader.Size]);
        ushort pid = header.PID;

        // Table packets are cached and injected at segment starts instead of being
        // copied through, so their continuity counters become ours to manage.
        if (pid == 0x0000)
        {
            packet.CopyTo(_patPacket);
            _patCached = true;
            return;
        }
        if (_psi.PmtPid != 0 && pid == _psi.PmtPid)
        {
            packet.CopyTo(_pmtPacket);
            _pmtCached = true;
            return;
        }

        // Every PES start of a known track advances the stream clock, so the tail
        // segment's duration stays honest regardless of the source's PCR cadence
        ulong? pes90k = null;
        if (_programMapped && header.PayloadUnitStart != 0 && (pid == _videoPid || pid == _audioPid))
        {
            pes90k = TryReadPesTimestamp(PayloadOf(packet, in header));
        }
        ushort cutTrackPid = _videoPid != 0 ? _videoPid : _audioPid;
        if (pes90k is { } ts)
        {
            _last90k = ts;
            if (pid == cutTrackPid && ShouldCutAt(packet, pid, ts))
            {
                if (_hasSegmentStart)
                {
                    double duration = ((ts - _segmentStart90k) & PtsMask) / 90000.0;
                    if (duration >= _targetDuration)
                    {
                        FinalizeSegment(duration);
                        _segmentStart90k = ts;
                    }
                }
                else if (_patCached && _pmtCached)
                {
                    _hasSegmentStart = true;
                    _segmentStart90k = ts;
                }
                if (_hasSegmentStart && _current.Length == 0)
                {
                    InjectProgramTables();
                }
            }
        }

        if (_hasSegmentStart)
        {
            // PCRs arrive much more often than cut points; they keep the tail
            // segment's duration honest when the stream ends mid-GOP
            if (TryReadPcr(packet) is { } pcr)
            {
                _last90k = pcr;
            }
            _current.Write(packet);
        }
        // Packets before the first cut point are dropped: a segment must begin at a
        // random access point, and a mid-GOP head would not decode anyway.
    }

    private static ulong? TryReadPcr(ReadOnlySpan<byte> packet)
    {
        ref readonly var ts = ref MemoryMarshal.AsRef<TSPacket>(packet);
        if (!ts.HasAdaptationFields || !ts.HasPCR
            || ts.AdaptationFields.AdaptationFieldLength < AdaptationFieldsBasic.Size - 1 + PCR.Size)
        {
            return null;
        }
        return ts.PCR.Base;
    }

    /// <summary>
    /// A valid cut point is a random-access video PES start, or any audio PES start
    /// when the program has no video (every AAC frame is a sync point). Only called
    /// for PES starts of the cut-driving track.
    /// </summary>
    private bool ShouldCutAt(ReadOnlySpan<byte> packet, ushort pid, ulong ts90k)
    {
        if (pid != _videoPid || _videoPid == 0)
        {
            return true; // audio-only program: every PES start is a sync point
        }

        ref readonly var ts = ref MemoryMarshal.AsRef<TSPacket>(packet);
        if (ts.HasAdaptationFields && ts.AdaptationFields.RandomAccessIndicator != 0)
        {
            return true;
        }
        if (_noRaiFallback)
        {
            return true;
        }

        // No random access point in sight: rather than producing nothing (or one
        // endlessly growing segment), degrade to cutting at plain PES starts.
        ulong waitedFrom;
        if (_hasSegmentStart)
        {
            waitedFrom = _segmentStart90k;
        }
        else
        {
            if (!_waitStart90kSet)
            {
                _waitStart90kSet = true;
                _waitStart90k = ts90k;
            }
            waitedFrom = _waitStart90k;
        }
        if (((ts90k - waitedFrom) & PtsMask) < _maxSegment90k)
        {
            return false;
        }
        if (!_forcedCutWarned)
        {
            _forcedCutWarned = true;
            s_logger.ZLogWarning(
                $"No random_access_indicator seen for {_targetDuration * ForcedCutFactor:F0}s; falling back to cutting at PES boundaries (segments may not start at keyframes)");
        }
        _noRaiFallback = true;
        return true;
    }

    private static ReadOnlySpan<byte> PayloadOf(ReadOnlySpan<byte> packet, in TSHeader header)
    {
        int payloadStart = TSHeader.Size;
        if ((header.AdaptationFieldControl & TSHeader.AdaptationFieldControlType.AdaptationField) != 0)
        {
            payloadStart += 1 + packet[TSHeader.Size];
        }
        return payloadStart >= packet.Length ? default : packet[payloadStart..];
    }

    /// <summary>Reads DTS (preferred) or PTS from a PES header at the payload start.</summary>
    private static ulong? TryReadPesTimestamp(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 9 + PESTimestamp.Size
            || payload[0] != 0x00 || payload[1] != 0x00 || payload[2] != 0x01)
        {
            return null;
        }
        var flags = (byte)(payload[7] >> 6);
        int headerDataLength = payload[8];
        if ((flags & 0b10) == 0 || headerDataLength < PESTimestamp.Size)
        {
            return null;
        }
        if (flags == 0b11 && headerDataLength >= 2 * PESTimestamp.Size
            && payload.Length >= 14 + PESTimestamp.Size)
        {
            return MemoryMarshal.AsRef<PESTimestamp>(payload.Slice(14, PESTimestamp.Size)).Value;
        }
        return MemoryMarshal.AsRef<PESTimestamp>(payload.Slice(9, PESTimestamp.Size)).Value;
    }

    private void InjectProgramTables()
    {
        WritePsiPacket(_patPacket, ref _ccPat);
        WritePsiPacket(_pmtPacket, ref _ccPmt);
    }

    private void WritePsiPacket(byte[] packet, ref byte cc)
    {
        packet[3] = (byte)((packet[3] & 0xF0) | (cc & 0x0F));
        cc++;
        _current.Write(packet);
    }

    public void Complete()
    {
        FlushRemainder();
        _playlist.Complete();
    }

    public HLSPlaylistHandover ExportHandover()
    {
        FlushRemainder();
        return _playlist.ExportHandover();
    }

    private void FlushRemainder()
    {
        if (_current.Length > 0 && _hasSegmentStart)
        {
            FinalizeSegment(((_last90k - _segmentStart90k) & PtsMask) / 90000.0);
        }
    }

    private void FinalizeSegment(double duration)
    {
        string name = _playlist.NextSegmentName(".ts");
        _storage.WriteBlob(name, _current.GetBuffer().AsSpan(0, (int)_current.Length));
        _current.SetLength(0);
        _playlist.AddSegment(name, duration);
    }

    // ---- IM2TSDemuxerSink (PSI-only: OnPes never fires) ----

    public void OnProgramMapped(byte videoStreamType, ushort videoPid, byte audioStreamType, ushort audioPid)
    {
        _videoPid = videoPid;
        _audioPid = audioPid;
        _programMapped = videoPid != 0 || audioPid != 0;
    }

    public void OnPes(byte streamType, ulong? pts90k, ulong? dts90k, ReadOnlySpan<byte> es)
    {
    }
}
