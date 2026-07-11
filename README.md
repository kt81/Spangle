Spangle Media Server
====================

Media server for those who want to sparkle✨ — a live streaming server in almost
pure C#, aiming for clarity of data models / data flow / state, minimal
allocations on modern IO, and top-tier .NET performance.

> Pre-1.0 / under active development. Interfaces may change without notice.

Current status
--------------

Two first-class ingest protocols, one canonical internal form, two output shapes:

- **Ingest**: RTMP (classic + enhanced; H.264/H.265/AV1 + AAC/Opus) and
  **SRT** (MPEG-TS; H.264/H.265 + AAC/Opus, streamid routing, optional passphrase
  encryption)
- **Output**: HLS with MPEG-2 TS segments, or CMAF/fMP4 — optionally **LL-HLS**
  (partial segments + blocking playlist reload), served over HTTP with per-stream
  routing (`/hls/{stream}/playlist.m3u8`)
- **Performance** (see [docs/PERFORMANCE.md](docs/PERFORMANCE.md)): zero allocations
  on the steady-state media path; RTMP chunk parsing at ≈3.2 GB/s; TS keyframe
  packetization at ~464 ns — faster than our hand-packed baseline, through the
  declarative bit fields of
  [Spangle.LusterBits](https://github.com/kt81/Spangle.LusterBits)

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the data flow, the data formats
at each boundary, and where state lives.

Quick try
---------

```console
$ dotnet run --project src/Spangle.MediaServer

# publish via RTMP:
$ ffmpeg -re -f lavfi -i testsrc2=size=640x360:rate=30 -f lavfi -i sine=frequency=440 \
    -c:v libx264 -g 60 -pix_fmt yuv420p -c:a aac -f flv rtmp://localhost:1935/live/test

# ...or via SRT:
$ ffmpeg -re -f lavfi -i testsrc2=size=640x360:rate=30 -f lavfi -i sine=frequency=440 \
    -c:v libx264 -g 60 -pix_fmt yuv420p -c:a aac -f mpegts "srt://localhost:9998?streamid=test"

# then open http://localhost:8080/ (test player) or http://localhost:8080/hls/test/playlist.m3u8
```

Configuration lives in `src/Spangle.MediaServer/spanglesettings.yaml`
(ports, segment format TS/fMP4, LL-HLS, SRT passphrase), overridable with
`SMS_`-prefixed environment variables.

Family
------

| Package | What |
|---|---|
| [Spangle.LusterBits](https://www.nuget.org/packages/Spangle.LusterBits) | Declarative bit fields for binary protocols (C# source generator); every TS/RTMP wire structure here is declared with it — the muxer composes and the demuxer reads through the same structs |
| [Spangle.Net.Transport.SRT](https://www.nuget.org/packages/Spangle.Net.Transport.SRT) | Self-contained SRT transport (native libsrt + mbedTLS bundled for 5 RIDs) |

Roadmap
-------

### Near term — correctness & operations

- [x] Publish authorization hook (`IPublishAuthorizer` in DI; default allows all —
      register your own to validate RTMP stream keys / SRT streamids)
- [x] Same-name publishers: the newest session takes over by default (last-wins,
      so a reconnect after a crash just works); the output continues under the
      same playlist with an `EXT-X-DISCONTINUITY`. First-wins or liveness-aware
      policies are one authorizer away
- [x] Tail frames survive an abrupt publisher disconnect (the pipeline drains
      into a final fractional segment before the playlist is finalized)
- [x] CI for this repository (build + test on push, like the sibling repos)
- [x] H.265 over SRT/TS ingest (hvcC built from in-band VPS/SPS/PPS, dimensions
      parsed from the SPS; both TS and CMAF outputs verified)
- [x] SRT→TS-HLS passthrough: source TS packets are re-segmented as-is (cuts at
      random-access PES starts, cached PAT/PMT injected per segment) instead of
      demux+remux; on by default, `Hls.TsPassthrough: false` restores the old path
- [x] In-memory HLS output (`IHLSStorage`): the live window is served straight from
      process memory by default — nothing touches disk, works on read-only
      filesystems; `Hls.Storage: File` keeps the output on disk as an archive

### Mid term — features

- [x] Timed metadata, source-driven: RTMP data events (onTextData, cue points, ...)
      → `AmfDataToId3Spinner` (the first DI-composed pipeline plugin) → timed ID3
      as stream_type 0x15 PES (TS) / ID3-in-emsg (CMAF); `Rtmp.TimedMetadata: false`
      removes the spinner hop
- [x] Timed metadata, server-injected: `POST /api/streams/{key}/metadata`
      (`{"name":..,"value":..}`) → `TimedMetadataInjector` spinner stamps it onto
      the media timeline; timed ID3 in the source TS (stream_type 0x15) also passes
      through from SRT ingest. Not available on the raw TS passthrough path.
- [x] Audio-only streams over SRT/TS ingest (video-less PMT with the PCR on the
      audio PID; both TS and CMAF outputs cut segments on the audio timeline)
- [x] Opus audio, end to end on the CMAF path: enhanced-RTMP v2 envelope and
      Opus-over-TS (SRT) ingest → 'Opus' sample entry + dOps output. The TS output
      drops Opus with a warning (no interoperable HLS/TS mapping exists) — except
      SRT passthrough, which carries the source's Opus PES verbatim
- [x] Audio-only over RTMP: with no in-protocol way to declare "no video is
      coming", a session with audio but no video codec is wired audio-only after
      `Rtmp.AudioOnlyFallbackMs` (default 3s; 0 disables)
- [x] LL-HLS playlist delta updates (`?_HLS_skip=YES` → `EXT-X-SKIP`); pays off
      with a larger `Hls.PlaylistWindow`
- [x] DASH v1: a live-profile MPD (SegmentTimeline) over the same CMAF segments the
      HLS side serves — one publisher session, two manifests, zero extra media.
      `/hls/{stream}/manifest.mpd`, test player at `/dash.html?s={stream}`.
      Note: ffmpeg's dash demuxer cannot read muxed representations (its own
      limitation); real MSE players (dash.js) play it — verified live
- [ ] Demuxed CMAF tracks (separate video/audio AdaptationSets and HLS renditions);
      the shared foundation for clean DASH, ABR ladders and MoQ tracks
- [ ] LL-DASH: chunked-transfer delivery of in-progress segments. By design this
      requires the memory storage backend (a future DVR would serve from memory
      while archiving to files; a file-only LL-DASH mode is out of spec)
- [ ] RTSP pull ingest (IP cameras): explicit numbered control flow like the RTMP
      receiver, with vendor dialects as a delegate table; TCP-interleaved first
- [ ] MoQ (Media over QUIC) ingest/egress: draft target and interop peer to be
      pinned at kickoff; raw QUIC first, WebTransport for browsers after

### Long term — goals of effort

- Web console (multi-server control, monitoring, settings)
- Failover with very little gap
- DRM / transcoder integration (undecided)

### Guiding constraints

- Target mainly UGC content
- Almost pure C# (native only where a protocol demands it, e.g. libsrt)
- Clarity first: an implementer should be able to read the declarations like
  the protocol specs they mirror
- Highly customizable (plugins via Spinners, forks welcome)

License
-------

The server is licensed under **AGPL-3.0** (see [LICENSE](./LICENSE)).
The reusable building blocks — LusterBits and the SRT transport — are published
separately under MIT, so embedding them in your own software carries no copyleft.
