namespace Wall42.Tests;

public class ConfigTests
{
    [Fact]
    public void EveryPresetParsesWithoutWarnings()
    {
        var names = Presets.Names();
        Assert.True(names.Count >= 16, $"presets found: {names.Count} in {Presets.Dir()}");
        foreach (var n in names)
        {
            var c = Presets.Load(n, out var w);
            Assert.NotNull(c);
            Assert.True(w.Count == 0, $"{n}: {string.Join("; ", w)}");
            Assert.True(c!.Motion.ParticleCount > 0, n);
            Assert.True(c.Motion.Fps > 0, n);
        }
    }

    [Fact]
    public void PresetValuesComeThrough()
    {
        var k = Presets.Load("kang", out _)!;
        Assert.Equal(420, k.Motion.ParticleCount);
        Assert.Equal("attention", k.Motion.Link.Mode);
        Assert.True(k.Motion.Link.OnlyNodes);
        Assert.Equal("#6E90A8", k.Motion.Link.Color);
        Assert.Equal(0.2f, k.Background.Radius);
        var sand = Presets.Load("sand", out _)!;
        Assert.Equal("sand", sand.Motion.Effect);
        Assert.Equal("vertical", sand.Background.Mode);
        Assert.Equal(1, sand.Motion.Streams);
        var snow = Presets.Load("snow", out _)!;
        Assert.Equal(0.55f, snow.Motion.Softness);
        Assert.Equal(9, snow.Motion.Wind);
        var sessions = Presets.Load("sessions", out _)!;
        Assert.True(sessions.Motion.Sessions?.Enabled);
    }

    [Fact]
    public void EmptyObjectIsTheMacDefault()
    {
        var c = Config.Parse("{}", out var w);
        Assert.Empty(w);
        Assert.Equal("gradient", c.Background.Mode);
        Assert.Equal("#0F041D", c.Background.CenterColor);
        Assert.Equal(1.02f, c.Background.Radius);
        Assert.Equal("floating", c.Motion.Effect);
        Assert.Equal(140, c.Motion.ParticleCount);
        Assert.Equal(30, c.Motion.Fps);
        Assert.Null(c.Motion.SecondaryFps);
        Assert.Equal("#1ADBF5", c.Motion.ColorA);
        Assert.Equal(168, c.Motion.Link.Distance);
        Assert.Equal("proximity", c.Motion.Link.Mode);
        Assert.Null(c.Motion.Link.Color);
        Assert.Equal(0.9f, c.Motion.Pulse.Strength);
        Assert.Equal("system", c.Motion.Activity.Source);
        Assert.Equal(0.85f, c.Motion.Activity.Smoothing);
        Assert.Equal(8, c.Motion.Wind);
        Assert.Equal(0, c.Motion.Softness);
        Assert.Null(c.Motion.Sessions);
        Assert.True(c.Ui.MenuBar);
    }

    [Fact]
    public void OldFileWithoutNewerKeysKeepsWorking()
    {
        // an early wall42-era file: no pulse, activity, glow, twinkle*, softness, sessions, ui
        var c = Config.Parse("""
            {"background":{"mode":"solid","solidColor":"#101010","centerColor":"#000000","edgeColor":"#000000","radius":1},
             "motion":{"effect":"floating","particleCount":200,"fps":24,"colorA":"#FF0000","colorB":"#00FF00","speed":5,
                       "sizeMin":3,"sizeMax":6,"nodeRatio":0.1,"nodeSizeMin":10,"nodeSizeMax":12,"brightness":0.8,"breathSpeed":0.5,
                       "link":{"enabled":true,"distance":100,"opacity":0.3,"boost":1},
                       "bokeh":{"ratio":0.1,"sizeMin":20,"sizeMax":30,"speed":10,"dimming":0.2}}}
            """, out var w);
        Assert.Empty(w);
        Assert.Equal(200, c.Motion.ParticleCount);
        Assert.Equal("solid", c.Background.Mode);
        Assert.Equal(100, c.Motion.Link.Distance);
        Assert.Equal("proximity", c.Motion.Link.Mode);     // default for the missing key
        Assert.Equal(0.35f, c.Motion.Pulse.Speed);
        Assert.Equal(1, c.Motion.Glow);
    }

    [Fact]
    public void WrongTypesAreSkippedNotFatal()
    {
        var c = Config.Parse("""{"motion":{"particleCount":"lots","fps":60,"link":{"distance":true,"opacity":0.5},"bokeh":7}}""", out var w);
        Assert.Equal(140, c.Motion.ParticleCount);     // bad → default
        Assert.Equal(60, c.Motion.Fps);                // good sibling kept
        Assert.Equal(168, c.Motion.Link.Distance);
        Assert.Equal(0.5f, c.Motion.Link.Opacity);
        Assert.Equal(3, w.Count);
        Assert.Contains(w, x => x.Contains("motion.particleCount"));
    }

    [Fact]
    public void GarbageGivesDefaultsAndAWarning()
    {
        var c = Config.Parse("{ not json", out var w);
        Assert.Single(w);
        Assert.Equal(140, c.Motion.ParticleCount);
        Config.Parse("[1,2]", out w);
        Assert.Single(w);
    }

    [Fact]
    public void TolerantSyntax()
    {
        var c = Config.Parse("""
            // comment
            {"motion":{"fps":20.0,"particleCount":99,},}
            """, out var w);
        Assert.Empty(w);
        Assert.Equal(20, c.Motion.Fps);                // Swift writes whole floats as 20 or 20.0; both are ints here
        Assert.Equal(99, c.Motion.ParticleCount);
    }

    [Fact]
    public void RoundTrip()
    {
        foreach (var n in Presets.Names())
        {
            var a = Presets.Load(n, out _)!;
            var json = a.ToJson();
            var b = Config.Parse(json, out var w);
            Assert.Empty(w);
            Assert.Equal(json, b.ToJson());
            Assert.Contains("\"particleCount\"", json);  // camelCase, the Mac key names
        }
    }

    [Fact]
    public void LoadWritesDefaultsWhenMissingAndWatcherSeesEdits()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wall42-test-" + Guid.NewGuid().ToString("N"));
        var f = Path.Combine(dir, "config.json");
        try
        {
            var c = Config.Load(out var warn, f);
            Assert.Null(warn);
            Assert.True(File.Exists(f));
            Assert.Equal(140, c.Motion.ParticleCount);
            var watch = new ConfigWatcher(f);
            Assert.False(watch.Changed());
            c.Motion.ParticleCount = 77;
            c.Save(f);
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddSeconds(5));   // mtime resolution on some filesystems
            Assert.True(watch.Changed());
            Assert.False(watch.Changed());
            Assert.Equal(77, Config.Load(out _, f).Motion.ParticleCount);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Hex()
    {
        Assert.Equal(new System.Numerics.Vector4(26 / 255f, 219 / 255f, 245 / 255f, 1), Color.Hex("#1ADBF5"));
        Assert.Equal(new System.Numerics.Vector4(0, 0, 0, 1), Color.Hex("nope"));
        Assert.Equal(new System.Numerics.Vector4(0, 0, 0, 1), Color.Hex(null));
        Assert.Equal(new System.Numerics.Vector4(1, 1, 1, 1), Color.Hex(" ffffff "));
    }
}

public class SignalAndActivityTests
{
    static readonly DateTime t0 = new(2026, 1, 1);

    [Fact]
    public void SignalParse()
    {
        var s = Signal.Parse("""{"kind":"think","level":0.9,"seconds":120}""")!;
        Assert.Equal("think", s.Kind);
        Assert.Equal(0.9, s.Num("level", 0));
        Assert.Equal(7, s.Num("missing", 7));
        Assert.Null(Signal.Parse("{}"));
        Assert.Null(Signal.Parse("garbage"));
    }

    [Fact]
    public void SignalFileIsConsumedOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wall42-sig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Signal.PathIn(dir), """{"kind":"insight","strength":1}""");
            Assert.Equal("insight", Signal.Take(dir)?.Kind);
            Assert.Null(Signal.Take(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LoadMapsThroughMinMaxWithSmoothing()
    {
        var a = new ActivityConfig();          // 0.08..0.75, smoothing 0.85
        var ac = new ActivityController();
        Assert.Equal(0, ac.Update(a, null, t0).Activity);      // no sample yet: unchanged
        Assert.Equal(0, ac.Update(a, 0.05f, t0).Activity);     // below min
        float v = 0;
        for (int i = 0; i < 60; i++) v = ac.Update(a, 0.9f, t0).Activity;   // above max → approaches 1
        Assert.InRange(v, 0.99f, 1f);
        var one = new ActivityController();
        Assert.Equal(0.15f, one.Update(a, 0.9f, t0).Activity, 3);          // one step of 0.85 smoothing
        var mid = new ActivityController();
        for (int i = 0; i < 100; i++) v = mid.Update(a, 0.08f + 0.67f / 2, t0).Activity;
        Assert.Equal(0.5f, v, 2);
    }

    [Fact]
    public void ManualOffAndThink()
    {
        var ac = new ActivityController();
        var manual = new ActivityConfig { Source = "manual", ManualLevel = 1, Smoothing = 0 };
        Assert.Equal(1, ac.Update(manual, null, t0).Activity);
        Assert.Equal(0, ac.Update(new ActivityConfig { Source = "off", Smoothing = 0 }, 0.9f, t0).Activity);
        ac.Think(0.9f, 10, t0);
        float v = 0;
        for (int i = 0; i < 20; i++) v = ac.Update(new ActivityConfig { Source = "off" }, null, t0.AddSeconds(1)).Activity;
        Assert.Equal(0.9f, v, 3);
        var (after, expired) = ac.Update(new ActivityConfig { Source = "off" }, null, t0.AddSeconds(11));
        Assert.True(expired);
        Assert.True(after < 0.9f);
        Assert.False(ac.Thinking);
    }

    [Fact]
    public void ThinkIsClampedTo5sAnd1h()
    {
        var ac = new ActivityController();
        ac.Think(2, 1, t0);
        Assert.False(ac.Update(new ActivityConfig(), null, t0.AddSeconds(4)).ThinkExpired);
        Assert.True(ac.Update(new ActivityConfig(), null, t0.AddSeconds(5.1)).ThinkExpired);
    }
}
