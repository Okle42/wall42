import Cocoa
import MetalKit
import simd
import ImageIO
import UniformTypeIdentifiers

// CPU 端粒子。欄位順序與大小必須跟 shader 裡的 Particle 一致（9 個 float，stride 40 bytes）。
struct Particle {
    var pos: SIMD2<Float>     // 世界座標（point，y 向下，原點在所有螢幕聯合矩形的左上角）
    var vel: SIMD2<Float>
    var size: Float
    var phase: Float
    var colorMix: Float   // 0 = colorA，1 = colorB
    var depth: Float      // 0 = 遠景（銳利小點），1 = 前景（散景光斑）
    var twinkle: Float    // 個別閃爍速度倍率，讓星點不同步明滅
    var boost: Float      // 臨時放大／提亮（attention 模式的焦點爆亮）
    var fade: Float       // 0..1 整體淡入淡出（雪落地、沙粒消散用；漂浮模式恆為 1）
}

struct LinkVertex {
    var pos: SIMD2<Float>
    var alpha: Float
    var colorMix: Float
    var t: Float        // 0 = 線的起點，1 = 終點，fragment 會拿到插值
    var seed: Float     // proximity：脈衝相位；traffic：-1 代表改用 age
    var age: Float      // traffic：0 剛建立、1 即將消失
}

// 與 shader 的 Uniforms 佈局必須逐欄位對齊（stride 176 bytes）。
// SIMD4 在兩邊都對齊 16，所以順序不能任意調換；新欄位一律加在最後。
struct Uniforms {
    var viewport: SIMD2<Float>   // 這個螢幕 drawable 的像素尺寸（背景漸層用）
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
    var viewOrigin: SIMD2<Float> // 這個螢幕在世界座標中的左上角（point）
    var viewSize: SIMD2<Float>   // 這個螢幕的大小（point）
    var pxScale: Float           // point → pixel（Retina = 2）
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
    float2 viewOrigin;
    float2 viewSize;
    float  pxScale;
};

// 世界座標（point）→ 這個螢幕的 NDC
static inline float2 world_to_ndc(float2 p, constant Uniforms &u) {
    float2 ndc = ((p - u.viewOrigin) / u.viewSize) * 2.0 - 1.0;
    ndc.y = -ndc.y;
    return ndc;
}

// 背景：中心色向邊緣色的徑向衰減，兼作電影感打光與暗角（每個螢幕各自一份）
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
    float  fade;
};

struct PointOut {
    float4 position [[position]];
    float2 uv;
    float  alpha;
    float  colorMix;
    float  depth;
};

// 粒子畫成 instanced quad 而不是 point sprite：
// point 的中心一跑出螢幕整顆就被裁掉，跨螢幕接縫會看到大光斑「啪」一下消失。
// quad 是真的幾何裁切，半顆在左螢幕、半顆在右螢幕也能連續。
vertex PointOut particle_vs(uint vid [[vertex_id]],
                            uint iid [[instance_id]],
                            constant Particle *ps [[buffer(0)]],
                            constant Uniforms &u  [[buffer(1)]])
{
    Particle p = ps[iid];
    PointOut o;
    float2 corner = float2((vid & 1) ? 1.0 : -1.0, (vid & 2) ? 1.0 : -1.0);
    float psize = p.size * (1.0 + p.boost * 1.6) * u.pxScale;   // 像素直徑
    float2 ndc = world_to_ndc(p.pos, u) + corner * psize / u.viewport;
    o.position = float4(ndc, 0.0, 1.0);
    o.uv       = corner * 0.5 + 0.5;
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
            * (1.0 + p.boost * 1.2) * (1.0 + u.insight * 0.9) * p.fade;
    return o;
}

fragment float4 particle_fs(PointOut in [[stage_in]],
                            constant Uniforms &u [[buffer(0)]])
{
    float d = length(in.uv - float2(0.5));
    if (d > 0.5 || in.alpha <= 0.0) discard_fragment();

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
    o.position = float4(world_to_ndc(v.pos, u), 0.0, 1.0);
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
let NO_DRAW = (ProcessInfo.processInfo.environment["WALL91_NO_DRAW"] == "1")

/// 所有螢幕共用的 GPU 資源：shader 只編譯一次，pipeline 只建一次。
final class GPU {
    let device: MTLDevice
    let queue: MTLCommandQueue
    let bg: MTLRenderPipelineState
    let particleAdd: MTLRenderPipelineState
    let particleNorm: MTLRenderPipelineState
    let lineAdd: MTLRenderPipelineState
    let lineNorm: MTLRenderPipelineState

    init?(device: MTLDevice) {
        guard let q = device.makeCommandQueue() else { return nil }
        let library: MTLLibrary
        do {
            library = try device.makeLibrary(source: shaderSource, options: nil)
        } catch {
            FileHandle.standardError.write("shader 編譯失敗: \(error)\n".data(using: .utf8)!)
            return nil
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
            return nil
        }
        self.device = device
        queue = q
        bg = bp; particleAdd = pa; particleNorm = pn; lineAdd = la; lineNorm = ln
    }
}

/// 一個螢幕一份的「鏡頭」：不擁有粒子，只把共用的 World 中屬於自己那塊畫出來。
/// 模擬在 World 裡只跑一份，所以兩螢幕的粒子、連線、焦點是同一片星空。
final class Renderer: NSObject, MTKViewDelegate {

    private let gpu: GPU
    private let world: World
    /// 這個螢幕在世界座標中的位置與大小（point）
    var viewOrigin = SIMD2<Float>(0, 0)
    var viewSize = SIMD2<Float>(1920, 1080)
    var pxScale: Float = 1
    private var viewport = SIMD2<Float>(1920, 1080)   // drawable 像素

    private(set) var frameCount: Int = 0

    // 截圖：跑到指定幀數時把畫面存成 PNG，用來離線檢視視覺成果
    /// 設了路徑就在下一個符合的幀拍一張，拍完自動清空 —— 可以重複觸發
    var snapshotPath: String?
    var snapshotAtFrame: Int = 0
    var onSnapshot: ((String) -> Void)?

    init(gpu: GPU, world: World) {
        self.gpu = gpu
        self.world = world
        super.init()
    }

    func mtkView(_ view: MTKView, drawableSizeWillChange size: CGSize) {
        viewport = SIMD2(Float(size.width), Float(size.height))
    }

    func draw(in view: MTKView) {
        // 模擬只推進一次：同一個 vsync 內第二個螢幕來呼叫時會直接略過
        world.advance()

        guard let drawable = view.currentDrawable,
              let rpd = view.currentRenderPassDescriptor,
              let cmd = gpu.queue.makeCommandBuffer(),
              let enc = cmd.makeRenderCommandEncoder(descriptor: rpd) else { return }

        if NO_DRAW {
            enc.endEncoding(); cmd.present(drawable); cmd.commit()
            frameCount += 1
            return
        }

        let ds = view.drawableSize
        if ds.width > 0 { viewport = SIMD2(Float(ds.width), Float(ds.height)) }
        var u = world.uniforms(viewport: viewport, origin: viewOrigin, size: viewSize, scale: pxScale)
        let ulen = MemoryLayout<Uniforms>.stride
        let normalBlend = (world.config.motion.blend ?? "additive") == "normal"

        // 背景 -> 連線 -> 粒子，由遠而近
        enc.setRenderPipelineState(gpu.bg)
        enc.setFragmentBytes(&u, length: ulen, index: 0)
        enc.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 3)

        let links = world.lastLinkCount
        if links > 0, let lb = world.linkBuffer {
            enc.setRenderPipelineState(normalBlend ? gpu.lineNorm : gpu.lineAdd)
            enc.setVertexBuffer(lb, offset: 0, index: 0)
            enc.setVertexBytes(&u, length: ulen, index: 1)
            enc.setFragmentBytes(&u, length: ulen, index: 0)
            enc.drawPrimitives(type: .line, vertexStart: 0, vertexCount: links * 2)
        }

        let n = world.drawCount
        if n > 0, let pb = world.particleBuffer {
            enc.setRenderPipelineState(normalBlend ? gpu.particleNorm : gpu.particleAdd)
            enc.setVertexBuffer(pb, offset: 0, index: 0)
            enc.setVertexBytes(&u, length: ulen, index: 1)
            enc.setFragmentBytes(&u, length: ulen, index: 0)
            enc.drawPrimitives(type: .triangleStrip, vertexStart: 0, vertexCount: 4, instanceCount: n)
        }

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
        guard let dst = gpu.device.makeTexture(descriptor: d),
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
}
