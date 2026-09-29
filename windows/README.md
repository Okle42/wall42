# wall42 for Windows（開發中）

macOS 版的 Windows 移植：桌布之上、桌面圖示之下的動態粒子層。設定檔 schema、preset、模擬邏輯、shader 數學都跟 Mac 同一份，
**被視窗完全蓋住就停止繪製**這個核心設計也一樣。

| 階段 | 內容 | 狀態 |
|---|---|---|
| W1 | 地基：Core（設定、preset、World 模擬）＋測試；D3D11 渲染；掛在 WorkerW 底下；遮擋／鎖定／休眠停畫；診斷開關 | ✅（見 W1-結果.md） |
| W2 | 系統匣圖示＋調參數面板（對應 MenuBar.swift／ControlPanel.swift） | 待做 |
| W3 | MCP（`wall42_*` tools，走同一套檔案信號）、Claude session 光點的資料來源 | 待做 |
| W4 | 桌布同步（把目前畫面設成系統桌布）、安裝／開機啟動／解除安裝 | 待做 |

## 結構

```
windows\
  Wall42.Core\            net8.0 類別庫，沒有 UI、沒有 GPU，可單元測試
    Config.cs             Config.swift 同 schema；所有欄位可省略、型別錯只略過該欄；mtime 熱重載
    Presets.cs            repo 的 presets\*.json（之後加安裝版副本）
    World.cs              World.swift 逐段移植：floating、連線 proximity／traffic／attention、脈衝、散景
    World.Effects.cs      snow、sand、Claude session 光點
    Layout.cs             螢幕（實體像素）→ 世界座標
    Activity.cs           GetSystemTimes 系統負載、平滑、think 到期、.signal 檔案信號
  Wall42.Core.Tests\      xunit：preset 全部可解析、世界步進不變量、多螢幕重映射、時鐘
  Wall42.Win\             net8.0-windows WinExe，AssemblyName wall42（PerMonitorV2 寫在 app.manifest）
    Shaders.hlsl          Renderer.swift 裡 Metal shader 的逐行 HLSL 版（執行期編譯，跟 Mac 一樣）
    Gpu.cs                D3D11 裝置、pipeline、粒子／連線 structured buffer（每步上傳一次，所有螢幕共用）、離線截圖、PNG
    Surface.cs            每台螢幕一個：WorkerW 底下的子視窗＋flip-model swap chain
    Desktop.cs            列舉螢幕、找 WorkerW（24H2 與舊版兩條路）
    Occlusion.cs          「這台螢幕是不是整個被蓋住」的覆蓋計算
    App.cs                主迴圈、fps 節拍、停畫判斷、WinEvent、廣播訊息、每秒 tick、log
    Program.cs            進入點、環境變數、WALL42_SNAPSHOT
  tools\Inspect\          唯讀檢查：Progman／WorkerW 階層、視窗樣式、點擊命中、PrintWindow 截桌面層
  tools\bench.ps1         實機量測：啟動 → 暖機 → 量 CPU／記憶體 → 檢查 → 依 pid 確認結束
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
