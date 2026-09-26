#!/bin/bash
# 從舊名 wall42 搬遷到 wall42（專案原名 wall42，2026-09-27 改名）。
#
# 做的事（每一步都先看狀態、做完馬上驗證；已經搬過的步驟會自動跳過，重跑安全）：
#   0. 備份：舊設定目錄、LaunchAgent plist、MCP 註冊內容、目前各螢幕桌布路徑
#   1. 停舊常駐：launchctl bootout gui/<uid>/com.kang.wall42，確認 wall42 行程不在
#   2. 搬設定：~/.config/wall42 → ~/.config/wall42（複製→逐檔比對→舊目錄移進備份區）
#   3. 搬 preset 與 backup/：舊 repo 目錄（~/github-repos/wall42）若不是目前這份，
#      把新 repo 沒有的 presets/*.json 與 backup/ 內容補過來（不覆蓋）
#   4. 系統桌布若指向 ~/.config/wall42/ 裡的圖，改指到 ~/.config/wall42/ 的同名檔
#   5. 移除舊程式與 LaunchAgent：~/.local/bin/wall42、com.kang.wall42.plist 移進備份區
#   6. 舊 log 移進備份區
#   7. MCP：claude mcp remove wall42 → claude mcp add wall42（指向這份 repo 的 mcp/wall42_mcp.py）
#
# 不做的事：不編譯、不安裝、不啟動 wall42。搬完請跑 ./install.sh
# （install.sh 偵測到舊版時會自己先呼叫這支）。
#
# 刪除一律改成「mv 進備份區」，不直接 rm；備份區位置會在最後印出。
# 還原：bash ~/.local/share/wall42-migration/<時間>/restore.sh（搬回舊檔、桌布改回、重新載入 com.kang.wall42）
#
# 用法:
#   scripts/migrate-from-wall42.sh --dry-run   只列出會做什麼，不改任何東西
#   scripts/migrate-from-wall42.sh             實際搬遷
set -u

DRY=0
for a in "$@"; do
  case "$a" in
    --dry-run|-n) DRY=1 ;;
    -h|--help) sed -n '2,25p' "$0"; exit 0 ;;
    *) echo "不認得的參數：${a}（用 --dry-run 或不帶參數）"; exit 2 ;;
  esac
done

REPO="$(cd "$(dirname "$0")/.." && pwd -P)"
UID_=$(id -u)
OLD_LABEL="com.kang.wall42"
OLD_PLIST="$HOME/Library/LaunchAgents/$OLD_LABEL.plist"
OLD_BIN="$HOME/.local/bin/wall42"
OLD_CFG="$HOME/.config/wall42"
OLD_LOG="$HOME/Library/Logs/wall42.log"
OLD_REPO="$HOME/github-repos/wall42"
NEW_CFG="$HOME/.config/wall42"
NEW_MCP_PY="$REPO/mcp/wall42_mcp.py"
TS=$(date +%Y%m%d_%H%M%S)
BK="$HOME/.local/share/wall42-migration/$TS"
CLAUDE_BIN=$(command -v claude || echo "$HOME/.local/bin/claude")

FAIL=0
say()  { echo "$*"; }
step() { echo; echo "── $* ──"; }
ok()   { echo "  ✓ $*"; }
skip() { echo "  · $*（跳過）"; }
bad()  { echo "  ✗ $*"; FAIL=1; }
# 會改東西的指令一律走 run：dry-run 只印出來
run()  {
  if [ $DRY = 1 ]; then echo "  [dry-run] $*"; return 0; fi
  "$@"
}
die()  { echo; echo "✗ 搬遷中止：$*"; [ $DRY = 0 ] && [ -d "$BK" ] && { echo "  備份在 $BK"; [ -f "$BK/restore.sh" ] && echo "  還原：bash '$BK/restore.sh'"; }; exit 1; }

# dry-run 完全不碰 launchctl（連唯讀的 print 也不跑），改用「plist 在＋行程在」推斷
old_loaded() {
  if [ $DRY = 1 ]; then [ -e "$OLD_PLIST" ] && old_running; return; fi
  launchctl print "gui/$UID_/$OLD_LABEL" >/dev/null 2>&1
}
old_running() { pgrep -x wall42 >/dev/null 2>&1; }
mcp_has() { [ -x "$CLAUDE_BIN" ] && "$CLAUDE_BIN" mcp get "$1" >/dev/null 2>&1; }

# 各螢幕目前的桌布路徑（一行一個，格式 index<TAB>path）；唯讀
wallpapers() {
  osascript -l JavaScript -e '
    ObjC.import("AppKit");
    var ws = $.NSWorkspace.sharedWorkspace, sc = $.NSScreen.screens, out = [];
    for (var i = 0; i < sc.count; i++) {
      var u = ws.desktopImageURLForScreen(sc.objectAtIndex(i));
      out.push(i + "\t" + (u.isNil() ? "" : ObjC.unwrap(u.path)));
    }
    out.join("\n");' 2>/dev/null
}
# 把第 idx 個螢幕的桌布設成 path
set_wallpaper() {
  osascript -l JavaScript - "$1" "$2" <<'JXA'
ObjC.import("AppKit");
function run(argv) {
  var sc = $.NSScreen.screens.objectAtIndex(parseInt(argv[0]));
  var err = Ref();
  var ok = $.NSWorkspace.sharedWorkspace.setDesktopImageURLForScreenOptionsError(
             $.NSURL.fileURLWithPath(argv[1]), sc, $({}), err);
  return ok ? "ok" : "fail";
}
JXA
}

# 產生 $BK/restore.sh：把這次移進備份區的東西搬回原位、桌布改回原值、重新載入舊常駐
write_restore_script() {
  cat > "$BK/restore.sh" <<'SH'
#!/bin/bash
# 由 scripts/migrate-from-wall42.sh 產生：把這次搬遷移進備份區的 wall42 檔案搬回原位。用法：bash restore.sh（不用 sudo）
# 不會刪除 wall42 的任何東西：~/.config/wall42、~/.local/bin/wall42 都留著，只停掉 wall42 常駐。
set -u
B="$(cd "$(dirname "$0")" && pwd)"
U=$(id -u)
back() {  # back <備份區裡的名稱> <原位置>
  if [ ! -e "$B/$1" ]; then return 0; fi
  if [ -e "$2" ]; then echo "  略過（原位置已經有東西）：$2"; return 0; fi
  mkdir -p "$(dirname "$2")" && mv "$B/$1" "$2" && echo "  已搬回 $2"
}
launchctl bootout "gui/$U/com.kang.wall42" 2>/dev/null && echo "  已停 wall42 常駐"
pkill -x wall42 2>/dev/null
back config-wall42.moved      "$HOME/.config/wall42"
back wall42                   "$HOME/.local/bin/wall42"
back com.kang.wall42.plist    "$HOME/Library/LaunchAgents/com.kang.wall42.plist"
back wall42.log               "$HOME/Library/Logs/wall42.log"
# 桌布改回搬遷前的路徑
if [ -s "$B/wallpapers-before.tsv" ]; then
  while IFS=$'\t' read -r idx p; do
    [ -n "$p" ] && [ -f "$p" ] || continue
    osascript -l JavaScript - "$idx" "$p" >/dev/null 2>&1 <<'JXA'
ObjC.import("AppKit");
function run(argv) {
  var sc = $.NSScreen.screens.objectAtIndex(parseInt(argv[0]));
  $.NSWorkspace.sharedWorkspace.setDesktopImageURLForScreenOptionsError($.NSURL.fileURLWithPath(argv[1]), sc, $({}), Ref());
}
JXA
    echo "  螢幕 $idx 桌布改回 $p"
  done < "$B/wallpapers-before.tsv"
fi
P="$HOME/Library/LaunchAgents/com.kang.wall42.plist"
if [ -f "$P" ] && ! launchctl print "gui/$U/com.kang.wall42" >/dev/null 2>&1; then
  launchctl bootstrap "gui/$U" "$P" && echo "  已重新載入舊常駐 com.kang.wall42"
fi
if [ -f "$B/mcp-wall42.txt" ]; then
  echo
  echo "MCP 要手動改回：claude mcp remove wall42 -s user；照 $B/mcp-wall42.txt 的 Command/Args 重新 claude mcp add --scope user wall42 -- …"
fi
SH
  chmod 755 "$BK/restore.sh"
}

say "wall42 → wall42 搬遷$([ $DRY = 1 ] && echo '（dry-run：不會改任何東西）')"
say "  repo    : $REPO"
say "  備份區  : $BK"

# ── 現況 ────────────────────────────────────────────────
step "現況"
FOUND=0
old_loaded  && { say "  舊常駐 $OLD_LABEL 已載入"; FOUND=1; }
old_running && { say "  wall42 行程執行中（pid $(pgrep -x wall42 | tr '\n' ' ')）"; FOUND=1; }
[ -e "$OLD_PLIST" ] && { say "  舊 LaunchAgent：$OLD_PLIST"; FOUND=1; }
[ -e "$OLD_BIN" ]   && { say "  舊執行檔：$OLD_BIN"; FOUND=1; }
[ -d "$OLD_CFG" ]   && { say "  舊設定目錄：${OLD_CFG}（$(ls -A "$OLD_CFG" | wc -l | tr -d ' ') 個檔）"; FOUND=1; }
[ -e "$OLD_LOG" ]   && { say "  舊 log：$OLD_LOG"; FOUND=1; }
mcp_has wall42      && { say "  MCP server wall42 已註冊"; FOUND=1; }
[ -d "$NEW_CFG" ]   && say "  新設定目錄已存在：$NEW_CFG"
mcp_has wall42      && say "  MCP server wall42 已註冊"
WP=$(wallpapers)
echo "$WP" | grep -q "$OLD_CFG/" && { say "  系統桌布指向舊設定目錄裡的圖"; FOUND=1; }
if [ $FOUND = 0 ] && "$CLAUDE_BIN" mcp get wall42 2>/dev/null | grep -qF "$NEW_MCP_PY"; then
  say "  沒有任何 wall42 殘留，已經搬遷完成。"
  exit 0
fi

# ── 0. 備份 ─────────────────────────────────────────────
step "0. 備份"
if [ $DRY = 1 ]; then
  say "  [dry-run] mkdir -p $BK 並備份：設定目錄、plist、MCP 註冊、桌布路徑、repo backup/"
else
  mkdir -p "$BK" || die "無法建立備份區 $BK"
  printf '%s\n' "$WP" > "$BK/wallpapers-before.tsv"
  write_restore_script && ok "還原腳本：$BK/restore.sh"
  if [ -d "$OLD_CFG" ]; then
    cp -Rp "$OLD_CFG" "$BK/config-wall42" && diff -r "$OLD_CFG" "$BK/config-wall42" >/dev/null \
      && ok "設定目錄已備份並比對一致" || die "設定目錄備份比對不一致"
  fi
  if [ -e "$OLD_PLIST" ]; then
    cp -p "$OLD_PLIST" "$BK/" && cmp -s "$OLD_PLIST" "$BK/$OLD_LABEL.plist" \
      && ok "plist 已備份" || die "plist 備份失敗"
  fi
  if mcp_has wall42; then
    "$CLAUDE_BIN" mcp get wall42 > "$BK/mcp-wall42.txt" 2>&1
    # 原始 JSON 設定一併留一份（唯讀讀取 ~/.claude.json）
    python3 - "$BK/mcp-wall42.json" <<'PY' 2>/dev/null
import json, os, sys
c = json.load(open(os.path.expanduser("~/.claude.json")))
e = (c.get("mcpServers") or {}).get("wall42")
if e is not None:
    json.dump(e, open(sys.argv[1], "w"), indent=2, ensure_ascii=False)
PY
    [ -s "$BK/mcp-wall42.txt" ] && ok "MCP 註冊內容已備份" || die "MCP 註冊內容備份失敗"
  fi
  if [ -d "$OLD_REPO/backup" ] && [ "$(cd "$OLD_REPO" && pwd -P)" != "$REPO" ]; then
    cp -Rp "$OLD_REPO/backup" "$BK/repo-backup-wall42" && ok "舊 repo 的 backup/ 已備份"
  fi
  ok "備份區：$BK"
fi

# ── 1. 停舊常駐 ─────────────────────────────────────────
step "1. 停舊常駐"
if old_loaded; then
  run launchctl bootout "gui/$UID_/$OLD_LABEL"
  if [ $DRY = 0 ]; then sleep 1; old_loaded && die "bootout 後 $OLD_LABEL 仍在載入中"; ok "已 bootout $OLD_LABEL"; fi
else
  skip "$OLD_LABEL 沒有載入"
fi
if [ $DRY = 1 ]; then
  old_running && say "  [dry-run] 若 bootout 後 wall42 行程仍在：pkill -x wall42"
elif old_running; then
  pkill -x wall42; sleep 1
  old_running && die "wall42 行程停不掉（pid $(pgrep -x wall42 | tr '\n' ' ')）"
  ok "wall42 行程已結束"
else
  ok "沒有 wall42 行程"
fi

# ── 2. 搬設定 ───────────────────────────────────────────
step "2. 搬設定 $OLD_CFG → $NEW_CFG"
if [ ! -d "$OLD_CFG" ]; then
  skip "沒有舊設定目錄"
elif [ ! -d "$NEW_CFG" ]; then
  run cp -Rp "$OLD_CFG" "$NEW_CFG"
  if [ $DRY = 0 ]; then
    diff -r "$OLD_CFG" "$NEW_CFG" >/dev/null || die "複製後比對不一致，舊目錄保留未動"
    ok "已複製並逐檔比對一致"
  fi
  run mv "$OLD_CFG" "$BK/config-wall42.moved"
  [ $DRY = 0 ] && { [ ! -e "$OLD_CFG" ] && ok "舊目錄已移進備份區" || die "舊目錄移除失敗"; }
else
  # 兩邊都有：只補新目錄沒有的檔，絕不覆蓋 wall42 的設定
  say "  新目錄已存在，只補缺的檔（不覆蓋）"
  for f in "$OLD_CFG"/* "$OLD_CFG"/.[!.]*; do
    [ -e "$f" ] || continue
    n=$(basename "$f")
    if [ -e "$NEW_CFG/$n" ]; then say "    保留 wall42 的 $n"; else run cp -Rp "$f" "$NEW_CFG/$n"; fi
  done
  run mv "$OLD_CFG" "$BK/config-wall42.moved"
  [ $DRY = 0 ] && { [ ! -e "$OLD_CFG" ] && ok "舊目錄已移進備份區" || die "舊目錄移除失敗"; }
fi

# ── 3. 搬 preset 與 backup/ ─────────────────────────────
step "3. 搬 preset 與 backup/"
if [ ! -d "$OLD_REPO" ]; then
  skip "沒有舊 repo 目錄 $OLD_REPO"
elif [ "$(cd "$OLD_REPO" && pwd -P)" = "$REPO" ]; then
  skip "目前這份 repo 就在 ${OLD_REPO}，preset 不用搬（repo 目錄改名是另一件事，見 README）"
else
  for f in "$OLD_REPO"/presets/*.json; do
    [ -e "$f" ] || continue
    n=$(basename "$f")
    if [ -e "$REPO/presets/$n" ]; then
      cmp -s "$f" "$REPO/presets/$n" || say "    presets/$n 兩邊內容不同，保留新 repo 的（舊的在 $OLD_REPO/presets/）"
    else
      run cp -p "$f" "$REPO/presets/$n"
      [ $DRY = 0 ] && { cmp -s "$f" "$REPO/presets/$n" && ok "補上 presets/$n" || bad "presets/$n 複製後不一致"; }
    fi
  done
  if [ -d "$OLD_REPO/backup" ]; then
    run mkdir -p "$REPO/backup"
    # -n：不覆蓋新 repo 既有的檔
    run cp -Rpn "$OLD_REPO/backup/." "$REPO/backup/"
    [ $DRY = 0 ] && ok "backup/ 已補齊（不覆蓋既有檔）"
  fi
fi
# original-wallpaper.txt 若記到舊設定目錄裡的圖，改成新路徑
ORIG="$REPO/backup/original-wallpaper.txt"
if [ -f "$ORIG" ] && grep -q "$OLD_CFG/" "$ORIG"; then
  run sed -i '' "s#$OLD_CFG/#$NEW_CFG/#g" "$ORIG"
  [ $DRY = 0 ] && ok "original-wallpaper.txt 已改指新路徑"
fi

# ── 4. 系統桌布改指新路徑 ───────────────────────────────
step "4. 系統桌布"
if ! echo "$WP" | grep -q "$OLD_CFG/"; then
  skip "沒有螢幕的桌布指向 $OLD_CFG"
else
  while IFS=$'\t' read -r idx p; do
    case "$p" in
      "$OLD_CFG/"*)
        np="$NEW_CFG/${p#"$OLD_CFG/"}"
        if [ $DRY = 1 ]; then
          say "  [dry-run] 螢幕 ${idx}：$p → $np"
        elif [ -f "$np" ]; then
          r=$(set_wallpaper "$idx" "$np")
          [ "$r" = ok ] && ok "螢幕 $idx 桌布改指 $np" || bad "螢幕 $idx 桌布設定失敗（原值在 $BK/wallpapers-before.tsv）"
        else
          bad "螢幕 ${idx}：新路徑 $np 不存在，桌布沒改（原值在 $BK/wallpapers-before.tsv）"
        fi ;;
    esac
  done <<< "$WP"
  if [ $DRY = 0 ]; then
    sleep 1
    wallpapers | grep -q "$OLD_CFG/" && bad "仍有螢幕的桌布指向舊目錄" || ok "驗證：已無桌布指向舊目錄"
  fi
fi

# ── 5. 移除舊程式與 LaunchAgent ─────────────────────────
step "5. 移除舊程式與 LaunchAgent（移進備份區，不直接刪）"
for f in "$OLD_PLIST" "$OLD_BIN"; do
  if [ -e "$f" ]; then
    run mv "$f" "$BK/"
    [ $DRY = 0 ] && { [ ! -e "$f" ] && ok "已移走 $f" || bad "移不走 $f"; }
  else
    skip "$f 不存在"
  fi
done

# ── 6. 舊 log ───────────────────────────────────────────
step "6. 舊 log"
if [ -e "$OLD_LOG" ]; then
  run mv "$OLD_LOG" "$BK/"
  [ $DRY = 0 ] && { [ ! -e "$OLD_LOG" ] && ok "舊 log 已移進備份區" || bad "舊 log 移不走"; }
else
  skip "沒有舊 log"
fi

# ── 7. MCP ─────────────────────────────────────────────
step "7. MCP：wall42 → wall42"
if [ ! -x "$CLAUDE_BIN" ]; then
  bad "找不到 claude CLI，MCP 請手動：claude mcp remove wall42 -s user；claude mcp add --scope user wall42 -- uv run --script $NEW_MCP_PY"
else
  [ -f "$NEW_MCP_PY" ] || die "找不到 $NEW_MCP_PY"
  if mcp_has wall42; then
    run "$CLAUDE_BIN" mcp remove wall42 -s user
    [ $DRY = 0 ] && { mcp_has wall42 && bad "wall42 移除後仍在" || ok "已移除 MCP wall42"; }
  else
    skip "MCP wall42 沒有註冊"
  fi
  # 已註冊但指向別處（例如 repo 目錄後來改名成 wall42）就重新註冊
  if mcp_has wall42 && "$CLAUDE_BIN" mcp get wall42 2>/dev/null | grep -qF "$NEW_MCP_PY"; then
    skip "MCP wall42 已註冊且指向 $NEW_MCP_PY"
  else
    if mcp_has wall42; then
      say "  MCP wall42 指向的不是 $NEW_MCP_PY，重新註冊"
      run "$CLAUDE_BIN" mcp remove wall42 -s user
    fi
    run "$CLAUDE_BIN" mcp add --scope user wall42 -- uv run --script "$NEW_MCP_PY"
    [ $DRY = 0 ] && { mcp_has wall42 && ok "已註冊 MCP wall42 → $NEW_MCP_PY" || bad "MCP wall42 註冊失敗"; }
  fi
fi

# ── 結果 ────────────────────────────────────────────────
echo
if [ $DRY = 1 ]; then
  echo "dry-run 結束，沒有改任何東西。實際搬遷：scripts/migrate-from-wall42.sh"
elif [ $FAIL = 0 ]; then
  echo "✓ 搬遷完成。備份在 $BK（還原：bash '$BK/restore.sh'）"
  echo "  下一步：./install.sh 安裝並啟動 wall42；重開 Claude Code 讓 MCP wall42 生效"
else
  echo "⚠ 搬遷完成但有步驟失敗（見上方 ✗）。備份在 $BK（還原：bash '$BK/restore.sh'）"
  exit 1
fi
