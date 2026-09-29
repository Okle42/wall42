using System.Numerics;

namespace Wall42;

// snow / sand effects and Claude session points (port of the World.swift extensions)
public sealed partial class World
{
    string effectName = "";          // "" = rebuild the effect state on the next step
    string activeEffect = "";        // the effect that actually ran last (the rebuild flag doesn't clear it)
    /// sand: 0 falling, 1 piled, 2 idle (waiting in the pool)
    byte[] grainState = Array.Empty<byte>();
    float[] grainAge = Array.Empty<float>();
    int[] grainBin = Array.Empty<int>();
    float[] grainDh = Array.Empty<float>();
    readonly List<int> idleGrains = new();
    float[] heights = Array.Empty<float>();       // pile height per binW-wide column
    float[] groundBins = Array.Empty<float>();    // ground y per column (bottom of the work area: above the taskbar)
    float[] streamAcc = Array.Empty<float>();
    /// Whole-pile sink speed (units/s), self-regulating: sinks faster when the grain pool runs low, so the
    /// pile stays as big as the particle count can sustain, neither growing forever nor starving the stream.
    float sinkSpeed = 2.5f;
    const float binW = 2;

    /// Test/status hook: how many sand grains are falling / piled / idle.
    public (int Falling, int Piled, int Idle) GrainCounts()
    {
        int f = 0, pl = 0, id = 0;
        foreach (var s in grainState) { if (s == 0) f++; else if (s == 1) pl++; else id++; }
        return (f, pl, id);
    }

    /// The "ground" at world x: the lowest work-area bottom of the screens covering x (above the taskbar);
    /// gaps no screen covers use the world bottom.
    float GroundY(float x)
    {
        float g = -1;
        foreach (var s in slots) if (x >= s.Origin.X && x < s.Origin.X + s.Size.X) g = MathF.Max(g, s.VisibleBottom);
        return g < 0 ? Size.Y : g;
    }

    /// top edge of the highest screen at x (snow drifts in from here)
    float TopY(float x)
    {
        float t = float.MaxValue;
        foreach (var s in slots) if (x >= s.Origin.X && x < s.Origin.X + s.Size.X) t = MathF.Min(t, s.Origin.Y);
        return t == float.MaxValue ? 0 : t;
    }

    void ResetEffect(string name)
    {
        var previous = activeEffect;
        effectName = name;
        activeEffect = name;
        var p = P;
        int n = RegularCount;
        // sand gathers particles into streams and piles; spread them out again when leaving it
        if (previous == "sand" && name != "sand")
            for (int i = 0; i < n; i++) { p[i].Pos = new(Range(0, Size.X), Range(0, Size.Y)); p[i].Fade = 1; }
        transfers.Clear();
        focuses.Clear();
        switch (name)
        {
            case "snow":
                // continue from where they are with falling speeds; those below the ground go back up
                for (int i = 0; i < n; i++)
                {
                    p[i].Fade = 1;
                    float g = GroundY(p[i].Pos.X);
                    if (p[i].Pos.Y > g) p[i].Pos.Y = Range(0, MathF.Max(1, g));
                }
                break;
            case "sand":
                grainState = new byte[n]; Array.Fill(grainState, (byte)2);
                grainAge = new float[n]; grainBin = new int[n]; grainDh = new float[n];
                idleGrains.Clear();
                for (int i = n - 1; i >= 0; i--) idleGrains.Add(i);
                for (int i = 0; i < n; i++) { p[i].Fade = 0; p[i].Pos = new(-100, -100); p[i].Vel = Vector2.Zero; }
                int nb = Math.Max(1, (int)MathF.Ceiling(Size.X / binW));
                heights = new float[nb];
                groundBins = new float[nb];
                for (int b = 0; b < nb; b++) groundBins[b] = GroundY((b + 0.5f) * binW);
                streamAcc = Array.Empty<float>();
                // pre-run 25 s so switching to sand shows a pile and a stream that already reaches it
                for (int k = 0; k < 500; k++) StepSand(0.05f);
                break;
            default:
                RecomputeAttributes();      // back to floating: speeds and fade from the seeds
                for (int i = 0; i < n; i++)
                    if (p[i].Pos.X < 0 || p[i].Pos.Y < 0) p[i].Pos = new(Range(0, Size.X), Range(0, Size.Y));
                break;
        }
        if (name != "sand") grainState = Array.Empty<byte>();
    }

    // ── snow ──
    /// Slow fall with sway; near bokeh flakes fall faster and sway wider (depth). Fades out before the
    /// bottom of the work area and restarts at the top; x is continuous across the world (crosses seams).
    void StepSnow(float dt)
    {
        var m = Config.Motion;
        float wind = m.Wind, w = Size.X, t = Elapsed;
        const float fadeZone = 110;
        var p = P;
        for (int i = 0; i < RegularCount; i++)
        {
            var sd = seeds[i];
            bool near = p[i].Depth > 0.5f;
            float fall = (near ? m.Bokeh.Speed : m.Speed) * (0.55f + 0.9f * sd.VelMag);
            float swayF = (near ? 0.25f : 0.45f) + 0.5f * sd.Twinkle;       // big flakes sway slow and wide
            float swayA = (near ? 26 : 12) + 16 * sd.VelMag;
            float vx = wind * (near ? 1.6f : 1) + MathF.Sin(t * swayF + sd.VelAngle) * swayA;
            p[i].Vel = new(vx, fall);
            p[i].Pos += p[i].Vel * dt;
            if (p[i].Pos.X < 0) p[i].Pos.X += w;
            if (p[i].Pos.X >= w) p[i].Pos.X -= w;

            float x = p[i].Pos.X, ground = GroundY(x), top = TopY(x);
            if (p[i].Pos.Y > ground)
            {
                // respawn at the top at a random x, heights staggered so they don't arrive in a row
                float nx = Range(0, w);
                p[i].Pos = new(nx, TopY(nx) - Range(4, 60));
                p[i].Fade = 0;
                continue;
            }
            float fin = Math.Clamp((p[i].Pos.Y - top + 10) / 70, 0, 1);
            float fout = Math.Clamp((ground - p[i].Pos.Y) / fadeZone, 0, 1);
            p[i].Fade = fin * fout * fout * (3 - 2 * fout);                 // smoothstep fade-out
        }
    }

    // ── sand ──
    /// Hourglass: a thin stream per screen, grains accelerate down and pile into a cone (angle of repose).
    /// The whole pile sinks slowly like the lower half of an hourglass; grains reaching the ground fade out,
    /// older grains are darker. Sinking as a whole (not per grain) avoids holes under a hanging pile.
    /// Busier = faster stream.
    void StepSand(float dt)
    {
        var m = Config.Motion;
        int n = RegularCount;
        if (grainState.Length != n || heights.Length == 0) return;
        const float g = 620;
        float vmax = MathF.Max(60, m.Speed);
        int perScreen = Math.Clamp(m.Streams, 1, 6);
        var p = P;

        // stream positions: spread per screen, drifting slowly so the pile becomes a dune, not a needle
        var streams = new List<Vector2>(slots.Length * perScreen);
        for (int si = 0; si < slots.Length; si++)
        {
            var s = slots[si];
            for (int k = 0; k < perScreen; k++)
            {
                float id = si * perScreen + k;
                float bx = s.Origin.X + s.Size.X * (k + 0.5f) / perScreen;
                float wander = MathF.Sin(Elapsed * 0.045f + id * 1.9f) * s.Size.X * 0.03f + MathF.Sin(Elapsed * 0.17f + id * 0.7f) * 5;
                streams.Add(new(bx + wander, s.VisibleTop - 6));
            }
        }
        if (streamAcc.Length != streams.Count) streamAcc = new float[streams.Count];

        float rate = 130 * (0.75f + Activity * 0.9f);
        float bias = MathF.Max(0.1f, m.SizeBias);
        for (int si = 0; si < streams.Count; si++)
        {
            streamAcc[si] += rate * dt;
            while (streamAcc[si] >= 1)
            {
                streamAcc[si] -= 1;
                int i = TakeGrain();
                if (i < 0) break;
                var sd = seeds[i];
                // gaussian-ish jitter: the stream is a bundle, not a line
                float gx = (Range(-1, 1) + Range(-1, 1) + Range(-1, 1)) * 1.3f;
                p[i].Pos = new(streams[si].X + gx, streams[si].Y - Range(0, 8));
                p[i].Vel = new(gx * 1.4f, Range(10, 40));
                p[i].Size = m.SizeMin + (MathF.Max(m.SizeMin, m.SizeMax) - m.SizeMin) * MathF.Pow(sd.Size, bias);
                p[i].Fade = 0; p[i].Depth = 0; p[i].Boost = 0;
                grainState[i] = 0;
                grainAge[i] = 0;
            }
        }

        // sink control: pool below 6% → sink faster, above 18% → slower
        float idleFrac = idleGrains.Count / (float)Math.Max(1, n);
        if (idleFrac < 0.06f) sinkSpeed = MathF.Min(14, sinkSpeed * (1 + 1.2f * dt));
        else if (idleFrac > 0.18f) sinkSpeed = MathF.Max(0.4f, sinkSpeed * (1 - 0.8f * dt));
        float sink = sinkSpeed * dt;
        for (int b = 0; b < heights.Length; b++) if (heights[b] > 0) heights[b] = MathF.Max(0, heights[b] - sink);

        for (int i = 0; i < n; i++)
        {
            switch (grainState[i])
            {
                case 0:   // falling
                {
                    p[i].Vel.Y = MathF.Min(vmax, p[i].Vel.Y + g * dt);
                    p[i].Pos += p[i].Vel * dt;
                    grainAge[i] += dt;
                    p[i].Fade = MathF.Min(1, grainAge[i] * 5);
                    int b = (int)(p[i].Pos.X / binW);
                    if (b < 0 || b >= heights.Length) { ReleaseGrain(i); continue; }
                    float surface = groundBins[b] - heights[b];
                    if (p[i].Pos.Y < surface) break;
                    // angle of repose: much higher than a neighbour → slide to the lower side, up to 60 bins
                    const float repose = binW * 0.62f;      // ~32°, gentle dunes
                    for (int k = 0; k < 60; k++)
                    {
                        float h = groundBins[b] - heights[b];               // surface y (smaller = higher)
                        float lh = b > 0 ? groundBins[b - 1] - heights[b - 1] : -1e9f;
                        float rh = b + 1 < heights.Length ? groundBins[b + 1] - heights[b + 1] : -1e9f;
                        bool canL = lh - h > repose, canR = rh - h > repose;
                        if (canL && canR) b += rng.Next(2) == 0 ? -1 : 1;
                        else if (canL) b--;
                        else if (canR) b++;
                        else break;
                    }
                    float sz = p[i].Size;
                    float dh = sz * sz * 0.32f / binW;      // less than the geometric area: grains overlap, the pile looks solid
                    heights[b] = MathF.Min(heights[b] + dh, 260);
                    grainDh[i] = dh;
                    grainBin[i] = b;
                    grainState[i] = 1;
                    grainAge[i] = 0;
                    p[i].Pos = new((b + Range(0.1f, 0.9f)) * binW, groundBins[b] - heights[b] + dh * 0.5f);
                    p[i].Vel = Vector2.Zero;
                    break;
                }
                case 1:   // piled: sinks with the pile, older = darker, fades out at the ground
                {
                    grainAge[i] += dt;
                    p[i].Pos.Y += sink;
                    float below = p[i].Pos.Y - groundBins[grainBin[i]];
                    if (below > 3) { ReleaseGrain(i); continue; }
                    float edge = Math.Clamp((3 - below) / 9, 0, 1);
                    p[i].Fade = edge * (0.38f + 0.62f * MathF.Exp(-grainAge[i] / 9));
                    break;
                }
            }
        }
    }

    int TakeGrain()
    {
        if (idleGrains.Count > 0) { int last = idleGrains[^1]; idleGrains.RemoveAt(idleGrains.Count - 1); return last; }
        // pool empty: recycle the oldest (deepest) piled grain. Rare while the sink control works.
        int best = -1; float bestAge = -1;
        for (int i = 0; i < grainState.Length; i++)
            if (grainState[i] == 1 && grainAge[i] > bestAge) { bestAge = grainAge[i]; best = i; }
        return best;
    }

    void ReleaseGrain(int i)
    {
        var p = P;
        grainState[i] = 2;
        p[i].Fade = 0; p[i].Pos = new(-100, -100); p[i].Vel = Vector2.Zero;
        idleGrains.Add(i);
    }

    // ── Claude session points ──

    /// Updates the session list. Known sessions keep their order (and so their place); new ones append.
    public void SetSessions(IReadOnlyList<SessionInfo> list)
    {
        bool enabled = Config.Motion.Sessions?.Enabled ?? false;
        var incoming = enabled ? list : Array.Empty<SessionInfo>();
        if (incoming.SequenceEqual(sessions)) return;
        var next = new List<SessionInfo>();
        foreach (var s in sessions) foreach (var n in incoming) if (n.Id == s.Id) { next.Add(n); break; }
        foreach (var n in incoming) if (!next.Exists(x => x.Id == n.Id)) next.Add(n);
        if (next.Select(x => x.Id).SequenceEqual(sessions.Select(x => x.Id)))
        {
            sessions.Clear(); sessions.AddRange(next);     // only busy flags changed
            return;
        }
        // membership changed: replace the tail points, drop thoughts involving sessions
        int reg = RegularCount;
        focuses.RemoveAll(f => f.Node >= reg || f.Targets.Exists(t => t >= reg));
        transfers.RemoveAll(t => t.A >= reg || t.B >= reg);
        ps.RemoveRange(reg, sessions.Count);
        sessions.Clear(); sessions.AddRange(next);
        foreach (var _ in sessions) ps.Add(Blank(Vector2.Zero));
        if (isNode.Count != ps.Count)
        {
            isNode.RemoveRange(Math.Min(reg, isNode.Count), isNode.Count - Math.Min(reg, isNode.Count));
            isNode.AddRange(Enumerable.Repeat(true, sessions.Count));
        }
        EnsureBuffers();
        PlaceSessionAnchors();
        StyleSessionParticles();
        RebuildLinkIndex();
        sessionSpawnAcc = new float[sessions.Count];
    }

    public IReadOnlyList<Vector2> SessionAnchors => sessionAnchors;

    /// Place from a hash of the id: stable, spread, inside some screen's work area (clear of the taskbar).
    void PlaceSessionAnchors()
    {
        if (slots.Length == 0) { sessionAnchors = Array.Empty<Vector2>(); return; }
        sessionAnchors = sessions.Select(s =>
        {
            ulong h = 1469598103934665603;                           // FNV-1a
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(s.Id)) h = unchecked((h ^ b) * 1099511628211);
            float u = (h & 0xFFFF) / 65535f, v = ((h >> 16) & 0xFFFF) / 65535f;
            var sl = slots[(int)((h >> 32) % (ulong)slots.Length)];
            float top = sl.VisibleTop + (sl.VisibleBottom - sl.VisibleTop) * 0.14f;
            float bottom = sl.VisibleBottom - (sl.VisibleBottom - sl.VisibleTop) * 0.18f;
            return new Vector2(sl.Origin.X + sl.Size.X * (0.10f + 0.80f * u), top + (bottom - top) * v);
        }).ToArray();
    }

    void StyleSessionParticles()
    {
        int reg = RegularCount;
        var m = Config.Motion;
        float sz = m.Sessions?.Size ?? MathF.Max(m.NodeSizeMax, 8) * 1.15f;
        var p = P;
        for (int k = 0; k < sessions.Count; k++)
        {
            int i = reg + k;
            if (i >= p.Length) break;
            p[i].Size = sz;
            p[i].Depth = -1;             // the shader draws an extra outer ring for depth < 0
            p[i].ColorMix = 0.5f;
            p[i].Twinkle = 0.35f;
            p[i].Fade = 1;
            p[i].Vel = Vector2.Zero;
            if (i < isNode.Count) isNode[i] = true;
        }
    }

    /// Session points circle their anchor very slowly: visibly alive, but they stay put.
    void StepSessionNodes()
    {
        int reg = RegularCount;
        if (sessionAnchors.Length != sessions.Count) PlaceSessionAnchors();
        var p = P;
        for (int k = 0; k < sessions.Count; k++)
        {
            int i = reg + k;
            float ph = k * 2.1f;
            p[i].Boost = 0;              // reset each frame; attention flares and busy glow stack on top
            p[i].Pos = sessionAnchors[k] + new Vector2(MathF.Cos(Elapsed * 0.11f + ph), MathF.Sin(Elapsed * 0.08f + ph)) * 7;
        }
    }

    /// Busy sessions keep a soft glow (besides the attention flares)
    void ApplySessionGlow()
    {
        int reg = RegularCount;
        var p = P;
        for (int k = 0; k < sessions.Count; k++)
        {
            float b = sessions[k].Busy ? 0.35f + 0.25f * MathF.Sin(Elapsed * 2.6f + k) : 0.08f;
            p[reg + k].Boost = MathF.Max(p[reg + k].Boost, b);
        }
    }

    /// Attention mode: session points start their own thoughts, often when busy, rarely when idle.
    void SpawnSessionFocuses(float dt, float maxDist, float lifeLo, float lifeHi)
    {
        if (sessions.Count == 0 || linkIdx.Count < 4) return;
        if (sessionSpawnAcc.Length != sessions.Count) sessionSpawnAcc = new float[sessions.Count];
        int reg = RegularCount;
        float reach = maxDist * 1.4f;
        var p = P;
        for (int k = 0; k < sessions.Count; k++)
        {
            sessionSpawnAcc[k] += (sessions[k].Busy ? 1.3f : 0.12f) * dt;
            if (sessionSpawnAcc[k] < 1) continue;
            sessionSpawnAcc[k] -= 1;
            int n = reg + k;
            if (focuses.Exists(f => f.Node == n && Elapsed - f.Born < f.Life * 0.5f)) continue;
            var pn = p[n].Pos;
            var targets = new List<int>();
            int want = sessions[k].Busy ? IntIn(5, 9) : IntIn(3, 5);
            int tries = 64 * Math.Max(1, (int)AreaScale);
            for (int s = 0; s < tries && targets.Count < want; s++)
            {
                int c = linkIdx[Pick(linkIdx.Count)];
                if (c == n || targets.Contains(c)) continue;
                if (Vector2.DistanceSquared(pn, p[c].Pos) < reach * reach) targets.Add(c);
            }
            if (targets.Count < 2) continue;
            focuses.Add(new(n, targets, Elapsed, Range(lifeLo, lifeHi)));
        }
    }
}
