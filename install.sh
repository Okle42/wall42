#!/bin/bash
# 安裝：把執行檔放到固定位置並註冊開機自動啟動。
# binary 複製出去而不是直接指向專案目錄，這樣日後搬動專案不會壞掉。
set -e
cd "$(dirname "$0")"
REPO="$(pwd -P)"
BIN="$HOME/.local/bin/wall42"
PLIST="$HOME/Library/LaunchAgents/com.kang.wall42.plist"

# 先編譯：編譯失敗就停在這裡，目前在跑的版本原封不動
./build.sh

mkdir -p "$HOME/.local/bin"
# 執行中的 binary 不能直接覆寫，先停再換
launchctl bootout "gui/$(id -u)/com.kang.wall42" 2>/dev/null || true
pkill -x wall42 2>/dev/null || true
cp -f wall42 "$BIN"

cat > "$PLIST" <<PLIST_EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key><string>com.kang.wall42</string>
    <key>ProgramArguments</key><array><string>$BIN</string></array>
    <!-- presets／README／backup 的位置；常駐的 binary 在 ~/.local/bin，推不回 repo -->
    <key>EnvironmentVariables</key><dict><key>WALL42_REPO</key><string>$REPO</string></dict>
    <!-- 不指定 ProcessType 時 launchd 會把程序當背景工作，排到 E-core 並限流，
         同樣的繪製工作會多花 5 倍 CPU 時間（實測 3.31% vs 0.61%）。
         這是會持續繪製的前景視覺程式，必須宣告 Interactive。 -->
    <key>ProcessType</key><string>Interactive</string>
    <key>RunAtLoad</key><true/>
    <key>KeepAlive</key><true/>
    <key>StandardOutPath</key><string>$HOME/Library/Logs/wall42.log</string>
    <key>StandardErrorPath</key><string>$HOME/Library/Logs/wall42.log</string>
</dict>
</plist>
PLIST_EOF

plutil -lint "$PLIST" >/dev/null
launchctl bootstrap "gui/$(id -u)" "$PLIST"
echo "已安裝並啟動"
echo "  執行檔  : $BIN"
echo "  設定檔  : $HOME/.config/wall42/config.json   （存檔即生效）"
echo "  log     : $HOME/Library/Logs/wall42.log"
echo "  停用    : ./uninstall.sh"
