#!/bin/bash
# wall91 自身 CPU 量測（不需要 sudo）。
# 暫停常駐 → 用指定執行檔跑一段時間 → 從它自己的 getrusage 紀錄取平均 → 重啟常駐。
# 量的是 wall91 行程本身；WindowServer 的份量 ps 量不準，要用 bench_powermetrics.sh。
#
# 用法：./bench_cpu.sh [執行檔] [秒數] [標籤]      其餘條件用環境變數帶，例如
#       WALL91_ONLY_MAIN=1 ./bench_cpu.sh ./wall91 30 單螢幕
# 注意：量測期間桌面若被視窗整片蓋住，wall91 會停畫、量到的是 0，請讓桌面露出一部分。
set -u
cd "$(dirname "$0")"
BIN=${1:-./wall91}
SECS=${2:-30}
TAG=${3:-run}
WARM=5
LABEL="gui/$(id -u)/com.kang.wall91"
PLIST="$HOME/Library/LaunchAgents/com.kang.wall91.plist"
mkdir -p logs
OUT="logs/benchcpu_$(date +%H%M%S)_${TAG}.log"

launchctl bootout "$LABEL" 2>/dev/null; pkill -x wall91 2>/dev/null; sleep 1
WALL91_DURATION=$((SECS + WARM + 1)) "$BIN" > "$OUT" 2>&1
launchctl bootstrap "gui/$(id -u)" "$PLIST" 2>/dev/null

python3 - "$OUT" "$WARM" "$TAG" <<'PY'
import re, sys
path, warm, tag = sys.argv[1], int(sys.argv[2]), sys.argv[3]
rows = [l for l in open(path) if "visible" in l or "OCCLUDED" in l or "SUSPENDED" in l]
rows = rows[warm:]
cpu = [float(m.group(1)) for l in rows for m in [re.search(r"cpu=([\d.]+)%", l)] if m]
fps = [float(m.group(1)) for l in rows for m in [re.search(r"fps=([\d.]+)", l)] if m]
mem = [float(m.group(1)) for l in rows for m in [re.search(r"mem=([\d.]+)MB", l)] if m]
avg = lambda a: sum(a) / len(a) if a else float("nan")
print(f"[{tag}] n={len(cpu)}  cpu={avg(cpu):.2f}%  (min {min(cpu, default=0):.2f} / max {max(cpu, default=0):.2f})  fps={avg(fps):.1f}  mem={avg(mem):.1f}MB  log={path}")
PY
