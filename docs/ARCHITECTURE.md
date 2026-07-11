# Architecture

Spangle turns live ingest protocols (RTMP and SRT) into web-deliverable streams
(HLS, LL-HLS/CMAF). This document describes the data flow, the data formats at
each boundary, and where state lives. It is written for someone implementing or modifying
this kind of system.

## Pipeline overview

One publisher connection = one pipeline instance. Every arrow below is a
`System.IO.Pipelines` pipe (backpressure-aware, single writer / single reader),
and every stage is a single async loop. There are two output shapes:

```
TS output (HLSSegmentFormat.MpegTs):

     TCP (RTMP) / SRT bytes        MediaFrame stream               MPEG-2 TS stream            files
publisher ──────► *ReceiverContext ──────► FlvToM2TSSpinner ──────► HLSSender ──────► *.ts / *.m3u8

CMAF output (HLSSegmentFormat.Fmp4, optionally LL-HLS):

     TCP (RTMP) / SRT bytes        MediaFrame stream
publisher ──────► *ReceiverContext ──────► CmafHLSSender ──────► init.mp4 / *.m4s (+parts) / *.m3u8
                                           (muxes ISO-BMFF itself; no spinner needed because
                                            canonical codec payloads are already valid fMP4 samples)
```

Both receivers (`RtmpReceiverContext`, `SRTReceiverContext`) emit the *same canonical
MediaFrame form*, so everything downstream is ingest-agnostic.

Output goes through `IHLSStorage`, keyed by the sanitized stream name, so multiple
publishers serve concurrently under `/hls/{stream}/...`. `MemoryHLSStorage` (the
MediaServer default) keeps the live window in process memory — no disk I/O, and blobs
the playlist trims are freed; `FileHLSStorage` writes
`<OutputDirectory>/<stream name>/` and the output doubles as an archive. Live
playlists are additionally published to the in-memory `HLSStreamRegistry` for LL-HLS
blocking reload.

- **Receiver** (`IReceiverContext`): speaks the ingest protocol and unwraps its envelope
  completely. Emits self-contained *media frames* (see below). All RTMP/FLV knowledge
  stays here.
- **Spinner** (`ISpinner`): a processing stage that owns one intake pipe and writes into
  the next stage's pipe. Spinners play two roles:
  - *format conversion* as the terminal stage — `FlvToM2TSSpinner` rebuilds codec
    payloads (AVC/HEVC length-prefixed NALUs → Annex B, AAC raw → ADTS) and muxes them
    into MPEG-2 TS
  - *interception (the plugin point)* — `LiveContext` accepts an ordered list of
    MediaFrame→MediaFrame spinners that are inserted between the receiver and the
    terminal stage, regardless of the output format. This is where cross-cutting
    processing plugs in later, e.g. turning AMF data events into timed metadata
    (ID3 for TS, `emsg` for CMAF), filtering, or on-the-fly transforms.
- **Sender** (`ISender`): delivers to viewers. `HLSSender` cuts the TS stream into segments
  and maintains a playlist; `CmafHLSSender` muxes MediaFrames itself. HTTP delivery is
  plain static file serving plus the in-memory playlist endpoint.
- **`LiveContext`** wires receiver → [media spinners…] → terminal once the leading codec
  is known: normally the video codec (`VideoCodecSet`), the audio codec when the source
  declared itself audio-only (TS PMT), or — for protocols that cannot declare it, like
  RTMP — an optional fallback timer that treats "audio present, no video in sight" as
  audio-only after a grace period.

Shutdown propagates through pipe completion: receiver completes its outlet → spinner
drains and completes its outlet → sender finalizes the last segment and writes
`#EXT-X-ENDLIST`.

## The media frame boundary

The receiver → spinner pipe carries `[MediaFrameHeader][payload]` records
(`Spinner/MediaFrame.cs`, host-endian, in-process only):

| field             | size | meaning                                                          |
|-------------------|------|------------------------------------------------------------------|
| Kind              | 1    | Video / Audio                                                     |
| Flags             | 1    | `KeyFrame` (random access point), `Config` (codec config, no media)|
| (reserved)        | 2    |                                                                    |
| Codec             | 4    | `VideoCodec` / `AudioCodec` enum value (FourCC-based)              |
| CompositionTimeMs | 4    | PTS − DTS in ms (B-frame reordering); 0 otherwise                  |
| Length            | 4    | payload bytes following this header                                |
| Timestamp         | 4    | DTS in milliseconds                                                |

Payloads at this boundary:

- Video `Config`: the raw codec configuration record (`avcC` for H.264, `hvcC` for H.265)
- Video frame: length-prefixed NALUs (the length size is declared in the config record)
- Audio `Config`: the AudioSpecificConfig (AAC, 2+ bytes) or the OpusHead
  identification header (Opus, 19+ bytes)
- Audio frame: one raw AAC frame or one Opus packet

Because each frame is self-contained (codec, timestamps, flags), a spinner needs no
side-channel and no knowledge of the ingest protocol.

**Contract for frames that arrive before the pipeline is wired** (the wiring triggers
on the first codec event, so early frames can see a null outlet): media frames may be
dropped — they could not be decoded without their config anyway — but `Config` frames
must be kept by the receiver and replayed into the outlet first, or the track stays
dead for the whole session. The RTMP receiver stashes an early AAC sequence header
for exactly this reason (video config cannot arrive early: it is itself the trigger).

## RTMP receiver

### Chunk → message assembly

RTMP multiplexes *messages* over *chunk streams*, and chunks of different chunk streams
are interleaved (audio chunks arrive between the chunks of a large video message).
Therefore header state and message assembly are kept **per chunk stream id**
(`Chunk/ChunkStreamState`): last message header fields, the computed absolute timestamp,
the last timestamp delta, and the partially assembled body.

The receive loop (`ReadState/00_ReadChunkHeader.cs`) processes exactly one chunk per
iteration:

1. read the basic header (fmt + csid) and the fmt-dependent message header
2. update the chunk stream's timestamp
   (Fmt0 absolute / Fmt1-2 delta / Fmt3 new-message reuses the last delta,
   Fmt3 continuation carries nothing)
3. append up to `ChunkSize` payload bytes to the chunk stream's assembly buffer
4. when the message is complete, dispatch it by message type id

The message type handlers live in files named after the type id:
`01_SetChunkSize`, `08_Audio`, `09_Video`, `18_DataAmf0`, `20_CommandAmf0`.
`08/09` unwrap the FLV tag (classic AVC and enhanced-RTMP envelopes) and emit media
frames; `18/20` parse AMF0 and drive the command handlers (`NetConnection`, `NetStream`).

Note: `ChunkSize` (incoming, announced by the peer) and `SendChunkSize` (outgoing,
announced by us) are independent. `RtmpWriter` splits outgoing messages into
`SendChunkSize` chunks with Fmt3 continuation headers.

### Connection state

`ReceivingState` tracks the protocol phase of one connection:

```
HandShaking → WaitingConnect → WaitingFCPublish → WaitingPublish → Publishing → Terminated
              (connect)        (releaseStream/    (publish)        (deleteStream/
                                FCPublish)                          FCUnpublish)
```

`Terminated` ends the receive loop. Timestamps, codecs and the media outlet also live on
`RtmpReceiverContext`; everything per-connection is reachable from that one object.

## MPEG-2 TS muxing

`Containers/M2TS/M2TSWriter` is a plain byte-level writer with per-PID continuity
counters. Fixed layout:

| PID    | content            | PES stream_id | PMT stream_type      |
|--------|--------------------|---------------|-----------------------|
| 0x0000 | PAT                | —             | —                     |
| 0x1000 | PMT                | —             | —                     |
| 0x0100 | video (PCR carrier)| 0xE0          | 0x1B (H.264) / 0x24 (H.265) |
| 0x0101 | audio              | 0xC0          | 0x0F (ADTS AAC)       |

Per video frame the spinner emits one PES packet (Annex B access unit: AUD, keyframes
additionally get the parameter sets re-injected so every segment can be decoded
independently). PCR (= DTS) rides on the first TS packet of every video PES.
Before each keyframe, PAT + PMT are re-sent; that pair marks a clean random access point
in the TS stream. Codecs without a standard TS mapping (AV1, VP9) are rejected — they
belong to the future fMP4/CMAF pipeline.

## HLS segmentation

TS mode: `HLSSegmenter` consumes 188-byte TS packets and only ever looks at three things:
PAT PUSI packets (potential cut points, since the muxer sends PAT right before
keyframes), PCR values (time), and everything else (opaque payload).
A segment is cut at the first keyframe whose PCR is at least `TargetSegmentDuration`
after the segment start; `EXTINF` is the exact PCR difference.

CMAF mode: `CmafSegmentBuilder` buffers `MediaFrame`s and builds fragments with
`CmafPackager` (`Containers/ISOBMFF`): the init segment carries the codec configuration
records verbatim (`avcC`/`hvcC`/`av1C` + `esds`), and each fragment is `styp+moof+mdat`.
Sample durations come from DTS deltas (video, 90kHz timescale) or are the fixed
1024 ticks per AAC frame (audio, sample-rate timescale).

Both modes share `HLSPlaylist` (sliding window, file deletion, `#EXT-X-ENDLIST` on
completion; `EXT-X-MAP`/version 6 for fMP4).

## LL-HLS

In low-latency mode (fMP4 only) each segment is built from ~`PartTargetDuration`-sized
fragments; every fragment is published immediately as an `#EXT-X-PART` file
(`segNNNNN.pNN.m4s`, `INDEPENDENT=YES` when it starts with a keyframe), and the segment
file is simply the concatenation of its parts. The playlist advertises
`#EXT-X-SERVER-CONTROL:CAN-BLOCK-RELOAD=YES`, `#EXT-X-PART-INF` and a
`#EXT-X-PRELOAD-HINT` for the next part.

Blocking reload works through `HLSStreamRegistry`: the builder publishes every playlist
update (text + newest MSN/part) to a per-stream `LivePlaylist`; the HTTP layer awaits
`_HLS_msn`/`_HLS_part` conditions on it before responding.

## Hosting (Spangle.MediaServer)

Kestrel listens on two ports from `spanglesettings.yaml`: the RTMP port with a raw
`ConnectionHandler` that runs the pipeline above, and an HTTP port. SRT ingest runs
beside Kestrel as a hosted service (`SrtIngestService`) since SRT is UDP-based and
brings its own listener. Live playlists are served from the registry (with blocking
reload); segments, parts, init files and ended playlists come from the configured
`IHLSStorage` — a small middleware for memory storage, static file serving with HLS
MIME types for file storage. The test player at `/` stays static. Options select the
segment format, LL-HLS, and the storage backend per server.

## SRT receiver

SRT carries MPEG-2 TS, so the ingest side is the mirror image of the TS output side:

```
SRT bytes ──► SRTReceiverContext ──► M2TSDemuxer ──► M2TSMediaFrameAdapter ──► MediaFrame stream
              (188-byte alignment,    (PAT/PMT,        (normalization)
               resync on loss)         per-PID PES
                                       reassembly)
```

- `M2TSDemuxer` is container-level only: PAT → PMT → per-PID PES reassembly with
  continuity checking (a lost packet drops the frame under assembly, not the stream).
  PES timestamps are read through the same LusterBits `PESTimestamp` struct the muxer
  composes with.
- `M2TSMediaFrameAdapter` normalizes elementary streams into the canonical MediaFrame
  form: H.264/H.265 Annex B access units become length-prefixed samples plus a Config
  frame built from the in-band parameter sets (avcC from SPS/PPS; hvcC from
  VPS/SPS/PPS via `HvcCBuilder`, which also extracts width/height from the SPS);
  ADTS AAC becomes raw AAC frames plus an AudioSpecificConfig; Opus (private PES
  0x06 with a registration descriptor "Opus") is unframed from its control headers
  into raw packets plus a synthesized OpusHead, with timestamps advanced per packet
  from the TOC; 33-bit 90 kHz PES timestamps are unwrapped to milliseconds. Because
  the output matches what the RTMP receiver emits, both HLS output paths work
  unchanged (Opus itself needs the CMAF output — the TS spinner drops it, while the
  TS passthrough forwards the source's Opus PES verbatim).
- Routing and security use the SRT counterparts of the RTMP stream key:
  `SRTClient.StreamId` (plain, or the `r=` key of Haivision Access Control ids) becomes
  the stream name; an optional listener passphrase (`Srt.Passphrase`) enforces wire
  encryption.
- **TS passthrough** (default for SRT→TS-HLS, `Hls.TsPassthrough`): since the source
  is already MPEG-2 TS, the demux→MediaFrame→remux round trip is skipped entirely.
  The receiver forwards the aligned packets raw (`RawTsPassthrough`), and
  `TSPassthroughSegmenter` re-segments them: a PSI-only `M2TSDemuxer` learns the
  program layout, cuts happen at random-access video PES starts (any audio PES start
  for audio-only programs) with durations from the PES DTS/PTS, and each segment
  begins with the latest cached PAT+PMT (their continuity counters are locally owned,
  since table packets are dropped from the flow and re-injected). Takeover handover
  works the same as the other senders. The MediaFrame spinner plugin point does not
  exist on this path — disable the option when spinners must run on SRT sessions.
- PSI sections spanning multiple TS packets are reassembled (per-PSI-PID section
  buffers with continuity checking), and audio-only programs are first-class: the
  adapter declares the source audio-only (PMT maps no video), the pipeline wires on
  the audio codec, and both output paths cut segments on the audio timeline — the
  TS muxer emits a video-less PMT with the PCR and random-access flags on the audio
  PID, and the CMAF packager builds an audio-only init segment.

## Publish authorization & takeover

Authorization is first-class: the mechanism lives in the library, the policy in the
host's DI. `LiveContext` wires an `IPublishGate` into the receiver, and each protocol
consults it at its natural rejection point — RTMP inside the `publish` command
(rejection = `NetStream.Publish.BadName`), SRT before any media is consumed.
The registered `IPublishAuthorizer` sees the request (protocol, raw name, sanitized
key, endpoint, and the existing same-name session if any) and answers
Allow / Deny / Takeover.

The default policy is allow-all with **last-wins** on contested names: a zombie
session blocking a publisher's reconnect is a worse failure than a takeover by a
holder of the same key. On takeover the previous session is shut down without
finalizing its output (no `EXT-X-ENDLIST`); its live playlist state (media sequence,
window) is stashed in the `HLSStreamRegistry` and the successor resumes it, marking
the boundary with `EXT-X-DISCONTINUITY`. Takeovers work across protocols — an SRT
publisher can take over an RTMP session of the same name.

## Known simplifications (as of now)

- Single program / fixed PIDs in TS output; PCR equals DTS (no PCR offset)
- LL-HLS: no rendition reports (`EXT-X-RENDITION-REPORT`; single-rendition output
  has nothing to report yet)
