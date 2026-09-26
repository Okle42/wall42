import Cocoa
import Metal
import simd

/// 一個螢幕在世界中的位置（世界座標：point，y 向下，原點＝所有螢幕聯合矩形的左上角）
struct ScreenSlot {
    var origin: SIMD2<Float>     // 螢幕左上角
    var size: SIMD2<Float>
    var visibleTop: Float        // 扣掉選單列後的可用區上緣
    var visibleBottom: Float     // 扣掉 Dock 後的可用區下緣（沙堆、雪落地用）
}

/// 整片星空只有一份模擬。每個螢幕的 Renderer 只是從不同位置看同一個世界，
/// 所以粒子、連線、脈衝、attention 焦點可以跨螢幕連續，而且 step 只跑一次。
///
/// 密度：設定檔的 particleCount 指的是「一台主螢幕大小的面積裡有幾顆」。
/// 實際粒子數＝particleCount × 世界面積 ÷ 主螢幕面積，所以接兩台時自動變兩倍，
/// 每台看起來的密度跟單螢幕時一樣。link.targetCount 也用同一個比例放大。
final class World {

    private let device: MTLDevice
    private(set) var config: Config

    // ── 世界幾何 ────────────────────────────────────────────
    private(set) var size = SIMD2<Float>(1920, 1080)
    private(set) var slots: [ScreenSlot] = []
    /// 世界面積 ÷ 主螢幕面積。單螢幕 = 1、兩台並排 = 2。
    private(set) var areaScale: Float = 1

    // ── 粒子 ────────────────────────────────────────────────
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
    private struct Transfer {
        var a: Int
        var b: Int
        var born: Float
        var life: Float
    }
    private var transfers: [Transfer] = []

    /// attention 模式的一次「思考」：聚焦某個節點，向鄰近節點查詢、收回、得出結論。
    private struct Focus {
        var node: Int
        var targets: [Int]
        var born: Float
        var life: Float
    }
    private var focuses: [Focus] = []

    // ── Claude session 光點：接在粒子陣列尾端的常駐節點 ─────────
    struct SessionInfo: Equatable {
        var id: String
        var busy: Bool
    }
    private(set) var sessions: [SessionInfo] = []
    /// 一般粒子數（尾端 sessions.count 顆是 session 光點）
    private var regularCount: Int { particles.count - sessions.count }
    private var sessionAnchors: [SIMD2<Float>] = []
    private var sessionSpawnAcc: [Float] = []

    // ── 效果狀態（snow／sand）────────────────────────────────
    private var effectName = ""          // 空字串＝下一步要重建效果狀態
    private var activeEffect = ""        // 上一次實際在跑的效果（重建旗標不會清掉它）
    /// sand：0 下落中、1 堆積中、2 閒置（在池子裡等著被放出來）
    private var grainState: [UInt8] = []
    private var grainAge: [Float] = []
    private var grainBin: [Int] = []
    private var grainDh: [Float] = []
    private var idleGrains: [Int] = []
    private var heights: [Float] = []        // 每 binW 寬一格的沙堆高度
    private var groundBins: [Float] = []     // 每格的地面 y（可用區下緣，避開 Dock）
    private var streamAcc: [Float] = []
    /// 沙堆整體下沉速度（point/秒）。自動調節：沙粒池快用完就沉快一點，
    /// 沙堆體積因此穩定在粒子數撐得起的大小，不會越堆越高也不會斷流。
    private var sinkSpeed: Float = 2.5
    private let binW: Float = 2

    private(set) var particleBuffer: MTLBuffer?
    private(set) var linkBuffer: MTLBuffer?
    private var particleCapacity = 0
    private var maxLinkVerts = 0
    private var linkDistSq: Float = 0

    // ── 時間 ────────────────────────────────────────────────
    private var lastStep = CFAbsoluteTimeGetCurrent()
    private(set) var elapsed: Float = 0
    /// 兩次 step 之間最短間隔。由 AppDelegate 依「目前畫得最快的螢幕」的 fps 設定。
    /// 兩台螢幕的 vsync 相位不同，不擋的話一秒會 step 兩倍次數。
    var stepInterval: Double = 1.0 / 30.0

    // ── 給畫面與選單列讀的狀態 ──────────────────────────────
    private(set) var lastLinkCount = 0
    private(set) var stepCount = 0
    var drawCount: Int { particles.count }
    /// 0 = 閒置、1 = 全力運算。由 main.swift 依系統負載或 MCP 指令更新。
    var activity: Float = 0
    /// 「想通」脈衝。由 MCP 觸發設成 1，之後每幀衰減回 0。
    private(set) var insight: Float = 0
    private var insightFocusPending = 0

    init(device: MTLDevice, config: Config) {
        self.device = device
        self.config = config
        linkDistSq = config.motion.link.distance * config.motion.link.distance
    }

    // MARK: - 世界幾何

    /// 依螢幕排列建立／重建世界。已有粒子時依比例映射到新世界，不重新佈點，
    /// 所以插拔螢幕或改排列時畫面不會整個重來。
    func setLayout(_ newSlots: [ScreenSlot], mainArea: Float) {
        var maxX: Float = 1, maxY: Float = 1
        for s in newSlots {
            maxX = max(maxX, s.origin.x + s.size.x)
            maxY = max(maxY, s.origin.y + s.size.y)
        }
        let old = size
        size = SIMD2(maxX, maxY)
        slots = newSlots
        areaScale = max(0.25, (size.x * size.y) / max(1, mainArea))

        if !particles.isEmpty, old.x > 0, old.y > 0 {
            let sx = size.x / old.x, sy = size.y / old.y
            for i in 0..<regularCount {
                particles[i].pos.x *= sx
                particles[i].pos.y *= sy
            }
            transfers.removeAll()
            focuses.removeAll()
        }
        resizeParticles()
        // 地面、沙流位置、session 錨點都跟螢幕排列有關，重算
        effectName = ""
        placeSessionAnchors()
    }

    /// 設定的粒子數換算成整片世界要幾顆
    private var targetCount: Int {
        max(1, Int((Float(max(1, config.motion.particleCount)) * areaScale).rounded()))
    }

    /// 讓粒子數符合目標：多的從尾端砍掉、少的補新的（新粒子隨機放）。
    private func resizeParticles() {
        let want = targetCount
        // session 光點在尾端，先拿下來，一般粒子調整完再接回去
        let tail = Array(particles.suffix(sessions.count))
        particles.removeLast(sessions.count)
        defer {
            particles.append(contentsOf: tail)
            ensureBuffers()
            recomputeAttributes()
        }
        if particles.isEmpty {
            seedParticles(want)
        } else if particles.count > want {
            particles.removeLast(particles.count - want)
            seeds.removeLast(seeds.count - want)
            transfers.removeAll(); focuses.removeAll()
        } else if particles.count < want {
            for _ in particles.count..<want {
                seeds.append(randomSeed())
                particles.append(blankParticle(at: SIMD2(Float.random(in: 0..<size.x),
                                                         Float.random(in: 0..<size.y))))
            }
        }
        if particles.count != grainState.count { effectName = "" }   // 效果狀態要重建
    }

    private func randomSeed() -> Seed {
        Seed(size: Float.random(in: 0...1),
             node: Float.random(in: 0...1),
             bokeh: Float.random(in: 0...1),
             color: Float.random(in: 0...1),
             velAngle: Float.random(in: 0...(2 * .pi)),
             velMag: Float.random(in: 0.35...1),
             twinkle: Float.random(in: 0...1))
    }

    private func blankParticle(at p: SIMD2<Float>) -> Particle {
        Particle(pos: p, vel: .zero, size: 0,
                 phase: Float.random(in: 0...(2 * .pi)),
                 colorMix: 0, depth: 0, twinkle: 1, boost: 0, fade: 1)
    }

    private func seedParticles(_ n: Int) {
        // 純隨機會聚簇成團、又留下大片空洞。改用 jittered grid：
        // 每格放一顆再隨機偏移，分布均勻但不機械。
        let aspect = size.x / size.y
        let cols = max(1, Int((Float(n) * aspect).squareRoot().rounded(.up)))
        let rows = max(1, Int((Float(n) / Float(cols)).rounded(.up)))
        let cw = size.x / Float(cols)
        let ch = size.y / Float(rows)
        seeds = (0..<n).map { _ in randomSeed() }
        particles = (0..<n).map { i in
            let gx = Float(i % cols), gy = Float(i / cols)
            let jx = (gx + Float.random(in: 0.15...0.85)) * cw
            let jy = (gy + Float.random(in: 0.15...0.85)) * ch
            return blankParticle(at: SIMD2(jx.truncatingRemainder(dividingBy: size.x),
                                           jy.truncatingRemainder(dividingBy: size.y)))
        }
    }

    /// buffer 只在容量不夠時才重配，平常粒子數小幅變動不會重建。
    private func ensureBuffers() {
        let n = max(1, particles.count)
        if n > particleCapacity || particleBuffer == nil {
            particleCapacity = n + 64
            particleBuffer = device.makeBuffer(length: MemoryLayout<Particle>.stride * particleCapacity,
                                               options: .storageModeShared)
        }
        // 每顆粒子最多畫 24 條線，避免在高粒子數時配置過大的 buffer
        let maxLinks = min(n * (n - 1) / 2, n * 24)
        let want = max(maxLinks * 2, 2)
        if want > maxLinkVerts || linkBuffer == nil {
            maxLinkVerts = want
            linkBuffer = device.makeBuffer(length: MemoryLayout<LinkVertex>.stride * maxLinkVerts,
                                           options: .storageModeShared)
        }
    }

    /// 由固定種子推算每顆粒子的尺寸／速度／色相／景深。
    /// 同樣的種子配同樣的設定一定得到同樣結果，可以安全地重複呼叫 ——
    /// 拖動滑桿時畫面是連續變化的，不會重骰。
    private func recomputeAttributes() {
        let m = config.motion
        if isNode.count != particles.count {
            isNode = [Bool](repeating: false, count: particles.count)
        }
        guard seeds.count == regularCount else { return }

        let bias = max(0.1, m.sizeBias ?? 2.2)
        let tv = max(0, m.twinkleVariance ?? 0.6)
        let bkMin = m.bokeh.sizeMin, bkMax = max(m.bokeh.sizeMin, m.bokeh.sizeMax)
        let ndMin = m.nodeSizeMin, ndMax = max(m.nodeSizeMin, m.nodeSizeMax)
        let pMin = m.sizeMin, pMax = max(m.sizeMin, m.sizeMax)

        for i in 0..<regularCount {
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
            if effectName != "snow" && effectName != "sand" { particles[i].fade = 1 }
        }
        styleSessionParticles()
        rebuildLinkIndex()
    }

    /// 預先算好哪些粒子要參與連線，不必每幀重新篩選。
    private func rebuildLinkIndex() {
        let onlyNodes = config.motion.link.onlyNodes ?? false
        linkIdx = particles.indices.filter { i in
            particles[i].depth <= 0.5 && (!onlyNodes || isNode[i])
        }
    }

    // MARK: - 設定

    /// 熱重載。粒子數變了才增減粒子，其餘就地套用，避免畫面閃動。
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
        if nm.effect != m.effect { effectName = "" }      // 下一步重建效果狀態
        if nm.nodeSizeMax != m.nodeSizeMax || nm.sessions?.size != m.sessions?.size {
            styleSessionParticles()
        }
        if (nm.sessions?.enabled ?? false) != (m.sessions?.enabled ?? false),
           !(nm.sessions?.enabled ?? false) {
            setSessions([])
        }

        if countChanged {
            transfers.removeAll()
            focuses.removeAll()
            resizeParticles()
        } else if shapeChanged {
            // 種子固定，重算是連續的，不必清掉進行中的傳輸
            recomputeAttributes()
        }
        // 顏色、亮度、連線透明度、呼吸速度都只走 uniform，不需要動 buffer
    }

    /// AI 真的解決一件事時觸發：全域提亮一下，attention 模式再額外爆焦點（每台螢幕大小一個）。
    func triggerInsight(_ strength: Float) {
        insight = max(insight, max(0, min(1.5, strength)))
        insightFocusPending = max(1, Int(areaScale.rounded()))
    }

    /// 從全部停畫恢復時呼叫：下一步的 dt 從現在算起，不會一次補上停了多久。
    func resetClock() { lastStep = CFAbsoluteTimeGetCurrent() }

    func uniforms(viewport: SIMD2<Float>, origin: SIMD2<Float>, size vs: SIMD2<Float>,
                  scale: Float) -> Uniforms {
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
                ?? ((parseHex(m.colorA) + parseHex(m.colorB)) * 0.5),
            viewOrigin: origin,
            viewSize: vs,
            pxScale: scale,
            worldSize: size,
            bgMode: b.mode == "vertical" ? 1 : 0,
            softness: max(0, min(1, m.softness ?? 0))
        )
    }

    // MARK: - 模擬

    /// 每個螢幕畫之前都會呼叫；距離上次 step 太近（同一個 vsync 的另一台螢幕）就略過。
    func advance() {
        let now = CFAbsoluteTimeGetCurrent()
        let gap = now - lastStep
        if gap < stepInterval * 0.75 { return }
        lastStep = now
        var dt = Float(gap)
        if dt > 0.1 { dt = 0.1 }       // 從暫停恢復時夾住，避免粒子瞬移
        elapsed += dt
        if insight > 0 { insight = max(0, insight - dt * 0.75) }   // 約 1.3 秒衰減完
        if NO_DRAW { return }
        step(dt)
        stepCount += 1
    }

    private func uploadParticles() {
        guard let pb = particleBuffer, !particles.isEmpty else { return }
        particles.withUnsafeBytes { raw in
            pb.contents().copyMemory(from: raw.baseAddress!, byteCount: raw.count)
        }
    }

    /// 更新位置並重建連線。連線只在遠景粒子之間，前景散景不連線（符合景深邏輯）。
    private func step(_ dt: Float) {
        let effect = config.motion.effect
        if effect != effectName { resetEffect(effect) }
        switch effect {
        case "snow": stepSnow(dt)
        case "sand": stepSand(dt)
        default:     stepFloating(dt)
        }
        stepSessionNodes()

        guard config.motion.link.enabled else {
            lastLinkCount = 0; transfers.removeAll(); focuses.removeAll()
            applySessionGlow()
            uploadParticles()
            return
        }

        switch config.motion.link.mode ?? "proximity" {
        case "traffic":   stepTraffic(dt)
        case "attention": stepAttention(dt)
        default:          stepProximity()
        }
        applySessionGlow()
        uploadParticles()
    }

    private func stepFloating(_ dt: Float) {
        let w = size.x, h = size.y
        for i in 0..<regularCount {
            particles[i].pos += particles[i].vel * dt
            if particles[i].pos.x < 0 { particles[i].pos.x += w }
            if particles[i].pos.x > w { particles[i].pos.x -= w }
            if particles[i].pos.y < 0 { particles[i].pos.y += h }
            if particles[i].pos.y > h { particles[i].pos.y -= h }
        }
    }

    private func stepProximity() {
        guard let lb = linkBuffer else { return }
        let dist = max(0.01, config.motion.link.distance)
        let op = config.motion.link.opacity
        let lp = lb.contents().bindMemory(to: LinkVertex.self, capacity: maxLinkVerts)
        var v = 0

        let m = linkIdx.count
        outer: for ii in 0..<m {
            let i = linkIdx[ii]
            let a = particles[i].pos
            let ca = particles[i].colorMix
            for jj in (ii + 1)..<max(ii + 1, m) {
                let j = linkIdx[jj]
                let d = a - particles[j].pos
                let dsq = d.x * d.x + d.y * d.y
                if dsq < linkDistSq {
                    if v + 2 > maxLinkVerts { break outer }
                    let alpha = (1.0 - (dsq.squareRoot() / dist)) * op
                    // seed 由兩端索引 hash 而來：連線每幀重建，用亂數會讓脈衝每幀跳掉
                    let hh = (i &* 73856093) ^ (j &* 19349663)
                    let seed = Float(hh & 0xFFFF) / 65535.0
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

    /// 注意力模式：一次「思考」＝ 聚焦一個節點 → 向鄰近放射查詢 → 脈衝回流 → 焦點爆亮。
    /// 三個階段都只是同一批線段的 alpha 與脈衝位置在變，沒有額外的幾何或 draw call。
    private func stepAttention(_ dt: Float) {
        guard let lb = linkBuffer else { return }
        let cfg = config.motion.link
        let maxDist = max(1, cfg.distance)
        let lifeLo = max(0.4, cfg.lifeMin ?? 1.1)
        let lifeHi = max(lifeLo + 0.1, cfg.lifeMax ?? 2.6)

        for i in particles.indices where particles[i].boost != 0 { particles[i].boost = 0 }
        focuses.removeAll { elapsed - $0.born >= $0.life }

        // MCP 觸發的「想通」：生一個目標特別多的焦點，直接跳到結論階段爆亮
        while insightFocusPending > 0, linkIdx.count >= 4 {
            insightFocusPending -= 1
            let n = linkIdx[Int.random(in: 0..<linkIdx.count)]
            let pn = particles[n].pos
            var targets: [Int] = []
            let reach = maxDist * 1.6      // 比平常遠，張得更開
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

        // 同時進行幾個思考：閒置時一兩個，忙碌時並行。依世界面積放大，每台螢幕的密度不變。
        let base = Float(cfg.targetCount ?? 60) * areaScale
        let want = max(2, Int(base / 4.5 * (0.55 + activity * 1.9)))
        var tries = 0
        // 隨機挑對象時先抽樣再篩距離；世界變大後同一距離內的候選變少，抽樣次數跟著放大
        let samples = Int(32 * max(1, areaScale))
        while focuses.count < want && tries < want * 5 && linkIdx.count >= 3 {
            tries += 1
            let n = linkIdx[Int.random(in: 0..<linkIdx.count)]
            if focuses.contains(where: { $0.node == n }) { continue }
            let pn = particles[n].pos
            var targets: [Int] = []
            let k = Int.random(in: 3...7)
            for _ in 0..<samples where targets.count < k {
                let c = linkIdx[Int.random(in: 0..<linkIdx.count)]
                if c == n || targets.contains(c) { continue }
                let d = pn - particles[c].pos
                if d.x * d.x + d.y * d.y < maxDist * maxDist { targets.append(c) }
            }
            guard targets.count >= 2 else { continue }
            focuses.append(Focus(node: n, targets: targets, born: elapsed,
                                 life: Float.random(in: lifeLo...lifeHi)))
        }

        spawnSessionFocuses(dt, maxDist: maxDist, lifeLo: lifeLo, lifeHi: lifeHi)

        let op = cfg.opacity
        let lp = lb.contents().bindMemory(to: LinkVertex.self, capacity: maxLinkVerts)
        var v = 0
        // 粒子從世界一邊繞到另一邊時，進行中的線會瞬間橫跨整個世界 —— 太長的直接不畫
        let tooFar = maxDist * 1.8

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
                let len = (d.x * d.x + d.y * d.y).squareRoot()
                if len > tooFar { continue }
                let far = min(1, len / maxDist)
                let alpha = op * fade * (1 - far * 0.5)
                // seed < 0 代表脈衝位置直接用 age 欄位，這裡塞的是程式算好的 head
                lp[v]     = LinkVertex(pos: src, alpha: alpha, colorMix: 0,
                                       t: 0, seed: -1, age: head)
                lp[v + 1] = LinkVertex(pos: dst, alpha: alpha, colorMix: 0,
                                       t: 1, seed: -1, age: head)
                v += 2
            }
        }
        lastLinkCount = v / 2
    }

    /// 流量模式：維護一池「進行中的傳輸」。每條連線 fade in → 脈衝跑完 → fade out，
    /// 然後換別的節點對。忙碌時同時進行的傳輸更多、壽命更短（跑得更急）。
    private func stepTraffic(_ dt: Float) {
        guard let lb = linkBuffer else { return }
        let cfg = config.motion.link
        let maxDist = max(1, cfg.distance)
        let lifeLo = max(0.15, cfg.lifeMin ?? 1.1)
        let lifeHi = max(lifeLo + 0.05, cfg.lifeMax ?? 2.6)
        // 忙碌時壓縮壽命，傳輸看起來更急促
        let rush = 1.0 - 0.45 * activity

        for i in particles.indices where particles[i].boost != 0 { particles[i].boost = 0 }
        transfers.removeAll { elapsed - $0.born >= $0.life }

        let base = Float(cfg.targetCount ?? 60) * areaScale
        let want = Int(base * (0.3 + activity * 1.25))
        let samples = Int(12 * max(1, areaScale))
        var guardCount = 0
        while transfers.count < want && guardCount < want * 6 && linkIdx.count >= 2 {
            guardCount += 1
            let a = linkIdx[Int.random(in: 0..<linkIdx.count)]
            let pa = particles[a].pos
            // 只挑距離內的對象，否則線會橫跨整個畫面
            var pick = -1
            for _ in 0..<samples {
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
        let lp = lb.contents().bindMemory(to: LinkVertex.self, capacity: maxLinkVerts)
        var v = 0
        let tooFar = maxDist * 1.8
        for t in transfers {
            if v + 2 > maxLinkVerts { break }
            let age = max(0, min(1, (elapsed - t.born) / max(0.01, t.life)))
            // 生滅包絡：兩端淡入淡出，中間最亮
            let fade = sin(age * Float.pi)
            let pa = particles[t.a].pos, pb = particles[t.b].pos
            let d = pa - pb
            let len = (d.x * d.x + d.y * d.y).squareRoot()
            if len > tooFar { continue }
            // 節點飄遠了就讓它更淡，避免出現突兀的長線
            let far = min(1, len / maxDist)
            let alpha = op * fade * (1 - far * 0.55)
            // seed = -1 告訴 shader：脈衝位置用 age，不要用全域時間
            lp[v]     = LinkVertex(pos: pa, alpha: alpha, colorMix: 0, t: 0, seed: -1, age: age)
            lp[v + 1] = LinkVertex(pos: pb, alpha: alpha, colorMix: 0, t: 1, seed: -1, age: age)
            v += 2
        }
        lastLinkCount = v / 2
    }
}

// MARK: - 效果：snow／sand

extension World {

    /// 某個世界 x 位置的「地面」：覆蓋這個 x 的螢幕可用區下緣（避開 Dock）。
    /// 兩台上下錯位時，取最低的那台；沒有螢幕覆蓋的空隙就用世界底部。
    fileprivate func groundY(_ x: Float) -> Float {
        var g: Float = -1
        for s in slots where x >= s.origin.x && x < s.origin.x + s.size.x {
            g = max(g, s.visibleBottom)
        }
        return g < 0 ? size.y : g
    }

    /// 某個 x 位置最高那台螢幕的上緣（雪從這裡飄進畫面）
    fileprivate func topY(_ x: Float) -> Float {
        var t: Float = .greatestFiniteMagnitude
        for s in slots where x >= s.origin.x && x < s.origin.x + s.size.x {
            t = min(t, s.origin.y)
        }
        return t == .greatestFiniteMagnitude ? 0 : t
    }

    /// 切換效果（或粒子數、螢幕排列改變）時重建效果狀態。
    fileprivate func resetEffect(_ name: String) {
        let previous = activeEffect
        effectName = name
        activeEffect = name
        // 流沙會把粒子集中成沙流與沙堆，離開時重新均勻撒開
        if previous == "sand" && name != "sand" {
            for i in 0..<regularCount {
                particles[i].pos = SIMD2(Float.random(in: 0..<size.x), Float.random(in: 0..<size.y))
                particles[i].fade = 1
            }
        }
        transfers.removeAll()
        focuses.removeAll()
        let n = regularCount
        switch name {
        case "snow":
            // 從現有位置接續，只是把速度換成落雪；太低的先送回上面
            for i in 0..<n {
                particles[i].fade = 1
                if particles[i].pos.y > groundY(particles[i].pos.x) {
                    particles[i].pos.y = Float.random(in: 0..<max(1, groundY(particles[i].pos.x)))
                }
            }
        case "sand":
            grainState = [UInt8](repeating: 2, count: n)
            grainAge = [Float](repeating: 0, count: n)
            grainBin = [Int](repeating: 0, count: n)
            grainDh = [Float](repeating: 0, count: n)
            idleGrains = Array((0..<n).reversed())
            for i in 0..<n {
                particles[i].fade = 0
                particles[i].pos = SIMD2(-100, -100)
                particles[i].vel = .zero
            }
            let nb = max(1, Int((size.x / binW).rounded(.up)))
            heights = [Float](repeating: 0, count: nb)
            groundBins = (0..<nb).map { groundY((Float($0) + 0.5) * binW) }
            streamAcc = []
            // 預先跑 25 秒，切過來時沙堆已經在、沙流已經接到底，不是空的
            for _ in 0..<500 { stepSand(0.05) }
        default:
            // 回到漂浮：速度與 fade 由種子重算
            recomputeAttributes()
            for i in 0..<n where particles[i].pos.x < 0 || particles[i].pos.y < 0 {
                particles[i].pos = SIMD2(Float.random(in: 0..<size.x), Float.random(in: 0..<size.y))
            }
        }
        if name != "sand" { grainState = [] }
    }

    // ── 下雪 ────────────────────────────────────────────────
    /// 緩降＋左右飄。前景散景雪片落得快、晃得大，做出景深。
    /// 落到螢幕可用區底部前淡出，從最上面重新飄下；x 是整片世界連續的，會飄過接縫。
    fileprivate func stepSnow(_ dt: Float) {
        let m = config.motion
        let wind = m.wind ?? 8
        let w = size.x
        let t = elapsed
        let fadeZone: Float = 110
        for i in 0..<regularCount {
            let sd = seeds[i]
            let near = particles[i].depth > 0.5
            let fall = (near ? m.bokeh.speed : m.speed) * (0.55 + 0.9 * sd.velMag)
            // 晃動：每片自己的頻率與相位；大片晃得慢而大
            let swayF = (near ? 0.25 : 0.45) + 0.5 * sd.twinkle
            let swayA: Float = (near ? 26 : 12) + 16 * sd.velMag
            let vx = wind * (near ? 1.6 : 1) + sin(t * swayF + sd.velAngle) * swayA
            particles[i].vel = SIMD2(vx, fall)
            particles[i].pos += particles[i].vel * dt
            if particles[i].pos.x < 0 { particles[i].pos.x += w }
            if particles[i].pos.x >= w { particles[i].pos.x -= w }

            let x = particles[i].pos.x
            let ground = groundY(x)
            let top = topY(x)
            if particles[i].pos.y > ground {
                // 重生在最上面、隨機 x，稍微錯開高度免得一排一起出現
                let nx = Float.random(in: 0..<w)
                particles[i].pos = SIMD2(nx, topY(nx) - Float.random(in: 4...60))
                particles[i].fade = 0
                continue
            }
            let fin = min(1, max(0, (particles[i].pos.y - top + 10) / 70))
            let fout = min(1, max(0, (ground - particles[i].pos.y) / fadeZone))
            particles[i].fade = fin * fout * fout * (3 - 2 * fout)      // smoothstep 淡出
        }
    }

    // ── 流沙 ────────────────────────────────────────────────
    /// 沙漏：每台螢幕上方有一道細沙流，沙粒加速落下、在底部堆成錐形（休止角滑落）。
    /// 整座沙堆像沙漏下半部一樣緩緩往下沉、沉到地面的沙粒淡出消失；越舊的沙粒越暗。
    /// 用「整體下沉」而不是每顆各自消失：各自消失會在沙堆底部挖出空洞、上面的沙懸空。
    /// 忙碌時沙流變快。
    fileprivate func stepSand(_ dt: Float) {
        let m = config.motion
        let n = regularCount
        guard grainState.count == n, !heights.isEmpty else { return }
        let g: Float = 620
        let vmax = max(60, m.speed)
        let perScreen = max(1, min(6, m.streams ?? 1))

        // 沙流位置：每台螢幕均分，緩慢漂移讓沙堆堆成小丘而不是一根針
        var streams: [SIMD2<Float>] = []
        for (si, s) in slots.enumerated() {
            for k in 0..<perScreen {
                let id = Float(si * perScreen + k)
                let base = s.origin.x + s.size.x * (Float(k) + 0.5) / Float(perScreen)
                let wander = sin(elapsed * 0.045 + id * 1.9) * s.size.x * 0.03
                           + sin(elapsed * 0.17 + id * 0.7) * 5
                streams.append(SIMD2(base + wander, s.visibleTop - 6))
            }
        }
        if streamAcc.count != streams.count { streamAcc = [Float](repeating: 0, count: streams.count) }

        // 放出新沙粒（活動度越高流得越快）
        let rate: Float = 130 * (0.75 + activity * 0.9)
        let bias = max(0.1, m.sizeBias ?? 2.2)
        for si in streams.indices {
            streamAcc[si] += rate * dt
            while streamAcc[si] >= 1 {
                streamAcc[si] -= 1
                guard let i = takeGrain() else { break }
                let sd = seeds[i]
                // 高斯抖動讓沙流成束而不是一條直線
                let gx = (Float.random(in: -1...1) + Float.random(in: -1...1) + Float.random(in: -1...1)) * 1.3
                particles[i].pos = SIMD2(streams[si].x + gx, streams[si].y - Float.random(in: 0...8))
                particles[i].vel = SIMD2(gx * 1.4, Float.random(in: 10...40))
                particles[i].size = m.sizeMin + (max(m.sizeMin, m.sizeMax) - m.sizeMin) * pow(sd.size, bias)
                particles[i].fade = 0
                particles[i].depth = 0
                particles[i].boost = 0
                grainState[i] = 0
                grainAge[i] = 0
            }
        }

        // 下沉速度控制：閒置池低於 6% 就沉快一點，高於 18% 就沉慢一點
        let idleFrac = Float(idleGrains.count) / Float(max(1, n))
        if idleFrac < 0.06 { sinkSpeed = min(14, sinkSpeed * (1 + 1.2 * dt)) }
        else if idleFrac > 0.18 { sinkSpeed = max(0.4, sinkSpeed * (1 - 0.8 * dt)) }
        let sink = sinkSpeed * dt
        for b in heights.indices where heights[b] > 0 { heights[b] = max(0, heights[b] - sink) }

        for i in 0..<n {
            switch grainState[i] {
            case 0:   // 下落
                particles[i].vel.y = min(vmax, particles[i].vel.y + g * dt)
                particles[i].pos += particles[i].vel * dt
                grainAge[i] += dt
                particles[i].fade = min(1, grainAge[i] * 5)
                var b = Int(particles[i].pos.x / binW)
                if b < 0 || b >= heights.count { releaseGrain(i); continue }
                let surface = groundBins[b] - heights[b]
                if particles[i].pos.y >= surface {
                    // 休止角：比鄰格高太多就往低的那邊滑，最多滑 60 格
                    let repose: Float = binW * 0.62       // 約 32° 的休止角，沙丘比較緩
                    for _ in 0..<60 {
                        let h = groundBins[b] - heights[b]      // 目前表面 y（越小越高）
                        let lh = b > 0 ? groundBins[b - 1] - heights[b - 1] : -1e9
                        let rh = b + 1 < heights.count ? groundBins[b + 1] - heights[b + 1] : -1e9
                        let canL = lh - h > repose, canR = rh - h > repose
                        if canL && canR { b += Bool.random() ? -1 : 1 }
                        else if canL { b -= 1 }
                        else if canR { b += 1 }
                        else { break }
                    }
                    let sz = particles[i].size
                    let dh = sz * sz * 0.32 / binW        // 比幾何面積小：沙粒彼此交疊，沙堆看起來是實的
                    heights[b] = min(heights[b] + dh, 260)
                    grainDh[i] = dh
                    grainBin[i] = b
                    grainState[i] = 1
                    grainAge[i] = 0
                    particles[i].pos = SIMD2((Float(b) + Float.random(in: 0.1...0.9)) * binW,
                                             groundBins[b] - heights[b] + dh * 0.5)
                    particles[i].vel = .zero
                }
            case 1:   // 堆積：跟著沙堆一起下沉，越舊越暗，沉到地面淡出
                grainAge[i] += dt
                particles[i].pos.y += sink
                let ground = groundBins[grainBin[i]]
                let below = particles[i].pos.y - ground
                if below > 3 { releaseGrain(i); continue }
                let edge = min(1, max(0, (3 - below) / 9))
                particles[i].fade = edge * (0.38 + 0.62 * exp(-grainAge[i] / 9))
            default:
                break
            }
        }
    }

    private func takeGrain() -> Int? {
        if let i = idleGrains.popLast() { return i }
        // 池子空了：回收最老（埋最深）的堆積沙粒。下沉控制正常時很少走到這裡。
        var best = -1
        var bestAge: Float = -1
        for i in 0..<grainState.count where grainState[i] == 1 && grainAge[i] > bestAge {
            bestAge = grainAge[i]; best = i
        }
        return best >= 0 ? best : nil
    }

    private func releaseGrain(_ i: Int) {
        grainState[i] = 2
        particles[i].fade = 0
        particles[i].pos = SIMD2(-100, -100)
        particles[i].vel = .zero
        idleGrains.append(i)
    }
}

// MARK: - Claude session 光點

extension World {

    /// 更新 session 清單。沿用既有順序（同一個 session 永遠在同一個位置），新的接在後面。
    func setSessions(_ list: [SessionInfo]) {
        let enabled = config.motion.sessions?.enabled ?? false
        let incoming = enabled ? list : []
        if incoming == sessions { return }
        // 依 id 保留原本的順序
        var next: [SessionInfo] = []
        for s in sessions { if let n = incoming.first(where: { $0.id == s.id }) { next.append(n) } }
        for n in incoming where !next.contains(where: { $0.id == n.id }) { next.append(n) }
        let sameIDs = next.map(\.id) == sessions.map(\.id)
        if sameIDs {
            sessions = next             // 只有忙碌狀態變了
            return
        }
        // 成員變了：換掉尾端的光點粒子，清掉牽涉到 session 的思考
        let reg = regularCount
        focuses.removeAll { $0.node >= reg || $0.targets.contains { $0 >= reg } }
        transfers.removeAll { $0.a >= reg || $0.b >= reg }
        particles.removeLast(sessions.count)
        sessions = next
        for _ in sessions { particles.append(blankParticle(at: .zero)) }
        if isNode.count != particles.count {
            isNode = Array(isNode.prefix(reg)) + [Bool](repeating: true, count: sessions.count)
        }
        ensureBuffers()
        placeSessionAnchors()
        styleSessionParticles()
        rebuildLinkIndex()
        sessionSpawnAcc = [Float](repeating: 0, count: sessions.count)
    }

    /// session 位置由 id 雜湊決定：穩定、分散，而且落在某台螢幕的可用區內（避開選單列與 Dock）。
    fileprivate func placeSessionAnchors() {
        guard !slots.isEmpty else { sessionAnchors = []; return }
        sessionAnchors = sessions.map { s in
            var h: UInt64 = 1469598103934665603          // FNV-1a
            for b in s.id.utf8 { h = (h ^ UInt64(b)) &* 1099511628211 }
            let u = Float(h & 0xFFFF) / 65535
            let v = Float((h >> 16) & 0xFFFF) / 65535
            let pick = Int((h >> 32) % UInt64(slots.count))
            let sl = slots[pick]
            let top = sl.visibleTop + (sl.visibleBottom - sl.visibleTop) * 0.14
            let bottom = sl.visibleBottom - (sl.visibleBottom - sl.visibleTop) * 0.18
            return SIMD2(sl.origin.x + sl.size.x * (0.10 + 0.80 * u),
                         top + (bottom - top) * v)
        }
    }

    fileprivate func styleSessionParticles() {
        let reg = regularCount
        let m = config.motion
        let sz = m.sessions?.size ?? max(m.nodeSizeMax, 8) * 1.15
        for k in 0..<sessions.count {
            let i = reg + k
            guard i < particles.count else { break }
            particles[i].size = sz
            particles[i].depth = -1          // shader 看到 depth < 0 會多畫一道外環
            particles[i].colorMix = 0.5
            particles[i].twinkle = 0.35
            particles[i].fade = 1
            particles[i].vel = .zero
            if i < isNode.count { isNode[i] = true }
        }
    }

    /// 光點在錨點附近極慢地繞小圈，看得出是活的但位置穩定
    fileprivate func stepSessionNodes() {
        let reg = regularCount
        if sessionAnchors.count != sessions.count { placeSessionAnchors() }
        for k in 0..<sessions.count {
            let i = reg + k
            let a = sessionAnchors[k]
            let ph = Float(k) * 2.1
            particles[i].boost = 0          // 每幀重來，attention 與忙碌光暈再往上疊
            particles[i].pos = a + SIMD2(cos(elapsed * 0.11 + ph), sin(elapsed * 0.08 + ph)) * 7
        }
    }

    /// 忙碌的 session 持續微微發亮（在 attention 爆亮之外的常駐呼吸）
    fileprivate func applySessionGlow() {
        let reg = regularCount
        for k in 0..<sessions.count {
            let i = reg + k
            let base: Float = sessions[k].busy
                ? 0.35 + 0.25 * sin(elapsed * 2.6 + Float(k))
                : 0.08
            particles[i].boost = max(particles[i].boost, base)
        }
    }

    /// attention 模式：session 光點自己發起思考。忙碌時頻繁、閒置時偶爾。
    fileprivate func spawnSessionFocuses(_ dt: Float, maxDist: Float, lifeLo: Float, lifeHi: Float) {
        guard !sessions.isEmpty, linkIdx.count >= 4 else { return }
        if sessionSpawnAcc.count != sessions.count {
            sessionSpawnAcc = [Float](repeating: 0, count: sessions.count)
        }
        let reg = regularCount
        let reach = maxDist * 1.4
        for k in 0..<sessions.count {
            let rate: Float = sessions[k].busy ? 1.3 : 0.12
            sessionSpawnAcc[k] += rate * dt
            guard sessionSpawnAcc[k] >= 1 else { continue }
            sessionSpawnAcc[k] -= 1
            let n = reg + k
            if focuses.contains(where: { $0.node == n && elapsed - $0.born < $0.life * 0.5 }) { continue }
            let pn = particles[n].pos
            var targets: [Int] = []
            let want = sessions[k].busy ? Int.random(in: 5...9) : Int.random(in: 3...5)
            for _ in 0..<(64 * max(1, Int(areaScale))) where targets.count < want {
                let c = linkIdx[Int.random(in: 0..<linkIdx.count)]
                if c == n || targets.contains(c) { continue }
                let d = pn - particles[c].pos
                if d.x * d.x + d.y * d.y < reach * reach { targets.append(c) }
            }
            guard targets.count >= 2 else { continue }
            focuses.append(Focus(node: n, targets: targets, born: elapsed,
                                 life: Float.random(in: lifeLo...lifeHi)))
        }
    }
}
