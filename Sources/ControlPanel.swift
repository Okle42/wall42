import Cocoa

// 調參數面板。原本只能編輯 JSON，要記參數名、不知道範圍、看不到即時效果。
// 這裡用資料驅動生成控制項：加一個參數只要多一列宣告，不用寫 UI 程式碼。

/// 一列控制項
enum Row {
    case slider(String, ClosedRange<Double>, Int,
                get: (Config) -> Double, set: (inout Config, Double) -> Void)
    case color(String, get: (Config) -> String, set: (inout Config, String) -> Void)
    case toggle(String, get: (Config) -> Bool, set: (inout Config, Bool) -> Void)
    case choice(String, [String], [String],
                get: (Config) -> String, set: (inout Config, String) -> Void)
}

struct Group {
    let title: String
    let rows: [Row]
}

/// 面板內容。只放「調校時會反覆拖動」的參數；
/// fps、sizeBias、twinkleVariance、連線壽命這些設一次就定了的留在 JSON。
func panelGroups() -> [Group] {
    [
    Group(title: "整體", rows: [
        .slider("亮度", 0...2, 2, get: { Double($0.motion.brightness) },
                set: { $0.motion.brightness = Float($1) }),
        .slider("發光感", 0...1, 2, get: { Double($0.motion.glow ?? 1) },
                set: { $0.motion.glow = Float($1) }),
        .choice("混合", ["additive", "normal"], ["疊加發光", "一般"],
                get: { $0.motion.blend ?? "additive" }, set: { $0.motion.blend = $1 }),
        .choice("效果", ["floating", "snow", "sand"], ["漂浮", "下雪", "流沙"],
                get: { $0.motion.effect }, set: { $0.motion.effect = $1 }),
        .toggle("Session 光點", get: { $0.motion.sessions?.enabled ?? false },
                set: { c, v in
                    var s = c.motion.sessions ?? SessionsConfig(enabled: nil, source: nil, size: nil)
                    s.enabled = v
                    c.motion.sessions = s
                }),
    ]),
    Group(title: "粒子", rows: [
        .slider("數量", 20...600, 0, get: { Double($0.motion.particleCount) },
                set: { $0.motion.particleCount = Int($1) }),
        .slider("最小", 1...30, 1, get: { Double($0.motion.sizeMin) },
                set: { $0.motion.sizeMin = Float($1) }),
        .slider("最大", 1...40, 1, get: { Double($0.motion.sizeMax) },
                set: { $0.motion.sizeMax = Float($1) }),
        .slider("節點比例", 0...0.6, 2, get: { Double($0.motion.nodeRatio) },
                set: { $0.motion.nodeRatio = Float($1) }),
        .slider("節點大小", 2...60, 1, get: { Double($0.motion.nodeSizeMax) },
                set: { c, v in
                    c.motion.nodeSizeMax = Float(v)
                    c.motion.nodeSizeMin = Float(v) * 0.62   // 保持上下限比例
                }),
        .slider("漂浮速度", 0...40, 1, get: { Double($0.motion.speed) },
                set: { $0.motion.speed = Float($1) }),
        .slider("閃爍幅度", 0...1, 2, get: { Double($0.motion.twinkleAmount ?? 0.45) },
                set: { $0.motion.twinkleAmount = Float($1) }),
        .slider("閃爍速度", 0...2.5, 2, get: { Double($0.motion.breathSpeed) },
                set: { $0.motion.breathSpeed = Float($1) }),
        .slider("閃爍差異", 0...1, 2, get: { Double($0.motion.twinkleVariance ?? 0.6) },
                set: { $0.motion.twinkleVariance = Float($1) }),
    ]),
    Group(title: "連線", rows: [
        .toggle("啟用", get: { $0.motion.link.enabled },
                set: { $0.motion.link.enabled = $1 }),
        .choice("模式", ["proximity", "traffic", "attention"], ["距離（固定網）", "流量（傳輸感）", "注意力（思考）"],
                get: { $0.motion.link.mode ?? "proximity" },
                set: { $0.motion.link.mode = $1 }),
        .toggle("只連節點", get: { $0.motion.link.onlyNodes ?? false },
                set: { $0.motion.link.onlyNodes = $1 }),
        .slider("亮度", 0...1, 2, get: { Double($0.motion.link.opacity) },
                set: { $0.motion.link.opacity = Float($1) }),
        .slider("加亮", 0.2...3, 2, get: { Double($0.motion.link.boost) },
                set: { $0.motion.link.boost = Float($1) }),
        .slider("距離", 40...500, 0, get: { Double($0.motion.link.distance) },
                set: { $0.motion.link.distance = Float($1) }),
        .slider("同時傳輸", 5...250, 0, get: { Double($0.motion.link.targetCount ?? 60) },
                set: { $0.motion.link.targetCount = Int($1) }),
    ]),
    Group(title: "脈衝", rows: [
        .slider("強度", 0...3, 2, get: { Double($0.motion.pulse?.strength ?? 0.9) },
                set: { c, v in c.motion.pulse = (c.motion.pulse ?? .default); c.motion.pulse?.strength = Float(v) }),
        .slider("速度", 0...2, 2, get: { Double($0.motion.pulse?.speed ?? 0.35) },
                set: { c, v in c.motion.pulse = (c.motion.pulse ?? .default); c.motion.pulse?.speed = Float(v) }),
        .slider("寬度", 0.0005...0.02, 4, get: { Double($0.motion.pulse?.width ?? 0.004) },
                set: { c, v in c.motion.pulse = (c.motion.pulse ?? .default); c.motion.pulse?.width = Float(v) }),
    ]),
    Group(title: "散景", rows: [
        .slider("比例", 0...0.4, 3, get: { Double($0.motion.bokeh.ratio) },
                set: { $0.motion.bokeh.ratio = Float($1) }),
        .slider("大小", 5...120, 0, get: { Double($0.motion.bokeh.sizeMax) },
                set: { c, v in
                    c.motion.bokeh.sizeMax = Float(v)
                    c.motion.bokeh.sizeMin = Float(v) * 0.48
                }),
        .slider("暗度", 0...1, 2, get: { Double($0.motion.bokeh.dimming) },
                set: { $0.motion.bokeh.dimming = Float($1) }),
    ]),
    Group(title: "顏色", rows: [
        .color("粒子 A", get: { $0.motion.colorA }, set: { $0.motion.colorA = $1 }),
        .color("粒子 B", get: { $0.motion.colorB }, set: { $0.motion.colorB = $1 }),
        .color("連線", get: { $0.motion.link.color ?? "#8899AA" },
               set: { $0.motion.link.color = $1 }),
        .color("背景中心", get: { $0.background.centerColor },
               set: { $0.background.centerColor = $1 }),
        .slider("漸層範圍", 0.2...2, 2, get: { Double($0.background.radius) },
                set: { $0.background.radius = Float($1) }),
    ]),
    Group(title: "忙碌程度", rows: [
        .choice("來源", ["system", "manual", "off"], ["跟隨系統負載", "手動", "關閉"],
                get: { $0.motion.activity?.source ?? "system" },
                set: { c, v in c.motion.activity = (c.motion.activity ?? .default); c.motion.activity?.source = v }),
        .slider("手動值", 0...1, 2, get: { Double($0.motion.activity?.manualLevel ?? 0) },
                set: { c, v in c.motion.activity = (c.motion.activity ?? .default); c.motion.activity?.manualLevel = Float(v) }),
    ]),
    ]
}

/// NSStackView 的座標系不是翻轉的，直接當 documentView 會讓捲動停在底部。
/// 包一層 flipped 容器，捲動才會從頂端開始。
private final class FlippedView: NSView {
    override var isFlipped: Bool { true }
}

final class ControlPanel: NSObject, NSWindowDelegate {

    private var panel: NSPanel?
    private weak var app: AppDelegate?
    private var rows: [Row] = []
    private var controls: [NSControl] = []
    private var valueLabels: [NSTextField?] = []
    private var saveWork: DispatchWorkItem?
    /// 用程式設定控制項的值時，AppKit 有些控制項（NSColorWell 尤其）會反過來
    /// 觸發 action。沒有這道閘，開啟面板本身就會把設定寫髒。
    private var updating = false

    init(app: AppDelegate) {
        self.app = app
        super.init()
    }

    func show() {
        if let p = panel {
            refresh()
            p.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }
        build()
        panel?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    private func build() {
        updating = true            // 建立控制項的過程也會觸發 action，整段都要擋
        defer { updating = false }
        let groups = panelGroups()
        rows = []; controls = []; valueLabels = []

        let stack = NSStackView()
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 6
        stack.edgeInsets = NSEdgeInsets(top: 14, left: 16, bottom: 14, right: 16)
        stack.translatesAutoresizingMaskIntoConstraints = false

        for g in groups {
            let title = NSTextField(labelWithString: g.title)
            title.font = .systemFont(ofSize: 11, weight: .semibold)
            title.textColor = .secondaryLabelColor
            let box = NSStackView(views: [title])
            box.orientation = .horizontal
            box.edgeInsets = NSEdgeInsets(top: 10, left: 0, bottom: 2, right: 0)
            stack.addArrangedSubview(box)

            for row in g.rows {
                stack.addArrangedSubview(makeRow(row))
                rows.append(row)
            }
        }

        let reset = NSButton(title: "重設為目前風格", target: self, action: #selector(resetToPreset))
        reset.bezelStyle = .rounded
        let footer = NSStackView(views: [reset])
        footer.edgeInsets = NSEdgeInsets(top: 12, left: 0, bottom: 0, right: 0)
        stack.addArrangedSubview(footer)

        let doc = FlippedView()
        doc.translatesAutoresizingMaskIntoConstraints = false
        doc.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.topAnchor.constraint(equalTo: doc.topAnchor),
            stack.leadingAnchor.constraint(equalTo: doc.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: doc.trailingAnchor),
            stack.bottomAnchor.constraint(equalTo: doc.bottomAnchor),
            stack.widthAnchor.constraint(equalToConstant: 360),
        ])

        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.drawsBackground = false
        scroll.documentView = doc

        let p = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 380, height: 620),
                        styleMask: [.titled, .closable, .utilityWindow, .resizable],
                        backing: .buffered, defer: false)
        p.title = "wall91 參數"
        p.isFloatingPanel = true
        p.hidesOnDeactivate = false
        p.contentView = scroll
        p.center()
        p.delegate = self
        panel = p
        refresh()
        // 確保從最上面開始看
        DispatchQueue.main.async { scroll.contentView.scroll(to: .zero); scroll.reflectScrolledClipView(scroll.contentView) }
    }

    private func makeRow(_ row: Row) -> NSView {
        let label = NSTextField(labelWithString: rowLabel(row))
        label.font = .systemFont(ofSize: 12)
        label.alignment = .right
        label.widthAnchor.constraint(equalToConstant: 76).isActive = true

        var control: NSControl
        var valueLabel: NSTextField?

        switch row {
        case let .slider(_, range, _, _, _):
            let s = NSSlider(value: range.lowerBound, minValue: range.lowerBound,
                             maxValue: range.upperBound,
                             target: self, action: #selector(changed(_:)))
            s.isContinuous = true
            s.widthAnchor.constraint(equalToConstant: 190).isActive = true
            control = s
            let v = NSTextField(labelWithString: "")
            v.font = .monospacedDigitSystemFont(ofSize: 11, weight: .regular)
            v.textColor = .secondaryLabelColor
            v.alignment = .right
            v.widthAnchor.constraint(equalToConstant: 56).isActive = true
            valueLabel = v
        case .color:
            let w = NSColorWell()
            w.target = self; w.action = #selector(changed(_:))
            w.widthAnchor.constraint(equalToConstant: 56).isActive = true
            w.heightAnchor.constraint(equalToConstant: 22).isActive = true
            control = w
        case .toggle:
            let b = NSButton(checkboxWithTitle: "", target: self, action: #selector(changed(_:)))
            control = b
        case let .choice(_, _, titles, _, _):
            let pop = NSPopUpButton()
            pop.addItems(withTitles: titles)
            pop.target = self; pop.action = #selector(changed(_:))
            pop.widthAnchor.constraint(equalToConstant: 190).isActive = true
            control = pop
        }

        control.tag = controls.count
        controls.append(control)
        valueLabels.append(valueLabel)

        let h = NSStackView(views: [label, control] + (valueLabel.map { [$0] } ?? []))
        h.orientation = .horizontal
        h.spacing = 8
        h.alignment = .centerY
        return h
    }

    private func rowLabel(_ r: Row) -> String {
        switch r {
        case let .slider(l, _, _, _, _): return l
        case let .color(l, _, _): return l
        case let .toggle(l, _, _): return l
        case let .choice(l, _, _, _, _): return l
        }
    }

    /// 從目前設定把所有控制項的值讀回來
    func refresh() {
        let wasUpdating = updating
        updating = true
        defer { updating = wasUpdating }    // build 呼叫時不要提早解除閘門
        refreshLocked()
    }

    private func refreshLocked() {
        guard let cfg = app?.renderer?.config else { return }
        for (i, row) in rows.enumerated() {
            switch row {
            case let .slider(_, _, dp, get, _):
                let v = get(cfg)
                (controls[i] as? NSSlider)?.doubleValue = v
                valueLabels[i]?.stringValue = String(format: "%.\(dp)f", v)
            case let .color(_, get, _):
                (controls[i] as? NSColorWell)?.color = NSColor(hex: get(cfg))
            case let .toggle(_, get, _):
                (controls[i] as? NSButton)?.state = get(cfg) ? .on : .off
            case let .choice(label, values, _, get, _):
                let v = get(cfg)
                let pop = controls[i] as? NSPopUpButton
                if let idx = values.firstIndex(of: v) {
                    pop?.selectItem(at: idx)
                }
                if ProcessInfo.processInfo.environment["WALL91_DEBUG_PANEL"] == "1" {
                    let msg = "refresh choice \(label): value=\(v) "
                        + "idx=\(values.firstIndex(of: v) ?? -1) "
                        + "selected=\(pop?.indexOfSelectedItem ?? -99) isPop=\(pop != nil)\n"
                    FileHandle.standardError.write(msg.data(using: .utf8)!)
                }
            }
        }
    }

    @objc private func changed(_ sender: NSControl) {
        guard !updating else { return }          // 這是程式在設值，不是使用者在操作
        guard let app = app, let r = app.renderer else { return }
        let i = sender.tag
        guard i < rows.count else { return }
        var cfg = r.config

        switch rows[i] {
        case let .slider(_, _, dp, _, set):
            let v = (sender as! NSSlider).doubleValue
            set(&cfg, v)
            valueLabels[i]?.stringValue = String(format: "%.\(dp)f", v)
        case let .color(_, _, set):
            set(&cfg, (sender as! NSColorWell).color.hexString)
        case let .toggle(_, _, set):
            set(&cfg, (sender as! NSButton).state == .on)
        case let .choice(_, values, _, _, set):
            let idx = (sender as! NSPopUpButton).indexOfSelectedItem
            if idx >= 0 && idx < values.count { set(&cfg, values[idx]) }
        }

        app.applyToAll(cfg)          // 即時生效（所有螢幕）
        scheduleSave(cfg)            // 停手後才寫檔，拖動時不狂寫
    }

    private func scheduleSave(_ cfg: Config) {
        saveWork?.cancel()
        let w = DispatchWorkItem { [weak self] in self?.app?.saveConfig(cfg) }
        saveWork = w
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.6, execute: w)
    }

    @objc private func resetToPreset() {
        guard let name = app?.currentPresetName else {
            NSSound.beep(); return
        }
        let url = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("github-repos/wall91/presets/\(name).json")
        guard let d = try? Data(contentsOf: url),
              let cfg = try? JSONDecoder().decode(Config.self, from: d) else { return }
        app?.applyToAll(cfg)
        app?.saveConfig(cfg)
        refresh()
    }

    func windowWillClose(_ n: Notification) {
        saveWork?.perform()          // 關閉前把未寫入的改動存下來
        saveWork = nil
    }
}

extension NSColor {
    convenience init(hex: String) {
        var t = hex.trimmingCharacters(in: .whitespaces)
        if t.hasPrefix("#") { t.removeFirst() }
        let v = UInt32(t, radix: 16) ?? 0
        self.init(srgbRed: CGFloat((v >> 16) & 0xFF) / 255,
                  green: CGFloat((v >> 8) & 0xFF) / 255,
                  blue: CGFloat(v & 0xFF) / 255, alpha: 1)
    }
    var hexString: String {
        guard let c = usingColorSpace(.sRGB) else { return "#000000" }
        return String(format: "#%02X%02X%02X",
                      Int((c.redComponent * 255).rounded()),
                      Int((c.greenComponent * 255).rounded()),
                      Int((c.blueComponent * 255).rounded()))
    }
}
