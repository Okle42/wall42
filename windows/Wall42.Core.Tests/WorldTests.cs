using System.Numerics;
using System.Runtime.InteropServices;

namespace Wall42.Tests;

public class WorldTests
{
    sealed class FakeClock { public double Now; public double Read() => Now; }

    static ScreenSlot Screen(float x, float y, float w, float h, float menu = 0, float taskbar = 32) =>
        new(new(x, y), new(w, h), y + menu, y + h - taskbar);

    static World Make(Config c, int seed = 42, FakeClock? clock = null, params ScreenSlot[] screens)
    {
        clock ??= new FakeClock();
        var w = new World(c, clock.Read, new Random(seed));
        if (screens.Length == 0) screens = new[] { Screen(0, 0, 1504, 1003) };
        w.SetLayout(screens, screens[0].Size.X * screens[0].Size.Y);
        return w;
    }

    static void Run(World w, int steps, float dt = 1 / 30f) { for (int i = 0; i < steps; i++) w.StepBy(dt); }

    static void AssertFinite(World w)
    {
        foreach (var p in w.Particles)
            Assert.True(float.IsFinite(p.Pos.X) && float.IsFinite(p.Pos.Y) && float.IsFinite(p.Size) && float.IsFinite(p.Fade)
                        && float.IsFinite(p.Boost), "non-finite particle");
        foreach (var l in w.Links) Assert.True(float.IsFinite(l.Alpha) && float.IsFinite(l.Pos.X) && float.IsFinite(l.Age));
    }

    [Fact]
    public void GpuLayoutsMatchTheShader()
    {
        Assert.Equal(44, Marshal.SizeOf<Particle>());
        Assert.Equal(28, Marshal.SizeOf<LinkVertex>());
        Assert.Equal(176, Marshal.SizeOf<Uniforms>());
        Assert.Equal(0, Marshal.OffsetOf<Uniforms>(nameof(Uniforms.Viewport)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<Uniforms>(nameof(Uniforms.ColorA)).ToInt32());
        Assert.Equal(80, Marshal.OffsetOf<Uniforms>(nameof(Uniforms.BgRadius)).ToInt32());
        Assert.Equal(128, Marshal.OffsetOf<Uniforms>(nameof(Uniforms.LinkColor)).ToInt32());
        Assert.Equal(160, Marshal.OffsetOf<Uniforms>(nameof(Uniforms.WorldSize)).ToInt32());
    }

    [Fact]
    public void DeterministicWithTheSameSeed()
    {
        var c = Presets.Load("kang", out _)!;
        var a = Make(c, 7); var b = Make(c.Clone(), 7);
        Run(a, 120); Run(b, 120);
        Assert.True(a.Particles.SequenceEqual(b.Particles));
        Assert.Equal(a.LastLinkCount, b.LastLinkCount);
        var other = Make(c.Clone(), 8); Run(other, 120);
        Assert.False(a.Particles.SequenceEqual(other.Particles));
    }

    [Fact]
    public void FloatingStaysInsideTheWorld()
    {
        var w = Make(new Config());
        Assert.Equal(140, w.DrawCount);
        Run(w, 900);
        foreach (var p in w.Particles)
        {
            Assert.InRange(p.Pos.X, 0, w.Size.X);
            Assert.InRange(p.Pos.Y, 0, w.Size.Y);
            Assert.Equal(1, p.Fade);
        }
        AssertFinite(w);
    }

    [Fact]
    public void ProximityLinksRespectDistanceAndOpacity()
    {
        var c = new Config();
        c.Motion.ParticleCount = 300;
        var w = Make(c);
        Run(w, 10);
        Assert.True(w.LastLinkCount > 0);
        var links = w.Links.ToArray();
        for (int i = 0; i < links.Length; i += 2)
        {
            Assert.True(Vector2.Distance(links[i].Pos, links[i + 1].Pos) < c.Motion.Link.Distance + 0.01f);
            Assert.InRange(links[i].Alpha, 0, c.Motion.Link.Opacity);
            Assert.Equal(0, links[i].T); Assert.Equal(1, links[i + 1].T);
            Assert.InRange(links[i].Seed, 0, 1);
        }
    }

    [Fact]
    public void OnlyNodesLinksOnlyNodes()
    {
        var c = Presets.Load("starfield-constellation", out _)!;
        var w = Make(c);
        Run(w, 5);
        var plain = new Config(); plain.Motion.ParticleCount = c.Motion.ParticleCount; plain.Motion.Link.Distance = c.Motion.Link.Distance;
        var all = Make(plain); Run(all, 5);
        Assert.True(w.LastLinkCount < all.LastLinkCount / 3, $"{w.LastLinkCount} vs {all.LastLinkCount}");
    }

    [Theory]
    [InlineData("attention")]
    [InlineData("traffic")]
    public void TimedLinkModesStayShortAndPulseByAge(string mode)
    {
        var c = Presets.Load("kang", out _)!;
        c.Motion.Link.Mode = mode;
        var w = Make(c);
        w.Activity = 0.5f;
        int seen = 0;
        for (int s = 0; s < 300; s++)
        {
            w.StepBy(1 / 30f);
            var links = w.Links.ToArray();
            seen = Math.Max(seen, links.Length / 2);
            for (int i = 0; i < links.Length; i += 2)
            {
                Assert.Equal(-1, links[i].Seed);
                Assert.InRange(links[i].Age, 0, 1);
                Assert.InRange(links[i].Alpha, 0, c.Motion.Link.Opacity);
                Assert.True(Vector2.Distance(links[i].Pos, links[i + 1].Pos) <= c.Motion.Link.Distance * 1.8f + 0.01f);
            }
        }
        Assert.True(seen > 5, $"{mode}: only {seen} links");
        AssertFinite(w);
    }

    [Fact]
    public void AttentionConclusionFlaresTheFocus()
    {
        var w = Make(Presets.Load("thinking", out _)!);
        float maxBoost = 0;
        for (int s = 0; s < 200; s++) { w.StepBy(1 / 30f); foreach (var p in w.Particles) maxBoost = MathF.Max(maxBoost, p.Boost); }
        Assert.InRange(maxBoost, 1.0f, 2.2f);
    }

    [Fact]
    public void EveryPresetStepsCleanlyOnOneAndTwoScreens()
    {
        foreach (var n in Presets.Names())
        {
            var c = Presets.Load(n, out _)!;
            var one = Make(c, 1);
            Run(one, 150);
            AssertFinite(one);
            var two = Make(c.Clone(), 1, null, Screen(0, 0, 1504, 1003), Screen(1504, -200, 1280, 1024));
            Run(two, 150);
            AssertFinite(two);
        }
    }

    [Fact]
    public void SnowFadesAndStaysAboveTheGround()
    {
        var w = Make(Presets.Load("snow", out _)!);
        Run(w, 600);
        var ground = w.Slots[0].VisibleBottom;
        int visible = 0;
        foreach (var p in w.Particles)
        {
            Assert.InRange(p.Fade, 0, 1);
            Assert.True(p.Pos.Y <= ground + 0.01f);
            Assert.InRange(p.Pos.X, 0, w.Size.X);
            if (p.Fade > 0.5f) visible++;
        }
        Assert.True(visible > w.DrawCount / 2, $"visible flakes {visible}/{w.DrawCount}");
    }

    [Fact]
    public void SandConservesGrainsAndBuildsAPile()
    {
        var w = Make(Presets.Load("sand", out _)!);
        w.StepBy(1 / 30f);           // effect reset (pre-runs 25 s)
        var (f, pl, id) = w.GrainCounts();
        Assert.Equal(w.DrawCount, f + pl + id);
        Assert.True(pl > 300, $"piled {pl}");
        Assert.True(f > 20, $"falling {f}");
        Run(w, 300);
        (f, pl, id) = w.GrainCounts();
        Assert.Equal(w.DrawCount, f + pl + id);
        var ground = w.Slots[0].VisibleBottom;
        foreach (var p in w.Particles) Assert.True(p.Pos.Y <= ground + 3.01f);
        AssertFinite(w);
    }

    [Fact]
    public void LeavingSandSpreadsParticlesAgain()
    {
        var c = Presets.Load("sand", out _)!;
        var w = Make(c);
        Run(w, 10);
        var n = Config.Parse(c.ToJson(), out _); n.Motion.Effect = "floating";
        w.Apply(n);
        Run(w, 2);
        foreach (var p in w.Particles) { Assert.Equal(1, p.Fade); Assert.InRange(p.Pos.X, 0, w.Size.X); Assert.InRange(p.Pos.Y, 0, w.Size.Y); }
    }

    [Fact]
    public void SecondScreenDoublesTheWorldAndRemapsProportionally()
    {
        var c = new Config();
        var w = Make(c, 3, null, Screen(0, 0, 1000, 500));
        Run(w, 30);
        var before = w.Particles.ToArray();
        Assert.Equal(140, before.Length);

        w.SetLayout(new[] { Screen(0, 0, 1000, 500), Screen(1000, 0, 1000, 500) }, 1000 * 500);
        Assert.Equal(new Vector2(2000, 500), w.Size);
        Assert.Equal(2, w.AreaScale, 3);
        Assert.Equal(280, w.DrawCount);
        var after = w.Particles.ToArray();
        for (int i = 0; i < before.Length; i++)
        {
            Assert.Equal(before[i].Pos.X * 2, after[i].Pos.X, 3);
            Assert.Equal(before[i].Pos.Y, after[i].Pos.Y, 3);
            Assert.Equal(before[i].Size, after[i].Size);          // same seeds, same look
        }
        // unplug again: back to 140, the survivors scaled back
        w.SetLayout(new[] { Screen(0, 0, 1000, 500) }, 1000 * 500);
        Assert.Equal(140, w.DrawCount);
        for (int i = 0; i < before.Length; i++) Assert.Equal(before[i].Pos.X, w.Particles[i].Pos.X, 2);
    }

    [Fact]
    public void OffsetScreensUseTheUnionRectAndPerScreenGround()
    {
        var w = Make(Presets.Load("snow", out _)!, 5, null, Screen(0, 0, 1000, 500), Screen(1000, 300, 800, 600));
        Assert.Equal(new Vector2(1800, 900), w.Size);
        Run(w, 400);
        foreach (var p in w.Particles)
        {
            var ground = p.Pos.X < 1000 ? 500 - 32 : 900 - 32;
            Assert.True(p.Pos.Y <= ground + 0.01f, $"{p.Pos}");
        }
    }

    [Fact]
    public void LayoutFromMonitorsUsesPhysicalPixelsOverOneScale()
    {
        var mons = new[]
        {
            new MonitorRect(0, 0, 2256, 1504, 0, 1456, true),
            new MonitorRect(-1920, 200, 0, 1280, 200, 1240, false),
        };
        var (slots, mainArea, ul, ut) = Layout.Build(mons, 1.5f);
        Assert.Equal((-1920, 0), (ul, ut));
        Assert.Equal(new Vector2(1280, 0), slots[0].Origin);
        Assert.Equal(new Vector2(1504, 1002.6667f), slots[0].Size);
        Assert.Equal(1456 / 1.5f, slots[0].VisibleBottom, 3);
        Assert.Equal(new Vector2(0, 200 / 1.5f), slots[1].Origin);
        Assert.Equal(1504 * 1002.6667f, mainArea, 0);
    }

    [Fact]
    public void ApplyKeepsPositionsForColourChangesAndResizesForCount()
    {
        var c = new Config();
        var w = Make(c);
        Run(w, 5);
        var before = w.Particles.ToArray();
        var n = c.Clone(); n.Motion.ColorA = "#FFFFFF"; n.Motion.Brightness = 0.5f;
        w.Apply(n);
        Assert.True(before.SequenceEqual(w.Particles.ToArray()));
        var bigger = n.Clone(); bigger.Motion.SizeMax = 30;
        w.Apply(bigger);
        Assert.Equal(before.Length, w.DrawCount);
        for (int i = 0; i < before.Length; i++) Assert.Equal(before[i].Pos, w.Particles[i].Pos);
        var more = bigger.Clone(); more.Motion.ParticleCount = 200;
        w.Apply(more);
        Assert.Equal(200, w.DrawCount);
    }

    [Fact]
    public void AdvanceClampsGapsAndResetClockAvoidsJumps()
    {
        var clock = new FakeClock();
        var w = Make(new Config(), 1, clock);
        clock.Now += 0.01;
        Assert.False(w.Advance());              // < 0.75 × 1/30 since the last step
        clock.Now += 0.03;
        Assert.True(w.Advance());
        Assert.Equal(0.04f, w.Elapsed, 4);
        var p0 = w.Particles.ToArray();
        clock.Now += 60;                        // a minute paused
        w.ResetClock();
        clock.Now += 1 / 30.0;
        Assert.True(w.Advance());
        Assert.Equal(0.04f + 1 / 30f, w.Elapsed, 4);
        var p1 = w.Particles.ToArray();
        for (int i = 0; i < p0.Length; i++)
            Assert.True(Vector2.Distance(p0[i].Pos, p1[i].Pos) < 5 || Vector2.Distance(p0[i].Pos, p1[i].Pos) > 900, "jumped");
        clock.Now += 5;                          // no reset: clamped to 0.1 s
        Assert.True(w.Advance());
        Assert.Equal(0.04f + 1 / 30f + 0.1f, w.Elapsed, 4);
    }

    [Fact]
    public void InsightDecaysInAboutOnePointThreeSeconds()
    {
        var w = Make(Presets.Load("thinking", out _)!);
        w.TriggerInsight(1);
        Assert.Equal(1, w.Insight);
        Run(w, 30);
        Assert.InRange(w.Insight, 0.2f, 0.3f);
        Run(w, 12);
        Assert.Equal(0, w.Insight);
    }

    [Fact]
    public void SessionPointsAreStableAndInsideAWorkArea()
    {
        var c = Presets.Load("sessions", out _)!;
        var screens = new[] { Screen(0, 0, 1504, 1003), Screen(1504, 0, 1280, 1024) };
        var w = Make(c, 9, null, screens);
        int regular = w.DrawCount;
        w.SetSessions(new World.SessionInfo[] { new("alpha", true), new("beta", false), new("gamma", false) });
        Assert.Equal(regular + 3, w.DrawCount);
        var anchors = w.SessionAnchors.ToArray();
        foreach (var a in anchors)
            Assert.Contains(screens, s => a.X >= s.Origin.X && a.X <= s.Origin.X + s.Size.X && a.Y >= s.VisibleTop && a.Y <= s.VisibleBottom);
        Run(w, 120);
        var tail = w.Particles[^3..].ToArray();
        for (int k = 0; k < 3; k++)
        {
            Assert.Equal(-1, tail[k].Depth);
            Assert.True(Vector2.Distance(tail[k].Pos, anchors[k]) <= 7 * 1.4143f, $"{k}: {tail[k].Pos} vs {anchors[k]} size={tail[k].Size}");
        }
        Assert.True(tail[0].Boost > 0.05f);                     // busy glow
        // same ids in another order: same places; one gone: others keep theirs
        w.SetSessions(new World.SessionInfo[] { new("gamma", false), new("alpha", false) });
        Assert.Equal(regular + 2, w.DrawCount);
        Assert.Equal(anchors[0], w.SessionAnchors[0]);
        Assert.Equal(anchors[2], w.SessionAnchors[1]);
        // disabling sessions removes them
        var off = c.Clone(); off.Motion.Sessions!.Enabled = false;
        w.Apply(off);
        Assert.Equal(regular, w.DrawCount);
        Run(w, 10);
        AssertFinite(w);
    }

    [Fact]
    public void SessionsIgnoredWhenDisabled()
    {
        var w = Make(new Config());
        int n = w.DrawCount;
        w.SetSessions(new World.SessionInfo[] { new("x", true) });
        Assert.Equal(n, w.DrawCount);
    }

    [Fact]
    public void UniformsFollowTheConfigAndActivity()
    {
        var c = Presets.Load("kang", out _)!;
        var w = Make(c);
        w.Activity = 1;
        var u = w.UniformsFor(new(2256, 1504), new(0, 0), new(1504, 1003), 1.5f);
        Assert.Equal(Color.Hex("#6E90A8"), u.LinkColor);
        Assert.Equal(c.Motion.Pulse.Speed * 2.2f, u.PulseSpeed, 4);
        Assert.Equal(c.Motion.BreathSpeed * 1.8f, u.BreathSpeed, 4);
        Assert.Equal(0, u.BgMode);
        var plain = Make(new Config());
        var up = plain.UniformsFor(new(1, 1), default, new(1, 1), 1);
        Assert.Equal((Color.Hex("#1ADBF5") + Color.Hex("#FC3D99")) * 0.5f, up.LinkColor);
        Assert.Equal(1, Make(Presets.Load("snow", out _)!).UniformsFor(new(1, 1), default, new(1, 1), 1).BgMode);
    }

    [Fact]
    public void NoStepKeepsTheClockButNotTheParticles()
    {
        var w = Make(new Config());
        w.NoStep = true;
        var before = w.Particles.ToArray();
        Run(w, 10);
        Assert.True(before.SequenceEqual(w.Particles.ToArray()));
        Assert.Equal(10 / 30f, w.Elapsed, 4);
    }
}
