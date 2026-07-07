Spangle Media Server (WIP)
===================

THIS PROJECT IS WORKING IN PROGRESS
====================================

Current status: RTMP (classic + enhanced) ingest of H.264/H.265 + AAC works end-to-end
into HLS (MPEG-2 TS segments + live playlist), served over HTTP by `Spangle.MediaServer`.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the data flow, the data formats at
each boundary, and where state lives.

Quick try:

```console
$ dotnet run --project src/Spangle.MediaServer
$ ffmpeg -re -f lavfi -i testsrc=size=640x360:rate=30 -f lavfi -i sine=frequency=440 \
    -c:v libx264 -g 60 -pix_fmt yuv420p -c:a aac -f flv rtmp://localhost:1935/live/test
# then open http://localhost:8080/ (test player) or http://localhost:8080/hls/playlist.m3u8
```

Planning Goal
------

### Media Streaming

- Ingest
  - RTMP (+ Enhanced)
  - RTSP (very low priority)
  - SRT
- Web Origin
  - HLS
  - LL-HLS
  - DASH (low priority)
  - LL-DASH (CMAF Chunked-Transfer) (low priority)
- Codecs
  - H.264
  - H.265
  - AV1
  - AAC
  - Opus

### Web Console (Goal of Effort)

- Control multiple servers
- Easy monitoring
- Setting editor

### Advanced Features (I don't know if I'm going to do it)

- DRM
- Transcoder integration (Hardware, x26X)

### Others

- Target mainly UGC content
- Almost pure C#
- High performance
- Highly customizable (plugin, fork, etc...)
- Fail over with very little gap (Goal of Effort)
- Out-of-the-box support for timed metadata 
