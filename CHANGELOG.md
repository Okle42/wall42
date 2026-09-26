# CHANGELOG

## 2026-09-27：改名 wall91 → wall42

專案原名 **wall91**，全面改名為 **wall42**（程式、執行檔、LaunchAgent label、設定目錄、log、
MCP server 與 tool、環境變數、腳本、文件）。

- 執行檔 `wall91` → `wall42`；LaunchAgent `com.kang.wall91` → `com.kang.wall42`
- 設定 `~/.config/wall91/` → `~/.config/wall42/`；log `~/Library/Logs/wall91.log` → `wall42.log`
- MCP server `wall91` → `wall42`，檔案 `mcp/wall91_mcp.py` → `mcp/wall42_mcp.py`，tool `wall91_*` → `wall42_*`
- 環境變數 `WALL91_*` → `WALL42_*`
- 新增 `scripts/migrate-from-wall91.sh`（冪等、先備份、`--dry-run`、每步驗證）；`./install.sh` 偵測到舊版時自動呼叫
- repo 位置不再寫死：Swift 讀 `WALL42_REPO`（install.sh 寫進 LaunchAgent，預設 `~/github-repos/wall42`），
  MCP 以腳本所在位置找 `presets/`
- 文件中提到的姊妹專案 cool91 同步改稱 cool42

## 2026-09-20 起（wall91 時期）

- 桌布層 Metal 粒子初始版本、多螢幕、休眠停畫、一片星空、非焦點螢幕降 fps、snow／sand／session 光點
