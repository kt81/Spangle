using System.IO.Pipelines;
using Spangle.Net.Moqt;
using Spangle.Net.Moqt.Wire;
using Spangle.Spinner;

namespace Spangle.Extensions.Moqt;

/// <summary>
/// Turns one LOC track's objects back into Spangle's canonical MediaFrames — the reverse of the
/// mapping <see cref="MoqSender"/> writes, and what makes MOQT an ingest and not only an egress.
/// A subscriber reads LOC objects (elementary frame in the payload, timestamp and codec config in
/// the Properties); this reassembles each into the in-band <see cref="MediaFrameHeader"/>-framed
/// form the rest of the pipeline already understands, so a MOQT source republishes through the
/// same HLS/CMAF path an RTMP or SRT source does.
/// <para>
/// One decoder per track. It emits a single Config frame — the decoder configuration the
/// downstream sender needs before any coded frame — the first time it has one, from the track's
/// catalog <c>initData</c> or from a keyframe's Video Config property, whichever comes first. LOC
/// audio has no config property at all, so for audio the catalog is the only source and it must be
/// supplied.
/// </para>
/// </summary>
public sealed class LocMediaDecoder
{
    private readonly MediaFrameKind _kind;
    private readonly uint _codec;
    private readonly LocDraft _draft;
    private byte[] _config;
    private bool _configEmitted;
    private bool _hasLastGroup;
    private ulong _lastGroupId;

    /// <summary>
    /// Creates a decoder for a track of <paramref name="kind"/> carrying <paramref name="codec"/>
    /// (a <see cref="VideoCodec"/> or <see cref="AudioCodec"/> value), written in
    /// <paramref name="draft"/>. <paramref name="initialConfig"/> is the decoder configuration from
    /// the catalog — required for audio, optional for video (whose keyframes carry their own).
    /// </summary>
    public LocMediaDecoder(MediaFrameKind kind, uint codec, LocDraft draft,
        ReadOnlyMemory<byte> initialConfig = default)
    {
        _kind = kind;
        _codec = codec;
        _draft = draft;
        _config = initialConfig.ToArray();
    }

    /// <summary>
    /// Decodes one object and writes the resulting frames to <paramref name="outlet"/>: the Config
    /// frame first if it has not gone out yet, then the media frame. A video object that opens a new
    /// group is a keyframe (LOC §4.2: a group boundary is an IDR boundary); every audio object is
    /// independently decodable.
    /// </summary>
    public void Decode(MoqObject moqObject, PipeWriter outlet)
    {
        ArgumentNullException.ThrowIfNull(moqObject);
        ArgumentNullException.ThrowIfNull(outlet);

        bool startsGroup = !_hasLastGroup || moqObject.GroupId != _lastGroupId;
        _lastGroupId = moqObject.GroupId;
        _hasLastGroup = true;

        (uint timestampMs, ReadOnlyMemory<byte> videoConfig) = ReadProperties(moqObject.Properties);

        // A keyframe may carry its own Video Config; prefer it and remember it (a mid-stream
        // resolution change re-sends it), falling back to what the catalog gave us.
        if (!videoConfig.IsEmpty)
        {
            _config = videoConfig.ToArray();
        }

        if (!_configEmitted && _config.Length > 0)
        {
            WriteFrame(outlet, MediaFrameFlags.Config, _config, timestampMs);
            _configEmitted = true;
        }

        // Without the decoder configuration a coded frame cannot be decoded downstream, so hold
        // media until it has been announced — a subscriber that joined mid-GoP waits for the next
        // keyframe, which carries it.
        if (!_configEmitted)
        {
            return;
        }

        MediaFrameFlags flags = _kind == MediaFrameKind.Video && startsGroup
            ? MediaFrameFlags.KeyFrame
            : MediaFrameFlags.None;
        WriteFrame(outlet, flags, moqObject.Payload.Span, timestampMs);
    }

    private (uint TimestampMs, ReadOnlyMemory<byte> VideoConfig) ReadProperties(
        IReadOnlyList<MoqKeyValuePair> extensions)
    {
        if (_draft == LocDraft.Draft01)
        {
            Loc01Metadata meta = Loc01Metadata.Read(extensions);
            // -01 states the timestamp in microseconds; MediaFrame counts milliseconds.
            uint ms = meta.CaptureTimestamp is { } us ? (uint)(us / 1000) : 0;
            return (ms, meta.VideoConfig);
        }

        Loc03Metadata m = Loc03Metadata.Read(extensions);
        return (Loc03TimestampMs(m), m.VideoConfig);
    }

    // -03 timestamps are counted in Timescale units per second, or — with no Timescale — are
    // wall-clock microseconds (§2.3.1.1). Either way the frame timeline downstream is milliseconds.
    private static uint Loc03TimestampMs(Loc03Metadata meta)
    {
        if (meta.Timestamp is not { } value)
        {
            return 0;
        }

        return meta.Timescale is { } timescale and > 0
            ? (uint)(value * 1000 / timescale)
            : (uint)(value / 1000);
    }

    private void WriteFrame(PipeWriter outlet, MediaFrameFlags flags, ReadOnlySpan<byte> payload, uint timestampMs)
    {
        MediaFrameHeader.Write(outlet, _kind, flags, _codec, compositionTimeMs: 0, payload.Length, timestampMs);
        payload.CopyTo(outlet.GetSpan(payload.Length));
        outlet.Advance(payload.Length);
    }
}
