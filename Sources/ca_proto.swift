// 對照原型：用 CAEmitterLayer（Core Animation）畫漂浮粒子。
// 動畫由系統 render server 負責，app 程序不需要每幀醒來。
// 代價：做不到「粒子靠近就連線」。
// 這支用來量「不要連線的話能省多少」，與 MTKView 版本數字直接對比。
import Cocoa
import QuartzCore

let COUNT    = Int(ProcessInfo.processInfo.environment["CA_COUNT"] ?? "120") ?? 120
let DURATION = Int(ProcessInfo.processInfo.environment["CA_DURATION"] ?? "0") ?? 0

func ts() -> String {
    let f = DateFormatter(); f.dateFormat = "HH:mm:ss"
    return f.string(from: Date())
}
func log(_ s: String) { print("[\(ts())] \(s)"); fflush(stdout) }

func makeDot(_ size: Int = 32) -> CGImage {
    let cs = CGColorSpaceCreateDeviceRGB()
    let ctx = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8,
                        bytesPerRow: 0, space: cs,
                        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    let colors = [CGColor(red: 0.62, green: 0.78, blue: 1.0, alpha: 1.0),
                  CGColor(red: 0.62, green: 0.78, blue: 1.0, alpha: 0.0)] as CFArray
    let grad = CGGradient(colorsSpace: cs, colors: colors, locations: [0, 1])!
    let c = CGPoint(x: size / 2, y: size / 2)
    ctx.drawRadialGradient(grad, startCenter: c, startRadius: 0,
                           endCenter: c, endRadius: CGFloat(size) / 2, options: [])
    return ctx.makeImage()!
}

final class D: NSObject, NSApplicationDelegate {
    var window: NSWindow!
    private var lastCPU: Double = 0
    private var lastReport = CFAbsoluteTimeGetCurrent()

    func applicationDidFinishLaunching(_ n: Notification) {
        let screen = NSScreen.main!
        let frame = screen.frame

        let view = NSView(frame: CGRect(origin: .zero, size: frame.size))
        view.wantsLayer = true
        view.layer?.backgroundColor = CGColor(red: 0, green: 0, blue: 0, alpha: 1)

        let lifetime: Float = 30
        let emitter = CAEmitterLayer()
        emitter.frame = view.bounds
        emitter.emitterShape = .rectangle
        emitter.emitterSize = frame.size
        emitter.emitterPosition = CGPoint(x: frame.width / 2, y: frame.height / 2)
        emitter.renderMode = .additive

        let cell = CAEmitterCell()
        cell.contents = makeDot()
        // 穩態粒子數 = birthRate × lifetime
        cell.birthRate = Float(COUNT) / lifetime
        cell.lifetime = lifetime
        cell.velocity = 10
        cell.velocityRange = 8
        cell.emissionRange = .pi * 2
        cell.scale = 0.12
        cell.scaleRange = 0.06
        cell.alphaSpeed = 0
        cell.color = CGColor(red: 1, green: 1, blue: 1, alpha: 0.7)
        emitter.emitterCells = [cell]
        // 預跑一個 lifetime，開場就有滿場粒子而不是慢慢長出來
        emitter.beginTime = CACurrentMediaTime() - Double(lifetime)

        view.layer?.addSublayer(emitter)

        window = NSWindow(contentRect: frame, styleMask: [.borderless],
                          backing: .buffered, defer: false)
        window.contentView = view
        window.isOpaque = true
        window.backgroundColor = .black
        window.ignoresMouseEvents = true
        window.hasShadow = false
        window.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopWindow)))
        window.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle, .fullScreenNone]
        window.orderFront(nil)

        Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in self?.report() }
        if DURATION > 0 {
            Timer.scheduledTimer(withTimeInterval: Double(DURATION), repeats: false) { _ in
                NSApp.terminate(nil)
            }
        }
        log("CAEmitterLayer 啟動 穩態粒子數≈\(COUNT) (birthRate=\(cell.birthRate)/s lifetime=\(lifetime)s)")
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
        let c = cpuSeconds()
        let pct = (lastCPU > 0 && dt > 0) ? (c - lastCPU) / dt * 100 : 0
        lastCPU = c
        log(String(format: "visible   cpu=%.2f%%  mem=%.1fMB", pct, memoryMB()))
    }
}

let app = NSApplication.shared
app.setActivationPolicy(.accessory)
let d = D()
app.delegate = d
app.run()
