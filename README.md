Spangle Media Server
====================

Media server for those who want to sparkle✨ — a live streaming server in almost
pure C#, aiming for clarity of data models / data flow / state, minimal
allocations on modern IO, and top-tier .NET performance.

> Pre-1.0 / under active development. Interfaces may change without notice.

Current status
--------------

Two first-class ingest protocols, one canonical internal form, two output shapes:

- **Ingest**: RTMP (classic + enhanced; H.264/H.265/AV1 + AAC) and
  **SRT** (MPEG-TS; H.264/H.265 + AAC, streamid routing, optional passphrase encryption)
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
- [ ] Tail frames can be lost on abrupt publisher disconnect
- [ ] CI for this repository (build + test on push, like the sibling repos)
- [x] H.265 over SRT/TS ingest (hvcC built from in-band VPS/SPS/PPS, dimensions
      parsed from the SPS; both TS and CMAF outputs verified)
- [ ] SRT→TS-HLS currently demuxes and re-muxes TS; a passthrough/re-segment
      path would halve that cost

### Mid term — features

- [ ] Timed metadata end-to-end: AMF events → data frames → ID3 (TS) / emsg (CMAF);
      the Spinner plugin point exists for exactly this
- [ ] Opus audio (CMAF path), audio-only streams
- [ ] LL-HLS playlist delta updates (`_HLS_skip`)
- [ ] DASH / LL-DASH (low priority)
- [ ] RTSP ingest (very low priority)

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
