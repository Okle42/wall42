using static Wall42.Native;

namespace Wall42;

/// The UI side of App: tray icon, control panel, manual pause, live config edits. Kept out of App.cs; the
/// frame loop calls UiStart / UiTick / UiConfigReloaded / UiStop and nothing else changes there.
sealed unsafe partial class App
{
    Tray? tray;
    ControlPanel? panel;
    // frames + cpu sampled every 5 s by the 1 s tick, for the menu's status line (no extra wake-ups)
    (double t, long frames, double cpu) uiSampleOld, uiSampleNew;
    int modalDepth;
    UIntPtr modalTimer;
    static readonly UiNative.TimerProc modalProc = (_, _, _, _) => current?.ModalFrame();
    UIntPtr trimTimer;
    string trimWhy = "";
    static readonly UiNative.TimerProc trimProc = (_, _, _, _) => current?.TrimNow();

    internal World World => world;
    internal Config Cfg => world.Config;
    internal bool ManualPaused => suspend.Contains("manual");
    internal ControlPanel? Panel => panel;
    internal Tray? TrayIcon => tray;

    void UiStart()
    {
        uiSampleOld = uiSampleNew = UiSample();
        SyncTray();
        if (Environment.GetEnvironmentVariable("WALL42_OPEN_PANEL") is { Length: > 0 } how) ShowPanel(how);   // test hook
    }

    void UiTick()
    {
        if (tickCount % 5 == 0) { uiSampleOld = uiSampleNew; uiSampleNew = UiSample(); tray?.RetryIfMissing(); }
    }

    void UiConfigReloaded()
    {
        SyncTray();
        panel?.Reload();
    }

    void UiStop()
    {
        panel?.Close(trim: false);
        panel = null;
        tray?.Dispose();
        tray = null;
        StopModalFrames(force: true);
        if (trimTimer != UIntPtr.Zero) { KillTimer(IntPtr.Zero, trimTimer); trimTimer = UIntPtr.Zero; }
    }

    /// ui.menuBar (same key as the Mac) turns the tray icon on and off, live.
    void SyncTray()
    {
        bool want = world.Config.Ui.MenuBar;
        if (want && tray == null) tray = new Tray(this);
        else if (!want && tray != null) { tray.Dispose(); tray = null; Log.Note("tray icon off (ui.menuBar=false)"); }
    }

    (double t, long frames, double cpu) UiSample()
    {
        long f = 0;
        foreach (var s in surfaces) f += s.Frames;
        return (Now, f, SelfUsage().CpuSeconds);
    }

    // ── what the tray and panel call ─────────────────────────────────

    internal void SetManualPause(bool on) => SetSuspended("manual", on);   // same exit as lock/sleep: no Present, no sim

    internal void Quit()
    {
        Log.Note("quit from the tray menu");
        quit = true;
    }

    /// One line for the top of the menu, like the Mac's.
    internal string StatusLine()
    {
        if (ManualPaused) return "已手動暫停";
        if (suspend.Count > 0) return "暫停中（" + string.Join("、", suspend.Select(ReasonText)) + "）";
        if (surfaces.Count == 0) return "找不到桌面層";
        if (surfaces.All(s => s.Occluded)) return "被視窗遮住，已停止繪製";
        var now = UiSample();
        var from = now.t - uiSampleNew.t >= 2 ? uiSampleNew : uiSampleOld;
        double dt = Math.Max(0.001, now.t - from.t);
        double fps = (now.frames - from.frames) / dt / Math.Max(1, surfaces.Count(s => s.Drawing));
        double cpuPct = (now.cpu - from.cpu) / dt * 100;
        return $"{fps:0} fps ・ {world.LastLinkCount} 條連線 ・ CPU {cpuPct:0.0}%";
    }

    static string ReasonText(string r) => r switch
    {
        "locked" => "已鎖定", "systemSleep" => "睡眠", "displayOff" => "螢幕關閉", "screensaver" => "螢幕保護程式",
        "sessionInactive" => "切換使用者", _ => r,
    };

    internal string ActivityLine()
    {
        var src = world.Config.Motion.Activity.Source;
        string label = src switch { "manual" => "手動", "off" => "關閉", _ => "跟隨系統負載" };
        int n = (int)Math.Round(Math.Clamp(world.Activity, 0, 1) * 10);
        return $"忙碌程度 {new string('▮', n)}{new string('▯', 10 - n)} ・ {label}";
    }

    /// Live: the panel's drag goes straight to the world (the file is written when the drag settles).
    internal void ApplyLive(Config cfg)
    {
        if (Program.EnvInt("WALL42_PARTICLES") is { } n) cfg.Motion.ParticleCount = n;
        if (Program.EnvInt("WALL42_FPS") is { } f) cfg.Motion.Fps = f;
        world.Apply(cfg);
        SyncFps();
    }

    /// After the UI wrote config.json: the same reload the 1 s poll would do, now, and the poll won't redo it.
    internal void ReloadAfterUiWrite(string what)
    {
        watcher.Touch();
        var cfg = Program.LoadConfig();
        world.Apply(cfg);
        SyncFps();
        SyncTray();
        Log.Note($"♻ config written by the UI ({what}) particles={cfg.Motion.ParticleCount} fps={cfg.Motion.Fps} effect={cfg.Motion.Effect}");
    }

    /// The panel's debounced save: the world already has these values, just don't bounce them back.
    internal void SavedByUi() => watcher.Touch();

    /// Edit config.json through the tree (unknown keys stay), write atomically, apply now.
    internal bool EditConfig(string what, Action<ConfigEdit> edit)
    {
        var path = Config.FilePath;
        var ed = ConfigEdit.Open(path, out var err);
        if (ed == null) { Log.Note($"⚠ not writing {path}: {err}"); return false; }
        edit(ed);
        try { ed.Save(path); }
        catch (Exception e) { Log.Note($"⚠ cannot write {path}: {e.Message}"); return false; }
        ReloadAfterUiWrite(what);
        panel?.Reload();
        return true;
    }

    internal bool ApplyPreset(string name)
    {
        var dir = Presets.Dir();
        var file = dir == null ? null : Path.Combine(dir, name + ".json");
        if (file == null || !File.Exists(file)) return false;
        var obj = ConfigEdit.ParseObject(Config.ReadShared(file));
        if (obj == null) { Log.Note($"⚠ preset {name} is not a JSON object"); return false; }
        return EditConfig("preset " + name, ed => ed.ApplyPreset(obj));
    }

    internal void SetActivitySource(string src) => EditConfig("activity " + src, ed => ed.SetString("motion.activity.source", src));

    internal void OpenConfigFolder()
    {
        var path = Config.FilePath;
        // explorer /select, highlights config.json in its folder
        UiNative.ShellExecuteW(IntPtr.Zero, "open", "explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"", null, UiNative.SW_SHOW);
    }

    /// how: "" / "show" = normal (activate), else "x,y[,noactivate]" for tests (e.g. off-screen, never taking focus).
    internal void ShowPanel(string how = "")
    {
        if (panel == null)
        {
            panel = new ControlPanel(this);
            panel.Closed += () => { panel = null; };
        }
        panel.Show(how);
    }

    /// After the menu or the panel closed: collect and trim the working set once things are quiet (one trim
    /// for a burst of closes; never while the panel is open). What the renderer still uses comes back as soft faults.
    internal void TrimSoon(string why, uint ms)
    {
        trimWhy = why;
        if (trimTimer != UIntPtr.Zero) KillTimer(IntPtr.Zero, trimTimer);
        trimTimer = UiNative.SetTimer(IntPtr.Zero, UIntPtr.Zero, ms, trimProc);
    }

    void TrimNow()
    {
        if (trimTimer != UIntPtr.Zero) { KillTimer(IntPtr.Zero, trimTimer); trimTimer = UIntPtr.Zero; }
        if (panel != null) return;
        var before = SelfUsage();
        UiNative.TrimWorkingSet();
        var after = SelfUsage();
        Log.Note($"{trimWhy}: trimmed ws {before.WorkingSetMB:0.0}→{after.WorkingSetMB:0.0} MB, private {after.PrivateMB:0.0} MB");
    }

    // ── frames while a modal loop runs (menu, window drag, colour dialog) ──

    /// Modal loops (TrackPopupMenu, moving the panel, ChooseColor) pump messages themselves and our frame loop
    /// is not running. A thread timer (dispatched by every loop) keeps the wallpaper moving meanwhile.
    internal void StartModalFrames()
    {
        if (modalDepth++ > 0) return;
        modalTimer = UiNative.SetTimer(IntPtr.Zero, UIntPtr.Zero, 15, modalProc);
    }

    internal void StopModalFrames(bool force = false)
    {
        if (force) modalDepth = 0; else if (modalDepth > 0 && --modalDepth > 0) return;
        if (modalTimer != UIntPtr.Zero) { KillTimer(IntPtr.Zero, modalTimer); modalTimer = UIntPtr.Zero; }
    }

    void ModalFrame()
    {
        if (surfaces.Any(s => s.Drawing)) RenderDue(Now);
    }
}
