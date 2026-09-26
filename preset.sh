#!/bin/bash
# 切換預設風格。切換前會先備份目前的設定。
cd "$(dirname "$0")"
CFG="$HOME/.config/wall91/config.json"

CUR=$(./_current_preset.py 2>/dev/null)

if [ -z "$1" ]; then
  echo "可用的預設風格：（* = 目前使用中）"
  for f in presets/*.json; do
    n=$(basename "$f" .json)
    case $n in
      neon)      d="cyan / hot pink，賽博霓虹（目前預設）";;
      deepsea)   d="青綠到藍，慢速，結構綿密";;
      amber)     d="暖色琥珀，晚上看不刺眼";;
      starfield)  d="無連線，400 顆小點，星空感";;
      starfield2) d="星空加強版：冪次尺寸、獨立閃爍、藍白到暖白";;
      starfield-constellation) d="星空＋星座連線（只連亮星，約 96 條）";;
      starfield-web)           d="星空＋全連線網格（約 1000 條，較密）";;
      neural)     d="AI 運算：密連線＋流動脈衝（霓虹感）";;
      compute)    d="分工運算：無霓虹、單色線、連線不斷生滅的傳輸感";;
      thinking)   d="AI 思考：聚焦→放射查詢→回流→想通爆亮";;
      kang)       d="★ 你調的最新版（attention 模式）";;
      kang-v1)    d="★ 你調的第一版（traffic、239 傳輸、快速細脈衝）";;
      minimal)   d="純黑底、無連線、少量大光點";;
      snow)      d="下雪：雪花緩降左右飄，兩螢幕連續";;
      sand)      d="流沙：細沙流落下堆成沙丘，像沙漏";;
      sessions)  d="kang＋每個 Claude session 一個帶環光點";;
      *)         d="";;
    esac
    mark=" "; [ "$n" = "$CUR" ] && mark="*"
    printf " %s %-26s %s\n" "$mark" "$n" "$d"
  done
  echo
  echo "用法: ./preset.sh <名稱>        例: ./preset.sh deepsea"
  echo "還原: ./preset.sh --restore    （回到切換前的設定）"
  exit 0
fi

if [ "$1" = "--restore" ]; then
  [ -f "$CFG.bak" ] || { echo "沒有備份可還原"; exit 1; }
  cp "$CFG.bak" "$CFG"
  echo "已還原切換前的設定"
  exit 0
fi

SRC="presets/$1.json"
[ -f "$SRC" ] || { echo "找不到預設風格：$1"; echo "跑 ./preset.sh 看有哪些"; exit 1; }
[ -f "$CFG" ] && cp "$CFG" "$CFG.bak"
cp "$SRC" "$CFG"
echo "已套用 $1（存檔即生效，畫面應該立刻變了）"
