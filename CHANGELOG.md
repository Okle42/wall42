# CHANGELOG

## 2026-10-01：閒置降速、滑鼠焦點時效、log 記閒置

依 09-30 常駐 17 小時 log 覆盤（詳見 `docs/優化修正計畫.md` #3～#5）：

- 閒置降速 `motion.idle`：10 分鐘無輸入 5fps、30 分鐘 1fps，有輸入 1 秒內恢復。
  螢幕被 Chrome 擋著不休眠時，原本 17 小時 0 次停畫、整夜 30fps。
- 焦點螢幕：滑鼠要最近 10 秒有移動才算。原本滑鼠停在另一台會讓兩台都全速（佔 49% 時間）。
- 分鐘統計加 `idle=秒`；新增測試開關 `WALL42_SIMULATE_IDLE=1`。

## 2026-09-30：開機 0 幀自動修復、log 瘦身

- 修正：重開機登入後 wall42 常駐在跑、視窗也在，但從頭到尾 `fps=0.0 steps=0`，桌布不動。
  MTKView 的 display link 在螢幕尚未就緒時建立，之後永遠不觸發 `draw(in:)`；手動重啟即恢復。
  新增看門狗：沒暫停、沒遮擋卻連續 5 秒 0 幀就重建畫面（最多連試 3 次，畫出來就歸零）。
  `WALL42_SIMULATE_STALL=1` 可重現並驗證。
- 優化：log 原本每秒一行，4 天長到 28MB。常駐時改成 60 秒一行統計，事件與異常照常當下寫；
  `WALL42_DURATION`（bench）或 `WALL42_VERBOSE=1` 時維持每秒一行。log 超過 20MB 自動清空。
- 詳見 `docs/優化修正計畫.md`。

## 2026-09-27：改名 wall42 → wall42

專案原名 **wall42**，全面改名為 **wall42**（程式、執行檔、LaunchAgent label、設定目錄、log、
MCP server 與 tool、環境變數、腳本、文件）。

- 執行檔 `wall42` → `wall42`；LaunchAgent `*.wall42` → `com.kang.wall42`
- 設定 `~/.config/wall42/` → `~/.config/wall42/`；log `~/Library/Logs/wall42.log` → `wall42.log`
- MCP server `wall42` → `wall42`，檔案 `mcp/wall42_mcp.py` → `mcp/wall42_mcp.py`，tool `wall42_*` → `wall42_*`
- 環境變數 `WALL42_*` → `WALL42_*`
- 新增 `scripts/migrate-from-wall42.sh`（冪等、先備份、`--dry-run`、每步驗證、在備份區產生 `restore.sh`）；
  `./install.sh` 先編譯，編譯成功且偵測到舊版時才自動呼叫
- repo 位置不再寫死：Swift 讀 `WALL42_REPO`（install.sh 寫進 LaunchAgent，預設 `~/github-repos/wall42`），
  MCP 以腳本所在位置找 `presets/`
- 文件中提到的姊妹專案 cool42 同步改稱 cool42

## 2026-09-20 起（wall42 時期）

- 桌布層 Metal 粒子初始版本、多螢幕、休眠停畫、一片星空、非焦點螢幕降 fps、snow／sand／session 光點
