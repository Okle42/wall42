#!/bin/bash
# 把目前調好的設定存成一個可切換的風格。
# 用法: ./save-preset.sh <名稱> [說明]
cd "$(dirname "$0")"
NAME="${1:-}"
[ -z "$NAME" ] && { echo "用法: ./save-preset.sh <名稱> [說明]"; exit 1; }
case "$NAME" in *[!a-zA-Z0-9_-]*) echo "名稱只能用英數、底線、減號"; exit 1;; esac
CFG="$HOME/.config/wall91/config.json"
[ -f "$CFG" ] || { echo "找不到設定檔 $CFG"; exit 1; }

# 同名的先備份，不要默默蓋掉
if [ -f "presets/$NAME.json" ]; then
  mkdir -p backup/styles
  cp "presets/$NAME.json" "backup/styles/${NAME}_replaced_$(date +%Y%m%d_%H%M%S).json"
  echo "原本的 $NAME 已備份到 backup/styles/"
fi
mkdir -p backup/styles
cp "$CFG" "backup/styles/${NAME}_$(date +%Y%m%d_%H%M).json"
python3 -c "
import json, sys, pathlib, os
c = json.load(open(os.path.expanduser('~/.config/wall91/config.json')))
c.pop('ui', None)
pathlib.Path('presets/$NAME.json').write_text(json.dumps(c, indent=2, sort_keys=True))
"
# 存完一定要回頭比對。只確認「config 對應到某個 preset」是不夠的 ——
# 兩邊同時錯成一樣的內容時，那種檢查也會通過。
python3 - "$NAME" <<'PYEOF'
import json, os, sys
name = sys.argv[1]
cfg = json.load(open(os.path.expanduser("~/.config/wall91/config.json")))
pre = json.load(open(f"presets/{name}.json"))
cfg.pop("ui", None); pre.pop("ui", None)
if json.dumps(cfg, sort_keys=True) != json.dumps(pre, sort_keys=True):
    print("✗ 存檔後比對不一致！設定可能在存檔過程中被改動，請再存一次")
    sys.exit(1)
m = pre["motion"]
print(f"已存成風格：{name}")
print(f"  驗證通過：mode={m['link'].get('mode')} targetCount={m['link'].get('targetCount')} "
      f"glow={m.get('glow')} brightness={m.get('brightness'):.3f}")
PYEOF
[ $? -ne 0 ] && exit 1
echo "（./preset.sh $NAME 可隨時叫回來）"
