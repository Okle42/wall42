import Cocoa
import MetalKit

// ── 環境變數只用於測試與量測；日常參數走設定檔 ──────────────────
func envInt(_ k: String) -> Int? { Int(ProcessInfo.processInfo.environment[k] ?? "") }
func envBool(_ k: String, _ d: Bool) -> Bool {
    guard let v = ProcessInfo.processInfo.environment[k] else { return d }
    return v == "1" || v.lowercased() == "true"
}

let DURATION   = envInt("WALL91_DURATION") ?? 0        // 0 = 一直跑
let ALL_SPACES = envBool("WALL91_ALL_SPACES", true)
// benchmark 專用：忽略遮擋一直畫，用來量「繪製時」的峰值消耗
let FORCE_DRAW = envBool("WALL91_FORCE_DRAW", false)
let SNAPSHOT   = ProcessInfo.processInfo.environment["WALL91_SNAPSHOT"]

func stamp() -> String {
    let f = DateFormatter(); f.dateFormat = "HH:mm:ss"
    return f.string(from: Date())
}
func log(_ s: String) { print("[\(stamp())] \(s)"); fflush(stdout) }

/// 一個螢幕一份：視窗＋MTKView＋Renderer。各螢幕獨立判斷遮擋，
/// 一個被蓋住只停那一個，另一個照畫（遮擋暫停是省資源的命脈）。
final class Surface {
    let displayID: CGDirectDisplayID
    let screen: NSScreen
    let window: NSWindow
    let view: MTKView
    let renderer: Renderer
    var occluded = false
    var pending: DispatchWorkItem?
    var lastFrames = 0

    init(screen: NSScreen, renderer: Renderer, device: MTLDevice, fps: Int) {
        self.screen = screen
        self.renderer = renderer
        displayID = (screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value ?? 0
        let frame = screen.frame
        view = MTKView(frame: CGRect(origin: .zero, size: frame.size), device: device)
        view.delegate = renderer
        view.colorPixelFormat = .bgra8Unorm
        view.clearColor = MTLClearColor(red: 0, green: 0, blue: 0, alpha: 1)
        view.preferredFramesPerSecond = fps
        view.enableSetNeedsDisplay = false
        view.isPaused = false

        window = NSWindow(contentRect: frame, styleMask: [.borderless],
                          backing: .buffered, defer: false, screen: screen)
        window.setFrame(frame, display: false)
        window.contentView = view
        window.isOpaque = true
        window.backgroundColor = .black
        window.ignoresMouseEvents = true          // 不擋點擊桌面圖示
        window.hasShadow = false
        window.isReleasedWhenClosed = false
        // 桌布層：蓋在系統桌布圖之上、桌面圖示之下
        window.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopWindow)))
        var behavior: NSWindow.CollectionBehavior = [.stationary, .ignoresCycle, .fullScreenNone]
        if ALL_SPACES { behavior.insert(.canJoinAllSpaces) }
        window.collectionBehavior = behavior
    }

    func close() {
        pending?.cancel()
        view.isPaused = true
        view.delegate = nil
        window.orderOut(nil)
        window.close()
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {

    private(set) var surfaces: [Surface] = []
    private var device: MTLDevice!
    /// 主螢幕那一份。選單列、面板讀設定與統計都看它（所有螢幕共用同一份設定）。
    var renderer: Renderer! { (surfaces.first { $0.screen == NSScreen.main } ?? surfaces.first)?.renderer }

    private var lastReport = CFAbsoluteTimeGetCurrent()
    private var menuBar: MenuBarController?
    private var panel: ControlPanel?
    // 給選單列讀的即時狀態
    private(set) var lastFps: Double = 0
    private(set) var lastCPUPercent: Double = 0
    private(set) var manuallyPaused = false
    /// 所有螢幕都被蓋住才算「被遮住」（給選單列顯示）
    var isOccluded: Bool { !surfaces.isEmpty && surfaces.allSatisfy { $0.occluded } }
    private var occlusionEvents = 0
    private var lastCPU: Double = 0
    private var configMTime: Date?

    func applicationDidFinishLaunching(_ note: Notification) {
        var (cfg, warn) = Config.load()
        if let w = warn { log("⚠ \(w)") }
        // 測試用覆寫
        if let n = envInt("WALL91_PARTICLES") { cfg.motion.particleCount = n }
        if let f = envInt("WALL91_FPS")       { cfg.motion.fps = f }
        configMTime = Config.modifiedAt()

        guard let device = MTLCreateSystemDefaultDevice() else { log("FATAL 沒有 Metal 裝置"); exit(1) }
        self.device = device
        rebuildSurfaces(cfg)
        guard !surfaces.isEmpty else { log("FATAL 沒有可用的螢幕或 Renderer 初始化失敗"); exit(1) }
        if let path = SNAPSHOT {
            renderer.snapshotPath = path
            renderer.snapshotAtFrame = envInt("WALL91_SNAPSHOT_FRAME") ?? 90
        }

        // 螢幕插拔、改解析度、排列變更時重建
        NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification,
            object: nil, queue: .main) { [weak self] _ in
                guard let self = self else { return }
                // 系統會連發好幾次，稍等穩定再重建
                self.pendingRebuild?.cancel()
                let w = DispatchWorkItem { [weak self] in
                    guard let self = self, let cfg = self.renderer?.config else { return }
                    log(">>> 螢幕配置改變，重建")
                    self.rebuildSurfaces(cfg)
                }
                self.pendingRebuild = w
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.8, execute: w)
            }

        Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            self?.tick()
        }
        if DURATION > 0 {
            Timer.scheduledTimer(withTimeInterval: Double(DURATION), repeats: false) { _ in
                log("到時間，結束"); NSApp.terminate(nil)
            }
        }

        log("啟動 particles=\(cfg.motion.particleCount) fps=\(cfg.motion.fps) "
            + "effect=\(cfg.motion.effect) 螢幕=" + surfaces.map { "\(Int($0.screen.frame.width))x\(Int($0.screen.frame.height))" }.joined(separator: ","))
        log("設定檔：\(Config.path.path)　（存檔後自動套用，不必重啟）")

        if cfg.ui?.menuBar ?? true {
            menuBar = MenuBarController(app: self)
        }
    }

    private var pendingRebuild: DispatchWorkItem?

    /// 依目前的螢幕清單建立每個螢幕的視窗。沿用同一個 displayID 的 renderer，
    /// 這樣改解析度或插拔另一台時，原本螢幕上的粒子不會重來。
    func rebuildSurfaces(_ cfg: Config) {
        var old: [CGDirectDisplayID: Renderer] = [:]
        for sf in surfaces { old[sf.displayID] = sf.renderer; sf.close() }
        surfaces.removeAll()
        for screen in NSScreen.screens {
            let id = (screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value ?? 0
            guard let r = old[id] ?? Renderer(device: device, config: cfg) else {
                log("⚠ 螢幕 \(id) 的 Renderer 初始化失敗，略過"); continue
            }
            let sf = Surface(screen: screen, renderer: r, device: device, fps: cfg.motion.fps)
            surfaces.append(sf)
            sf.window.orderFront(nil)
            NotificationCenter.default.addObserver(
                forName: NSWindow.didChangeOcclusionStateNotification,
                object: sf.window, queue: .main) { [weak self, weak sf] _ in
                    if let sf = sf { self?.occlusionChanged(sf) }
                }
        }
        log("螢幕數 \(surfaces.count)")
    }

    /// 設定套到每個螢幕
    func applyToAll(_ cfg: Config) {
        for sf in surfaces { sf.renderer.apply(cfg) }
        syncFPSIfNeeded(cfg)
    }

    // ── 遮擋暫停：整個省資源架構的關鍵 ────────────────────────────
    /// 不對稱去抖：
    ///   恢復顯示 -> 立即生效（使用者體感優先）
    ///   進入遮擋 -> 延遲 300ms（視窗切換瞬間系統的 occlusion 判定會震盪，
    ///              實測一次切換會來回跳 7 次、白畫 16 幀）
    private func occlusionChanged(_ sf: Surface) {
        let visible = sf.window.occlusionState.contains(.visible)
        occlusionEvents += 1
        sf.pending?.cancel()
        if visible {
            applyOcclusion(sf, false)
        } else {
            let work = DispatchWorkItem { [weak self, weak sf] in
                if let sf = sf { self?.applyOcclusion(sf, true) }
            }
            sf.pending = work
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3, execute: work)
        }
    }

    /// 開啟調參數面板（選單列呼叫）
    func showControlPanel() {
        if panel == nil { panel = ControlPanel(app: self) }
        panel?.show()
    }

    /// 面板改完設定後寫回檔案。同時更新 mtime 記錄，
    /// 免得自己寫的檔案又被熱重載讀一次、白跑一輪 apply。
    func saveConfig(_ cfg: Config) {
        guard let d = try? JSONEncoder.pretty.encode(cfg) else { return }
        let tmp = Config.path.appendingPathExtension("tmp")
        do {
            try d.write(to: tmp)
            _ = try FileManager.default.replaceItemAt(Config.path, withItemAt: tmp)
            configMTime = Config.modifiedAt()
        } catch {
            log("⚠ 寫入設定失敗：\(error.localizedDescription)")
        }
    }

    /// fps 不走 uniform，改了要通知 MTKView
    func syncFPSIfNeeded(_ cfg: Config) {
        for sf in surfaces where sf.view.preferredFramesPerSecond != cfg.motion.fps {
            sf.view.preferredFramesPerSecond = cfg.motion.fps
        }
    }

    /// 從選單列手動暫停／繼續。與遮擋暫停獨立，兩者任一成立就不畫。
    func toggleManualPause() {
        manuallyPaused.toggle()
        for sf in surfaces {
            sf.view.isPaused = manuallyPaused || sf.occluded
            if manuallyPaused { sf.view.releaseDrawables() }
        }
        log(manuallyPaused ? ">>> 使用者手動暫停" : ">>> 使用者手動繼續")
    }

    /// 目前設定和哪個 preset 一致（比對時忽略 activity，因為切換風格會保留它）。
    var currentPresetName: String? {
        func normalized(_ url: URL) -> String? {
            guard let d = try? Data(contentsOf: url),
                  var o = try? JSONSerialization.jsonObject(with: d) as? [String: Any],
                  var mo = o["motion"] as? [String: Any] else { return nil }
            mo.removeValue(forKey: "activity")
            o["motion"] = mo
            o.removeValue(forKey: "ui")
            guard let out = try? JSONSerialization.data(withJSONObject: o,
                                                        options: [.sortedKeys]) else { return nil }
            return String(data: out, encoding: .utf8)
        }
        guard let cur = normalized(Config.path) else { return nil }
        let dir = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("github-repos/wall91/presets")
        let files = (try? FileManager.default.contentsOfDirectory(atPath: dir.path)) ?? []
        for f in files where f.hasSuffix(".json") {
            if normalized(dir.appendingPathComponent(f)) == cur {
                return String(f.dropLast(5))
            }
        }
        return nil
    }

    private func applyOcclusion(_ sf: Surface, _ isOccluded: Bool) {
        if FORCE_DRAW { return }
        guard isOccluded != sf.occluded else { return }
        sf.occluded = isOccluded
        sf.view.isPaused = isOccluded || manuallyPaused
        if isOccluded { sf.view.releaseDrawables() }   // 順帶把 framebuffer 還回去，記憶體掉 42%
        log(">>> 螢幕 \(sf.displayID) \(isOccluded ? "OCCLUDED（停止繪製）" : "VISIBLE（恢復繪製）")")
    }

    // ── 每秒：熱重載檢查 ＋ 統計 ─────────────────────────────────
    private func tick() {
        reloadIfChanged()
        updateActivity()
        checkSyncRequest()
        report()
    }

    // ── 把目前畫面同步成系統桌布 ──────────────────────────────
    // wall91 是蓋在桌布上的視窗，沒有改系統桌布設定。好處是移除即還原，
    // 代價是系統設定顯示的跟眼睛看到的不一致、而且啟動前的空窗期會露出舊桌布。
    // 這裡抓一張當前畫面設成系統桌布，把那兩個落差補起來。

    /// 產物（桌布圖、slot 記錄）跟著設定檔走，測試指定別的 config 時才不會互相污染
    private var wall91Dir: URL {
        Config.path.deletingLastPathComponent()
    }
    /// 控制信號固定在這裡。MCP 與 CLI 永遠寫這個路徑，
    /// 不能跟著 WALL91_CONFIG 漂移，否則指定別的設定檔時就收不到指令了。
    private var signalDir: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".config/wall91", isDirectory: true)
    }
    private var backupDir: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("github-repos/wall91/backup")
    }

    /// 由 `touch ~/.config/wall91/.sync-request` 觸發（CLI 與 MCP 都走這個）
    private func checkSyncRequest() {
        let dir = signalDir
        let req = dir.appendingPathComponent(".sync-request")
        if FileManager.default.fileExists(atPath: req.path) {
            try? FileManager.default.removeItem(at: req)
            syncWallpaper()
        }
        // AI 注入的事件：think（暫時拉高忙碌度）與 insight（想通爆亮）
        let sig = dir.appendingPathComponent(".signal")
        if let d = try? Data(contentsOf: sig) {
            try? FileManager.default.removeItem(at: sig)
            handleSignal(d)
        }

        // 面板也用同一套檔案信號，這樣 CLI 和 MCP 都開得起來
        let showPanel = dir.appendingPathComponent(".show-panel")
        if FileManager.default.fileExists(atPath: showPanel.path) {
            try? FileManager.default.removeItem(at: showPanel)
            showControlPanel()
        }
    }

    /// 處理 MCP 寫進來的事件。格式：{"kind":"think","level":0.9,"seconds":120}
    ///                        或 {"kind":"insight","strength":1.0}
    private func handleSignal(_ data: Data) {
        guard let o = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let kind = o["kind"] as? String else { return }
        switch kind {
        case "think":
            let level = Float((o["level"] as? Double) ?? 0.85)
            let secs = (o["seconds"] as? Double) ?? 120
            activityOverride = (max(0, min(1, level)),
                                Date().addingTimeInterval(min(3600, max(5, secs))))
            log(">>> think 啟動 level=\(level) 持續 \(Int(secs))s")
        case "insight":
            let st = Float((o["strength"] as? Double) ?? 1.0)
            for sf in surfaces { sf.renderer.triggerInsight(st) }
            log(">>> insight 觸發 strength=\(st)")
        default:
            log("⚠ 不認得的信號：\(kind)")
        }
    }

    func syncWallpaper() {
        backupOriginalWallpaperIfNeeded()
        // macOS 對「同一路徑」的桌布會吃快取不重繪，A/B 交替避開
        let slot = (try? String(contentsOf: wall91Dir.appendingPathComponent(".slot"),
                                encoding: .utf8)) == "a" ? "b" : "a"
        try? slot.write(to: wall91Dir.appendingPathComponent(".slot"),
                        atomically: true, encoding: .utf8)
        // 清掉另一格的舊圖（含舊版單螢幕檔名）
        let other = slot == "a" ? "b" : "a"
        if let files = try? FileManager.default.contentsOfDirectory(atPath: wall91Dir.path) {
            for f in files where f.hasPrefix("wallpaper_\(other)") && f.hasSuffix(".png") {
                try? FileManager.default.removeItem(at: wall91Dir.appendingPathComponent(f))
            }
        }
        // 每個螢幕截自己的畫面、設成自己的桌布
        for sf in surfaces {
            let out = wall91Dir.appendingPathComponent("wallpaper_\(slot)_\(sf.displayID).png")
            let screen = sf.screen
            sf.renderer.snapshotAtFrame = 0
            sf.renderer.onSnapshot = { [weak self] path in
                DispatchQueue.main.async { self?.applyWallpaper(URL(fileURLWithPath: path), screen: screen) }
            }
            sf.renderer.snapshotPath = out.path
        }
        log("同步桌布：擷取 \(surfaces.count) 個螢幕的畫面…")
    }

    private func applyWallpaper(_ url: URL, screen: NSScreen) {
        do {
            try NSWorkspace.shared.setDesktopImageURL(url, for: screen, options: [:])
            log("同步桌布完成 -> \(url.lastPathComponent)")
        } catch {
            log("⚠ 設定系統桌布失敗：\(error.localizedDescription)")
        }
    }

    /// 只在第一次同步前記錄，之後不覆寫，免得把我們自己產的圖記成「原本的」。
    private func backupOriginalWallpaperIfNeeded() {
        let f = backupDir.appendingPathComponent("original-wallpaper.txt")
        guard !FileManager.default.fileExists(atPath: f.path) else { return }
        guard let screen = NSScreen.main,
              let cur = NSWorkspace.shared.desktopImageURL(for: screen) else { return }
        if cur.path.contains("/.config/wall91/") { return }
        try? FileManager.default.createDirectory(at: backupDir, withIntermediateDirectories: true)
        try? cur.path.write(to: f, atomically: true, encoding: .utf8)
        log("已備份原本的系統桌布路徑 -> backup/original-wallpaper.txt")
    }

    private func reloadIfChanged() {
        guard let m = Config.modifiedAt() else { return }
        guard m != configMTime else { return }
        configMTime = m
        let (cfg, warn) = Config.load()
        if let w = warn { log("⚠ \(w)"); return }
        applyToAll(cfg)
        let wantMenuBar = cfg.ui?.menuBar ?? true
        if wantMenuBar && menuBar == nil { menuBar = MenuBarController(app: self) }
        if !wantMenuBar && menuBar != nil { menuBar = nil }
        panel?.refresh()
        log("♻ 設定已重新載入 particles=\(cfg.motion.particleCount) fps=\(cfg.motion.fps)")
    }

    // ── 系統整體 CPU 負載（0..1），用來驅動「在運算」的視覺強度 ──
    private var lastTicks: (user: UInt32, system: UInt32, idle: UInt32, nice: UInt32)?
    private var smoothedActivity: Float = 0
    /// MCP 的 think 指令：暫時覆寫忙碌度，到期自動失效回到原本的來源
    private var activityOverride: (level: Float, until: Date)?

    private func systemLoad() -> Float? {
        var size = mach_msg_type_number_t(MemoryLayout<host_cpu_load_info_data_t>.size
                                          / MemoryLayout<integer_t>.size)
        var info = host_cpu_load_info_data_t()
        let kr = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(size)) {
                host_statistics(mach_host_self(), HOST_CPU_LOAD_INFO, $0, &size)
            }
        }
        guard kr == KERN_SUCCESS else { return nil }
        let cur = (user: info.cpu_ticks.0, system: info.cpu_ticks.1,
                   idle: info.cpu_ticks.2, nice: info.cpu_ticks.3)
        defer { lastTicks = cur }
        guard let p = lastTicks else { return nil }   // 第一次沒有基準，跳過
        let du = Double(cur.user &- p.user), ds = Double(cur.system &- p.system)
        let di = Double(cur.idle &- p.idle), dn = Double(cur.nice &- p.nice)
        let total = du + ds + di + dn
        guard total > 0 else { return nil }
        return Float((du + ds + dn) / total)
    }

    /// 依設定把系統負載（或手動值）轉成 0..1 的活動度，並做指數平滑。
    private func updateActivity() {
        let a = renderer.config.motion.activity ?? .default
        let src = a.source ?? "system"
        var target: Float = 0

        // think 期間直接覆寫，並平滑過去，避免畫面瞬間跳滿
        if let o = activityOverride {
            if Date() < o.until {
                smoothedActivity = smoothedActivity * 0.55 + o.level * 0.45
                for sf in surfaces { sf.renderer.activity = smoothedActivity }
                return
            }
            activityOverride = nil          // 到期自動回復
            log(">>> think 期間結束，交還給 \(src)")
        }

        switch src {
        case "off":    target = 0
        case "manual": target = max(0, min(1, a.manualLevel ?? 0))
        default:
            guard let load = systemLoad() else { return }
            let lo = a.minLoad ?? 0.08, hi = max((a.maxLoad ?? 0.75), (a.minLoad ?? 0.08) + 0.01)
            target = max(0, min(1, (load - lo) / (hi - lo)))
        }
        let k = max(0, min(0.99, a.smoothing ?? 0.85))
        smoothedActivity = smoothedActivity * k + target * (1 - k)
        for sf in surfaces { sf.renderer.activity = smoothedActivity }
    }

    private func cpuSeconds() -> Double {
        var u = rusage()
        guard getrusage(RUSAGE_SELF, &u) == 0 else { return -1 }
        return Double(u.ru_utime.tv_sec) + Double(u.ru_utime.tv_usec) / 1e6
             + Double(u.ru_stime.tv_sec) + Double(u.ru_stime.tv_usec) / 1e6
    }

    private func memoryMB() -> Double {
        var info = task_vm_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<natural_t>.size)
        let kr = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count)
            }
        }
        return kr == KERN_SUCCESS ? Double(info.phys_footprint) / 1048576.0 : -1
    }

    private func report() {
        let now = CFAbsoluteTimeGetCurrent()
        let dt = now - lastReport; lastReport = now
        // fps 以畫最多的那個螢幕為準（各螢幕同一個 fps 設定）
        var delta = 0, links = 0
        for sf in surfaces {
            let d = sf.renderer.frameCount - sf.lastFrames
            sf.lastFrames = sf.renderer.frameCount
            delta = max(delta, d)
            links += sf.occluded ? 0 : sf.renderer.lastLinkCount
        }
        let fps = dt > 0 ? Double(delta) / dt : 0

        let c = cpuSeconds()
        let cpuPct = (lastCPU > 0 && dt > 0) ? (c - lastCPU) / dt * 100.0 : 0
        lastCPU = c
        let mem = memoryMB()
        lastFps = fps
        lastCPUPercent = cpuPct

        if isOccluded {
            let verdict = delta == 0 ? "OK" : "⚠ 仍在繪製"
            log(String(format: "OCCLUDED  cpu=%.2f%%  frames=+%d %@  mem=%.1fMB",
                       cpuPct, delta, verdict, mem))
        } else {
            let vis = surfaces.filter { !$0.occluded }.count
            log(String(format: "visible   screens=%d/%d  fps=%.1f  cpu=%.2f%%  links=%d  act=%.2f  mem=%.1fMB",
                       vis, surfaces.count, fps, cpuPct, links, renderer.activity, mem))
        }
    }

    func applicationWillTerminate(_ note: Notification) {
        log("結束：總幀數=\(surfaces.map { $0.renderer.frameCount }) occlusion 事件=\(occlusionEvents)")
    }
}

let app = NSApplication.shared
app.setActivationPolicy(.accessory)      // 不進 Dock、不進 Cmd-Tab
let delegate = AppDelegate()
app.delegate = delegate
app.run()
