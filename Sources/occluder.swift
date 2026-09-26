// 測試工具：延遲 N 秒後開一個覆蓋全螢幕的不透明視窗，持續 M 秒後結束。
// 用來驗證 wall42 的桌布層視窗會不會收到 occluded 通知。
// 視窗 ignoresMouseEvents，所以操作會穿透到下面，不會卡住使用者。
import Cocoa

let delay    = Double(ProcessInfo.processInfo.environment["OCC_DELAY"] ?? "8") ?? 8
let duration = Double(ProcessInfo.processInfo.environment["OCC_DURATION"] ?? "15") ?? 15

func ts() -> String {
    let f = DateFormatter(); f.dateFormat = "HH:mm:ss"
    return f.string(from: Date())
}

final class D: NSObject, NSApplicationDelegate {
    var w: NSWindow!
    func applicationDidFinishLaunching(_ n: Notification) {
        print("[\(ts())] occluder: \(delay)s 後遮擋，持續 \(duration)s"); fflush(stdout)
        Timer.scheduledTimer(withTimeInterval: delay, repeats: false) { _ in self.cover() }
    }
    func cover() {
        let screen = NSScreen.main!
        w = NSWindow(contentRect: screen.frame, styleMask: [.borderless],
                     backing: .buffered, defer: false)
        w.backgroundColor = NSColor(white: 0.12, alpha: 1.0)
        w.isOpaque = true
        w.level = .normal
        w.ignoresMouseEvents = true
        w.hasShadow = false
        w.collectionBehavior = [.stationary, .ignoresCycle]
        w.orderFront(nil)
        print("[\(ts())] occluder: 遮擋視窗已開啟 (全螢幕不透明)"); fflush(stdout)

        Timer.scheduledTimer(withTimeInterval: duration, repeats: false) { _ in
            print("[\(ts())] occluder: 移除遮擋"); fflush(stdout)
            self.w.orderOut(nil)
            Timer.scheduledTimer(withTimeInterval: 3.0, repeats: false) { _ in
                NSApp.terminate(nil)
            }
        }
    }
}

let app = NSApplication.shared
app.setActivationPolicy(.accessory)
let d = D()
app.delegate = d
app.run()
