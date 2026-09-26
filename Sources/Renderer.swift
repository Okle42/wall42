import Cocoa
import MetalKit
import simd
import ImageIO
import UniformTypeIdentifiers

// CPU 端粒子。欄位順序與大小必須跟 shader 裡的 Particle 一致（stride 32 bytes）。
struct Particle {
    var pos: SIMD2<Float>
    var vel: SIMD2<Float>
    var size: Float
    var phase: Float
    var colorMix: Float   // 0 = colorA，1 = colorB
    var depth: Float      // 0 = 遠景（銳利小點），1 = 前景（散景光斑）
    var twinkle: Float    // 個別閃爍速度倍率，讓星點不同步明滅
    var boost: Float      // 臨時放大／提亮（attention 模式的焦點爆亮）
}

struct LinkVertex {
    var pos: SIMD2<Float>
    var alpha: Float
    var colorMix: Float
    var t: Float        // 0 = 線的起點，1 = 終點，fragment 會拿到插值
    var seed: Float     // proximity：脈衝相位；traffic：-1 代表改用 age
    var age: Float      // traffic：0 剛建立、1 即將消失
}

// 與 shader 的 Uniforms 佈局必須逐欄位對齊（總長 96 bytes）。
// SIMD4 在兩邊都對齊 16，所以順序不能任意調換。
struct Uniforms {
    var viewport: SIMD2<Float>
    var time: Float
    var brightness: Float
    var colorA: SIMD4<Float>
    var colorB: SIMD4<Float>
    var bgCenter: SIMD4<Float>
    var bgEdge: SIMD4<Float>
    var bgRadius: Float
    var linkBoost: Float
    var breathSpeed: Float
    var twinkleAmount: Float
    var bokehDimming: Float
    var pulseSpeed: Float
    var pulseStrength: Float
    var pulseWidth: Float
    var activity: Float      // 0 = 閒置，1 = 全力運算
    var glow: Float          // 0 = 無發光感，1 = 霓虹
    var insight: Float       // 「想通」脈衝，由 MCP 觸發後隨時間衰減
    var linkColor: SIMD4<Float>
}

// Metal Toolchain 未安裝時無法離線編譯 .metal，改在執行期由 source 編譯。
let shaderSource = """
#include <metal_stdlib>
using namespace metal;

struct Uniforms {
    float2 viewport;
    float  time;
    float  brightness;
    float4 colorA;
    float4 colorB;
    float4 bgCenter;
    float4 bgEdge;
    float  bgRadius;
    float  linkBoost;
    float  breathSpeed;
    float  twinkleAmount;
    float  bokehDimming;
    float  pulseSpeed;
    float  pulseStrength;
    float  pulseWidth;
    float  activity;
    float  glow;
    float  insight;
    float4 linkColor;
};

// 背景：中心色向邊緣色的徑向衰減，兼作電影感打光與暗角
vertex float4 bg_vs(uint vid [[vertex_id]])
{
    // 覆蓋全螢幕的單一三角形，比 quad 少一個頂點也少一條接縫
    float2 p[3] = { float2(-1.0, -1.0), float2(3.0, -1.0), float2(-1.0, 3.0) };
    return float4(p[vid], 0.0, 1.0);
}

fragment float4 bg_fs(float4 pos [[position]],
                      constant Uniforms &u [[buffer(0)]])
{
    float2 uv = pos.xy / u.viewport;
    float2 c  = uv - 0.5;
    c.x *= u.viewport.x / u.viewport.y;      // 修正長寬比，免得漸層被拉成橢圓
    float r = length(c);
    float t = smoothstep(0.0, u.bgRadius, r);
    t = t * t;                               // 邊緣真的黑，但過渡拉長不結塊
    float3 col = mix(u.bgCenter.rgb, u.bgEdge.rgb, t);
    // 8-bit 暗部漸層會出現同心圓色帶，加半個色階的雜訊打散它
    float n = fract(sin(dot(pos.xy, float2(12.9898, 78.233))) * 43758.5453);
    col += (n - 0.5) / 255.0;
    return float4(col, 1.0);
}

struct Particle {
    float2 pos;
    float2 vel;
    float  size;
    float  phase;
    float  colorMix;
    float  depth;
    float  twinkle;
    float  boost;
};

struct PointOut {
    float4 position [[position]];
    float  psize    [[point_size]];
    float  alpha;
    float  colorMix;
    float  depth;
};

vertex PointOut particle_vs(uint vid [[vertex_id]],
                            constant Particle *ps [[buffer(0)]],
                            constant Uniforms &u  [[buffer(1)]])
{
    Particle p = ps[vid];
    PointOut o;
    float2 ndc = (p.pos / u.viewport) * 2.0 - 1.0;
    ndc.y = -ndc.y;
    o.position = float4(ndc, 0.0, 1.0);
    o.psize    = p.size * (1.0 + p.boost * 1.6);
    o.colorMix = p.colorMix;
    o.depth    = p.depth;
    // 閃爍：三個不可通約的頻率疊加。單一正弦是均勻起伏，看起來像機械呼吸；
    // 真實星光是大氣擾動造成的不規則抖動，峰值不等高、節奏不整齊。
    float t  = u.time * u.breathSpeed * p.twinkle;
    float ph = p.phase;
    float w = sin(t + ph) * 0.55
            + sin(t * 2.31 + ph * 1.7) * 0.28
            + sin(t * 4.67 + ph * 2.9) * 0.17;
    // 小星點閃得明顯、大節點穩定 —— 暗的點光源受擾動影響更大
    float sizeFactor = clamp(1.0 - (p.size - 3.0) * 0.045, 0.30, 1.0);
    float amp = u.twinkleAmount * sizeFactor;
    float breathe = 1.0 - amp * 0.5 + amp * 0.5 * w;
    o.alpha = mix(breathe, breathe * u.bokehDimming, p.depth) * u.brightness
            * (1.0 + p.boost * 1.2) * (1.0 + u.insight * 0.9);
    return o;
}

fragment float4 particle_fs(PointOut in [[stage_in]],
                            float2 pc [[point_coord]],
                            constant Uniforms &u [[buffer(0)]])
{
    float d = length(pc - float2(0.5));
    if (d > 0.5) discard_fragment();

    // 衰減曲線隨景深改變：
    //   遠景 exp 高 -> 收得快 -> 銳利亮點
    //   前景 exp 低 -> 收得慢 -> 柔邊光斑，假造失焦散景，成本與畫小點相同
    // glow=0 時邊緣收得更快，看起來是實心柔邊圓點而不是光暈
    float sharpness = mix(mix(4.2, 2.4, u.glow), 0.85, in.depth);
    float halo = pow(smoothstep(0.5, 0.0, d), sharpness);
    // 白色過曝亮核只在 glow 高時出現 —— 這是「霓虹感」的主要來源
    float core = pow(smoothstep(0.30, 0.0, d), 1.4) * (1.0 - in.depth * 0.88) * u.glow;

    float3 col = mix(u.colorA.rgb, u.colorB.rgb, in.colorMix);
    float3 rgb = col * halo * mix(1.0, 1.7, u.glow) + float3(1.0) * core * 1.15;
    float a = clamp(halo + core, 0.0, 1.0) * in.alpha;
    return float4(rgb * in.alpha, a);
}

struct LinkVertex {
    float2 pos;
    float  alpha;
    float  colorMix;
    float  t;
    float  seed;
    float  age;
};

struct LineOut {
    float4 position [[position]];
    float  alpha;
    float  colorMix;
    float  t;
    float  seed;     // proximity 模式：脈衝相位；traffic 模式：< 0 表示改用 age
    float  age;      // traffic 模式：0=剛建立 1=即將消失
};

vertex LineOut line_vs(uint vid [[vertex_id]],
                       constant LinkVertex *vs [[buffer(0)]],
                       constant Uniforms   &u  [[buffer(1)]])
{
    LinkVertex v = vs[vid];
    LineOut o;
    float2 ndc = (v.pos / u.viewport) * 2.0 - 1.0;
    ndc.y = -ndc.y;
    o.position = float4(ndc, 0.0, 1.0);
    o.alpha    = v.alpha * u.brightness * (1.0 + u.insight * 1.4);
    o.colorMix = v.colorMix;
    o.t        = v.t;
    o.seed     = v.seed;
    o.age      = v.age;
    return o;
}

fragment float4 line_fs(LineOut in [[stage_in]],
                        constant Uniforms &u [[buffer(0)]])
{
    // 線一律單色：兩端給不同顏色時，線看起來會一直在跳
    float3 col = u.linkColor.rgb;
    float base = in.alpha * u.linkBoost;

    // 沿線跑的脈衝。fragment 已經有 t 的插值，所以不需要額外幾何，
    // 成本就是每個線段像素多算一個指數。
    // traffic 模式（seed < 0）：脈衝位置直接等於這條連線的年齡，
    // 於是「建立 -> 傳輸 -> 消失」剛好是一次完整的資料傳遞。
    float head = in.seed < 0.0
        ? in.age
        : fract(u.time * u.pulseSpeed * (0.6 + in.seed * 0.8) + in.seed);
    float d = abs(in.t - head);
    d = min(d, 1.0 - d);                       // 讓脈衝從尾端接回頭端
    float w = max(0.0008, u.pulseWidth);
    float pulse = exp(-(d * d) / w) * u.pulseStrength;

    // 運算越忙，脈衝越亮、線本身也稍微提亮
    float a = base * (1.0 + u.activity * 0.5) + pulse;
    float3 rgb = col * a + float3(1.0) * pulse * 0.35 * u.activity * u.glow;
    return float4(rgb, a);
}
"""

// 診斷用：只清畫面、不下任何 draw call，量框架本身的每幀開銷
let NO_DRAW = (ProcessInfo.processInfo.environment["WALL42_NO_DRAW"] == "1")

final class Renderer: NSObject, MTKViewDelegate {

    private let device: MTLDevice
    private let queue: MTLCommandQueue
    private var bgPipeline: MTLRenderPipelineState!
    private var particleAdd: MTLRenderPipelineState!
    private var particleNorm: MTLRenderPipelineState!
    private var lineAdd: MTLRenderPipelineState!
    private var lineNorm: MTLRenderPipelineState!

    private var particles: [Particle] = []
    /// 每顆粒子固定的隨機因子。屬性一律由這組種子「推算」而不是當場亂數，
    /// 所以拖動滑桿調整範圍時，粒子只會平滑縮放，不會整個重骰造成畫面跳動。
    private struct Seed {
        var size: Float
        var node: Float
        var bokeh: Float
        var color: Float
        var velAngle: Float
        var velMag: Float
        var twinkle: Float
    }
    private var seeds: [Seed] = []

    private var isNode: [Bool] = []        // 只在 CPU 端用於連線篩選
    private var linkIdx: [Int] = []        // 實際參與連線的粒子索引，避免每幀掃過全部

    /// traffic 模式的一次傳輸：從 a 到 b，活 life 秒，期間脈衝跑完全程。
    /// 連線不斷生滅才像在分工運算；距離模式那種固定接線看起來是結構圖，不是運算。
    private struct Transfer {
        var a: Int
        var b: Int
        var born: Float
        var life: Float
    }
    private var transfers: [Transfer] = []

    /// attention 模式的一次「思考」：聚焦某個節點，向鄰近節點查詢、收回、得出結論。
    /// 隨機兩兩傳輸看起來是雜訊；思考是有結構的，所以用焦點＋放射的形狀。
    private struct Focus {
        var node: Int
        var targets: [Int]
        var born: Float
        var life: Float
    }
    private var focuses: [Focus] = []
    private var particleBuffer: MTLBuffer!
    private var linkBuffer: MTLBuffer!
    private var maxLinkVerts: Int = 0

    private(set) var config: Config
    private var linkDistSq: Float = 0

    private var viewport = SIMD2<Float>(1920, 1080)
    private var lastTime = CFAbsoluteTimeGetCurrent()
    private var elapsed: Float = 0

    private(set) var frameCount: Int = 0
    private(set) var lastLinkCount: Int = 0
    /// 0 = 閒置、1 = 全力運算。由 main.swift 依系統負載或 MCP 指令更新。
    var activity: Float = 0
    /// 「想通」脈衝。由 MCP 觸發設成 1，之後每幀衰減回 0。
    private(set) var insight: Float = 0
    private var insightFocusPending = false

    /// AI 真的解決一件事時觸發：全域提亮一下，attention 模式再額外爆一個大焦點。
    func triggerInsight(_ strength: Float) {
        insight = max(insight, max(0, min(1.5, strength)))
        insightFocusPending = true
    }

    // 截圖：跑到指定幀數時把畫面存成 PNG，用來離線檢視視覺成果
    /// 設了路徑就在下一個符合的幀拍一張，拍完自動清空 —— 可以重複觸發
    var snapshotPath: String?
    var snapshotAtFrame: Int = 0
    var onSnapshot: ((String) -> Void)?

    init?(device: MTLDevice, config: Config) {
        guard let q = device.makeCommandQueue() else { return nil }
        self.device = device
        self.queue = q
        self.config = config
        super.init()

        guard buildPipelines() else { return nil }
        rebuildBuffers()
        seedParticles()
        linkDistSq = config.motion.link.distance * config.motion.link.distance
    }

    private func buildPipelines() -> Bool {
        let library: MTLLibrary
        do {
            library = try device.makeLibrary(source: shaderSource, options: nil)
        } catch {
            FileHandle.standardError.write("shader 編譯失敗: \(error)\n".data(using: .utf8)!)
            return false
        }

        enum Blend { case none, additive, normal }
        func make(_ vs: String, _ fs: String, _ blend: Blend) -> MTLRenderPipelineState? {
            let d = MTLRenderPipelineDescriptor()
            d.vertexFunction = library.makeFunction(name: vs)
            d.fragmentFunction = library.makeFunction(name: fs)
            d.colorAttachments[0].pixelFormat = .bgra8Unorm
            guard let c = d.colorAttachments[0] else { return nil }
            switch blend {
            case .none: break
            case .additive:
                // 發光疊加：重疊處越疊越亮，這是霓虹感的來源之一
                c.isBlendingEnabled = true
                c.rgbBlendOperation = .add; c.alphaBlendOperation = .add
                c.sourceRGBBlendFactor = .one; c.destinationRGBBlendFactor = .one
                c.sourceAlphaBlendFactor = .one; c.destinationAlphaBlendFactor = .one
            case .normal:
                // 一般透明度混合：重疊不過曝，看起來是實體而不是光
                c.isBlendingEnabled = true
                c.rgbBlendOperation = .add; c.alphaBlendOperation = .add
                c.sourceRGBBlendFactor = .one          // fragment 已自乘 alpha
                c.destinationRGBBlendFactor = .oneMinusSourceAlpha
                c.sourceAlphaBlendFactor = .one
                c.destinationAlphaBlendFactor = .oneMinusSourceAlpha
            }
            return try? device.makeRenderPipelineState(descriptor: d)
        }

        guard let bp = make("bg_vs", "bg_fs", .none),
              let pa = make("particle_vs", "particle_fs", .additive),
              let pn = make("particle_vs", "particle_fs", .normal),
              let la = make("line_vs", "line_fs", .additive),
              let ln = make("line_vs", "line_fs", .normal) else {
            FileHandle.standardError.write("pipeline 建立失敗\n".data(using: .utf8)!)
            return false
        }
        bgPipeline = bp
        particleAdd = pa; particleNorm = pn
        lineAdd = la; lineNorm = ln
        return true
    }

    private func rebuildBuffers() {
        let n = max(1, config.motion.particleCount)
        // 每顆粒子最多畫 24 條線，避免在高粒子數時配置過大的 buffer
        let maxLinks = min(n * (n - 1) / 2, n * 24)
        maxLinkVerts = max(maxLinks * 2, 2)
        particleBuffer = device.makeBuffer(length: MemoryLayout<Particle>.stride * n,
                                           options: .storageModeShared)
        linkBuffer = device.makeBuffer(length: MemoryLayout<LinkVertex>.stride * maxLinkVerts,
                                       options: .storageModeShared)
    }

    private func seedParticles() {
        let n = max(1, config.motion.particleCount)
        // 純隨機會聚簇成團、又留下大片空洞。改用 jittered grid：
        // 每格放一顆再隨機偏移，分布均勻但不機械。
        let aspect = viewport.x / viewport.y
        let cols = max(1, Int((Float(n) * aspect).squareRoot().rounded(.up)))
        let rows = max(1, Int((Float(n) / Float(cols)).rounded(.up)))
        let cw = viewport.x / Float(cols)
        let ch = viewport.y / Float(rows)

        seeds = (0..<n).map { _ in
            Seed(size: Float.random(in: 0...1),
                 node: Float.random(in: 0...1),
                 bokeh: Float.random(in: 0...1),
                 color: Float.random(in: 0...1),
                 velAngle: Float.random(in: 0...(2 * .pi)),
                 velMag: Float.random(in: 0.35...1),
                 twinkle: Float.random(in: 0...1))
        }

        particles = (0..<n).map { i in
            let gx = Float(i % cols), gy = Float(i / cols)
            let jx = (gx + Float.random(in: 0.15...0.85)) * cw
            let jy = (gy + Float.random(in: 0.15...0.85)) * ch
            return Particle(
                pos: SIMD2(jx.truncatingRemainder(dividingBy: viewport.x),
                           jy.truncatingRemainder(dividingBy: viewport.y)),
                vel: .zero, size: 0,
                phase: Float.random(in: 0...(2 * .pi)),
                colorMix: 0, depth: 0, twinkle: 1, boost: 0)
        }
        recomputeAttributes()
    }

    /// 由固定種子推算每顆粒子的尺寸／速度／色相／景深。
    /// 同樣的種子配同樣的設定一定得到同樣結果，可以安全地重複呼叫 ——
    /// 拖動滑桿時畫面是連續變化的，不會重骰。
    private func recomputeAttributes() {
        let m = config.motion
        if isNode.count != particles.count {
            isNode = [Bool](repeating: false, count: particles.count)
        }
        guard seeds.count == particles.count else { return }

        let bias = max(0.1, m.sizeBias ?? 2.2)
        let tv = max(0, m.twinkleVariance ?? 0.6)
        let bkMin = m.bokeh.sizeMin, bkMax = max(m.bokeh.sizeMin, m.bokeh.sizeMax)
        let ndMin = m.nodeSizeMin, ndMax = max(m.nodeSizeMin, m.nodeSizeMax)
        let pMin = m.sizeMin, pMax = max(m.sizeMin, m.sizeMax)

        for i in particles.indices {
            let sd = seeds[i]
            // 用固定種子比大小而不是重骰：調 ratio 時只有邊界附近的粒子換身分
            let isBokeh = sd.bokeh < m.bokeh.ratio
            let node = !isBokeh && sd.node < m.nodeRatio
            isNode[i] = node

            let speed = max(0.01, isBokeh ? m.bokeh.speed : m.speed) * sd.velMag
            particles[i].vel = SIMD2(cos(sd.velAngle) * speed, sin(sd.velAngle) * speed)

            particles[i].size = isBokeh
                ? bkMin + (bkMax - bkMin) * sd.size
                : (node ? ndMin + (ndMax - ndMin) * sd.size
                        // 冪次分布：大量微小點＋少數亮點，均勻會讓中等尺寸過多
                        : pMin + (pMax - pMin) * pow(sd.size, bias))

            // 色相偏向兩極，中間過渡色少一點，雙色對比才明顯
            particles[i].colorMix = sd.color < 0.5
                ? sd.color * 0.44
                : 0.78 + (sd.color - 0.5) * 0.44
            particles[i].depth = isBokeh ? 1.0 : 0.0
            particles[i].twinkle = max(0.15, 1 - tv) + (2 * tv) * sd.twinkle
        }
        rebuildLinkIndex()
    }

    /// 預先算好哪些粒子要參與連線。isNode 與 depth 在兩次 randomize 之間不變，
    /// 不必每幀重新篩選——否則星座模式下仍會掃過全部粒子再逐一 continue。
    private func rebuildLinkIndex() {
        let onlyNodes = config.motion.link.onlyNodes ?? false
        linkIdx = particles.indices.filter { i in
            particles[i].depth <= 0.5 && (!onlyNodes || isNode[i])
        }
    }

    /// 熱重載。粒子數變了才重建 buffer 並重新佈點，其餘就地套用，避免畫面閃動。
    func apply(_ new: Config) {
        let m = config.motion, nm = new.motion
        let countChanged = nm.particleCount != m.particleCount
        let shapeChanged = nm.sizeMin != m.sizeMin || nm.sizeMax != m.sizeMax
            || nm.speed != m.speed || nm.nodeRatio != m.nodeRatio
            || nm.nodeSizeMin != m.nodeSizeMin || nm.nodeSizeMax != m.nodeSizeMax
            || nm.bokeh.ratio != m.bokeh.ratio || nm.bokeh.sizeMin != m.bokeh.sizeMin
            || nm.bokeh.sizeMax != m.bokeh.sizeMax || nm.bokeh.speed != m.bokeh.speed
            || nm.sizeBias != m.sizeBias || nm.twinkleVariance != m.twinkleVariance
            || (nm.link.onlyNodes ?? false) != (m.link.onlyNodes ?? false)

        config = new
        linkDistSq = nm.link.distance * nm.link.distance

        if countChanged {
            transfers.removeAll()
            focuses.removeAll()
            rebuildBuffers()
            seedParticles()
        } else if shapeChanged {
            // 種子固定，重算是連續的，不必清掉進行中的傳輸
            recomputeAttributes()
        }
        // 顏色、亮度、連線透明度、呼吸速度都只走 uniform，不需要動 buffer
    }

    private func uniforms() -> Uniforms {
        let m = config.motion, b = config.background
        let solid = b.mode == "solid"
        return Uniforms(
            viewport: viewport,
            time: elapsed,
            brightness: m.brightness,
            colorA: parseHex(m.colorA),
            colorB: parseHex(m.colorB),
            bgCenter: solid ? parseHex(b.solidColor) : parseHex(b.centerColor),
            bgEdge:   solid ? parseHex(b.solidColor) : parseHex(b.edgeColor),
            bgRadius: max(0.01, b.radius),
            linkBoost: m.link.boost,
            breathSpeed: m.breathSpeed * (1.0 + activity * 0.8),
            twinkleAmount: max(0, min(1, m.twinkleAmount ?? 0.45)),
            bokehDimming: m.bokeh.dimming,
            // 忙碌時脈衝跑更快、更亮 —— 這就是「在運算」的體感來源
            pulseSpeed: (m.pulse?.speed ?? 0.35) * (0.45 + activity * 1.75),
            pulseStrength: (m.pulse?.strength ?? 0.9) * (0.25 + activity * 1.15),
            pulseWidth: m.pulse?.width ?? 0.004,
            activity: activity,
            glow: max(0, min(1, m.glow ?? 1.0)),
            insight: insight,
            // 線一律單色。沒指定就取兩極色的中間值。
            linkColor: m.link.color.map(parseHex)
                ?? ((parseHex(m.colorA) + parseHex(m.colorB)) * 0.5)
        )
    }

    func mtkView(_ view: MTKView, drawableSizeWillChange size: CGSize) {
        let old = viewport
        viewport = SIMD2(Float(size.width), Float(size.height))
        guard old.x > 0, old.y > 0 else { return }
        let sx = viewport.x / old.x, sy = viewport.y / old.y
        for i in particles.indices {
            particles[i].pos.x *= sx
            particles[i].pos.y *= sy
        }
    }

    func draw(in view: MTKView) {
        let now = CFAbsoluteTimeGetCurrent()
        var dt = Float(now - lastTime)
        lastTime = now
        if dt > 0.1 { dt = 0.1 }       // 從暫停恢復時夾住，避免粒子瞬移
        elapsed += dt
        if insight > 0 { insight = max(0, insight - dt * 0.75) }   // 約 1.3 秒衰減完

        if !NO_DRAW { step(dt) }

        guard let drawable = view.currentDrawable,
              let rpd = view.currentRenderPassDescriptor,
              let cmd = queue.makeCommandBuffer(),
              let enc = cmd.makeRenderCommandEncoder(descriptor: rpd) else { return }

        if NO_DRAW {
            enc.endEncoding(); cmd.present(drawable); cmd.commit()
            frameCount += 1
            return
        }

        var u = uniforms()
        let ulen = MemoryLayout<Uniforms>.stride
        let normalBlend = (config.motion.blend ?? "additive") == "normal"

        // 背景 -> 連線 -> 粒子，由遠而近
        enc.setRenderPipelineState(bgPipeline)
        enc.setFragmentBytes(&u, length: ulen, index: 0)
        enc.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 3)

        if config.motion.link.enabled && lastLinkCount > 0 {
            enc.setRenderPipelineState(normalBlend ? lineNorm : lineAdd)
            enc.setVertexBuffer(linkBuffer, offset: 0, index: 0)
            enc.setVertexBytes(&u, length: ulen, index: 1)
            enc.setFragmentBytes(&u, length: ulen, index: 0)
            enc.drawPrimitives(type: .line, vertexStart: 0, vertexCount: lastLinkCount * 2)
        }

        enc.setRenderPipelineState(normalBlend ? particleNorm : particleAdd)
        enc.setVertexBuffer(particleBuffer, offset: 0, index: 0)
        enc.setVertexBytes(&u, length: ulen, index: 1)
        enc.setFragmentBytes(&u, length: ulen, index: 0)
        enc.drawPrimitives(type: .point, vertexStart: 0, vertexCount: particles.count)

        enc.endEncoding()

        if let path = snapshotPath, frameCount >= snapshotAtFrame {
            snapshotPath = nil          // 拍一次就清掉，下次要拍再設
            capture(drawable.texture, cmd: cmd, to: path)
        }

        cmd.present(drawable)
        cmd.commit()
        frameCount += 1
    }

    /// 把 drawable 複製到可讀 texture 再存成 PNG。
    /// drawable 的 storageMode 是 private，不能直接 getBytes，必須先 blit。
    private func capture(_ src: MTLTexture, cmd: MTLCommandBuffer, to path: String) {
        let d = MTLTextureDescriptor.texture2DDescriptor(
            pixelFormat: src.pixelFormat, width: src.width, height: src.height, mipmapped: false)
        d.storageMode = .shared
        d.usage = [.shaderRead]
        guard let dst = device.makeTexture(descriptor: d),
              let blit = cmd.makeBlitCommandEncoder() else { return }
        blit.copy(from: src, sourceSlice: 0, sourceLevel: 0,
                  sourceOrigin: MTLOrigin(x: 0, y: 0, z: 0),
                  sourceSize: MTLSize(width: src.width, height: src.height, depth: 1),
                  to: dst, destinationSlice: 0, destinationLevel: 0,
                  destinationOrigin: MTLOrigin(x: 0, y: 0, z: 0))
        blit.endEncoding()

        cmd.addCompletedHandler { [weak self] _ in
            guard let self else { return }
            let w = dst.width, h = dst.height
            var bytes = [UInt8](repeating: 0, count: w * h * 4)
            dst.getBytes(&bytes, bytesPerRow: w * 4,
                         from: MTLRegionMake2D(0, 0, w, h), mipmapLevel: 0)
            for i in stride(from: 0, to: bytes.count, by: 4) { bytes.swapAt(i, i + 2) }  // BGRA -> RGBA
            guard let provider = CGDataProvider(data: Data(bytes) as CFData),
                  let img = CGImage(width: w, height: h, bitsPerComponent: 8, bitsPerPixel: 32,
                                    bytesPerRow: w * 4, space: CGColorSpaceCreateDeviceRGB(),
                                    bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipLast.rawValue),
                                    provider: provider, decode: nil,
                                    shouldInterpolate: false, intent: .defaultIntent),
                  let dest = CGImageDestinationCreateWithURL(
                    URL(fileURLWithPath: path) as CFURL, "public.png" as CFString, 1, nil)
            else { return }
            CGImageDestinationAddImage(dest, img, nil)
            guard CGImageDestinationFinalize(dest) else { return }
            self.onSnapshot?(path)
        }
    }

    /// 注意力模式：一次「思考」＝ 聚焦一個節點 → 向鄰近放射查詢 → 脈衝回流 → 焦點爆亮。
    /// 三個階段都只是同一批線段的 alpha 與脈衝位置在變，沒有額外的幾何或 draw call。
    private func stepAttention(_ dt: Float) {
        let cfg = config.motion.link
        let maxDist = max(1, cfg.distance)
        let lifeLo = max(0.4, cfg.lifeMin ?? 1.1)
        let lifeHi = max(lifeLo + 0.1, cfg.lifeMax ?? 2.6)

        for i in particles.indices { particles[i].boost = 0 }
        focuses.removeAll { elapsed - $0.born >= $0.life }

        // MCP 觸發的「想通」：生一個目標特別多的焦點，直接跳到結論階段爆亮
        if insightFocusPending, linkIdx.count >= 4 {
            insightFocusPending = false
            let n = linkIdx[Int.random(in: 0..<linkIdx.count)]
            let pn = particles[n].pos
            var targets: [Int] = []
            let reach = max(1, cfg.distance) * 1.6      // 比平常遠，張得更開
            for _ in 0..<60 where targets.count < 10 {
                let c = linkIdx[Int.random(in: 0..<linkIdx.count)]
                if c == n || targets.contains(c) { continue }
                let d = pn - particles[c].pos
                if d.x * d.x + d.y * d.y < reach * reach { targets.append(c) }
            }
            if targets.count >= 3 {
                // born 往前推到 80%，一出生就在下結論
                let life: Float = 1.6
                focuses.append(Focus(node: n, targets: targets,
                                     born: elapsed - life * 0.8, life: life))
            }
        }

        // 同時進行幾個思考：閒置時一兩個，忙碌時並行
        let want = max(2, Int(Float(cfg.targetCount ?? 60) / 4.5 * (0.55 + activity * 1.9)))
        var tries = 0
        while focuses.count < want && tries < want * 5 && linkIdx.count >= 3 {
            tries += 1
            let n = linkIdx[Int.random(in: 0..<linkIdx.count)]
            if focuses.contains(where: { $0.node == n }) { continue }
            let pn = particles[n].pos
            // 挑附近的節點當查詢對象
            var targets: [Int] = []
            for _ in 0..<32 where targets.count < Int.random(in: 3...7) {
                let c = linkIdx[Int.random(in: 0..<linkIdx.count)]
                if c == n || targets.contains(c) { continue }
                let d = pn - particles[c].pos
                if d.x * d.x + d.y * d.y < maxDist * maxDist { targets.append(c) }
            }
            guard targets.count >= 2 else { continue }
            focuses.append(Focus(node: n, targets: targets, born: elapsed,
                                 life: Float.random(in: lifeLo...lifeHi)))
        }

        let op = cfg.opacity
        let lp = linkBuffer.contents().bindMemory(to: LinkVertex.self, capacity: maxLinkVerts)
        var v = 0

        for f in focuses {
            let age = max(0, min(1, (elapsed - f.born) / max(0.01, f.life)))
            // 查詢 45% → 回流 35% → 下結論 20%
            var head: Float
            var fade: Float
            if age < 0.45 {
                let t = age / 0.45
                head = t; fade = min(1, t * 4.5)
            } else if age < 0.80 {
                let t = (age - 0.45) / 0.35
                head = 1 - t; fade = 1
            } else {
                let t = (age - 0.80) / 0.20
                head = 0; fade = 1 - t
                // 結論：焦點爆亮再收
                particles[f.node].boost = max(particles[f.node].boost, sin(t * .pi) * 2.2)
            }

            let src = particles[f.node].pos
            for tIdx in f.targets {
                if v + 2 > maxLinkVerts { break }
                let dst = particles[tIdx].pos
                let d = src - dst
                let far = min(1, (d.x * d.x + d.y * d.y).squareRoot() / maxDist)
                let alpha = op * fade * (1 - far * 0.5)
                // seed < 0 代表脈衝位置直接用 age 欄位，這裡塞的是我們算好的 head
                lp[v]     = LinkVertex(pos: src, alpha: alpha, colorMix: 0,
                                       t: 0, seed: -1, age: head)
                lp[v + 1] = LinkVertex(pos: dst, alpha: alpha, colorMix: 0,
                                       t: 1, seed: -1, age: head)
                v += 2
            }
        }
        lastLinkCount = v / 2
        particleBuffer.contents()
            .copyMemory(from: particles, byteCount: MemoryLayout<Particle>.stride * particles.count)
    }

    /// 流量模式：維護一池「進行中的傳輸」。每條連線 fade in → 脈衝跑完 → fade out，
    /// 然後換別的節點對。忙碌時同時進行的傳輸更多、壽命更短（跑得更急）。
    private func stepTraffic(_ dt: Float) {
        let cfg = config.motion.link
        let maxDist = max(1, cfg.distance)
        let lifeLo = max(0.15, cfg.lifeMin ?? 1.1)
        let lifeHi = max(lifeLo + 0.05, cfg.lifeMax ?? 2.6)
        // 忙碌時壓縮壽命，傳輸看起來更急促
        let rush = 1.0 - 0.45 * activity

        for i in particles.indices where particles[i].boost != 0 { particles[i].boost = 0 }
        transfers.removeAll { elapsed - $0.born >= $0.life }

        let base = Float(cfg.targetCount ?? 60)
        let want = Int(base * (0.3 + activity * 1.25))
        var guardCount = 0
        while transfers.count < want && guardCount < want * 6 && linkIdx.count >= 2 {
            guardCount += 1
            let a = linkIdx[Int.random(in: 0..<linkIdx.count)]
            let pa = particles[a].pos
            // 只挑距離內的對象，否則線會橫跨整個畫面
            var pick = -1
            for _ in 0..<12 {
                let c = linkIdx[Int.random(in: 0..<linkIdx.count)]
                if c == a { continue }
                let d = pa - particles[c].pos
                if d.x * d.x + d.y * d.y < maxDist * maxDist { pick = c; break }
            }
            guard pick >= 0 else { continue }
            let b = pick
            if transfers.contains(where: { ($0.a == a && $0.b == b) || ($0.a == b && $0.b == a) }) {
                continue
            }
            transfers.append(Transfer(a: a, b: b, born: elapsed,
                                      life: Float.random(in: lifeLo...lifeHi) * rush))
        }

        let op = cfg.opacity
        let lp = linkBuffer.contents().bindMemory(to: LinkVertex.self, capacity: maxLinkVerts)
        var v = 0
        for t in transfers {
            if v + 2 > maxLinkVerts { break }
            let age = max(0, min(1, (elapsed - t.born) / max(0.01, t.life)))
            // 生滅包絡：兩端淡入淡出，中間最亮
            let fade = sin(age * Float.pi)
            let pa = particles[t.a].pos, pb = particles[t.b].pos
            // 節點飄遠了就讓它更淡，避免出現突兀的長線
            let d = pa - pb
            let far = min(1, (d.x * d.x + d.y * d.y).squareRoot() / maxDist)
            let alpha = op * fade * (1 - far * 0.55)
            // seed = -1 告訴 shader：脈衝位置用 age，不要用全域時間
            lp[v]     = LinkVertex(pos: pa, alpha: alpha, colorMix: 0, t: 0, seed: -1, age: age)
            lp[v + 1] = LinkVertex(pos: pb, alpha: alpha, colorMix: 0, t: 1, seed: -1, age: age)
            v += 2
        }
        lastLinkCount = v / 2
    }

    /// 更新位置並重建連線。連線只在遠景粒子之間，前景散景不連線（符合景深邏輯）。
    private func step(_ dt: Float) {
        let w = viewport.x, h = viewport.y
        for i in particles.indices {
            particles[i].pos += particles[i].vel * dt
            if particles[i].pos.x < 0 { particles[i].pos.x += w }
            if particles[i].pos.x > w { particles[i].pos.x -= w }
            if particles[i].pos.y < 0 { particles[i].pos.y += h }
            if particles[i].pos.y > h { particles[i].pos.y -= h }
        }
        particleBuffer.contents()
            .copyMemory(from: particles, byteCount: MemoryLayout<Particle>.stride * particles.count)

        guard config.motion.link.enabled else { lastLinkCount = 0; transfers.removeAll(); return }

        switch config.motion.link.mode ?? "proximity" {
        case "traffic":   stepTraffic(dt);   return
        case "attention": stepAttention(dt); return
        default: break
        }

        let dist = max(0.01, config.motion.link.distance)
        let op = config.motion.link.opacity
        let lp = linkBuffer.contents().bindMemory(to: LinkVertex.self, capacity: maxLinkVerts)
        var v = 0

        let m = linkIdx.count
        outer: for ii in 0..<m {
            let i = linkIdx[ii]
            let a = particles[i].pos
            let ca = particles[i].colorMix
            for jj in (ii + 1)..<m {
                let j = linkIdx[jj]
                let d = a - particles[j].pos
                let dsq = d.x * d.x + d.y * d.y
                if dsq < linkDistSq {
                    if v + 2 > maxLinkVerts { break outer }
                    let alpha = (1.0 - (dsq.squareRoot() / dist)) * op
                    // seed 由兩端索引 hash 而來：連線每幀重建，用亂數會讓脈衝每幀跳掉
                    let h = (i &* 73856093) ^ (j &* 19349663)
                    let seed = Float(h & 0xFFFF) / 65535.0
                    lp[v]     = LinkVertex(pos: a, alpha: alpha, colorMix: ca,
                                           t: 0, seed: seed, age: 0)
                    lp[v + 1] = LinkVertex(pos: particles[j].pos, alpha: alpha,
                                           colorMix: particles[j].colorMix,
                                           t: 1, seed: seed, age: 0)
                    v += 2
                }
            }
        }
        lastLinkCount = v / 2
    }
}
