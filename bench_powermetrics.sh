#!/bin/bash
# wall42 真實耗電量測（含 WindowServer 的份量）—— 需要 sudo，請使用者自己跑。
#
# 為什麼要這支：ps / getrusage 只量得到 wall42 自己，但桌布層每一幀都要
# WindowServer 合成，那部分 ps 量不準（閒置基線自己就會跳 25%）。
# powermetrics 直接讀 CPU/GPU 功耗與每個行程的 CPU ms/s，才看得到總帳。
#
# 怎麼跑（約 3 分鐘）：
#   1. 關掉會動的東西：影片、音樂、瀏覽器分頁、正在跑的 build／AI 任務
#   2. 按 F11（或四指張開）顯示桌面，讓兩台螢幕的桌布都露出來 —— 被視窗蓋住時
#      wall42 會停畫，量到的就不是「在畫」的成本
#   3. 在終端機執行：  ./bench_powermetrics.sh
#      會先問一次電腦登入密碼（sudo），之後全自動，期間不要碰滑鼠鍵盤
#      （動到滑鼠會改變焦點螢幕的 fps，也會讓 WindowServer 多做事）
#   4. 跑完會印出對照表，並存到 logs/powermetrics_日期時間/
#
# 三段量測，每段 SECS 秒（預設 45）：
#   A. wall42 停掉（系統靜態桌布）  → 基線
#   B. wall42 執行中、兩螢幕都可見   → 實際成本
#   C. wall42 執行中、只開主螢幕     → 單螢幕成本（WALL42_ONLY_MAIN=1）
# 結束時一定會把常駐 wall42 開回來（包含 Ctrl-C 中斷時）。
set -u
cd "$(dirname "$0")"
SECS=${SECS:-45}
SETTLE=8
LABEL="gui/$(id -u)/com.kang.wall42"
PLIST="$HOME/Library/LaunchAgents/com.kang.wall42.plist"
BIN="$HOME/.local/bin/wall42"
OUT="logs/powermetrics_$(date +%Y%m%d_%H%M%S)"
mkdir -p "$OUT"

restore() {
  pkill -x wall42 2>/dev/null
  sleep 1
  launchctl bootstrap "gui/$(id -u)" "$PLIST" 2>/dev/null
  echo "（常駐 wall42 已恢復）"
}
trap 'restore; exit 1' INT TERM

echo "需要 sudo 權限來執行 powermetrics（只讀取功耗，不改任何設定）"
sudo -v || { echo "沒有取得 sudo，結束"; exit 1; }

measure() {   # $1 = 段名
  echo "[$(date +%H:%M:%S)] 量測 $1（${SECS} 秒）…"
  sudo powermetrics --samplers cpu_power,gpu_power,tasks --show-process-energy \
       -i 1000 -n "$SECS" > "$OUT/$1.txt" 2>/dev/null
}

stop_all() { launchctl bootout "$LABEL" 2>/dev/null; pkill -x wall42 2>/dev/null; sleep 2; }

# A. 基線
stop_all
sleep "$SETTLE"
measure A_off

# B. 兩螢幕
launchctl bootstrap "gui/$(id -u)" "$PLIST"
sleep "$SETTLE"
measure B_two_screens

# C. 單螢幕（用同一個執行檔、只開主螢幕）
stop_all
WALL42_ONLY_MAIN=1 WALL42_DURATION=$((SECS + SETTLE + 5)) "$BIN" > "$OUT/C_wall42.log" 2>&1 &
sleep "$SETTLE"
measure C_main_only
wait 2>/dev/null

restore
trap - INT TERM

python3 - "$OUT" <<'PY'
import re, sys, os
out = sys.argv[1]
def parse(name):
    t = open(os.path.join(out, name + ".txt"), errors="replace").read()
    def avg(pat):
        v = [float(x) for x in re.findall(pat, t)]
        return sum(v) / len(v) if v else float("nan")
    # tasks 表格：名稱 ... CPU ms/s ... ；只取在意的兩個行程
    def proc(nm):
        v = []
        for line in t.splitlines():
            if line.startswith(nm):
                cols = line.split()
                # 欄位：Name ID CPU_ms/s User% Deadlines... ；名稱可能含空白，從名稱後面找第一個浮點數
                nums = [c for c in cols[len(nm.split()):] if re.match(r"^-?[\d.]+$", c)]
                if len(nums) >= 2:
                    v.append(float(nums[1]))     # nums[0] 是 PID，nums[1] 是 CPU ms/s
        return sum(v) / len(v) if v else 0.0
    return {
        "cpu_mW": avg(r"CPU Power:\s*([\d.]+)\s*mW"),
        "gpu_mW": avg(r"GPU Power:\s*([\d.]+)\s*mW"),
        "combined_mW": avg(r"Combined Power \(CPU \+ GPU \+ ANE\):\s*([\d.]+)\s*mW"),
        "WindowServer_ms": proc("WindowServer"),
        "wall42_ms": proc("wall42"),
    }
rows = [("A 停掉（基線）", "A_off"), ("B 兩螢幕", "B_two_screens"), ("C 只開主螢幕", "C_main_only")]
res = {k: parse(f) for k, f in rows}
base = res["A 停掉（基線）"]
print()
print(f"{'情境':<14}{'CPU mW':>9}{'GPU mW':>9}{'合計 mW':>10}{'Δ合計':>9}{'WindowServer ms/s':>19}{'wall42 ms/s':>13}")
for k, _ in rows:
    r = res[k]
    print(f"{k:<14}{r['cpu_mW']:>9.0f}{r['gpu_mW']:>9.0f}{r['combined_mW']:>10.0f}"
          f"{r['combined_mW'] - base['combined_mW']:>+9.0f}{r['WindowServer_ms']:>19.1f}{r['wall42_ms']:>13.1f}")
print()
print("ms/s ＝ 每秒用掉幾毫秒 CPU（10 ms/s ≈ 單核 1%）。Δ合計 是相對基線多耗的功率。")
print(f"原始資料：{out}/")
PY
