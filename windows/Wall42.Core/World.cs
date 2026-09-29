using System.Numerics;
using System.Runtime.InteropServices;

namespace Wall42;

/// One screen's place in the world. World units are Mac "points": physical pixels ÷ the primary
/// screen's scale, so the preset sizes/speeds/distances look the same as on the Mac. y points down,
/// origin = top-left of the union of all monitors.
public record struct ScreenSlot(Vector2 Origin, Vector2 Size, float VisibleTop, float VisibleBottom);

/// GPU particle; layout must match the HLSL `Particle` (11 floats, stride 44).
[StructLayout(LayoutKind.Sequential)]
public struct Particle
{
    public Vector2 Pos, Vel;
    public float Size, Phase;
    public float ColorMix;     // 0 = colorA, 1 = colorB
    public float Depth;        // 0 = far (sharp dot), 1 = near (bokeh), -1 = session point (ring)
    public float Twinkle;      // per-particle twinkle speed
    public float Boost;        // temporary brighten/enlarge (attention focus)
    public float Fade;         // 0..1 (snow landing, sand; 1 when floating)
}

/// GPU link vertex; layout must match the HLSL `LinkVertex` (7 floats, stride 28).
[StructLayout(LayoutKind.Sequential)]
public struct LinkVertex
{
    public Vector2 Pos;
    public float Alpha, ColorMix;
    public float T;            // 0 = start, 1 = end; interpolated in the pixel shader
    public float Seed;         // proximity: pulse phase; < 0 = the pulse position is Age
    public float Age;
    public LinkVertex(Vector2 pos, float alpha, float colorMix, float t, float seed, float age)
    { Pos = pos; Alpha = alpha; ColorMix = colorMix; T = t; Seed = seed; Age = age; }
}

/// Constant buffer; layout must match the HLSL cbuffer (packed so nothing straddles 16 bytes, 176 bytes).
[StructLayout(LayoutKind.Sequential)]
public struct Uniforms
{
    public Vector2 Viewport; public float Time, Brightness;
    public Vector4 ColorA, ColorB, BgCenter, BgEdge;
    public float BgRadius, LinkBoost, BreathSpeed, TwinkleAmount;
    public float BokehDimming, PulseSpeed, PulseStrength, PulseWidth;
    public float Activity, Glow, Insight, Softness;
    public Vector4 LinkColor;
    public Vector2 ViewOrigin, ViewSize;
    public Vector2 WorldSize; public float PxScale, BgMode;
}

/// The whole sky is ONE simulation (port of World.swift). Every monitor's renderer only looks at its
/// part, so particles, links, pulses and attention foci cross monitor seams and the step runs once.
/// Density: particleCount means "per primary-screen area"; the real count scales with the world area.
/// Time and randomness are injected so tests (and WALL42_SNAPSHOT) are deterministic.
public sealed partial class World
{
    readonly Func<double> clock;
    readonly Random rng;
    public Config Config { get; private set; }

    // ── geometry ──
    public Vector2 Size { get; private set; } = new(1920, 1080);
    ScreenSlot[] slots = Array.Empty<ScreenSlot>();
    public IReadOnlyList<ScreenSlot> Slots => slots;
    /// world area ÷ primary-screen area: 1 for one screen, 2 for two side by side
    public float AreaScale { get; private set; } = 1;

    // ── particles ──
    readonly List<Particle> ps = new();
    /// Fixed random factors per particle. Attributes are *derived* from them, never re-rolled, so moving a
    /// slider rescales particles smoothly instead of reshuffling the screen.
    struct Seed { public float Size, Node, Bokeh, Color, VelAngle, VelMag, Twinkle; }
    readonly List<Seed> seeds = new();
    readonly List<bool> isNode = new();
    readonly List<int> linkIdx = new();       // particles that take part in links (pre-filtered)

    record struct Transfer(int A, int B, float Born, float Life);
    readonly List<Transfer> transfers = new();
    sealed record Focus(int Node, List<int> Targets, float Born, float Life);
    readonly List<Focus> focuses = new();

    // ── Claude session points: permanent nodes at the end of the particle list ──
    public readonly record struct SessionInfo(string Id, bool Busy);
    readonly List<SessionInfo> sessions = new();
    public IReadOnlyList<SessionInfo> Sessions => sessions;
    int RegularCount => ps.Count - sessions.Count;
    Vector2[] sessionAnchors = Array.Empty<Vector2>();
    float[] sessionSpawnAcc = Array.Empty<float>();

    // ── links ──
    LinkVertex[] links = new LinkVertex[2];
    float linkDistSq;

    // ── time ──
    double lastStep;
    public float Elapsed { get; private set; }
    /// Minimum gap between steps, set from the fastest drawing monitor's fps. Two monitors wake at
    /// different vsync phases; without this the sim would step twice per frame.
    public double StepInterval { get; set; } = 1.0 / 30.0;
    /// WALL42_NO_DRAW: the clock runs, the sim does not (framework baseline measurement)
    public bool NoStep { get; set; }

    // ── state for renderers and status ──
    public int LastLinkCount { get; private set; }
    public int StepCount { get; private set; }
    public int DrawCount => ps.Count;
    /// 0 = idle, 1 = full compute. Set from the system load or MCP.
    public float Activity { get; set; }
    public float Insight { get; private set; }
    int insightFocusPending;

    public ReadOnlySpan<Particle> Particles => CollectionsMarshal.AsSpan(ps);
    public ReadOnlySpan<LinkVertex> Links => links.AsSpan(0, LastLinkCount * 2);
    public int ParticleCapacityHint => ps.Count;
    Span<Particle> P => CollectionsMarshal.AsSpan(ps);

    public World(Config config, Func<double>? clock = null, Random? rng = null)
    {
        Config = config;
        this.clock = clock ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
        this.rng = rng ?? new Random();
        linkDistSq = config.Motion.Link.Distance * config.Motion.Link.Distance;
        lastStep = this.clock();
    }

    float R01() => rng.NextSingle();
    float Range(float a, float b) => a + (b - a) * rng.NextSingle();
    int Pick(int n) => rng.Next(n);
    int IntIn(int lo, int hiInclusive) => rng.Next(lo, hiInclusive + 1);

    // ── geometry ────────────────────────────────────────────────────

    /// Builds or rebuilds the world from the monitor layout. Existing particles are remapped
    /// proportionally instead of re-seeded, so plugging a monitor in doesn't restart the picture.
    public void SetLayout(IReadOnlyList<ScreenSlot> newSlots, float mainArea)
    {
        float maxX = 1, maxY = 1;
        foreach (var s in newSlots) { maxX = MathF.Max(maxX, s.Origin.X + s.Size.X); maxY = MathF.Max(maxY, s.Origin.Y + s.Size.Y); }
        var old = Size;
        Size = new(maxX, maxY);
        slots = newSlots.ToArray();
        AreaScale = MathF.Max(0.25f, Size.X * Size.Y / MathF.Max(1, mainArea));

        if (ps.Count > 0 && old.X > 0 && old.Y > 0)
        {
            float sx = Size.X / old.X, sy = Size.Y / old.Y;
            var p = P;
            for (int i = 0; i < RegularCount; i++) { p[i].Pos.X *= sx; p[i].Pos.Y *= sy; }
            transfers.Clear();
            focuses.Clear();
        }
        ResizeParticles();
        effectName = "";                          // ground, sand streams and session anchors depend on the layout
        PlaceSessionAnchors();
    }

    int TargetCount => Math.Max(1, (int)MathF.Round(Math.Max(1, Config.Motion.ParticleCount) * AreaScale));

    /// Matches the particle count to the target: extra ones are cut from the end, missing ones added at random.
    void ResizeParticles()
    {
        int want = TargetCount;
        // session points live at the end: take them off, fix the regular ones, put them back
        var tail = ps.GetRange(ps.Count - sessions.Count, sessions.Count);
        ps.RemoveRange(ps.Count - sessions.Count, sessions.Count);
        if (ps.Count == 0) SeedParticles(want);
        else if (ps.Count > want)
        {
            ps.RemoveRange(want, ps.Count - want);
            seeds.RemoveRange(want, seeds.Count - want);
            transfers.Clear(); focuses.Clear();
        }
        else if (ps.Count < want)
            for (int i = ps.Count; i < want; i++)
            {
                seeds.Add(RandomSeed());
                ps.Add(Blank(new(Range(0, Size.X), Range(0, Size.Y))));
            }
        if (ps.Count != grainState.Length) effectName = "";     // effect state must be rebuilt
        ps.AddRange(tail);
        EnsureBuffers();
        RecomputeAttributes();
    }

    Seed RandomSeed() => new()
    {
        Size = R01(), Node = R01(), Bokeh = R01(), Color = R01(),
        VelAngle = Range(0, 2 * MathF.PI), VelMag = Range(0.35f, 1), Twinkle = R01(),
    };

    Particle Blank(Vector2 p) => new() { Pos = p, Phase = Range(0, 2 * MathF.PI), Twinkle = 1, Fade = 1 };

    void SeedParticles(int n)
    {
        // pure random clumps and leaves holes: jittered grid, one per cell with a random offset
        float aspect = Size.X / Size.Y;
        int cols = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(n * aspect)));
        int rows = Math.Max(1, (int)MathF.Ceiling(n / (float)cols));
        float cw = Size.X / cols, ch = Size.Y / rows;
        seeds.Clear();
        for (int i = 0; i < n; i++) seeds.Add(RandomSeed());
        ps.Clear();
        for (int i = 0; i < n; i++)
        {
            float gx = i % cols, gy = i / cols;
            float jx = (gx + Range(0.15f, 0.85f)) * cw, jy = (gy + Range(0.15f, 0.85f)) * ch;
            ps.Add(Blank(new(jx % Size.X, jy % Size.Y)));
        }
    }

    /// Link vertex array grows only when too small. At most 24 links per particle.
    void EnsureBuffers()
    {
        int n = Math.Max(1, ps.Count);
        long maxLinks = Math.Min((long)n * (n - 1) / 2, (long)n * 24);
        int want = (int)Math.Max(maxLinks * 2, 2);
        if (want > links.Length) links = new LinkVertex[want];
    }

    /// Size/speed/hue/depth from the fixed seeds. Same seeds + same config = same result, so it is
    /// safe to call repeatedly: dragging a slider changes the picture continuously, nothing re-rolls.
    void RecomputeAttributes()
    {
        var m = Config.Motion;
        if (isNode.Count != ps.Count) { isNode.Clear(); isNode.AddRange(Enumerable.Repeat(false, ps.Count)); }
        if (seeds.Count != RegularCount) return;

        float bias = MathF.Max(0.1f, m.SizeBias);
        float tv = MathF.Max(0, m.TwinkleVariance);
        float bkMin = m.Bokeh.SizeMin, bkMax = MathF.Max(m.Bokeh.SizeMin, m.Bokeh.SizeMax);
        float ndMin = m.NodeSizeMin, ndMax = MathF.Max(m.NodeSizeMin, m.NodeSizeMax);
        float pMin = m.SizeMin, pMax = MathF.Max(m.SizeMin, m.SizeMax);
        var p = P;
        for (int i = 0; i < RegularCount; i++)
        {
            var sd = seeds[i];
            // compare the fixed seed instead of re-rolling: changing a ratio only flips particles near the edge
            bool bokeh = sd.Bokeh < m.Bokeh.Ratio;
            bool node = !bokeh && sd.Node < m.NodeRatio;
            isNode[i] = node;
            float speed = MathF.Max(0.01f, bokeh ? m.Bokeh.Speed : m.Speed) * sd.VelMag;
            p[i].Vel = new(MathF.Cos(sd.VelAngle) * speed, MathF.Sin(sd.VelAngle) * speed);
            p[i].Size = bokeh ? bkMin + (bkMax - bkMin) * sd.Size
                : node ? ndMin + (ndMax - ndMin) * sd.Size
                // power law: many tiny dots, few bright ones; uniform gives too many medium ones
                : pMin + (pMax - pMin) * MathF.Pow(sd.Size, bias);
            // hues lean to the two poles, fewer in-between colours, so the two-colour contrast shows
            p[i].ColorMix = sd.Color < 0.5f ? sd.Color * 0.44f : 0.78f + (sd.Color - 0.5f) * 0.44f;
            p[i].Depth = bokeh ? 1 : 0;
            p[i].Twinkle = MathF.Max(0.15f, 1 - tv) + 2 * tv * sd.Twinkle;
            if (effectName != "snow" && effectName != "sand") p[i].Fade = 1;
        }
        StyleSessionParticles();
        RebuildLinkIndex();
    }

    void RebuildLinkIndex()
    {
        bool onlyNodes = Config.Motion.Link.OnlyNodes;
        linkIdx.Clear();
        var p = P;
        for (int i = 0; i < p.Length; i++)
            if (p[i].Depth <= 0.5f && (!onlyNodes || isNode[i])) linkIdx.Add(i);
    }

    // ── config ──────────────────────────────────────────────────────

    /// Hot reload. Only a changed particle count adds/removes particles; everything else applies in place.
    /// Pass a NEW Config instance (the old one is what the change is compared against).
    public void Apply(Config next)
    {
        MotionConfig m = Config.Motion, nm = next.Motion;
        bool countChanged = nm.ParticleCount != m.ParticleCount;
        bool shapeChanged = nm.SizeMin != m.SizeMin || nm.SizeMax != m.SizeMax
            || nm.Speed != m.Speed || nm.NodeRatio != m.NodeRatio
            || nm.NodeSizeMin != m.NodeSizeMin || nm.NodeSizeMax != m.NodeSizeMax
            || nm.Bokeh.Ratio != m.Bokeh.Ratio || nm.Bokeh.SizeMin != m.Bokeh.SizeMin
            || nm.Bokeh.SizeMax != m.Bokeh.SizeMax || nm.Bokeh.Speed != m.Bokeh.Speed
            || nm.SizeBias != m.SizeBias || nm.TwinkleVariance != m.TwinkleVariance
            || nm.Link.OnlyNodes != m.Link.OnlyNodes;
        bool wasSessions = m.Sessions?.Enabled ?? false, nowSessions = nm.Sessions?.Enabled ?? false;
        bool restyle = nm.NodeSizeMax != m.NodeSizeMax || nm.Sessions?.Size != m.Sessions?.Size;

        Config = next;
        linkDistSq = nm.Link.Distance * nm.Link.Distance;
        if (nm.Effect != m.Effect) effectName = "";
        if (restyle) StyleSessionParticles();
        if (wasSessions != nowSessions && !nowSessions) SetSessions(Array.Empty<SessionInfo>());

        if (countChanged) { transfers.Clear(); focuses.Clear(); ResizeParticles(); }
        else if (shapeChanged) RecomputeAttributes();     // fixed seeds: continuous, in-flight transfers can stay
        // colours, brightness, link opacity, breath speed only go through the uniforms
    }

    /// The AI solved something: a global flash; attention mode also bursts one focus per screen-sized area.
    public void TriggerInsight(float strength)
    {
        Insight = MathF.Max(Insight, Math.Clamp(strength, 0, 1.5f));
        insightFocusPending = Math.Max(1, (int)MathF.Round(AreaScale));
    }

    /// After a full pause: the next dt counts from now, particles don't jump by the paused time.
    public void ResetClock() => lastStep = clock();

    public Uniforms UniformsFor(Vector2 viewport, Vector2 origin, Vector2 viewSize, float pxScale)
    {
        var m = Config.Motion; var b = Config.Background;
        bool solid = b.Mode == "solid";
        return new Uniforms
        {
            Viewport = viewport, Time = Elapsed, Brightness = m.Brightness,
            ColorA = Color.Hex(m.ColorA), ColorB = Color.Hex(m.ColorB),
            BgCenter = Color.Hex(solid ? b.SolidColor : b.CenterColor),
            BgEdge = Color.Hex(solid ? b.SolidColor : b.EdgeColor),
            BgRadius = MathF.Max(0.01f, b.Radius),
            LinkBoost = m.Link.Boost,
            BreathSpeed = m.BreathSpeed * (1 + Activity * 0.8f),
            TwinkleAmount = Math.Clamp(m.TwinkleAmount, 0, 1),
            BokehDimming = m.Bokeh.Dimming,
            // busier = faster, brighter pulses: that is where the "computing" feel comes from
            PulseSpeed = m.Pulse.Speed * (0.45f + Activity * 1.75f),
            PulseStrength = m.Pulse.Strength * (0.25f + Activity * 1.15f),
            PulseWidth = m.Pulse.Width,
            Activity = Activity,
            Glow = Math.Clamp(m.Glow, 0, 1),
            Insight = Insight,
            // links are always one colour; unset = midpoint of the two poles
            LinkColor = m.Link.Color != null ? Color.Hex(m.Link.Color) : (Color.Hex(m.ColorA) + Color.Hex(m.ColorB)) * 0.5f,
            ViewOrigin = origin, ViewSize = viewSize, PxScale = pxScale, WorldSize = Size,
            BgMode = b.Mode == "vertical" ? 1 : 0,
            Softness = Math.Clamp(m.Softness, 0, 1),
        };
    }

    // ── simulation ──────────────────────────────────────────────────

    /// Called before each monitor draws; skipped when the last step was too recent (another monitor, same vsync).
    public bool Advance()
    {
        double now = clock(), gap = now - lastStep;
        if (gap < StepInterval * 0.75) return false;
        lastStep = now;
        StepBy((float)Math.Min(gap, 0.1));        // clamp: coming back from a pause must not teleport particles
        return true;
    }

    /// One fixed step (tests, snapshots).
    public void StepBy(float dt)
    {
        Elapsed += dt;
        if (Insight > 0) Insight = MathF.Max(0, Insight - dt * 0.75f);     // ~1.3 s decay
        if (NoStep) return;
        Step(dt);
        StepCount++;
    }

    /// Positions, then links. Links only between far particles; the near bokeh never links (depth logic).
    void Step(float dt)
    {
        var effect = Config.Motion.Effect;
        if (effect != effectName) ResetEffect(effect);
        switch (effect)
        {
            case "snow": StepSnow(dt); break;
            case "sand": StepSand(dt); break;
            default: StepFloating(dt); break;
        }
        StepSessionNodes();
        if (!Config.Motion.Link.Enabled)
        {
            LastLinkCount = 0; transfers.Clear(); focuses.Clear();
            ApplySessionGlow();
            return;
        }
        switch (Config.Motion.Link.Mode)
        {
            case "traffic": StepTraffic(dt); break;
            case "attention": StepAttention(dt); break;
            default: StepProximity(); break;
        }
        ApplySessionGlow();
    }

    void StepFloating(float dt)
    {
        float w = Size.X, h = Size.Y;
        var p = P;
        for (int i = 0; i < RegularCount; i++)
        {
            p[i].Pos += p[i].Vel * dt;
            if (p[i].Pos.X < 0) p[i].Pos.X += w;
            if (p[i].Pos.X > w) p[i].Pos.X -= w;
            if (p[i].Pos.Y < 0) p[i].Pos.Y += h;
            if (p[i].Pos.Y > h) p[i].Pos.Y -= h;
        }
    }

    void StepProximity()
    {
        float dist = MathF.Max(0.01f, Config.Motion.Link.Distance), op = Config.Motion.Link.Opacity;
        var p = P;
        int v = 0, m = linkIdx.Count;
        for (int ii = 0; ii < m; ii++)
        {
            int i = linkIdx[ii];
            var a = p[i].Pos;
            for (int jj = ii + 1; jj < m; jj++)
            {
                int j = linkIdx[jj];
                var d = a - p[j].Pos;
                float dsq = d.X * d.X + d.Y * d.Y;
                if (dsq >= linkDistSq) continue;
                if (v + 2 > links.Length) goto done;
                float alpha = (1 - MathF.Sqrt(dsq) / dist) * op;
                // seed hashed from both ends: links are rebuilt every frame, a random seed would make pulses jump
                int hh = unchecked(i * 73856093 ^ j * 19349663);
                float seed = (hh & 0xFFFF) / 65535f;
                links[v] = new(a, alpha, p[i].ColorMix, 0, seed, 0);
                links[v + 1] = new(p[j].Pos, alpha, p[j].ColorMix, 1, seed, 0);
                v += 2;
            }
        }
    done:
        LastLinkCount = v / 2;
    }

    /// Attention: one "thought" = focus a node → query neighbours → answers flow back → the focus flares.
    /// All three phases are just alpha and pulse position on the same segments: no extra geometry.
    void StepAttention(float dt)
    {
        var cfg = Config.Motion.Link;
        float maxDist = MathF.Max(1, cfg.Distance);
        float lifeLo = MathF.Max(0.4f, cfg.LifeMin), lifeHi = MathF.Max(lifeLo + 0.1f, cfg.LifeMax);
        var p = P;
        for (int i = 0; i < p.Length; i++) if (p[i].Boost != 0) p[i].Boost = 0;
        focuses.RemoveAll(f => Elapsed - f.Born >= f.Life);

        // MCP "insight": a focus with many targets, born straight into its conclusion
        while (insightFocusPending > 0 && linkIdx.Count >= 4)
        {
            insightFocusPending--;
            int n = linkIdx[Pick(linkIdx.Count)];
            var pn = p[n].Pos;
            var targets = new List<int>();
            float reach = maxDist * 1.6f;
            for (int k = 0; k < 60 && targets.Count < 10; k++)
            {
                int c = linkIdx[Pick(linkIdx.Count)];
                if (c == n || targets.Contains(c)) continue;
                if (Vector2.DistanceSquared(pn, p[c].Pos) < reach * reach) targets.Add(c);
            }
            if (targets.Count >= 3) { const float life = 1.6f; focuses.Add(new(n, targets, Elapsed - life * 0.8f, life)); }
        }

        // parallel thoughts: one or two when idle, many when busy; scaled by area so density per screen holds
        float @base = cfg.TargetCount * AreaScale;
        int want = Math.Max(2, (int)(@base / 4.5f * (0.55f + Activity * 1.9f)));
        int tries = 0;
        int samples = (int)(32 * MathF.Max(1, AreaScale));    // bigger world, fewer candidates in range: sample more
        while (focuses.Count < want && tries < want * 5 && linkIdx.Count >= 3)
        {
            tries++;
            int n = linkIdx[Pick(linkIdx.Count)];
            if (focuses.Exists(f => f.Node == n)) continue;
            var pn = p[n].Pos;
            var targets = new List<int>();
            int k = IntIn(3, 7);
            for (int s = 0; s < samples && targets.Count < k; s++)
            {
                int c = linkIdx[Pick(linkIdx.Count)];
                if (c == n || targets.Contains(c)) continue;
                if (Vector2.DistanceSquared(pn, p[c].Pos) < maxDist * maxDist) targets.Add(c);
            }
            if (targets.Count < 2) continue;
            focuses.Add(new(n, targets, Elapsed, Range(lifeLo, lifeHi)));
        }

        SpawnSessionFocuses(dt, maxDist, lifeLo, lifeHi);

        float op = cfg.Opacity, tooFar = maxDist * 1.8f;     // wrapped-around particles would draw world-wide lines
        int v = 0;
        foreach (var f in focuses)
        {
            float age = Math.Clamp((Elapsed - f.Born) / MathF.Max(0.01f, f.Life), 0, 1);
            float head, fade;
            if (age < 0.45f) { float t = age / 0.45f; head = t; fade = MathF.Min(1, t * 4.5f); }       // query
            else if (age < 0.80f) { float t = (age - 0.45f) / 0.35f; head = 1 - t; fade = 1; }          // answers
            else
            {
                float t = (age - 0.80f) / 0.20f; head = 0; fade = 1 - t;                                  // conclusion
                p[f.Node].Boost = MathF.Max(p[f.Node].Boost, MathF.Sin(t * MathF.PI) * 2.2f);
            }
            var src = p[f.Node].Pos;
            foreach (var ti in f.Targets)
            {
                if (v + 2 > links.Length) break;
                var dst = p[ti].Pos;
                float len = Vector2.Distance(src, dst);
                if (len > tooFar) continue;
                float far = MathF.Min(1, len / maxDist);
                float alpha = op * fade * (1 - far * 0.5f);
                // seed < 0: the shader takes the pulse position from age, which here is the computed head
                links[v] = new(src, alpha, 0, 0, -1, head);
                links[v + 1] = new(dst, alpha, 0, 1, -1, head);
                v += 2;
            }
        }
        LastLinkCount = v / 2;
    }

    /// Traffic: a pool of transfers; each fades in → one pulse runs → fades out, then new pairs.
    /// Busy = more transfers in parallel and shorter lives (more hurried).
    void StepTraffic(float dt)
    {
        var cfg = Config.Motion.Link;
        float maxDist = MathF.Max(1, cfg.Distance);
        float lifeLo = MathF.Max(0.15f, cfg.LifeMin), lifeHi = MathF.Max(lifeLo + 0.05f, cfg.LifeMax);
        float rush = 1 - 0.45f * Activity;
        var p = P;
        for (int i = 0; i < p.Length; i++) if (p[i].Boost != 0) p[i].Boost = 0;
        transfers.RemoveAll(t => Elapsed - t.Born >= t.Life);

        float @base = cfg.TargetCount * AreaScale;
        int want = (int)(@base * (0.3f + Activity * 1.25f));
        int samples = (int)(12 * MathF.Max(1, AreaScale));
        int guard = 0;
        while (transfers.Count < want && guard < want * 6 && linkIdx.Count >= 2)
        {
            guard++;
            int a = linkIdx[Pick(linkIdx.Count)];
            var pa = p[a].Pos;
            int pick = -1;                         // only partners within reach, or lines cross the whole screen
            for (int s = 0; s < samples; s++)
            {
                int c = linkIdx[Pick(linkIdx.Count)];
                if (c == a) continue;
                if (Vector2.DistanceSquared(pa, p[c].Pos) < maxDist * maxDist) { pick = c; break; }
            }
            if (pick < 0) continue;
            int b = pick;
            if (transfers.Exists(t => (t.A == a && t.B == b) || (t.A == b && t.B == a))) continue;
            transfers.Add(new(a, b, Elapsed, Range(lifeLo, lifeHi) * rush));
        }

        float op = cfg.Opacity, tooFar = maxDist * 1.8f;
        int v = 0;
        foreach (var t in transfers)
        {
            if (v + 2 > links.Length) break;
            float age = Math.Clamp((Elapsed - t.Born) / MathF.Max(0.01f, t.Life), 0, 1);
            float fade = MathF.Sin(age * MathF.PI);                 // fade in/out, brightest mid-life
            Vector2 pa = p[t.A].Pos, pb = p[t.B].Pos;
            float len = Vector2.Distance(pa, pb);
            if (len > tooFar) continue;
            float far = MathF.Min(1, len / maxDist);                // drifting apart = fainter, no jarring long lines
            float alpha = op * fade * (1 - far * 0.55f);
            links[v] = new(pa, alpha, 0, 0, -1, age);
            links[v + 1] = new(pb, alpha, 0, 1, -1, age);
            v += 2;
        }
        LastLinkCount = v / 2;
    }
}
