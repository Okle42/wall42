using System.Diagnostics;

namespace Wall42;

/// Diagnostics env (same names as the Mac; daily settings go through the config file):
///   WALL42_FORCE_DRAW=1   ignore occlusion/suspend, always draw (peak cost)
///   WALL42_NO_DRAW=1      clear + present only, no sim, no draw calls (framework baseline)
///   WALL42_SNAPSHOT=path  headless: step the world to frame 90 (WALL42_SNAPSHOT_FRAME) and render the
///                         primary monitor's view offscreen to a PNG, then exit (no window, no desktop needed)
///   WALL42_DURATION=s     exit after s seconds
///   WALL42_PARTICLES / WALL42_FPS   override the config
///   WALL42_ONLY_MAIN=1    primary monitor only
///   WALL42_CONFIG=path    another config file (e.g. a preset) · WALL42_REPO=path  where presets\ is
///   WALL42_SEED=n         random seed (snapshots default to 42, so they are reproducible)
///   WALL42_ACTIVITY=0..1  snapshot activity (default: manualLevel for source=manual, else 0 = idle machine)
///   WALL42_REPORT=s       status line interval in the log (default 10 s; 1 = like the Mac)
///   WALL42_LOG=path       log file (default %LOCALAPPDATA%\wall42\wall42.log)
///   WALL42_DEBUG_EVENTS=1 on exit, log which window events (event:class) woke us most
static class Program
{
    public static int? EnvInt(string k) => int.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : null;
    public static bool EnvBool(string k) => Environment.GetEnvironmentVariable(k) is "1" or "true" or "TRUE" or "True";

    [STAThread]
    static int Main(string[] args)
    {
        var snap = Environment.GetEnvironmentVariable("WALL42_SNAPSHOT");
        try
        {
            if (!string.IsNullOrWhiteSpace(snap)) return Snapshot(Path.GetFullPath(snap));
            using var app = new App();
            return app.Run();
        }
        catch (Exception e)
        {
            Log.Note("FATAL " + e);
            return 1;
        }
    }

    public static Config LoadConfig()
    {
        var cfg = Config.Load(out var warn);
        if (warn != null) Log.Note("⚠ " + warn);
        if (EnvInt("WALL42_PARTICLES") is { } n) cfg.Motion.ParticleCount = n;
        if (EnvInt("WALL42_FPS") is { } f) cfg.Motion.Fps = f;
        return cfg;
    }

    /// Headless snapshot: deterministic (fixed seed, fixed 1/fps steps), primary monitor size and scale.
    static int Snapshot(string path)
    {
        var sw = Stopwatch.StartNew();
        var cfg = LoadConfig();
        var mons = Desktop.Monitors();
        if (Program.EnvBool("WALL42_ONLY_MAIN")) mons = mons.Take(1).ToList();
        var primary = mons[0];
        float unit = primary.Dpi / 96f;
        var (slots, mainArea, _, _) = Layout.Build(mons.Select(m => m.Rect).ToList(), unit);
        int frames = EnvInt("WALL42_SNAPSHOT_FRAME") ?? 90;
        float dt = 1f / Math.Max(1, cfg.Motion.Fps);
        double t = 0;
        var world = new World(cfg, () => t, new Random(EnvInt("WALL42_SEED") ?? 42));
        world.SetLayout(slots, mainArea);
        float act = cfg.Motion.Activity.Source == "manual" ? cfg.Motion.Activity.ManualLevel : 0;
        if (float.TryParse(Environment.GetEnvironmentVariable("WALL42_ACTIVITY"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var a)) act = a;
        world.Activity = Math.Clamp(act, 0, 1);
        for (int i = 0; i < frames; i++) world.StepBy(dt);

        using var gpu = new Gpu();
        int w = primary.Rect.Width, h = primary.Rect.Height;
        var u = world.UniformsFor(new(w, h), slots[0].Origin, slots[0].Size, unit);
        var rgba = gpu.RenderOffscreen(world, w, h, u, cfg.Motion.Blend == "normal");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Png.Write(path, rgba, w, h);
        Log.Note($"snapshot {path} {w}x{h} frame={frames} particles={world.DrawCount} links={world.LastLinkCount} " +
                 $"effect={cfg.Motion.Effect} gpu={gpu.AdapterName} {sw.ElapsedMilliseconds}ms");
        return 0;
    }
}
