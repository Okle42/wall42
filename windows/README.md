# wall42 for Windows（開發中）

macOS 版的 Windows 移植：桌布之上、桌面圖示之下的動態粒子層。設定檔 schema、preset、模擬邏輯、shader 數學都跟 Mac 同一份，
**被視窗完全蓋住就停止繪製**這個核心設計也一樣。

| 階段 | 內容 | 狀態 |
|---|---|---|
| W1 | 地基：Core（設定、preset、World 模擬）＋測試；D3D11 渲染；掛在 WorkerW 底下；遮擋／鎖定／休眠停畫；診斷開關 | ✅（見 W1-結果.md） |
| W2 | 系統匣圖示＋調參數面板（對應 MenuBar.swift／ControlPanel.swift） | 2A 系統匣＋面板 ✅（見下「系統匣與控制面板」） |
| W3 | MCP（`wall42_*` tools，走同一套檔案信號）、Claude session 光點的資料來源 | 待做 |
| W4 | 桌布同步（把目前畫面設成系統桌布）、安裝／開機啟動／解除安裝 | 待做 |

| W2 | 系統匣圖示＋調參數面板（對應 MenuBar.swift／ControlPanel.swift） | 待做 |
| W2-AI | MCP（10 個 `wall42_*` tools，同一支 `mcp\wall42_mcp.py`）、狀態檔、Claude session 光點資料來源、桌布同步 | ✅（見下方「AI 連動」） |
| W4 | 安裝／開機啟動／解除安裝 | 待做 |

## 結構

```
windows\
  Wall42.Core\            net8.0 類別庫，沒有 UI、沒有 GPU，可單元測試
    Config.cs             Config.swift 同 schema；所有欄位可省略、型別錯只略過該欄；mtime 熱重載
    Presets.cs            repo 的 presets\*.json（之後加安裝版副本）
    ConfigEdit.cs         系統匣／面板寫設定檔用：當 JSON 樹編輯（不認得的 key 保留、沒動的數字原文不變、原子寫入）、套 preset、比對目前是哪個 preset
    World.cs              World.swift 逐段移植：floating、連線 proximity／traffic／attention、脈衝、散景
    World.Effects.cs      snow、sand、Claude session 光點
    Layout.cs             螢幕（實體像素）→ 世界座標
    Activity.cs           GetSystemTimes 系統負載、平滑、think 到期、.signal 檔案信號
    Sessions.cs           Claude session 檔解析（pid＋procStart 防 pid 重用）、sessions.json 餵入、何時讀（只在有畫時）、路徑
    PresetMatch.cs        目前設定是哪個 preset（忽略 activity／ui，同 _current_preset.py）
  Wall42.Core.Tests\      xunit：preset 全部可解析、世界步進不變量、多螢幕重映射、時鐘
  Wall42.Win\             net8.0-windows WinExe，AssemblyName wall42（PerMonitorV2 寫在 app.manifest）
    Shaders.hlsl          Renderer.swift 裡 Metal shader 的逐行 HLSL 版（執行期編譯，跟 Mac 一樣）
    Gpu.cs                D3D11 裝置、pipeline、粒子／連線 structured buffer（每步上傳一次，所有螢幕共用）、離線截圖、PNG
    Surface.cs            每台螢幕一個：WorkerW 底下的子視窗＋flip-model swap chain
    Desktop.cs            列舉螢幕、找 WorkerW（24H2 與舊版兩條路）
    Occlusion.cs          「這台螢幕是不是整個被蓋住」的覆蓋計算
    App.cs                主迴圈、fps 節拍、停畫判斷、WinEvent、廣播訊息、每秒 tick、log
    App.Ai.cs             （partial）session 光點更新、status.json、.sync-request、restore-wallpaper／status 信號
    Wallpaper.cs          IDesktopWallpaper（逐螢幕）：讀目前桌布、備份一次、設定／還原；WALL42_SYNC_DRY 演練
    Program.cs            進入點、環境變數、WALL42_SNAPSHOT
    App.Ui.cs             App 的 UI 那一半：系統匣／面板的進入點、手動暫停、即時套用、選單與拖動時照樣出幀、關閉後 trim
    Tray.cs               系統匣圖示（Shell_NotifyIcon）＋右鍵選單（TrackPopupMenu），對應 MenuBar.swift
    ControlPanel.cs       調參數面板（自己畫的 Win32 視窗，資料驅動），對應 ControlPanel.swift
    Canvas.cs             面板用的 DIB 畫布（逐像素抗鋸齒圓角／圓／線＋GDI ClearType 文字）、圖示產生器
    UiNative.cs           系統匣／面板用的 Win32
  tools\Inspect\          唯讀檢查：Progman／WorkerW 階層、視窗樣式、點擊命中、PrintWindow 截桌面層
  tools\bench.ps1         實機量測：啟動 → 暖機 → 量 CPU／記憶體 → 檢查 → 依 pid 確認結束
  tools\UiProbe\          測試驅動：只對指定 pid 的視窗送選單命令、開面板（不搶焦點）、PrintWindow 截選單／面板
```

## 建置、測試、執行

```powershell
cd windows
dotnet build Wall42.Win -c Release          # 0 警告（TreatWarningsAsErrors）
dotnet test Wall42.Core.Tests -c Release
Wall42.Win\bin\Release\net8.0-windows\wall42.exe            # 常駐跑；結束：taskkill /pid <pid>（送 WM_CLOSE）

# 看某個 preset 的樣子，不需要桌面露出來（無視窗、跑到第 90 幀、存主螢幕畫面）
$env:WALL42_CONFIG = "$PWD\..\presets\neon.json"; $env:WALL42_SNAPSHOT = "$PWD\snapshots\neon.png"
Wall42.Win\bin\Release\net8.0-windows\wall42.exe

# 實機量測（含視窗階層檢查）
dotnet build tools\Inspect -c Release
powershell -ExecutionPolicy Bypass -File tools\bench.ps1 -Label paused -Warmup 60 -Seconds 60 -Config ..\presets\kang.json
powershell -ExecutionPolicy Bypass -File tools\bench.ps1 -Label drawing -ForceDraw -Warmup 30 -Seconds 40 -Config ..\presets\kang.json
```

設定檔 `%APPDATA%\wall42\config.json`（`WALL42_CONFIG` 可指定別的檔），不存在就寫一份預設值；**存檔即生效**（每秒看 mtime）。
控制信號固定在 `%APPDATA%\wall42\.signal`，不跟著 `WALL42_CONFIG` 漂移（跟 Mac 同理由）：
`{"kind":"think","level":0.9,"seconds":120}`、`{"kind":"insight","strength":1}`、測試用 `{"kind":"debug-suspend","reason":"locked","on":true}`。
log：`%LOCALAPPDATA%\wall42\wall42.log`（2 MB 輪替成 `wall42.old.log`）。

## 安裝、打包（見 W2-安裝結果.md）

```powershell
powershell -ExecutionPolicy Bypass -File pack.ps1          # → dist\wall42.exe（內含 runtime，65 MB）、dist\wall42-small.exe（2.5 MB，需 .NET 8 Runtime）
dist\wall42.exe                     # 雙擊：先跳出說明視窗，按「安裝」才動手
wall42.exe --install [--quiet]      # 放到 %LOCALAPPDATA%\wall42\bin、presets 放到 %LOCALAPPDATA%\wall42\presets、
                                    # 設定檔不存在才建立、HKCU Run 開機啟動、列在「設定 > 應用程式」、啟動（已裝就是更新）
wall42.exe --uninstall [--quiet]    # 停掉、拿掉 Run 與清單項目、刪 bin 與 log；設定檔與 presets 保留（跟 Mac 一樣）
wall42.exe --version | --run        # --run：不管在哪裡都直接跑（packed exe 不問安裝）
powershell -ExecutionPolicy Bypass -File tests\w2_install_e2e.ps1   # 沙盒 e2e（先跑 pack.ps1）
```

從不改系統桌布。dev build 也能 `--install`（複製建置輸出的所有檔案＋repo 的 presets）。
測試用沙盒：`WALL42_HOME`（取代 `%LOCALAPPDATA%\wall42`）、`WALL42_DATA`（取代 `%APPDATA%\wall42`，含 .signal）、`WALL42_REG_ROOT`（Run／Uninstall 的 HKCU 機碼）。
圖示：`tools\make_icon.ps1` 產生 `Wall42.Win\wall42.ico`。

## 系統匣與控制面板

- **系統匣圖示**（`ui.menuBar`，同 Mac 的 key，預設開，熱重載即時開關）：左鍵＝開控制面板，右鍵＝選單：
  狀態列（fps・連線數・CPU／被遮住／已手動暫停／暫停原因）、忙碌程度長條、**風格**（presets 清單，目前這個打 ✓，切換＝寫設定檔）、
  **忙碌程度**（系統負載／手動／關）、暫停繪製／繼續繪製、開啟控制面板…、開啟設定檔資料夾、結束 wall42。
  選單跟著 Windows 深色／淺色。手動暫停走跟鎖定一樣的停畫出口（不 Present、不跑模擬），不寫進設定檔（同 Mac）。
- **控制面板**：Mac 面板的所有列＋幀率、背景模式與三個背景色、Session 光點開關；數量用對數刻度（20–3000，sand 是 2400）。
  拖動時直接套到桌布，停手 400 ms 後才寫檔；顏色點色塊開系統選色器，連線顏色右鍵改回「自動」；最上面選風格、「重設」回到開始改之前的風格。
  寫檔一律經 `ConfigEdit`：不認得的 key 保留、沒動的值原文不變、先寫 `.tmp` 再取代；設定檔不是合法 JSON 時面板唯讀、不覆寫。
  切風格時 preset 的 background／motion 取代目前的，但保留 `motion.activity`、`ui` 與其他頂層 key（例如別的功能的設定）。
- **記憶體**：兩者都是純 Win32（沒有 WinForms／WPF），面板是自己畫的單一視窗（一張 DIB＋三個字型）。關閉就整個釋放，
  選單或面板關掉後 GC＋`SetProcessWorkingSetSize(-1,-1)`（ShoWork42 設定視窗同一招）。
- 選單、拖動面板視窗、選色器都是 modal loop，我們的主迴圈不會跑：期間用 thread timer 照樣出幀，桌布不會凍住。
- 測試：`tools\UiProbe`（見檔頭）只對自己那個 pid 的視窗送命令、開面板時不搶焦點；`WALL42_THEME=light|dark` 覆寫面板主題、
  `WALL42_OPEN_PANEL=x,y,noactivate` 啟動就開面板。

## 診斷開關（環境變數，名稱跟 Mac 相同）

| 變數 | 用途 |
|---|---|
| `WALL42_FORCE_DRAW=1` | 忽略遮擋與停畫原因一直畫，量峰值 |
| `WALL42_NO_DRAW=1` | 只 clear＋Present，不跑模擬、不下 draw call，量框架底線 |
| `WALL42_SNAPSHOT=路徑` | 無視窗：固定種子、固定 1/fps 步進到第 90 幀（`WALL42_SNAPSHOT_FRAME`），離線渲染主螢幕畫面存 PNG 後結束 |
| `WALL42_DURATION=秒` | 跑幾秒後自己結束 |
| `WALL42_PARTICLES` / `WALL42_FPS` | 覆寫設定檔 |
| `WALL42_ONLY_MAIN=1` | 只開主螢幕 |
| `WALL42_REPO=路徑` | presets\ 所在（不設就從 exe 位置往上找） |
| `WALL42_SEED` / `WALL42_ACTIVITY` | 截圖用的亂數種子（預設 42）與活動度（預設：manual 用 manualLevel，否則 0） |
| `WALL42_REPORT=秒` | log 狀態列間隔（預設 10；1 = 像 Mac 每秒一行） |
| `WALL42_LOG=路徑` | log 位置 |
| `WALL42_DEBUG_EVENTS=1` | 結束時記下哪些視窗事件（事件:class）最常把我們叫醒 |
| `WALL42_SIGNAL_DIR=路徑` | 信號資料夾（.signal、.sync-request、sessions.json）搬去別處；只給測試用，預設固定 `%APPDATA%\wall42` |
| `WALL42_SYNC_DRY=1` | 桌布同步／還原只演練：擷取、存 PNG、記下目前桌布，但**不**呼叫 SetWallpaper／SetPosition／SetBackgroundColor |

## 做法（對照 macOS 版）

- **桌布層**：`SendMessageTimeout(Progman, 0x052C, 0xD, 1)` 讓 Explorer 分出 WorkerW。24H2 起 WorkerW 是 Progman 的**子視窗**、
  在 `SHELLDLL_DefView`（圖示）底下；舊版是圖示那個頂層視窗後面的頂層 WorkerW。我們的每台螢幕視窗用 `WS_CHILD` 直接建在 WorkerW 底下
  （失敗才退回「先建 popup 再 SetParent」）。樣式 `WS_DISABLED`＋`WS_EX_TRANSPARENT|WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW`，
  `WM_NCHITTEST` 回 `HTTRANSPARENT`、`WM_MOUSEACTIVATE` 回 `MA_NOACTIVATE`：永遠不拿焦點、不吃點擊。
  Explorer 重啟（`TaskbarCreated`）或子視窗被帶走時重新掛上，World 不重建（沙堆照樣在）。
- **世界座標**：所有螢幕實體像素的聯合矩形 ÷ 主螢幕 DPI 倍率。所以世界單位＝Mac 的 point，preset 裡的大小、速度、距離看起來一樣大；
  混合 DPI 的螢幕仍在同一個座標空間、邊對邊接得上。
- **渲染**：Metal shader 逐行翻成 HLSL；沒有 vertex buffer／input layout，全部用 `SV_VertexID`／`SV_InstanceID` 讀 structured buffer
  （等同 Metal 的 `[[vertex_id]]` 讀 `constant Particle *`）。粒子是 instanced quad（跨螢幕接縫的大光斑兩邊各畫一半），連線是 line list。
  粒子／連線 buffer 每個模擬步只上傳一次，所有螢幕共用。
- **節拍**：高解析度 waitable timer（`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`，不動全系統的 `timeBeginPeriod`）＋`MsgWaitForMultipleObjectsEx`。
  焦點螢幕（滑鼠或前景視窗所在）用 `fps`，其他用 `secondaryFps`；模擬跟著畫得最快的那台，第二台同一個 vsync 內來呼叫會略過。
  `MaximumFrameLatency = 1`、`Present(1)`。
- **停畫（整支程式的核心）**：任一成立該螢幕就完全不 Present、不跑模擬；全部停了主執行緒就睡在 `MsgWaitForMultipleObjectsEx`，
  只有視窗事件和每秒 tick 會叫醒。恢復時若其他螢幕都停著就 `ResetClock()`，從當下時間接續，粒子不瞬移。停超過 5 秒把 swap chain 縮成 8×8。
  - 被蓋住：Win32 沒有 `occlusionState`，自己算——可見、非最小化、非 cloaked、不透明的頂層視窗聯集減去螢幕矩形，剩空＝蓋住；
    單一視窗整個包住螢幕記為 fullscreen。矩形用 DWM 的 `EXTENDED_FRAME_BOUNDS`（`GetWindowRect` 多了隱形邊框）。
    不對稱去抖同 Mac：露出來立即畫、蓋住要穩定 300 ms 才停。
  - 事件：WinEvent（前景、最小化、顯示／隱藏、cloak、位置）＋ fallback tick（畫的時候 1 秒、全停時 2 秒）。
  - 鎖定／切換使用者：`WTSRegisterSessionNotification`；睡眠：`PBT_APMSUSPEND`；螢幕關閉：`GUID_CONSOLE_DISPLAY_STATE`；
    螢幕保護程式：每秒讀 `SPI_GETSCREENSAVERRUNNING`（沒有廣播）。
- **活動度**：`GetSystemTimes` 差值 → minLoad／maxLoad 對應 → smoothing 指數平滑，跟 Mac 同公式；`.signal` 的 think（到期自動回復）與 insight。

## AI 連動（MCP、session 光點、狀態檔、桌布同步）

### MCP

同一支 `mcp\wall42_mcp.py` 同時支援 macOS 與 Windows（`sys.platform` 分支，macOS 行為不變），10 個 tool 都能用：
`wall42_status`、`wall42_list_presets`、`wall42_set_preset`、`wall42_set_activity`、`wall42_set`、`wall42_think`、`wall42_insight`、
`wall42_sync_wallpaper`、`wall42_sessions`、`wall42_control`。

```powershell
# 需要 Python ≥3.10 與 mcp>=2,<3（檔頭的 uv script metadata 有寫；有 uv 就用 uv 跑，會自己裝在隔離環境）
claude mcp add --scope user wall42 -- uv run --script C:\path\to\wall42\mcp\wall42_mcp.py
# 或用已經裝好 mcp 套件的 python
claude mcp add --scope user wall42 -- python C:\path\to\wall42\mcp\wall42_mcp.py
```

| 項目 | Windows 位置 |
|---|---|
| 設定檔 | `%APPDATA%\wall42\config.json`（`WALL42_CONFIG`） |
| 信號 `.signal`、`.sync-request`、`sessions.json` | `%APPDATA%\wall42`（`WALL42_SIGNAL_DIR`），不跟著設定檔漂移 |
| log、`status.json`、桌布 PNG、`backup\original-wallpaper.json` | `%LOCALAPPDATA%\wall42`（跟著 `WALL42_LOG` 的資料夾） |
| `wall42_control` 的 exe | `WALL42_EXE` → `%LOCALAPPDATA%\wall42\wall42.exe` → `%LOCALAPPDATA%\Programs\wall42\wall42.exe` → repo 的 Release 產物 |

- **即時狀態**讀 `status.json`（Mac 讀 log 最後一行）：running、pid＋procStart、state（visible／OCCLUDED／SUSPENDED／paused）、
  每台螢幕 drawing／occluded／coveredBy／trimmed／targetFps、實測 fps、自身 CPU（單核與全機）、工作集／private、連線數、活動度、
  thinking、preset（比對 presets\*.json）、effect、session 數、最後一次桌布同步。**每 5 秒最多重算一次**，先算一個不配置記憶體的指紋，
  沒變就連 JSON 都不做；只有量測值在動的話 30 秒才重寫一次。MCP 要最新值時送 `{"kind":"status"}` 請它立刻重寫。
- **running 判斷**：狀態檔的 pid 還活著**而且**建立時間等於 procStart；沒有狀態檔才退回 `tasklist` 找 wall42.exe。
  （Windows 的 `os.kill(pid, 0)` 會 TerminateProcess，絕對不能拿來探測。）
- **stop**：`taskkill /pid`（不帶 /f，送 WM_CLOSE，wall42 自己收尾），6 秒沒結束才 /f。**start**：detached 啟動 exe。

### Claude session 光點

`motion.sessions.enabled` 打開時（`sessions` preset 或 `wall42_sessions(enable=True)`），每個 Claude Code session 一個帶外環的常駐光點，
位置由 sessionId 的 FNV 雜湊決定（跟 Mac 一樣，同一個 session 永遠在同一處）。

- `source: auto`（預設）：讀 `%USERPROFILE%\.claude\sessions\<pid>.json`。算數的條件：**pid 活著，而且行程建立時間 == 檔案裡的 `procStart`**
  （FILETIME；實機比對三個 session 完全相等，容許 10 ms）——session 死掉後 pid 被別的程式重用就不會誤算。`status == "busy"` 算忙碌。
  `.key` 檔、壞檔、沒 pid 的略過。依 startedAt 排序。
- `%APPDATA%\wall42\sessions.json` 存在（或 `source: file`）時優先：`{"count":5,"busy":2}` 或 `{"sessions":[{"id":"a","busy":true}]}`。
- **只在有畫的時候讀**，最多每 2 秒一次；全部停畫時完全不碰資料夾（0 次檔案存取），恢復繪製的那一秒立刻重讀。

### 桌布同步

`wall42_sync_wallpaper()` 或手動建立 `%APPDATA%\wall42\.sync-request`（內容 `{"dry":true}` = 只演練）。常駐每秒檢查一次：

1. 把每台螢幕**目前這一幀**離線渲染成該螢幕解析度的 PNG：`%LOCALAPPDATA%\wall42\wallpaper_{a|b}_{n}.png`（A/B 交替，刪掉另一格；
   同路徑桌布會被快取不重繪）。World 不前進，停畫時也能拍。
2. 用 `IDesktopWallpaper` 讀出每台螢幕目前的桌布、位置模式、背景色，**每次都寫進 log**；第一次同步前另存
   `backup\original-wallpaper.json`（之後不覆寫，路徑已經是我們自己的 PNG 時也不存）。
3. 依螢幕矩形對應 monitor ID，`SetWallpaper(id, png)`；位置模式是 tile／span 才改成 fill（圖跟螢幕一樣大，其餘模式都是 1:1）。
4. 還原：`{"kind":"restore-wallpaper"}` 信號或 `wall42.exe --restore-wallpaper`（`--dry` 只演練），照備份設回每台螢幕的路徑、位置、背景色。

`WALL42_SYNC_DRY=1` 時第 3、4 步只寫 log（`would SetWallpaper(...)`）。

### 實測（i5-8250U、單螢幕 2256×1504、sessions preset 423 顆、3 個活的 Claude session）

| 情境 | CPU（單核） | CPU（全機） | 工作集 |
|---|---|---|---|
| 被蓋住停畫、sessions 開著（跟 W1 同時跑、同條件對照，量 150 s） | 0.458% | 0.057% | 84 MB |
| 　同時段 W1（無 AI 部分） | 0.385% | 0.048% | 76 MB |
| 繪製中＋session 光點（debug-force，量 40 s） | 4.65% | 0.58% | 107 MB |

- 停畫時 AI 部分的每秒 tick 平均 0.4 ms（log 結束行 `ai: … aiTick=`），差距 ≈0.07% 單核；session 資料夾 0 次讀取。
- 桌布同步一次 140–270 ms（渲染＋PNG 編碼 2256×1504），之後強制 GC 把 LOH 上的畫面緩衝還回去。

## 已知坑

1. **HLSL 原始碼必須是純 ASCII**：Vortice 把字串交給 D3DCompile 的長度被非 ASCII 字元打亂，註解裡一個「→」就會變成
   「line 179 undeclared identifier」這種不存在行號的錯誤。
2. **Metal 的 `smoothstep(0.5, 0.0, d)`（邊界反過來）在 HLSL 是未定義行為**：shader 裡自己寫 `sstep`。
3. **全系統 LOCATIONCHANGE 很吵**：別的程式有跟著視窗跑的光暈（layered、點擊穿透）時每秒 20 次移動事件，每次都叫醒我們重算覆蓋，
   停畫時 CPU 反而是 2%。解法：點擊穿透的 layered 視窗不算遮擋也不觸發；全部停畫時 LOCATIONCHANGE 只掛在「目前蓋住螢幕的視窗」的行程上
   （沒被算進遮擋的視窗只會增加遮擋，不可能讓螢幕露出來；我們在最底層，z-order 無關）。
4. **`System.Diagnostics.Process` 的 WorkingSet／CPU 每次都快照全系統行程**，常駐程式每幾秒查一次也看得出來：改用 `GetProcessTimes`／`K32GetProcessMemoryInfo`。
5. **量測要暖機 60 秒**：.NET 前幾十秒的 tiered JIT 會把「停畫」量成 1–2%。
6. **message-only 視窗收不到廣播**（`TaskbarCreated`、`WM_DISPLAYCHANGE`）：主控視窗用不顯示的頂層 popup。
7. 高 DPI：程式與量測工具都要 Per-Monitor V2（WinForms 專案不能在 manifest 寫 DPI，要用 `ApplicationHighDpiMode`）。
8. 桌面被使用者的視窗蓋住時要看效果：用 `WALL42_SNAPSHOT`（離線渲染）或 `tools\Inspect <png>`（`PrintWindow(Progman, PW_RENDERFULLCONTENT)`
   會連同我們的 flip-model 畫面與圖示一起截到）。**不要**用 Win+D／顯示桌面去動使用者的視窗。
9. 舊版 Windows（24H2 以前）的 WorkerW 在我們結束後可能留著最後一幀：結束時 `RedrawWindow(WorkerW)` 請 Explorer 重畫；
   24H2 實測結束後直接回到原本的桌布，不需要任何還原（我們從不改系統桌布設定）。
10. PowerShell 5.1：含中文的 .ps1 要 UTF-8 with BOM；Git Bash 傳 `/參數` 要 `MSYS_NO_PATHCONV=1`。
11. **8.3 短檔名路徑很貴**：`%TEMP%` 可能是 `C:\Users\ABCDEF~1\…`，每次 `File.Exists` 要解析短名，實測 1.3 ms（長路徑 0.09 ms）。
    測試用的設定／信號資料夾請用長路徑，否則停畫 CPU 會被量高。
12. `mcp` 套件沒裝時，repo 裡的 `mcp\` 資料夾會被 Python 當成 namespace package（`import mcp` 不報錯但沒有 `mcp.server`）；
    直接測 tool 函式時要自己注入替身模組。
