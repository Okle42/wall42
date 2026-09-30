import Cocoa
import MetalKit

// ── 環境變數只用於測試與量測；日常參數走設定檔 ──────────────────
func envInt(_ k: String) -> Int? { Int(ProcessInfo.processInfo.environment[k] ?? "") }
func envBool(_ k: String, _ d: Bool) -> Bool {
    guard let v = ProcessInfo.processInfo.environment[k] else { return d }
    return v == "1" || v.lowercased() == "true"
}

let DURATION   = envInt("WALL42_DURATION") ?? 0        // 0 = 一直跑
let ALL_SPACES = envBool("WALL42_ALL_SPACES", true)
// benchmark 專用：忽略遮擋一直畫，用來量「繪製時」的峰值消耗
let FORCE_DRAW = envBool("WALL42_FORCE_DRAW", false)
let SNAPSHOT   = ProcessInfo.processInfo.environment["WALL42_SNAPSHOT"]
// 量測用：只開主螢幕（單螢幕基準），其他螢幕不建視窗
let ONLY_MAIN  = envBool("WALL42_ONLY_MAIN", false)
// 每秒一行統計（bench 腳本解析用）。常駐時改 60 秒一行，免得 log 一天長 7MB
let VERBOSE    = DURATION > 0 || envBool("WALL42_VERBOSE", false)
// 測試用：第一次建立的畫面不讓 display link 觸發，重現開機後 0 幀，驗證看門狗
let SIMULATE_STALL = envBool("WALL42_SIMULATE_STALL", false)
// 測試用：閒置秒數改成「啟動後經過幾秒」，不受實際鍵盤滑鼠影響，驗證閒置降速
let SIMULATE_IDLE = envBool("WALL42_SIMULATE_IDLE", false)
let LAUNCHED_AT = CFAbsoluteTimeGetCurrent()
/// log 超過這個大小就清空重來（launchd 以 append 開檔，無法由外部輪替）
let LOG_MAX_BYTES: UInt64 = 20 * 1024 * 1024

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
    /// 滑鼠或前景 App 的視窗在這台上。非焦點螢幕降 fps 省資源。
    var focused = true
    var pending: DispatchWorkItem?
    var lastFrames = 0

    init(screen: NSScreen, renderer: Renderer, device: MTLDevice, fps: Int, stall: Bool = false) {
        self.screen = screen
        self.renderer = renderer
        displayID = (screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value ?? 0
        let frame = screen.frame
        view = MTKView(frame: CGRect(origin: .zero, size: frame.size), device: device)
        // stall：不接 delegate，draw(in:) 永遠不會被呼叫，等同 display link 不觸發
        view.delegate = stall ? nil : renderer
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
    private var gpu: GPU!
    /// 整片星空只有一份模擬，所有螢幕共用
    private(set) var world: World!
    /// 選單列、面板讀設定與統計的入口（名稱沿用舊版，實際就是共用的 World）
    var renderer: World! { world }

    private var lastReport = CFAbsoluteTimeGetCurrent()
    private var lastSteps = 0
    private var stalledSeconds = 0
    private var stallRebuilds = 0
    private var stallSimulated = false
    /// 0 有人在用｜1 降速｜2 幾乎停住
    private var idleLevel = 0
    private var lastMouse = NSPoint.zero
    private var lastMouseMove = CFAbsoluteTimeGetCurrent()
    private var reportTick = 0
    private var menuBar: MenuBarController?
    private var panel: ControlPanel?
    // 給選單列讀的即時狀態
    private(set) var lastFps: Double = 0
    private(set) var lastCPUPercent: Double = 0
    private(set) var manuallyPaused = false
    /// 所有螢幕都被蓋住才算「被遮住」（給選單列顯示）
    var isOccluded: Bool { !surfaces.isEmpty && surfaces.allSatisfy { $0.occluded } }
    private var occlusionEvents = 0
    /// 螢幕休眠／鎖定／螢幕保護程式／切換使用者時暫停繪製的原因。任何一個成立就全部停畫。
    /// 遮擋判定在這些情境不一定可靠（休眠時視窗在系統眼中仍「可見」），所以另外明確處理。
    private(set) var suspendReasons = Set<String>()
    /// 最近一次停畫／恢復的時間。停畫後第一份統計涵蓋停畫前的幀，不能拿來判定「仍在繪製」。
    private var lastPauseChange: CFAbsoluteTime = 0
    private var lastCPU: Double = 0
    private var configMTime: Date?

    func applicationDidFinishLaunching(_ note: Notification) {
        trimLogIfNeeded()      // 先清再寫，啟動訊息才不會跟著被清掉
        var (cfg, warn) = Config.load()
        if let w = warn { log("⚠ \(w)") }
        // 測試用覆寫
        if let n = envInt("WALL42_PARTICLES") { cfg.motion.particleCount = n }
        if let f = envInt("WALL42_FPS")       { cfg.motion.fps = f }
        configMTime = Config.modifiedAt()

        guard let device = MTLCreateSystemDefaultDevice() else { log("FATAL 沒有 Metal 裝置"); exit(1) }
        self.device = device
        guard let g = GPU(device: device) else { log("FATAL shader／pipeline 建立失敗"); exit(1) }
        gpu = g
        world = World(device: device, config: cfg)
        rebuildSurfaces(cfg)
        guard !surfaces.isEmpty else { log("FATAL 沒有可用的螢幕"); exit(1) }
        if let path = SNAPSHOT, let r = surfaces.first?.renderer {
            r.snapshotPath = path
            r.snapshotAtFrame = envInt("WALL42_SNAPSHOT_FRAME") ?? 90
        }

        // 螢幕插拔、改解析度、排列變更時重建
        NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification,
            object: nil, queue: .main) { [weak self] _ in
                guard let self = self else { return }
                // 系統會連發好幾次，稍等穩定再重建
                self.pendingRebuild?.cancel()
                let w = DispatchWorkItem { [weak self] in
                    guard let self = self, let cfg = self.world?.config else { return }
                    log(">>> 螢幕配置改變，重建")
                    self.rebuildSurfaces(cfg)
                }
                self.pendingRebuild = w
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.8, execute: w)
            }

        installPowerObservers()

        Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            self?.tick()
        }
        if DURATION > 0 {
            Timer.scheduledTimer(withTimeInterval: Double(DURATION), repeats: false) { _ in
                log("到時間，結束"); NSApp.terminate(nil)
            }
        }

        log("啟動 particles=\(cfg.motion.particleCount)/主螢幕面積（整片世界實際 \(world.drawCount) 顆） fps=\(cfg.motion.fps) "
            + "effect=\(cfg.motion.effect) 螢幕=" + surfaces.map { "\(Int($0.screen.frame.width))x\(Int($0.screen.frame.height))" }.joined(separator: ","))
        log("設定檔：\(Config.path.path)　（存檔後自動套用，不必重啟）")

        if cfg.ui?.menuBar ?? true {
            menuBar = MenuBarController(app: self)
        }
    }

    private var pendingRebuild: DispatchWorkItem?
    /// 測試用：模擬「拔掉副螢幕」（debug-relayout 信號），驗證世界重建時粒子依比例保留
    private var debugMainOnly = false

    /// 依目前的螢幕清單建立每個螢幕的視窗。沿用同一個 displayID 的 renderer，
    /// 這樣改解析度或插拔另一台時，原本螢幕上的粒子不會重來。
    func rebuildSurfaces(_ cfg: Config) {
        for sf in surfaces { sf.close() }
        surfaces.removeAll()
        let screens = (ONLY_MAIN || debugMainOnly) ? Array(NSScreen.screens.prefix(1)) : NSScreen.screens
        guard !screens.isEmpty else { return }

        // 世界＝所有螢幕的聯合矩形（含上下錯位）。Cocoa 是 y 向上，世界座標翻成 y 向下。
        let union = screens.dropFirst().reduce(screens[0].frame) { $0.union($1.frame) }
        func slot(_ s: NSScreen) -> ScreenSlot {
            let f = s.frame, v = s.visibleFrame
            return ScreenSlot(origin: SIMD2(Float(f.minX - union.minX), Float(union.maxY - f.maxY)),
                              size: SIMD2(Float(f.width), Float(f.height)),
                              visibleTop: Float(union.maxY - v.maxY),
                              visibleBottom: Float(union.maxY - v.minY))
        }
        let main = screens[0].frame       // screens[0] 永遠是有選單列的主螢幕
        world.setLayout(screens.map(slot), mainArea: Float(main.width * main.height))
        log(String(format: "世界 %.0fx%.0f（主螢幕面積的 %.2f 倍）粒子 %d 顆",
                   world.size.x, world.size.y, world.areaScale, world.drawCount))

        for screen in screens {
            let r = Renderer(gpu: gpu, world: world)
            let sl = slot(screen)
            r.viewOrigin = sl.origin
            r.viewSize = sl.size
            r.pxScale = Float(screen.backingScaleFactor)
            let sf = Surface(screen: screen, renderer: r, device: device, fps: cfg.motion.fps,
                             stall: SIMULATE_STALL && !stallSimulated)
            surfaces.append(sf)
            sf.window.orderFront(nil)
            NotificationCenter.default.addObserver(
                forName: NSWindow.didChangeOcclusionStateNotification,
                object: sf.window, queue: .main) { [weak self, weak sf] _ in
                    if let sf = sf { self?.occlusionChanged(sf) }
                }
        }
        stallSimulated = true
        syncFPSIfNeeded(cfg)
        log("螢幕數 \(surfaces.count)")
    }

    /// 設定套到每個螢幕
    func applyToAll(_ cfg: Config) {
        world.apply(cfg)
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

    /// fps 不走 uniform，改了要通知 MTKView。焦點螢幕用 fps，其他用 secondaryFps。
    func syncFPSIfNeeded(_ cfg: Config) {
        let full = max(1, cfg.motion.fps)
        let low = max(1, min(full, cfg.motion.secondaryFps ?? max(1, full / 2)))
        var fastest = 1
        let cap = idleFpsCap(cfg)
        for sf in surfaces {
            let want = min(cap, (surfaces.count == 1 || sf.focused) ? full : low)
            if sf.view.preferredFramesPerSecond != want {
                sf.view.preferredFramesPerSecond = want
            }
            if !sf.view.isPaused { fastest = max(fastest, want) }
        }
        // 模擬跟著畫得最快的那台走；全部停著時沿用 full，恢復的第一幀不會被擋
        world.stepInterval = 1.0 / Double(surfaces.contains { !$0.view.isPaused } ? fastest : min(cap, full))
    }

    // ── 閒置降速 ─────────────────────────────────────────────
    /// 最後一次鍵盤／滑鼠／觸控板輸入距今秒數（不需要輔助使用權限）
    private var idleSeconds: Double {
        if SIMULATE_IDLE { return CFAbsoluteTimeGetCurrent() - LAUNCHED_AT }
        return CGEventSource.secondsSinceLastEventType(.combinedSessionState,
                                                eventType: CGEventType(rawValue: ~0)!)
    }

    private func idleFpsCap(_ cfg: Config) -> Int {
        let ic = cfg.motion.idle
        switch idleLevel {
        case 2: return max(1, ic?.deepFps ?? 1)
        case 1: return max(1, ic?.slowFps ?? 5)
        default: return Int.max
        }
    }

    private func updateIdle() {
        let ic = world.config.motion.idle
        var level = 0
        if ic?.enabled ?? true {
            let idle = idleSeconds
            if idle >= Double(ic?.deepAfter ?? 1800) { level = 2 }
            else if idle >= Double(ic?.slowAfter ?? 600) { level = 1 }
        }
        guard level != idleLevel else { return }
        let from = idleLevel
        idleLevel = level
        syncFPSIfNeeded(world.config)
        let names = ["恢復", "閒置降速", "閒置幾乎停住"]
        log(">>> \(names[level])（\(from)→\(level)，閒置 \(Int(idleSeconds)) 秒）fps=" +
            surfaces.map { "\($0.view.preferredFramesPerSecond)" }.joined(separator: "/"))
    }

    // ── Claude session 光點的資料來源 ─────────────────────────────
    private var sessionTick = 0
    private var lastSessionSummary = ""

    /// 每 2 秒更新一次。設定沒開就什麼都不讀。
    /// 來源：~/.config/wall42/sessions.json（MCP 餵的）優先；否則讀 ~/.claude/sessions/*.json。
    private func updateSessions() {
        guard let sc = world.config.motion.sessions, sc.enabled ?? false else {
            if !world.sessions.isEmpty { world.setSessions([]) }
            return
        }
        sessionTick += 1
        if sessionTick % 2 == 0 && !world.sessions.isEmpty { return }
        let feed = signalDir.appendingPathComponent("sessions.json")
        var list: [World.SessionInfo] = []
        var from = "auto"
        if (sc.source ?? "auto") == "file" || FileManager.default.fileExists(atPath: feed.path) {
            from = "file"
            if let d = try? Data(contentsOf: feed),
               let o = (try? JSONSerialization.jsonObject(with: d)) as? [String: Any] {
                if let arr = o["sessions"] as? [[String: Any]] {
                    for (k, e) in arr.enumerated() {
                        list.append(.init(id: (e["id"] as? String) ?? "feed-\(k)",
                                          busy: (e["busy"] as? Bool) ?? false))
                    }
                } else {
                    let n = max(0, min(64, (o["count"] as? Int) ?? 0))
                    let b = max(0, (o["busy"] as? Int) ?? 0)
                    list = (0..<n).map { .init(id: "feed-\($0)", busy: $0 < b) }
                }
            }
        } else {
            list = scanClaudeSessions()
        }
        world.setSessions(list)
        let summary = "\(from) \(list.count) 個（忙碌 \(list.filter(\.busy).count)）"
        if summary != lastSessionSummary {
            lastSessionSummary = summary
            log(">>> sessions \(summary)")
        }
    }

    /// Claude Code 每個 session 會寫 ~/.claude/sessions/<pid>.json，含 sessionId 與 status（busy/idle）。
    /// pid 還活著的才算；檔案格式不對的直接略過。
    private func scanClaudeSessions() -> [World.SessionInfo] {
        let dir = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".claude/sessions")
        guard let files = try? FileManager.default.contentsOfDirectory(atPath: dir.path) else { return [] }
        var out: [(Double, World.SessionInfo)] = []
        for f in files where f.hasSuffix(".json") {
            guard let d = try? Data(contentsOf: dir.appendingPathComponent(f)),
                  let o = (try? JSONSerialization.jsonObject(with: d)) as? [String: Any],
                  let pid = (o["pid"] as? NSNumber)?.int32Value else { continue }
            if kill(pid, 0) != 0 && errno != EPERM { continue }     // 行程已不在
            let id = (o["sessionId"] as? String) ?? "pid-\(pid)"
            let busy = (o["status"] as? String) == "busy"
            let started = (o["startedAt"] as? NSNumber)?.doubleValue ?? 0
            out.append((started, .init(id: id, busy: busy)))
        }
        return out.sorted { $0.0 < $1.0 }.map(\.1)
    }

    // ── 焦點螢幕：滑鼠所在＋前景 App 視窗所在 ─────────────────────
    /// 每秒檢查一次。只有一台螢幕時直接略過，不做任何查詢。
    private func updateFocus() {
        guard surfaces.count > 1 else { return }
        var ids = Set<CGDirectDisplayID>()
        // 滑鼠最近 10 秒有動才算焦點：停在另一台不動時，那台原本會一直全速
        // （09-30 覆盤：兩台同時 30fps 佔 49% 時間，比一台降速多 1.6% CPU）
        let mouse = NSEvent.mouseLocation
        let now = CFAbsoluteTimeGetCurrent()
        if mouse != lastMouse { lastMouse = mouse; lastMouseMove = now }
        if now - lastMouseMove < 10,
           let sf = surfaces.first(where: { NSMouseInRect(mouse, $0.screen.frame, false) }) {
            ids.insert(sf.displayID)
        }
        if let key = frontmostWindowCenter() {
            // CGWindow 座標是 y 向下、原點在主螢幕左上角，換回 Cocoa 座標
            let mainH = NSScreen.screens.first?.frame.height ?? 0
            let p = NSPoint(x: key.x, y: mainH - key.y)
            if let sf = surfaces.first(where: { NSMouseInRect(p, $0.screen.frame, false) }) {
                ids.insert(sf.displayID)
            }
        }
        if ids.isEmpty { return }        // 判斷不出來就維持原狀
        var changed = false
        for sf in surfaces {
            let f = ids.contains(sf.displayID)
            if f != sf.focused { sf.focused = f; changed = true }
        }
        if changed {
            syncFPSIfNeeded(world.config)
            log(">>> 焦點螢幕 " + surfaces.map { "\($0.displayID):\($0.focused ? "焦點" : "降速")\($0.view.preferredFramesPerSecond)" }.joined(separator: " "))
        }
    }

    /// 前景 App 最上層一般視窗的中心點（CGWindow 座標）。
    private func frontmostWindowCenter() -> CGPoint? {
        guard let pid = NSWorkspace.shared.frontmostApplication?.processIdentifier,
              let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements],
                                                    kCGNullWindowID) as? [[String: Any]] else { return nil }
        for w in list {
            guard (w[kCGWindowOwnerPID as String] as? Int32) == pid,
                  (w[kCGWindowLayer as String] as? Int) == 0,
                  let b = w[kCGWindowBounds as String] as? [String: Any],
                  let r = CGRect(dictionaryRepresentation: b as CFDictionary),
                  r.width > 80, r.height > 80 else { continue }
            return CGPoint(x: r.midX, y: r.midY)
        }
        return nil
    }

    /// 從選單列手動暫停／繼續。與遮擋暫停獨立，兩者任一成立就不畫。
    func toggleManualPause() {
        manuallyPaused.toggle()
        for sf in surfaces { updatePaused(sf) }
        log(manuallyPaused ? ">>> 使用者手動暫停" : ">>> 使用者手動繼續")
    }

    /// 單一出口：手動暫停、遮擋、休眠／鎖定，任一成立就停畫並把 drawable 還回去。
    /// 從停畫恢復時重設時鐘，免得粒子依「停了多久」一次補算而瞬移。
    func updatePaused(_ sf: Surface) {
        let stop = manuallyPaused || sf.occluded || !suspendReasons.isEmpty
        if stop == sf.view.isPaused { return }
        lastPauseChange = CFAbsoluteTimeGetCurrent()
        sf.view.isPaused = stop
        defer { syncFPSIfNeeded(world.config) }
        if stop { sf.view.releaseDrawables() }
        else if !surfaces.contains(where: { $0 !== sf && !$0.view.isPaused }) {
            world.resetClock()     // 其他螢幕都停著：模擬也停了，從現在接續
        }
    }

    // ── 螢幕休眠／鎖定／螢幕保護程式 ────────────────────────────
    private func installPowerObservers() {
        let ws = NSWorkspace.shared.notificationCenter
        let pairs: [(Notification.Name, String, Bool)] = [
            (NSWorkspace.screensDidSleepNotification, "screenSleep", true),
            (NSWorkspace.screensDidWakeNotification, "screenSleep", false),
            (NSWorkspace.willSleepNotification, "systemSleep", true),
            (NSWorkspace.didWakeNotification, "systemSleep", false),
            (NSWorkspace.sessionDidResignActiveNotification, "sessionInactive", true),
            (NSWorkspace.sessionDidBecomeActiveNotification, "sessionInactive", false),
        ]
        for (name, reason, on) in pairs {
            ws.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                self?.setSuspended(reason, on)
            }
        }
        // 鎖定與螢幕保護程式只有分散式通知
        let dn = DistributedNotificationCenter.default()
        let dpairs: [(String, String, Bool)] = [
            ("com.apple.screenIsLocked", "locked", true),
            ("com.apple.screenIsUnlocked", "locked", false),
            ("com.apple.screensaver.didstart", "screensaver", true),
            ("com.apple.screensaver.didstop", "screensaver", false),
        ]
        for (name, reason, on) in dpairs {
            dn.addObserver(forName: Notification.Name(name), object: nil, queue: .main) { [weak self] _ in
                self?.setSuspended(reason, on)
            }
        }
    }

    func setSuspended(_ reason: String, _ on: Bool) {
        let before = suspendReasons
        if on { suspendReasons.insert(reason) } else { suspendReasons.remove(reason) }
        // 系統醒來時螢幕休眠通知不一定成對，醒來一律清掉休眠類原因
        if !on && (reason == "systemSleep" || reason == "screenSleep") {
            suspendReasons.remove("systemSleep"); suspendReasons.remove("screenSleep")
        }
        guard before != suspendReasons else { return }
        for sf in surfaces { updatePaused(sf) }
        log(">>> \(on ? "SUSPEND" : "RESUME") \(reason)　目前暫停原因=\(suspendReasons.sorted())")
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
        let dir = Config.repoDir
            .appendingPathComponent("presets")
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
        updatePaused(sf)          // 停畫時順帶把 framebuffer 還回去，記憶體掉 42%
        log(">>> 螢幕 \(sf.displayID) \(isOccluded ? "OCCLUDED（停止繪製）" : "VISIBLE（恢復繪製）")")
    }

    /// stdout 是 launchd 開的 log 檔（O_APPEND），超過上限就截成 0，下一行從檔頭寫起。
    /// 每 10 分鐘檢查一次；stdout 不是一般檔案（終端機、管線）時什麼都不做。
    private func trimLogIfNeeded() {
        guard reportTick % 600 == 0 else { return }
        var st = stat()
        guard fstat(STDOUT_FILENO, &st) == 0, (st.st_mode & S_IFMT) == S_IFREG,
              UInt64(st.st_size) > LOG_MAX_BYTES else { return }
        ftruncate(STDOUT_FILENO, 0); lseek(STDOUT_FILENO, 0, SEEK_SET)
        log("log 超過 \(LOG_MAX_BYTES / 1024 / 1024)MB，已清空")
    }

    // ── 每秒：熱重載檢查 ＋ 統計 ─────────────────────────────────
    private func tick() {
        trimLogIfNeeded()
        reloadIfChanged()
        updateFocus()
        updateIdle()
        updateSessions()
        updateActivity()
        checkSyncRequest()
        report()
    }

    // ── 把目前畫面同步成系統桌布 ──────────────────────────────
    // wall42 是蓋在桌布上的視窗，沒有改系統桌布設定。好處是移除即還原，
    // 代價是系統設定顯示的跟眼睛看到的不一致、而且啟動前的空窗期會露出舊桌布。
    // 這裡抓一張當前畫面設成系統桌布，把那兩個落差補起來。

    /// 產物（桌布圖、slot 記錄）跟著設定檔走，測試指定別的 config 時才不會互相污染
    private var wall42Dir: URL {
        Config.path.deletingLastPathComponent()
    }
    /// 控制信號固定在這裡。MCP 與 CLI 永遠寫這個路徑，
    /// 不能跟著 WALL42_CONFIG 漂移，否則指定別的設定檔時就收不到指令了。
    private var signalDir: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".config/wall42", isDirectory: true)
    }
    private var backupDir: URL {
        Config.repoDir
            .appendingPathComponent("backup")
    }

    /// 由 `touch ~/.config/wall42/.sync-request` 觸發（CLI 與 MCP 都走這個）
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
        case "snapshot":
            // 測試用：每個螢幕各存一張目前畫面（不改系統桌布）。{"kind":"snapshot","dir":"/path"}
            let dir = (o["dir"] as? String) ?? NSTemporaryDirectory()
            let tag = (o["tag"] as? String) ?? "snap"
            for sf in surfaces {
                sf.renderer.onSnapshot = nil
                sf.renderer.snapshotAtFrame = 0
                sf.renderer.snapshotPath = (dir as NSString).appendingPathComponent("\(tag)_\(sf.displayID).png")
            }
            log(">>> snapshot -> \(dir)")
        case "debug-relayout":
            // 測試用：{"kind":"debug-relayout","mainOnly":true} 模擬只剩主螢幕，false 還原
            debugMainOnly = (o["mainOnly"] as? Bool) ?? false
            log(">>> debug-relayout mainOnly=\(debugMainOnly)")
            rebuildSurfaces(world.config)
        case "debug-suspend":
            // 測試用：直接走休眠／鎖定的同一條路徑。{"kind":"debug-suspend","reason":"locked","on":true}
            setSuspended((o["reason"] as? String) ?? "debug", (o["on"] as? Bool) ?? true)
        case "insight":
            let st = Float((o["strength"] as? Double) ?? 1.0)
            world.triggerInsight(st)
            log(">>> insight 觸發 strength=\(st)")
        default:
            log("⚠ 不認得的信號：\(kind)")
        }
    }

    func syncWallpaper() {
        backupOriginalWallpaperIfNeeded()
        // macOS 對「同一路徑」的桌布會吃快取不重繪，A/B 交替避開
        let slot = (try? String(contentsOf: wall42Dir.appendingPathComponent(".slot"),
                                encoding: .utf8)) == "a" ? "b" : "a"
        try? slot.write(to: wall42Dir.appendingPathComponent(".slot"),
                        atomically: true, encoding: .utf8)
        // 清掉另一格的舊圖（含舊版單螢幕檔名）
        let other = slot == "a" ? "b" : "a"
        if let files = try? FileManager.default.contentsOfDirectory(atPath: wall42Dir.path) {
            for f in files where f.hasPrefix("wallpaper_\(other)") && f.hasSuffix(".png") {
                try? FileManager.default.removeItem(at: wall42Dir.appendingPathComponent(f))
            }
        }
        // 每個螢幕截自己的畫面、設成自己的桌布
        for sf in surfaces {
            let out = wall42Dir.appendingPathComponent("wallpaper_\(slot)_\(sf.displayID).png")
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

    /// 只在第一次同步前記錄，之後不覆寫，免得把 wall42 自己產的圖記成「原本的」。
    private func backupOriginalWallpaperIfNeeded() {
        let f = backupDir.appendingPathComponent("original-wallpaper.txt")
        guard !FileManager.default.fileExists(atPath: f.path) else { return }
        guard let screen = NSScreen.main,
              let cur = NSWorkspace.shared.desktopImageURL(for: screen) else { return }
        if cur.path.contains("/.config/wall42/") { return }
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
        let a = world.config.motion.activity ?? .default
        let src = a.source ?? "system"
        var target: Float = 0

        // think 期間直接覆寫，並平滑過去，避免畫面瞬間跳滿
        if let o = activityOverride {
            if Date() < o.until {
                smoothedActivity = smoothedActivity * 0.55 + o.level * 0.45
                world.activity = smoothedActivity
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
        world.activity = smoothedActivity
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
        var delta = 0
        var perScreen: [String] = []
        for sf in surfaces {
            let d = sf.renderer.frameCount - sf.lastFrames
            sf.lastFrames = sf.renderer.frameCount
            delta = max(delta, d)
            perScreen.append(String(format: "%.0f", dt > 0 ? Double(d) / dt : 0))
        }
        let links = world.lastLinkCount
        let steps = world.stepCount - lastSteps
        lastSteps = world.stepCount
        let fps = dt > 0 ? Double(delta) / dt : 0

        let c = cpuSeconds()
        let cpuPct = (lastCPU > 0 && dt > 0) ? (c - lastCPU) / dt * 100.0 : 0
        lastCPU = c
        let mem = memoryMB()
        lastFps = fps
        lastCPUPercent = cpuPct

        let settling = now - lastPauseChange < 1.1
        reportTick += 1
        // 常駐時只在整分鐘寫統計；異常（仍在繪製、0 幀）不受限，當下就寫
        let periodic = VERBOSE || reportTick % 60 == 1
        if !suspendReasons.isEmpty {
            let verdict = delta == 0 ? "OK" : (settling ? "（剛切換）" : "⚠ 仍在繪製")
            guard periodic || delta > 0 else { return }
            log(String(format: "SUSPENDED(%@)  cpu=%.2f%%  frames=+%d %@  mem=%.1fMB",
                       suspendReasons.sorted().joined(separator: ","), cpuPct, delta, verdict, mem))
        } else if isOccluded {
            let verdict = delta == 0 ? "OK" : (settling ? "（剛切換）" : "⚠ 仍在繪製")
            guard periodic || delta > 0 else { return }
            log(String(format: "OCCLUDED  cpu=%.2f%%  frames=+%d %@  mem=%.1fMB",
                       cpuPct, delta, verdict, mem))
        } else {
            // 看門狗：開機登入時 WindowServer 還沒就緒就啟動，MTKView 的 display link
            // 會綁到失效的螢幕，之後永遠不呼叫 draw(in:)（實測 fps=0、steps=0 持續不停）。
            // 該畫而連續 5 秒一幀都沒有 -> 重建畫面；重建後仍不畫就不再重試。
            let shouldDraw = !manuallyPaused && surfaces.contains { !$0.view.isPaused }
            stalledSeconds = (shouldDraw && delta == 0 && !settling) ? stalledSeconds + 1 : 0
            if stalledSeconds >= 5 && stallRebuilds < 3 {
                stalledSeconds = 0
                stallRebuilds += 1
                log(">>> ⚠ 該畫卻連續 5 秒 0 幀（display link 沒觸發），重建畫面（第 \(stallRebuilds) 次）")
                rebuildSurfaces(world.config)
                return
            }
            if delta > 0 { stallRebuilds = 0 }
            guard periodic || stalledSeconds > 0 else { return }
            let vis = surfaces.filter { !$0.occluded }.count
            log(String(format: "visible   screens=%d/%d  fps=%.1f [%@]  steps=%d  cpu=%.2f%%  links=%d  act=%.2f  mem=%.1fMB  idle=%.0f",
                       vis, surfaces.count, fps, perScreen.joined(separator: "/"), steps,
                       cpuPct, links, world.activity, mem, idleSeconds))
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
