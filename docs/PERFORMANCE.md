# Performance

Spangle aims to be allocation-minimal and CPU-efficient on top of modern .NET IO
(`System.IO.Pipelines`, spans, pooled buffers). This document describes how to measure it
and records the current baseline so regressions are visible.

## Micro-benchmarks

```console
$ cd benchmarks/Spangle.Core.Benchmarks
$ dotnet run -c Release -- --filter '*'            # full run
$ dotnet run -c Release -- --filter '*' --job short # quick pass
```

- `ChunkParsingBenchmarks` — the RTMP receive core (`TryReadChunk`): parses a synthetic
  session of interleaved 4KB video / 300B audio messages, split at the negotiated chunk size.
- `M2TSWriterBenchmarks` — muxing one access unit into TS packets
  (keyframe = PAT+PMT+PES with PCR/RAI; inter frame = PES only).
- `CmafPackagerBenchmarks` — building one CMAF fragment (moof+mdat) of a 1-second part
  (30×5KB video + 43×300B audio samples).

## Load test

```console
$ tools/loadtest.sh [publishers=4] [seconds=30] [format=TS|fMP4] [lowlatency=false] [realtime=true]
```

Builds the MediaServer in Release, starts it, pushes N synthetic publishers
(720p30 x264 ~2.5Mbps + AAC via ffmpeg), collects `dotnet-counters` (`System.Runtime`)
for the duration and prints CPU / allocation-rate / GC totals.
Requires ffmpeg and `dotnet tool install -g dotnet-counters`.
Set `realtime=false` to push as fast as ffmpeg encodes (throughput ceiling instead of
steady-state cost).

## Baseline (2026-07-08)

Environment: AMD Ryzen 7 7800X3D (8C/16T), Windows 11, .NET 10.0.9, Server GC.

### Micro (ShortRun)

| Benchmark | Case | Mean | Allocated |
|---|---|---:|---:|
| ChunkParsing.ParseSession | ChunkSize=128 (~2.23 MB session) | 693.8 µs (≈3.2 GB/s) | 0 B |
| ChunkParsing.ParseSession | ChunkSize=4096 | 93.7 µs (≈23.6 GB/s) | 0 B |
| M2TSWriter.KeyFrame | PAT+PMT+PES, 5KB AU | 463.5 ns | 0 B |
| M2TSWriter.InterFrame | PES, 5KB AU | 187.0 ns | 0 B |
| CmafPackager.BuildFragment | 30v+43a samples | 6.6 µs | 128 B |

The steady-state media path allocates nothing per frame; the 128 B in `BuildFragment`
is the `IReadOnlyList` enumerator (twice per part, irrelevant at part cadence).

Note on `M2TSWriter`: the TS header / adaptation field / PCR / PES timestamp writes go
through LusterBits bit-field structs (declaration mirrors the spec tables), composed
with the generated `ComposeTo()` factories straight into the packet span: one store
per byte with generation-time-folded masks and constant bits included, and no
intermediate struct. History of this path: per-field setters roughly doubled the
hand-packed baseline (510.8 / 162.3 ns), `Compose()` + `MemoryMarshal.Write` restored
parity (523.1 / 186.0 ns), and `ComposeTo()` now beats the hand-packed baseline on the
compose-heavy keyframe path (463.5 ns) by eliminating the stack struct and the copy.

### Load (4 publishers × 720p30 ≈ 10 Mbps aggregate ingest, 30 s, realtime)

| Metric | TS | fMP4 + LL-HLS (0.5s parts) |
|---|---:|---:|
| CPU (cores, mean) | 0.41 | 0.21 |
| Allocation rate (mean) | 2.84 MB/s | 2.71 MB/s |
| GC gen0 / gen1 / gen2 (total) | 4 / 1 / 2 | 4 / 1 / 2 |
| GC pause (total) | 7 ms | 8 ms |
| Working set (max) | 88 MB | 97 MB |

Observations:

- The GC is essentially idle under load (a handful of collections in 35 s, most of them
  startup); allocation rate is dominated by JIT/startup and per-segment bookkeeping.
- CMAF costs roughly half the CPU of the TS path. The TS pipeline pays a by-design
  clarity tax: the spinner muxes to TS and the segmenter re-parses it. If this ever
  matters, in-band segment-boundary hints can remove the second pass.
- Numbers above include ffmpeg handshake/teardown windows; treat them as an upper bound
  for the server's own cost.
