# Architecture

Spangle turns live ingest protocols (RTMP today, SRT later) into web-deliverable streams
(HLS today, LL-HLS/CMAF later). This document describes the data flow, the data formats at
each boundary, and where state lives. It is written for someone implementing or modifying
this kind of system.

## Pipeline overview

One publisher connection = one pipeline instance. Every arrow below is a
`System.IO.Pipelines` pipe (backpressure-aware, single writer / single reader),
and every stage is a single async loop.

```
        TCP bytes                MediaFrame stream               MPEG-2 TS stream            files
publisher ──────► RtmpReceiverContext ──────► FlvToM2TSSpinner ──────► HLSSender ──────► *.ts / *.m3u8
                  (Transport.Rtmp)            (Spinner)               (Transport.HLS)         │
                                                                                              ▼
                                                                                   Kestrel static files
                                                                                   (Spangle.MediaServer)
```

- **Receiver** (`IReceiverContext`): speaks the ingest protocol and unwraps its envelope
  completely. Emits self-contained *media frames* (see below). All RTMP/FLV knowledge
  stays here.
- **Spinner** (`ISpinner`): converts between media formats. `FlvToM2TSSpinner` rebuilds
  codec payloads (AVC/HEVC length-prefixed NALUs → Annex B, AAC raw → ADTS) and muxes them
  into MPEG-2 TS. A spinner owns one intake pipe and writes into the next stage's pipe.
- **Sender** (`ISender`): delivers to viewers. `HLSSender` cuts the TS stream into segments
  and maintains a playlist; HTTP delivery itself is plain static file serving.
- **`LiveContext`** wires one receiver to one sender by choosing a spinner once the video
  codec is known (`VideoCodecSet` event).

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
- Audio `Config`: AudioSpecificConfig (2+ bytes)
- Audio frame: one raw AAC frame

Because each frame is self-contained (codec, timestamps, flags), a spinner needs no
side-channel and no knowledge of the ingest protocol.

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

`HLSSegmenter` consumes 188-byte TS packets and only ever looks at three things:
PAT PUSI packets (potential cut points, since the muxer sends PAT right before
keyframes), PCR values (time), and everything else (opaque payload).
A segment is cut at the first keyframe whose PCR is at least `TargetSegmentDuration`
after the segment start; `EXTINF` is the exact PCR difference. The playlist keeps a
sliding window (live) and gets `#EXT-X-ENDLIST` when the stream completes.

## Hosting (Spangle.MediaServer)

Kestrel listens on two ports from `spanglesettings.yaml`: the RTMP port with a raw
`ConnectionHandler` that runs the pipeline above, and an HTTP port serving the HLS output
directory (`/hls/...`) plus a test player at `/`. One publisher at a time writes into the
configured output directory; per-stream routing is future work.

## Known simplifications (as of now)

- Single program / fixed PIDs; one publisher per output directory
- PCR equals DTS (no PCR offset); PCR ~every frame instead of a 100ms scheduler
- Sender contexts still expose separate `VideoIntake`/`AudioIntake`, but the muxed TS
  actually flows through `VideoIntake` — the interface predates the muxer and should be
  reshaped (e.g. a single `MediaIntake`) together with the CMAF work
- Extended timestamps (>0xFFFFFF ms ≈ 4.6h) are consumed but not fully exercised by tests
- Abrupt disconnects may lose the tail frames that were still in flight
