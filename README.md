# wall42

> 原名 **wall42**，2026-09-27 改名為 wall42。從舊版升級直接跑 `./install.sh`，
> 偵測到舊版會自動先跑 `scripts/migrate-from-wall42.sh`（見下方「從 wall42 升級」）。

macOS 桌布層動態粒子。懸浮微光粒子＋靠近連線的神經網路結構，跑在系統桌布之上、桌面圖示之下。

**被視窗完全遮擋時停止繪製，CPU 歸零、記憶體降 42%。** 這是整支程式的核心設計，不是附帶功能。

---

## 使用

```bash
./preset.sh         # 列出預設風格
./preset.sh neon    # 套用（存檔即生效，畫面立刻變）
./build.sh          # 編譯
./wall42            # 前景跑，Ctrl-C 結束
./install.sh        # 安裝並開機自動啟動
./uninstall.sh      # 移除（設定檔保留，系統桌布設定從未被動過）
```

設定檔 `~/.config/wall42/config.json`，**存檔即生效，不必重啟**。

---

## 參數

### background — 背景層

| 參數 | 說明 | 建議 |
|---|---|---|
| `mode` | `gradient` 徑向漸層／`solid` 純色 | gradient 有電影感打光，solid 最省 |
| `centerColor` | 漸層中心色 | 深色系，太亮會蓋過粒子 |
| `edgeColor` | 漸層邊緣色 | `#000000` |
| `radius` | 漸層擴散範圍 | 0.6 收成一團、1.02 攤開、1.5 幾乎全滿 |
| `solidColor` | `mode: solid` 時的顏色 | |

### motion — 動態層

| 參數 | 說明 | 建議 |
|---|---|---|
| `particleCount` | **一台主螢幕面積裡的粒子數**（密度）。多螢幕時整片世界實際顆數＝此值 × 世界面積 ÷ 主螢幕面積，兩台並排就是兩倍，每台看起來密度不變 | 140。**實測 CPU 與粒子數幾乎無關**，300 顆跟 120 顆差 0.6% |
| `fps` | 影格率上限 | 30。60Hz 螢幕只有 60/30/20/15 有效，寫 24 會被吃成 20 |
| `secondaryFps` | 非焦點螢幕的 fps（滑鼠與前景 App 視窗都不在上面的那台）。不設＝fps 的一半；只有一台螢幕時不作用 | 15 |
| `colorA` / `colorB` | 兩極色，粒子色相在兩者間分布 | 預設 cyan `#1ADBF5` / hot pink `#FC3D99` |
| `speed` | 漂浮速度 | 11。太快會失去「懸浮」感 |
| `sizeMin/Max` | 一般粒子大小 | 7–13。低於 5 白色亮核顯示不出來 |
| `nodeRatio` | 較大「節點」的比例 | 0.20 |
| `nodeSizeMin/Max` | 節點大小 | 17–27 |
| `brightness` | 整體亮度倍率 | 1.0 |
| `breathSpeed` | 呼吸明滅快慢 | 0.7 |
| `sizeBias` | 尺寸分布偏斜。1 = 均勻，越大越多小顆 | 2.2。星空類調到 3.0 |
| `twinkleVariance` | 每顆閃爍快慢的差異。0 = 全部同步呼吸 | 0.6。星空類調到 0.85 |

### motion.effect — 效果（熱重載，存檔即切換）

| 值 | 行為 | 相關參數 |
|---|---|---|
| `floating` | 原本的懸浮微光＋連線網路 | 全部 |
| `snow` | 雪花緩降、左右飄，落到螢幕可用區底部（避開 Dock）前淡出、從最上面重新飄下；x 在整片世界連續，會飄過接縫。散景粒子是近處大雪片，落得快晃得大 | `speed` 落速、`wind` 風速（正值往右，預設 8）、`softness` 邊緣柔和度、`bokeh.*` 近景雪片 |
| `sand` | 沙漏：每台螢幕上方一道細沙流，沙粒加速落下、依休止角滑落堆成沙丘；整座沙堆緩緩下沉、沉到底淡出，越舊的沙越暗。忙碌度越高沙流越快 | `speed` 終端落速、`streams` 每台幾道沙流（預設 1）、`sizeMin/Max` 沙粒大小、`particleCount` 沙粒池（建議 2000 以上） |

`softness`（0..1）：粒子邊緣柔和度。0 是原本的銳利亮點，雪片建議 0.5。
`background.mode` 多了 `vertical`：用整片世界算的上→下漸層（上 `edgeColor`、下 `centerColor`，`radius` 當曲線），兩台螢幕接起來沒有斷層，雪與沙的 preset 用這個。

### motion.sessions — Claude session 光點

每個活躍的 Claude session 一個帶細外環的常駐亮點（位置由 session id 雜湊決定，同一個 session 永遠在同一處，落在某台螢幕的可用區內）。
忙碌的 session 會持續發光，並在 attention 網路裡頻繁發起查詢（閒置時偶爾）。任何 effect 都能疊加。

| 參數 | 說明 |
|---|---|
| `enabled` | 開關 |
| `source` | `auto`（預設）讀 `~/.claude/sessions/<pid>.json`，pid 活著才算、`status=busy` 算忙碌；`file` 只看 `~/.config/wall42/sessions.json` |
| `size` | 光點大小，預設 nodeSizeMax × 1.15 |

`~/.config/wall42/sessions.json` 存在時優先於 auto，格式 `{"count":5,"busy":2}` 或 `{"sessions":[{"id":"a","busy":true}]}`，由 MCP `wall42_sessions(count, busy)` 寫入；`wall42_sessions()` 不給 count 就刪掉它回到自動。

### motion.link — 連線

| 參數 | 說明 | 建議 |
|---|---|---|
| `enabled` | 關掉就是純漂浮粒子 | |
| `distance` | 超過這個距離不連線 | 168。加大結構更密，但連線數是 O(n²) 成長 |
| `opacity` | 線的透明度 | 0.40。壓到 0.26 以下神經網路的結構感會消失 |
| `boost` | 線的額外亮度倍率 | 1.35 |
| `onlyNodes` | 只讓「節點」那些大粒子連線，做出星座圖的效果 | 星空類設 true。順帶讓 O(n²) 只在少數節點間算 |

### 連線模式（motion.link.mode）

| 模式 | 行為 | 觀感 |
|---|---|---|
| `proximity` | 距離內就連，連線是幾何關係的直接反映 | 固定的網＝結構圖，不像在運算 |
| `traffic` | 一池進行中的傳輸，淡入→脈衝跑過→淡出，不斷換對象 | 有傳輸感，但兩兩隨機配對像雜訊 |
| `attention` | **聚焦一個節點 → 向鄰近放射查詢 → 脈衝回流 → 焦點爆亮代表想通 → 轉移** | 有結構，像在思考。忙碌時多個焦點並行 |

`attention` 的三個階段（查詢 45%／回流 35%／下結論 20%）只是同一批線段的 alpha
與脈衝位置在變，沒有額外幾何或 draw call——實測 CPU 比 `traffic` 還低
（1.33% vs 4.2%，因為同時存在的線段少很多）。

### motion.pulse — 沿連線流動的脈衝

「有東西在算」的視覺來源。線段 fragment 已有沿線位置插值，只要算一個隨時間移動的
高斯波包，不需要額外幾何，成本是每個線段像素多一個指數運算。

| 參數 | 說明 | 建議 |
|---|---|---|
| `speed` | 每秒跑完幾條線 | 0.35。實際速度會再乘上活動度 |
| `strength` | 脈衝亮度 | 1.2 |
| `width` | 高斯寬度，越小越像一個點 | 0.003 |

### motion.activity — 忙碌程度

| 參數 | 說明 |
|---|---|
| `source` | `system` 讀系統 CPU 負載／`manual` 由 MCP 指定／`off` 關閉 |
| `manualLevel` | source=manual 時的值，0..1 |
| `smoothing` | 0..0.99，越大變化越慢。0.85 約 6 秒爬滿，不會抽動 |
| `minLoad` / `maxLoad` | 系統負載對應到 activity 0 與 1 的範圍 |

活動度會同時影響：脈衝速度與亮度、連線亮度、粒子呼吸速度。

### motion.bokeh — 前景散景

用大尺寸＋放緩的衰減曲線**假造失焦光斑**，不做真的 blur pass，成本跟畫小點一樣。

| 參數 | 說明 | 建議 |
|---|---|---|
| `ratio` | 散景粒子佔比 | 0.15 |
| `sizeMin/Max` | 光斑大小 | 34–72 |
| `speed` | 前景飄得比遠景快，製造視差 | 18 |
| `dimming` | 散景壓暗多少 | 0.22。越小景深對比越強 |

---

## 多螢幕：一片星空

所有螢幕合成一個世界（依「系統設定 → 顯示器」的實際排列取聯合矩形，含上下錯位），
粒子、連線、脈衝、attention 焦點都在整片世界裡跑，跨螢幕的線是連續的。
模擬只有一份（每幀 step 一次），每台螢幕只是從自己的位置看同一個世界，
所以 CPU 不會隨螢幕數倍增。粒子畫成 instanced quad，大光斑跨過接縫時兩邊各畫一半，不會突然消失。

- `link.targetCount` 同樣依面積放大，每台螢幕的思考密度跟單螢幕時一樣
- 背景漸層每台螢幕各一份（打光中心在各自螢幕正中）
- 螢幕排列改變時世界重建，既有粒子依比例映射到新世界，不會整片重來
- 遮擋暫停仍依各螢幕獨立：有任一台可見模擬就跑，全部被蓋住就整個停

## 實測數據（Mac mini M4 / 1920×1080 / 140 顆 / 30fps）

| 情境 | CPU（單核） | CPU（全機 10 核） | 記憶體 |
|---|---|---|---|
| 桌面可見 | 4.54% | 0.45% | 81.5MB |
| **被視窗遮擋** | **0.07%** | **0.007%** | **47.4MB** |
| 對照：什麼都不畫 | 4.44% | 0.44% | 81.3MB |

**整套視覺效果只比「什麼都不畫」多 0.10% CPU。** 成本幾乎全在 MTKView 每幀醒來的框架固定開銷，不在繪製本身——所以調粒子數、連線數、加背景漸層都不會讓它變貴。

---

## MCP

`mcp/wall42_mcp.py`　已註冊為使用者層級 MCP server（`claude mcp add --scope user wall42`）。
控制介面就是設定檔本身——wall42 每秒檢查 mtime，所以 MCP 只要寫檔，不需要任何 IPC。

| tool | 用途 |
|---|---|
| `wall42_status` | 執行狀態、是否被遮擋、fps、CPU、記憶體、連線數、活動度 |
| `wall42_list_presets` | 列出風格 |
| `wall42_set_preset` | 切換風格（會保留目前的 activity 設定） |
| `wall42_set_activity` | **忙碌感控制**：`manual`＋level 手動拉高、`system` 交還系統負載、`off` 關閉 |
| `wall42_set` | 改單一設定，如 `motion.link.distance` |
| `wall42_think` | **跑長任務前**拉高思考密度，到期自動回復 |
| `wall42_insight` | **想通一件事的當下**觸發爆亮脈衝，1.3 秒自然衰減 |
| `wall42_sync_wallpaper` | 把目前畫面同步成系統桌布（換風格後可重拍） |
| `wall42_sessions` | Claude session 光點：不給 count＝自動讀 ~/.claude/sessions 並回傳偵測到的清單；給 count/busy＝外部餵數字；enable=True 在目前設定打開光點 |
| `wall42_control` | start / stop / restart |

### AI 連動

```
跑長任務前   wall42_think(seconds=300, level=0.9)   # 到期自動回復，不必記得關
想通的當下   wall42_insight()                        # 閃一下，1.3 秒衰減
```

`think` **會自動到期**是刻意的：AI 可能忘記關掉，畫面就會一直卡在全速。
要提早收掉用 `wall42_set_activity(mode="system")`。

事件走**檔案信號**（`~/.config/wall42/.signal`，原子寫入），wall42 每秒檢查一次，
不需要 socket 或任何 IPC。信號路徑固定在 `~/.config/wall42`，不跟著 `WALL42_CONFIG`
漂移——否則指定別的設定檔測試時就收不到指令了。

### 跟 cool42 的分工

兩者都讀系統負載但互不依賴：**cool42 管溫度與風扇**（要不要開工），
**wall42 管「讓人看得出機器在忙」**。AI 開始長時間運算前呼叫
`wall42_set_activity(mode="manual", level=1.0)`，脈衝會沿連線加速流動、線條變亮；
結束後設回 `mode="system"`。

## 桌布同步

wall42 **沒有改系統桌布設定**——它是蓋在桌布圖層之上、桌面圖示之下的一個視窗。
好處是 `./uninstall.sh` 執行的瞬間原本的桌布就回來了，不需要還原任何東西。
代價是兩個落差：

1. 系統設定顯示的是原本那張，跟你眼睛看到的不一樣
2. 開機到 launchd 啟動它之間的空窗期，會露出舊桌布

```bash
./sync-wallpaper.sh      # 抓目前畫面設成系統桌布，補上這兩個落差
```

換過風格或調過顏色之後可以再跑一次重拍。原本的桌布路徑會存到
`backup/original-wallpaper.txt`，`./uninstall.sh` 會自動還原。

實作上是**檔案信號**：`touch ~/.config/wall42/.sync-request`，常駐實例每秒檢查到就
擷取當前畫面。所以抓的是你此刻看到的那一幀，不是重新渲染的。
輸出用 `wallpaper_a.png` / `wallpaper_b.png` 交替——macOS 對同一路徑的桌布會吃快取不重繪。

## 改完程式碼要重新 install

常駐跑的是 `~/.local/bin/wall42`，不是專案目錄裡那份。**只跑 `./build.sh` 不會影響常駐中的實例**——
改完程式碼要 `./install.sh` 才會生效（它會停掉舊的、換掉 binary、重新啟動）。

曾因此看到新設定被舊 binary 忽略：`onlyNodes` 沒生效，460 顆星全部互連、跑出 5000 多條線。

## 預設風格

`./preset.sh <名稱>`　切換前會自動備份，`./preset.sh --restore` 還原。

| 名稱 | 說明 |
|---|---|
| `neon` | cyan / hot pink，賽博霓虹 |
| `deepsea` | 青綠到藍，慢速，結構綿密 |
| `amber` | 暖色琥珀，夜間不刺眼 |
| `starfield` | 無連線，400 顆小點 |
| `starfield2` | 星空加強版：冪次尺寸、獨立閃爍、藍白到暖白 |
| `starfield-constellation` | 星空＋星座連線，只連亮星，約 96 條 |
| `starfield-web` | 星空＋全連線網格，約 1000 條，較密 |
| `neural` | AI 運算：密連線＋流動脈衝，跟著系統負載變化 |
| `compute` | 分工運算：無霓虹、單色線、連線不斷生滅 |
| `thinking` | **AI 思考**：聚焦→放射查詢→回流→想通爆亮 |
| `minimal` | 純黑底、無連線、少量大光點 |
| `snow` | 下雪：柔邊雪花＋近景大雪片，夜空垂直漸層，兩螢幕連續 |
| `sand` | 流沙：每台螢幕一道細沙流落成沙丘，像沙漏 |
| `sessions` | `kang` 風格＋每個 Claude session 一個帶環光點 |

## 從 wall42 升級

`scripts/migrate-from-wall42.sh` 把舊名留下的東西搬到新名，**冪等**（重跑只會跳過已完成的步驟）、
每一步做完都驗證，刪除一律改成移進備份區 `~/.local/share/wall42-migration/<時間>/`。

```bash
scripts/migrate-from-wall42.sh --dry-run   # 先看會做什麼，不改任何東西
scripts/migrate-from-wall42.sh             # 實際搬遷（./install.sh 偵測到舊版時會自動呼叫）
./install.sh                               # 裝上 wall42 並啟動
```

| 舊（wall42） | 新（wall42） |
|---|---|
| LaunchAgent `com.kang.wall42` | `com.kang.wall42` |
| `~/.local/bin/wall42` | `~/.local/bin/wall42` |
| `~/.config/wall42/`（設定、sessions.json、桌布圖） | `~/.config/wall42/` |
| `~/Library/Logs/wall42.log` | `~/Library/Logs/wall42.log` |
| MCP server `wall42`、tool `wall42_*` | `wall42`、`wall42_*` |
| 環境變數 `WALL42_*` | `WALL42_*` |

系統桌布若是之前同步出來、指向 `~/.config/wall42/` 裡的圖，搬遷時會改指新目錄的同名檔（原值先存進備份區）。
MCP 改名後要重開 Claude Code 才會載入 `wall42`。

## 已知限制

- 桌布層是非官方做法（Apple 沒有正式 API），未來 macOS 版本可能改變這一層的行為。Plash、Backdrop 等同類 app 都是同樣做法。
- 螢幕休眠、系統睡眠、鎖定、螢幕保護程式、切換使用者時一律停畫並釋放 drawable（log 會出現 `SUSPENDED(原因)`），醒來從當下時間接續，粒子不會瞬移。
- 全螢幕 App 所在的螢幕會自動停畫（遮擋判定），另一台照畫。Spaces／Stage Manager 驗證紀錄見 `docs/bench-20260926.md`。
- 記憶體 81MB 幾乎全是 AppKit＋Metal 框架的固定開銷，程式自己只用 0.2–0.4MB。
- 設定檔新增欄位一律要宣告成 Optional，否則舊設定檔缺少該 key 會讓 Codable 整份解碼失敗、使用者的設定被丟回預設值。

## 診斷開關（環境變數）

| 變數 | 用途 |
|---|---|
| `WALL42_FORCE_DRAW=1` | 忽略遮擋一直畫，量峰值消耗用 |
| `WALL42_NO_DRAW=1` | 只 clear 不下 draw call，量框架底線 |
| `WALL42_SNAPSHOT=路徑` | 第 90 幀存一張 PNG |
| `WALL42_DURATION=秒` | 跑幾秒後自動結束 |
| `WALL42_PARTICLES` / `WALL42_FPS` | 覆寫設定檔，測試用 |
| `WALL42_ONLY_MAIN=1` | 只開主螢幕，量單螢幕基準用 |
| `WALL42_REPO=路徑` | repo 位置（presets／README／backup）。`./install.sh` 會寫進 LaunchAgent；不設就是 `~/github-repos/wall42` |

測試用信號（寫到 `~/.config/wall42/.signal`）：
`{"kind":"snapshot","dir":"/路徑","tag":"x"}` 每個螢幕各存一張目前畫面（不改系統桌布）；
`{"kind":"debug-suspend","reason":"locked","on":true}` 走跟鎖定一樣的停畫路徑。

`./bench_cpu.sh [執行檔] [秒數] [標籤]` 暫停常駐、跑指定執行檔量自身 CPU、再把常駐開回來。
`sudo` 版的總帳（含 WindowServer 合成成本）：`./bench_powermetrics.sh`，用法寫在檔頭。
量測紀錄：`docs/bench-20260926.md`。
