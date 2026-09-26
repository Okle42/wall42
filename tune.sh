#!/bin/bash
# 開啟調參數面板（等同選單列的「調整參數…」）
if ! pgrep -x wall91 >/dev/null; then
  echo "wall91 沒有在執行，先跑 ./install.sh"
  exit 1
fi
mkdir -p "$HOME/.config/wall91"
touch "$HOME/.config/wall91/.show-panel"
echo "面板開啟中…"
