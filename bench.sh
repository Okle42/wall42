#!/bin/bash
# Phase 0 效能測試矩陣：粒子數 × 影格率
# CPU 由 app 自己用 getrusage 量（ps -o %cpu 給的是累計平均，不能用）
# GPU 由 ioreg 讀 IOAccelerator 的 Device Utilization %（不需要 sudo）
cd "$(dirname "$0")"
mkdir -p logs

SECS=${SECS:-12}
WARMUP=${WARMUP:-3}
STAMP=$(date +%Y%m%d_%H%M%S)
OUT="logs/bench_${STAMP}.csv"
PROGRESS="logs/bench_progress.log"

gpu() {
  ioreg -r -d 1 -c IOAccelerator 2>/dev/null \
    | grep -o '"Device Utilization %"=[0-9]*' | head -1 | cut -d= -f2
}

say() { echo "[$(date +%H:%M:%S)] $*" | tee -a "$PROGRESS"; }

: > "$PROGRESS"
say "=== Phase 0 效能測試矩陣開始（每組 ${SECS}s，暖機 ${WARMUP}s）==="

# 先量閒置基線：GPU 使用率是全系統的，要扣掉背景本來就有的用量
say "量測閒置基線..."
BASE_SUM=0
for i in 1 2 3 4 5; do
  g=$(gpu); BASE_SUM=$((BASE_SUM + ${g:-0})); sleep 1
done
BASE_GPU=$(echo "scale=1; $BASE_SUM / 5" | bc)
say "閒置 GPU 基線 = ${BASE_GPU}%"

echo "particles,fps,cpu_pct,gpu_pct,gpu_minus_base,mem_mb,actual_fps,links" > "$OUT"

TOTAL=9
DONE=0
for P in 120 200 300; do
for F in 24 30 60; do
  DONE=$((DONE + 1))
  say "[$DONE/$TOTAL] particles=$P fps=$F ..."
  LOG="logs/run_${P}_${F}.log"

  WALL42_FORCE_DRAW=1 WALL42_PARTICLES=$P WALL42_FPS=$F WALL42_DURATION=$((SECS + WARMUP + 2)) \
    ./wall42 > "$LOG" 2>&1 &
  PID=$!

  sleep $WARMUP   # 跳過啟動期，讓 fps 與 CPU 穩定

  GPU_SUM=0; N=0
  for i in $(seq 1 $SECS); do
    g=$(gpu); GPU_SUM=$((GPU_SUM + ${g:-0})); N=$((N + 1))
    sleep 1
  done
  wait $PID 2>/dev/null

  GPU_AVG=$(echo "scale=1; $GPU_SUM / $N" | bc)
  GPU_DELTA=$(echo "scale=1; $GPU_AVG - $BASE_GPU" | bc)

  # 從 app 自己的 log 取穩定段的平均（跳過前 WARMUP 行 visible 紀錄）
  read CPU MEM FPS LINKS <<< $(
    grep "visible" "$LOG" | tail -n +$((WARMUP + 1)) | python3 -c "
import sys, re
cpu=[]; mem=[]; fps=[]; lk=[]
for line in sys.stdin:
    m = re.search(r'fps=([\d.]+).*cpu=([\d.]+)%.*links=(\d+).*mem=([\d.]+)MB', line)
    if m:
        fps.append(float(m.group(1))); cpu.append(float(m.group(2)))
        lk.append(int(m.group(3)));    mem.append(float(m.group(4)))
def avg(a): return sum(a)/len(a) if a else 0
print(f'{avg(cpu):.2f} {avg(mem):.1f} {avg(fps):.1f} {avg(lk):.0f}')
"
  )

  echo "$P,$F,$CPU,$GPU_AVG,$GPU_DELTA,$MEM,$FPS,$LINKS" >> "$OUT"
  say "    cpu=${CPU}%  gpu=${GPU_AVG}% (淨 ${GPU_DELTA}%)  mem=${MEM}MB  實際fps=${FPS}  連線=${LINKS}"
done
done

say "=== 完成，結果寫入 $OUT ==="
column -s, -t "$OUT" | tee -a "$PROGRESS"
