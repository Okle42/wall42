#!/bin/bash
# 移除：停掉常駐、拿掉開機啟動、刪除執行檔。設定檔保留。
cd "$(dirname "$0")"
launchctl bootout "gui/$(id -u)/com.kang.wall91" 2>/dev/null || true
pkill -x wall91 2>/dev/null || true
rm -f "$HOME/Library/LaunchAgents/com.kang.wall91.plist"
rm -f "$HOME/.local/bin/wall91"

# 如果做過桌布同步，把系統桌布設回原本那張
ORIG="backup/original-wallpaper.txt"
if [ -f "$ORIG" ]; then
  P=$(cat "$ORIG")
  if [ -f "$P" ]; then
    osascript -e "tell application \"System Events\" to tell every desktop to set picture to \"$P\"" 2>/dev/null \
      && echo "系統桌布已還原：$P" \
      || echo "⚠ 自動還原失敗，請到系統設定手動選回：$P"
  else
    echo "⚠ 備份記錄的桌布檔案已不存在：$P"
  fi
  rm -f "$HOME/.config/wall91/wallpaper_a.png" "$HOME/.config/wall91/wallpaper_b.png"
fi
echo "已移除常駐與開機啟動。"
echo "設定檔保留在 $HOME/.config/wall91/config.json（要清就自己刪）"
echo "（沒做過桌布同步的話，系統桌布設定從頭到尾就沒被動過）"
