import Cocoa

/// 選單列常駐圖示。整個 app 原本只有終端機一個入口，
/// 對一個桌面視覺工具來說很不合理 —— 這層補上圖形入口。
/// 做在同一個程序裡，不另外開 process；不要的話設定 ui.menuBar = false。
final class MenuBarController: NSObject, NSMenuDelegate {

    private var item: NSStatusItem?
    private let menu = NSMenu()
    private weak var app: AppDelegate?

    // 開啟選單時才更新內容，平常不做任何事
    private let statusItem = NSMenuItem(title: "", action: nil, keyEquivalent: "")
    private let activityItem = NSMenuItem(title: "", action: nil, keyEquivalent: "")
    private let pauseItem = NSMenuItem(title: "暫停繪製", action: nil, keyEquivalent: "")
    private let presetMenu = NSMenu()

    init(app: AppDelegate) {
        self.app = app
        super.init()
        build()
    }

    private func build() {
        let si = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        si.button?.image = NSImage(systemSymbolName: "circle.hexagongrid",
                                   accessibilityDescription: "wall42")
        si.button?.image?.isTemplate = true
        si.menu = menu
        item = si

        menu.delegate = self
        menu.autoenablesItems = false

        for it in [statusItem, activityItem] {
            it.isEnabled = false
            menu.addItem(it)
        }
        menu.addItem(.separator())

        let presetRoot = NSMenuItem(title: "風格", action: nil, keyEquivalent: "")
        presetRoot.submenu = presetMenu
        menu.addItem(presetRoot)
        menu.addItem(.separator())

        pauseItem.target = self
        pauseItem.action = #selector(togglePause)
        menu.addItem(pauseItem)

        let tune = NSMenuItem(title: "調整參數…", action: #selector(openPanel),
                              keyEquivalent: ",")
        tune.target = self
        menu.addItem(tune)

        let cfg = NSMenuItem(title: "開啟設定檔（JSON）", action: #selector(openConfig),
                             keyEquivalent: "")
        cfg.target = self
        menu.addItem(cfg)

        let doc = NSMenuItem(title: "參數說明（README）", action: #selector(openReadme),
                             keyEquivalent: "")
        doc.target = self
        menu.addItem(doc)

        menu.addItem(.separator())
        let quit = NSMenuItem(title: "結束 wall42", action: #selector(quitApp),
                              keyEquivalent: "q")
        quit.target = self
        menu.addItem(quit)
    }

    // MARK: 開啟時才更新，避免常駐輪詢

    func menuWillOpen(_ menu: NSMenu) {
        guard let app = app else { return }
        let r = app.renderer!
        let m = r.config.motion

        if app.isOccluded {
            statusItem.title = "被視窗遮住，已停止繪製"
        } else if app.manuallyPaused {
            statusItem.title = "已手動暫停"
        } else {
            statusItem.title = String(format: "%.0f fps ・ %d 條傳輸 ・ CPU %.1f%%",
                                      app.lastFps, r.lastLinkCount, app.lastCPUPercent)
        }

        let src = (m.activity?.source ?? "system")
        let label = src == "system" ? "跟隨系統負載" : (src == "manual" ? "手動" : "關閉")
        activityItem.title = String(format: "忙碌程度 %@ ・ %@",
                                    bar(r.activity), label)

        pauseItem.title = app.manuallyPaused ? "繼續繪製" : "暫停繪製"
        rebuildPresetMenu()
    }

    /// 用方塊畫一條長度條，比顯示 0.37 這種數字直觀
    private func bar(_ v: Float) -> String {
        let n = Int((max(0, min(1, v)) * 10).rounded())
        return String(repeating: "▮", count: n) + String(repeating: "▯", count: 10 - n)
    }

    private func rebuildPresetMenu() {
        presetMenu.removeAllItems()
        let dir = Config.repoDir
            .appendingPathComponent("presets")
        let names = ((try? FileManager.default.contentsOfDirectory(atPath: dir.path)) ?? [])
            .filter { $0.hasSuffix(".json") }
            .map { String($0.dropLast(5)) }
            .sorted()
        let current = app?.currentPresetName
        for n in names {
            let it = NSMenuItem(title: n, action: #selector(applyPreset(_:)), keyEquivalent: "")
            it.target = self
            it.representedObject = dir.appendingPathComponent("\(n).json")
            it.state = (n == current) ? .on : .off
            presetMenu.addItem(it)
        }
        if names.isEmpty {
            let it = NSMenuItem(title: "（找不到 presets 目錄）", action: nil, keyEquivalent: "")
            it.isEnabled = false
            presetMenu.addItem(it)
        }
    }

    // MARK: 動作

    @objc private func applyPreset(_ sender: NSMenuItem) {
        guard let url = sender.representedObject as? URL,
              let data = try? Data(contentsOf: url) else { return }
        // 沿用現有的活動度設定，不要被風格檔洗掉
        var obj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
        if let act = currentActivityDict(),
           var motion = obj?["motion"] as? [String: Any] {
            motion["activity"] = act
            obj?["motion"] = motion
        }
        guard let obj,
              let out = try? JSONSerialization.data(withJSONObject: obj,
                                                    options: [.prettyPrinted, .sortedKeys])
        else { return }
        try? out.write(to: Config.path)     // 寫檔即可，熱重載會在 1 秒內接手
    }

    private func currentActivityDict() -> [String: Any]? {
        guard let d = try? Data(contentsOf: Config.path),
              let o = try? JSONSerialization.jsonObject(with: d) as? [String: Any],
              let m = o["motion"] as? [String: Any] else { return nil }
        return m["activity"] as? [String: Any]
    }

    @objc private func openPanel() { app?.showControlPanel() }

    @objc private func togglePause() { app?.toggleManualPause() }

    @objc private func openConfig() {
        NSWorkspace.shared.open(Config.path)
    }

    @objc private func openReadme() {
        let url = Config.repoDir
            .appendingPathComponent("README.zh-TW.md")
        NSWorkspace.shared.open(url)
    }

    @objc private func quitApp() { NSApp.terminate(nil) }
}
