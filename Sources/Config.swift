import Foundation
import simd

// 設定分成兩組、彼此獨立：換背景不影響動態，換動態不影響背景。
// 檔案：~/.config/wall91/config.json　存檔後自動熱重載，不需重啟。

struct BackgroundConfig: Codable {
    var mode: String            // "solid" | "gradient"
    var solidColor: String
    var centerColor: String     // gradient 用
    var edgeColor: String
    var radius: Float           // 漸層擴散範圍，越大越攤平

    static let `default` = BackgroundConfig(
        mode: "gradient",
        solidColor: "#000000",
        centerColor: "#0F041D",
        edgeColor: "#000000",
        radius: 1.02
    )
}

struct LinkConfig: Codable {
    var enabled: Bool
    var distance: Float         // 超過這個距離就不連線
    var opacity: Float
    var boost: Float            // 線的額外亮度倍率
    // 只讓「節點」那些較大的粒子連線 —— 星空用，做出星座圖的感覺。
    // 順帶讓 O(n^2) 只在少數節點之間算，比全部連線便宜兩個數量級。
    var onlyNodes: Bool?
    /// "proximity"＝距離內就連（靜態的網，像結構圖）
    /// "traffic"  ＝一次一次的傳輸：連線有壽命，fade in→脈衝跑過→fade out，
    ///              不斷換對象，看起來才像在分工運算而不是固定接線
    var mode: String?
    var targetCount: Int?       // traffic：同時進行中的傳輸數
    var lifeMin: Float?         // traffic：一次傳輸的秒數下限
    var lifeMax: Float?
    var color: String?          // 線的顏色。不設就用兩極色的中間色。
                                // 兩端不同色會讓線看起來在跳動，所以預設單色。

    static let `default` = LinkConfig(enabled: true, distance: 168, opacity: 0.40,
                                      boost: 1.35, onlyNodes: false,
                                      mode: "proximity", targetCount: 60,
                                      lifeMin: 1.1, lifeMax: 2.6, color: nil)
}

struct BokehConfig: Codable {
    var ratio: Float            // 前景散景粒子佔比
    var sizeMin: Float
    var sizeMax: Float
    var speed: Float
    var dimming: Float          // 散景壓暗多少，做出景深

    static let `default` = BokehConfig(ratio: 0.15, sizeMin: 34, sizeMax: 72,
                                       speed: 18, dimming: 0.22)
}

/// 沿連線流動的脈衝 —— 「有東西在算」的視覺來源
struct PulseConfig: Codable {
    var speed: Float        // 每秒跑完幾條線
    var strength: Float     // 亮度
    var width: Float        // 高斯寬度，越小越像一個點

    static let `default` = PulseConfig(speed: 0.35, strength: 0.9, width: 0.004)
}

/// 活動度來源：桌布要跟著誰忙
struct ActivityConfig: Codable {
    var source: String?     // "system"（讀系統 CPU）｜"manual"（由 MCP 指定）｜"off"
    var manualLevel: Float? // source = manual 時使用
    var smoothing: Float?   // 0..1，越大變化越慢，避免畫面抽動
    var minLoad: Float?     // 對應 activity = 0 的系統負載
    var maxLoad: Float?     // 對應 activity = 1 的系統負載

    static let `default` = ActivityConfig(source: "system", manualLevel: 0,
                                          smoothing: 0.85, minLoad: 0.08, maxLoad: 0.75)
}

struct MotionConfig: Codable {
    var effect: String          // 目前只有 "floating"
    /// 一台「主螢幕大小的面積」裡的粒子數（密度），多螢幕時依世界面積自動放大
    var particleCount: Int
    var fps: Int                // 60Hz 螢幕實際只有 60/30/20/15 可用
    var colorA: String          // 兩極色，粒子色相在兩者間分布
    var colorB: String
    var speed: Float
    var sizeMin: Float
    var sizeMax: Float
    var nodeRatio: Float        // 少數較大的「節點」
    var nodeSizeMin: Float
    var nodeSizeMax: Float
    var brightness: Float
    var breathSpeed: Float      // 呼吸明滅的快慢
    /// 0 = 沒有發光感（實心柔邊點）　1 = 霓虹（白色過曝亮核＋彩色光暈）
    var glow: Float?
    /// "additive" 疊加發光｜"normal" 一般透明度混合，重疊不會過曝
    var blend: String?
    // 以下為後加欄位，必須是 Optional：舊設定檔沒有這些 key，
    // 非 Optional 會讓 Codable 整份解碼失敗、使用者的設定被丟掉。
    var twinkleAmount: Float?   // 閃爍幅度，0 = 完全不閃、1 = 亮度在 0..1 之間跑
    var twinkleVariance: Float? // 每顆閃爍快慢的差異幅度，0 = 全部同步
    var sizeBias: Float?        // 尺寸分布偏斜，1 = 均勻，越大越多小顆
    var link: LinkConfig
    var bokeh: BokehConfig
    var pulse: PulseConfig?         // Optional：舊設定檔沒有這個 key
    var activity: ActivityConfig?

    static let `default` = MotionConfig(
        effect: "floating",
        particleCount: 140,
        fps: 30,
        colorA: "#1ADBF5",      // cyan
        colorB: "#FC3D99",      // hot pink
        speed: 11,
        sizeMin: 7, sizeMax: 13,
        nodeRatio: 0.20, nodeSizeMin: 17, nodeSizeMax: 27,
        brightness: 1.0,
        breathSpeed: 0.7,
        glow: 1.0,
        blend: "additive",
        twinkleAmount: 0.45,
        twinkleVariance: 0.6,
        sizeBias: 2.2,
        link: .default,
        bokeh: .default,
        pulse: .default,
        activity: .default
    )
}

/// 圖形入口。原本整個 app 只有終端機一個入口，對桌面視覺工具來說不合理。
struct UIConfig: Codable {
    var menuBar: Bool?      // 選單列常駐圖示

    static let `default` = UIConfig(menuBar: true)
}

struct Config: Codable {
    var background: BackgroundConfig
    var motion: MotionConfig
    var ui: UIConfig?       // Optional：舊設定檔沒有這個 key

    static let `default` = Config(background: .default, motion: .default, ui: .default)

    static var path: URL {
        // WALL91_CONFIG 讓截圖／測試用獨立設定檔，不干擾常駐中的實例
        if let p = ProcessInfo.processInfo.environment["WALL91_CONFIG"], !p.isEmpty {
            return URL(fileURLWithPath: (p as NSString).expandingTildeInPath)
        }
        let dir = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".config/wall91", isDirectory: true)
        return dir.appendingPathComponent("config.json")
    }

    /// 讀設定；檔案不存在就寫一份預設值出來，讓使用者有東西可改。
    static func load() -> (Config, String?) {
        let url = path
        guard FileManager.default.fileExists(atPath: url.path) else {
            let c = Config.default
            try? FileManager.default.createDirectory(
                at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            if let d = try? JSONEncoder.pretty.encode(c) { try? d.write(to: url) }
            return (c, nil)
        }
        do {
            let d = try Data(contentsOf: url)
            return (try JSONDecoder().decode(Config.self, from: d), nil)
        } catch {
            // 設定寫壞時沿用預設值繼續跑，不要讓桌布整個掛掉
            return (Config.default, "設定檔解析失敗，暫用預設值：\(error.localizedDescription)")
        }
    }

    static func modifiedAt() -> Date? {
        (try? FileManager.default.attributesOfItem(atPath: path.path)[.modificationDate]) as? Date
    }
}

extension JSONEncoder {
    static var pretty: JSONEncoder {
        let e = JSONEncoder()
        e.outputFormatting = [.prettyPrinted, .sortedKeys]
        return e
    }
}

/// "#RRGGBB" -> 線性化前的 sRGB 分量。解析失敗回傳黑色而不是崩潰。
func parseHex(_ s: String) -> SIMD4<Float> {
    var t = s.trimmingCharacters(in: .whitespaces)
    if t.hasPrefix("#") { t.removeFirst() }
    guard t.count == 6, let v = UInt32(t, radix: 16) else { return SIMD4(0, 0, 0, 1) }
    return SIMD4(Float((v >> 16) & 0xFF) / 255.0,
                 Float((v >> 8) & 0xFF) / 255.0,
                 Float(v & 0xFF) / 255.0,
                 1.0)
}
