#!/bin/bash
# 把 wall91 目前的畫面抓一張設成系統桌布。
# 這樣系統設定顯示的就是它，開機到 wall91 啟動之間的空窗期也不會跳回舊桌布。
# 原本的桌布路徑會備份到 backup/original-wallpaper.txt，uninstall.sh 會還原。
cd "$(dirname "$0")"
if ! pgrep -x wall91 >/dev/null; then
  echo "wall91 沒有在執行。先跑 ./install.sh 或 ./wall91"
  exit 1
fi
mkdir -p "$HOME/.config/wall91"
touch "$HOME/.config/wall91/.sync-request"
echo "已送出同步要求，等常駐實例擷取畫面…"
for i in 1 2 3 4 5 6 7 8; do
  sleep 1
  if grep -q "同步桌布完成" <(tail -12 "$HOME/Library/Logs/wall91.log" 2>/dev/null); then
    tail -3 "$HOME/Library/Logs/wall91.log" | grep -E "同步桌布|備份原本"
    exit 0
  fi
done
echo "逾時，看看 log：tail ~/Library/Logs/wall91.log"
exit 1
