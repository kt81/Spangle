#!/usr/bin/env bash
# Spangle load test: pushes N synthetic RTMP publishers into a Release MediaServer
# while collecting .NET runtime counters (CPU, allocation rate, GC counts).
#
# Usage: tools/loadtest.sh [publishers=4] [seconds=30] [format=TS|fMP4] [lowlatency=false] [realtime=true]
#   realtime=true  : ffmpeg paces at wall clock (-re), measures steady-state cost
#   realtime=false : ffmpeg pushes as fast as it encodes, measures throughput ceiling
#
# Requires: ffmpeg, dotnet-counters (dotnet tool install -g dotnet-counters)
set -eu

N=${1:-4}
DUR=${2:-30}
FMT=${3:-TS}
LL=${4:-false}
RT=${5:-true}

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/loadtest-out"
SERVER_DIR="$ROOT/src/Spangle.MediaServer"
mkdir -p "$OUT"
rm -f "$OUT"/counters.csv "$OUT"/server.log "$OUT"/ffmpeg*.log
rm -rf "$SERVER_DIR/hls-out"

echo "== Building (Release) =="
dotnet build -c Release "$SERVER_DIR" -v q --nologo

echo "== Starting server (format=$FMT lowLatency=$LL) =="
(
  cd "$SERVER_DIR"
  SMS_Spangle__Hls__SegmentFormat=$FMT SMS_Spangle__Hls__LowLatency=$LL \
    exec dotnet "$SERVER_DIR/bin/Release/net10.0/Spangle.MediaServer.dll" > "$OUT/server.log" 2>&1
) &
SERVER_JOB=$!
sleep 4

# The Windows PID of the server process (bash job pids are not usable for dotnet-counters)
SERVER_PID=$(netstat -ano | grep -E "TCP.*:8080 .*LISTENING" | head -1 | awk '{print $NF}')
if [ -z "$SERVER_PID" ]; then
  echo "Server did not start; see $OUT/server.log" >&2
  exit 1
fi

echo "== Collecting counters (pid=$SERVER_PID) =="
COLLECT_DUR=$(printf '00:00:%02d' $((DUR + 5)))
dotnet-counters collect -p "$SERVER_PID" --format csv -o "$OUT/counters.csv" \
  --duration "$COLLECT_DUR" --counters System.Runtime > "$OUT/counters.log" 2>&1 &
COUNTERS_PID=$!

REFLAG=""
[ "$RT" = "true" ] && REFLAG="-re"

echo "== Starting $N publishers (720p30 x264 ~2.5Mbps + AAC, ${DUR}s, realtime=$RT) =="
FFPIDS=()
for i in $(seq 1 "$N"); do
  ffmpeg $REFLAG -f lavfi -i "testsrc2=size=1280x720:rate=30" \
    -f lavfi -i "sine=frequency=440:sample_rate=44100" \
    -t "$DUR" -c:v libx264 -preset veryfast -b:v 2500k -g 60 -pix_fmt yuv420p \
    -c:a aac -b:a 128k -f flv "rtmp://127.0.0.1:1935/live/load$i" \
    > "$OUT/ffmpeg$i.log" 2>&1 &
  FFPIDS+=($!)
done
wait "${FFPIDS[@]}" || true
echo "== Publishers finished; waiting for the collector =="
wait $COUNTERS_PID || true
taskkill //F //PID "$SERVER_PID" > /dev/null 2>&1 || true
kill $SERVER_JOB 2>/dev/null || true

echo "== Summary ($OUT/counters.csv) =="
awk -F',' '
NR > 1 {
    name = $3; val = $5 + 0;
    cnt[name]++; tot[name] += val;
    if (val > max[name]) max[name] = val;
}
function mean(n) { return cnt[n] > 0 ? tot[n] / cnt[n] : 0 }
END {
    cpuUser = "dotnet.process.cpu.time (s / 1 sec)[cpu.mode=user]";
    cpuSys  = "dotnet.process.cpu.time (s / 1 sec)[cpu.mode=system]";
    alloc   = "dotnet.gc.heap.total_allocated (By / 1 sec)";
    loh     = "dotnet.gc.last_collection.heap.size (By)[gc.heap.generation=loh]";
    ws      = "dotnet.process.memory.working_set (By)";
    pause   = "dotnet.gc.pause.time (s / 1 sec)";
    gen0    = "dotnet.gc.collections ({collection} / 1 sec)[gc.heap.generation=gen0]";
    gen1    = "dotnet.gc.collections ({collection} / 1 sec)[gc.heap.generation=gen1]";
    gen2    = "dotnet.gc.collections ({collection} / 1 sec)[gc.heap.generation=gen2]";

    printf "CPU (cores, user+sys)     mean=%.3f  max=%.3f\n", mean(cpuUser) + mean(cpuSys), max[cpuUser] + max[cpuSys];
    printf "Allocation rate           mean=%.2f MB/s  max=%.2f MB/s\n", mean(alloc) / 1048576, max[alloc] / 1048576;
    printf "GC collections            gen0=%d gen1=%d gen2=%d (total)\n", tot[gen0], tot[gen1], tot[gen2];
    printf "GC pause time             total=%.3f s\n", tot[pause];
    printf "LOH size                  max=%.2f MB\n", max[loh] / 1048576;
    printf "Working set               max=%.1f MB\n", max[ws] / 1048576;
}' "$OUT/counters.csv"
