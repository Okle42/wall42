using System.Diagnostics;
using System.Runtime.InteropServices;
using static Wall42.Native;

namespace Wall42;

/// The resident process: one hidden top-level window for broadcasts (TaskbarCreated, display change, power,
/// session), one child surface per monitor under the desktop WorkerW, one World, one frame loop.
/// Drawing stops completely (no Present, no sim) when every surface is paused; the thread then sleeps in
/// MsgWaitForMultipleObjectsEx and only wakes for window events and the 1 s tick.
sealed unsafe class App : IDisposable
{
    const uint TIMER_TICK = 1, TIMER_OCC = 2, TIMER_LAYOUT = 3, TIMER_REATTACH = 4, TIMER_QUIT = 5;

    readonly bool forceDraw = Program.EnvBool("WALL42_FORCE_DRAW"), noDraw = Program.EnvBool("WALL42_NO_DRAW"),
        onlyMain = Program.EnvBool("WALL42_ONLY_MAIN");
    readonly int reportEvery = Math.Max(1, Program.EnvInt("WALL42_REPORT") ?? 10);

    static App? current;
    static readonly WndProc wndProc = Dispatch;      // must outlive every window of both classes
    readonly WinEventProc eventProc;
    readonly List<IntPtr> hooks = new();
    IntPtr hwnd, frameTimer, powerNotify;
    readonly uint taskbarCreated;

    Gpu gpu;
    readonly World world;
    readonly ConfigWatcher watcher;
    readonly List<Surface> surfaces = new();
    IntPtr worker;
    List<MonitorRect> layoutKey = new();
    float unit = 1;
    readonly HashSet<string> suspend = new();
    readonly CpuLoad cpu = new();
    readonly ActivityController activity = new();
    bool quit, occArmed, needRebuild, needGpu;
    int tickCount, occEvents;
    readonly Stopwatch clock = Stopwatch.StartNew();
    double Now => clock.Elapsed.TotalSeconds;
    double lastReport, lastPauseChange;
    TimeSpan lastCpu;
    int lastSteps;

    public App()
    {
        current = this;
        eventProc = OnWinEvent;
        var cfg = Program.LoadConfig();
        world = new World(cfg) { NoStep = noDraw };
        watcher = new ConfigWatcher(Config.FilePath);
        taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        gpu = new Gpu();
    }

    public int Run()
    {
        var inst = GetModuleHandleW(null);
        foreach (var cls in new[] { "wall42.main", Surface.ClassName })
        {
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
                hInstance = inst, lpszClassName = cls,
            };
            if (RegisterClassExW(ref wc) == 0) throw new InvalidOperationException("RegisterClassEx " + Marshal.GetLastWin32Error());
        }
        // hidden, never shown. NOT message-only: those miss broadcasts like TaskbarCreated and WM_DISPLAYCHANGE.
        hwnd = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "wall42.main", "wall42", WS_POPUP, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        WTSRegisterSessionNotification(hwnd, 0);                                  // NOTIFY_FOR_THIS_SESSION
        var g = GUID_CONSOLE_DISPLAY_STATE;
        powerNotify = RegisterPowerSettingNotification(hwnd, ref g, 0);            // DEVICE_NOTIFY_WINDOW_HANDLE
        // high-resolution waitable timer: exact frame pacing without timeBeginPeriod (which would tax the whole system)
        frameTimer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (frameTimer == IntPtr.Zero) frameTimer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 0, TIMER_ALL_ACCESS);

        // occlusion is event driven; the 1 s tick is only the fallback
        foreach (var (lo, hi) in new[] {
                     (EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND),
                     (EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND),
                     (EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE),
                     (EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE),
                     (EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED) })
            hooks.Add(SetWinEventHook(lo, hi, IntPtr.Zero, eventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS));

        var cfg = world.Config;
        Log.Note($"start pid={Environment.ProcessId} gpu={gpu.AdapterName} particles={cfg.Motion.ParticleCount}/primary-area fps={cfg.Motion.Fps} " +
                 $"effect={cfg.Motion.Effect} config={Config.FilePath}" + (forceDraw ? " FORCE_DRAW" : "") + (noDraw ? " NO_DRAW" : ""));
        BuildSurfaces(true);
        SetTimer(hwnd, (UIntPtr)TIMER_TICK, 1000, IntPtr.Zero);
        if (Program.EnvInt("WALL42_DURATION") is > 0 and var d) SetTimer(hwnd, (UIntPtr)TIMER_QUIT, (uint)d * 1000, IntPtr.Zero);

        IntPtr* handles = stackalloc IntPtr[1];
        while (!quit)
        {
            if (needGpu) RecreateGpu();
            if (needRebuild) { needRebuild = false; BuildSurfaces(false); }
            double now = Now;
            RenderDue(now);
            double next = double.MaxValue;
            foreach (var s in surfaces) if (s.Drawing && s.NextDue < next) next = s.NextDue;
            uint n = 0;
            if (next != double.MaxValue)
            {
                long due = -Math.Max(1, (long)((next - Now) * 1e7));                      // relative, 100 ns units
                SetWaitableTimer(frameTimer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
                handles[0] = frameTimer; n = 1;
            }
            MsgWaitForMultipleObjectsEx(n, handles, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT) { quit = true; break; }
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        Log.Note($"exit: frames=[{string.Join(",", surfaces.Select(s => s.Frames))}] occlusionEvents={occEvents}");
        return 0;
    }

    // ── frames ──────────────────────────────────────────────────────

    void RenderDue(double now)
    {
        bool normal = world.Config.Motion.Blend == "normal";
        foreach (var s in surfaces)
        {
            if (!s.Drawing || s.NextDue > now + 0.0005) continue;
            // one step for all monitors: a second monitor due in the same vsync is skipped inside Advance
            world.Advance();
            gpu.Upload(world);
            if (!s.Render(world, normal, noDraw))
            {
                if (s.Hwnd != IntPtr.Zero && IsWindow(s.Hwnd)) needGpu = true;   // device lost
                else needRebuild = true;                                          // window taken down
                continue;
            }
            s.NextDue += s.Interval;
            if (s.NextDue < now) s.NextDue = now + s.Interval;                   // fell behind: don't burst
        }
    }

    /// Surfaces for the current monitors under the current WorkerW. The World survives (layout remap only).
    void BuildSurfaces(bool first)
    {
        foreach (var s in surfaces) s.Dispose();
        surfaces.Clear();
        var mons = Desktop.Monitors();
        if (onlyMain) mons = mons.Take(1).ToList();
        if (mons.Count == 0) { Log.Note("no monitors"); return; }
        unit = mons[0].Dpi / 96f;
        var rects = mons.Select(m => m.Rect).ToList();
        var (slots, mainArea, _, _) = Layout.Build(rects, unit);
        // same monitors (Explorer restart, re-attach): keep the world as is, sand piles and all
        if (first || !rects.SequenceEqual(layoutKey))
        {
            world.SetLayout(slots, mainArea);
            layoutKey = rects;
            Log.Note($"world {world.Size.X:0}x{world.Size.Y:0} units (×{unit:0.##} px) areaScale={world.AreaScale:0.00} particles={world.DrawCount} " +
                     "monitors=" + string.Join(" ", mons.Select(m => $"{m.Device}{(m.Rect.Primary ? "*" : "")}:{m.Rect.Left},{m.Rect.Top} {m.Rect.Width}x{m.Rect.Height}@{m.Dpi}")));
        }
        var (w, how) = Desktop.FindWorkerW();
        worker = w;
        Log.Note($"desktop layer: {how} · {Desktop.Describe(worker)}");
        if (worker == IntPtr.Zero) return;                      // the tick retries
        for (int i = 0; i < mons.Count; i++)
        {
            try { surfaces.Add(new Surface(gpu, mons[i], slots[i], unit, worker)); }
            catch (Exception e) { Log.Note($"surface {mons[i].Device}: {e.Message}"); }
        }
        Log.Note($"surfaces {surfaces.Count}: " + string.Join(" ", surfaces.Select(s => $"{s.Hwnd:X} parent={GetParent(s.Hwnd):X}")));
        SyncFps();
        RecomputeOcclusion();
    }

    void RecreateGpu()
    {
        needGpu = false;
        Log.Note("GPU lost: recreating the device and surfaces");
        foreach (var s in surfaces) s.Dispose();
        surfaces.Clear();
        gpu.Dispose();
        gpu = new Gpu();
        BuildSurfaces(false);
    }

    /// Focused monitor gets fps, the others secondaryFps (Mac: mouse + frontmost window). The sim follows the
    /// fastest DRAWING monitor; with everything paused it keeps fps so the first frame back isn't held.
    void SyncFps()
    {
        var m = world.Config.Motion;
        int full = Math.Max(1, m.Fps), low = Math.Clamp(m.SecondaryFps ?? Math.Max(1, full / 2), 1, full);
        int fastest = 0;
        foreach (var s in surfaces)
        {
            int want = surfaces.Count == 1 || s.Focused ? full : low;
            s.Interval = 1.0 / want;
            if (s.Drawing) fastest = Math.Max(fastest, want);
        }
        world.StepInterval = 1.0 / (fastest > 0 ? fastest : full);
    }

    /// The single exit for pausing: occluded or suspended (lock, sleep, display off, screen saver, session switch).
    void UpdateDrawing()
    {
        double now = Now;
        bool changed = false;
        foreach (var s in surfaces)
        {
            bool stop = !forceDraw && (s.Occluded || suspend.Count > 0);
            if (stop == !s.Drawing) continue;
            changed = true;
            lastPauseChange = now;
            if (stop) { s.Drawing = false; s.PausedAt = now; continue; }
            // resuming with every other monitor paused: the sim was paused too, continue from now (no jump)
            if (!surfaces.Any(o => o != s && o.Drawing)) world.ResetClock();
            s.Drawing = true;
            s.NextDue = now;
        }
        if (changed) SyncFps();
    }

    // ── occlusion ───────────────────────────────────────────────────

    bool[] covered = Array.Empty<bool>();
    string[] coveredBy = Array.Empty<string>();

    /// Asymmetric debounce like the Mac: uncovered → draw immediately; covered → only after 300 ms stable
    /// (while switching windows the coverage flickers; pausing on every flicker would stutter).
    void RecomputeOcclusion()
    {
        if (surfaces.Count == 0) return;
        if (covered.Length != surfaces.Count) { covered = new bool[surfaces.Count]; coveredBy = new string[surfaces.Count]; }
        Occlusion.Compute(surfaces, covered, coveredBy);
        double now = Now, wait = double.MaxValue;
        for (int i = 0; i < surfaces.Count; i++)
        {
            var s = surfaces[i];
            if (!covered[i])
            {
                s.CoveredSince = -1;
                if (s.Occluded) { s.Occluded = false; Log.Note($">>> {s.Mon.Device} VISIBLE (drawing)"); }
                continue;
            }
            if (s.CoveredSince < 0) s.CoveredSince = now;
            if (s.Occluded) continue;
            double left = 0.3 - (now - s.CoveredSince);
            if (left > 0) { wait = Math.Min(wait, left); continue; }
            s.Occluded = true;
            s.CoveredBy = coveredBy[i];
            Log.Note($">>> {s.Mon.Device} OCCLUDED by {coveredBy[i]} (paused)");
        }
        if (wait != double.MaxValue) ArmOcc((uint)Math.Ceiling(wait * 1000) + 5);
        UpdateDrawing();
    }

    void ArmOcc(uint ms)
    {
        if (occArmed) return;          // don't re-arm: during a drag events come every frame and would starve it
        occArmed = true;
        SetTimer(hwnd, (UIntPtr)TIMER_OCC, ms, IntPtr.Zero);
    }

    void OnWinEvent(IntPtr hook, uint ev, IntPtr h, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || idChild != 0 || h == IntPtr.Zero) return;   // carets, cursors, menus' items…
        if (ev != EVENT_SYSTEM_FOREGROUND && GetAncestor(h, GA_ROOT) != h) return;   // only top-level windows matter
        occEvents++;
        ArmOcc(60);
    }

    // ── the hidden window: timers and broadcasts ────────────────────

    static IntPtr Dispatch(IntPtr h, uint msg, IntPtr wp, IntPtr lp)
    {
        var app = current;
        if (app != null && h == app.hwnd) return app.MainProc(h, msg, wp, lp);
        switch (msg)
        {
            case WM_NCHITTEST: return new IntPtr(HTTRANSPARENT);      // clicks fall through to whatever is below
            case WM_MOUSEACTIVATE: return new IntPtr(MA_NOACTIVATE);
            case WM_ERASEBKGND: return new IntPtr(1);                   // the swap chain paints everything
            case WM_DESTROY:
                if (app != null) foreach (var s in app.surfaces) if (s.Hwnd == h) { s.OnDestroyed(); if (s.Dead) app.needRebuild = true; }
                break;
        }
        return DefWindowProcW(h, msg, wp, lp);
    }

    IntPtr MainProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp)
    {
        if (msg == taskbarCreated && taskbarCreated != 0)
        {
            // Explorer restarted: its WorkerW (and our children with it) are gone. Give it a moment to settle.
            Log.Note(">>> TaskbarCreated: Explorer restarted, re-attaching");
            SetTimer(hwnd, (UIntPtr)TIMER_REATTACH, 1500, IntPtr.Zero);
            return IntPtr.Zero;
        }
        switch (msg)
        {
            case WM_TIMER:
                var id = (uint)wp;
                if (id == TIMER_TICK) Tick();
                else if (id == TIMER_OCC) { KillTimer(hwnd, (UIntPtr)TIMER_OCC); occArmed = false; RecomputeOcclusion(); }
                else if (id == TIMER_LAYOUT || id == TIMER_REATTACH) { KillTimer(hwnd, (UIntPtr)id); needRebuild = true; }
                else if (id == TIMER_QUIT) { Log.Note("duration reached, exiting"); quit = true; }
                return IntPtr.Zero;
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                SetTimer(hwnd, (UIntPtr)TIMER_LAYOUT, 800, IntPtr.Zero);       // Windows sends several: settle first
                return IntPtr.Zero;
            case WM_SETTINGCHANGE:
                if ((uint)wp == SPI_SETWORKAREA) SetTimer(hwnd, (UIntPtr)TIMER_LAYOUT, 800, IntPtr.Zero);   // taskbar moved/resized
                break;
            case WM_POWERBROADCAST:
                switch ((uint)wp)
                {
                    case PBT_APMSUSPEND: SetSuspended("systemSleep", true); break;
                    case PBT_APMRESUMEAUTOMATIC: case PBT_APMRESUMESUSPEND: SetSuspended("systemSleep", false); break;
                    case PBT_POWERSETTINGCHANGE:
                        var ps = (POWERBROADCAST_SETTING*)lp;
                        if (ps->PowerSetting == GUID_CONSOLE_DISPLAY_STATE) SetSuspended("displayOff", ps->Data == 0);   // 0 off, 1 on, 2 dimmed
                        break;
                }
                return new IntPtr(1);
            case WM_WTSSESSION_CHANGE:
                switch ((int)wp)
                {
                    case WTS_SESSION_LOCK: SetSuspended("locked", true); break;
                    case WTS_SESSION_UNLOCK: SetSuspended("locked", false); break;
                    case WTS_CONSOLE_DISCONNECT: case WTS_REMOTE_DISCONNECT: SetSuspended("sessionInactive", true); break;
                    case WTS_CONSOLE_CONNECT: case WTS_REMOTE_CONNECT: SetSuspended("sessionInactive", false); break;
                }
                return IntPtr.Zero;
            case WM_QUERYENDSESSION: return new IntPtr(1);
            case WM_ENDSESSION: if (wp != IntPtr.Zero) quit = true; return IntPtr.Zero;
            case WM_CLOSE: quit = true; return IntPtr.Zero;                   // taskkill /pid (without /f)
        }
        return DefWindowProcW(h, msg, wp, lp);
    }

    void SetSuspended(string reason, bool on)
    {
        var before = string.Join(",", suspend.OrderBy(x => x));
        if (on) suspend.Add(reason); else suspend.Remove(reason);
        // waking up: sleep-type notifications don't always come in pairs
        if (!on && reason is "systemSleep" or "displayOff") { suspend.Remove("systemSleep"); suspend.Remove("displayOff"); }
        var after = string.Join(",", suspend.OrderBy(x => x));
        if (before == after) return;
        UpdateDrawing();
        Log.Note($">>> {(on ? "SUSPEND" : "RESUME")} {reason} · reasons=[{after}]");
    }

    // ── once a second ───────────────────────────────────────────────

    void Tick()
    {
        tickCount++;
        if (watcher.Changed())
        {
            var cfg = Program.LoadConfig();
            world.Apply(cfg);
            SyncFps();
            Log.Note($"♻ config reloaded particles={cfg.Motion.ParticleCount} fps={cfg.Motion.Fps} effect={cfg.Motion.Effect}");
        }
        HandleSignal();
        var (act, expired) = activity.Update(world.Config.Motion.Activity, cpu.Sample(), DateTime.Now);
        world.Activity = act;
        if (expired) Log.Note(">>> think expired");
        // screen saver: no broadcast for it, polling one flag is cheap
        if (SystemParametersInfoW(SPI_GETSCREENSAVERRUNNING, 0, out int saver, 0)) SetSuspended("screensaver", saver != 0);
        // a surface lost its parent (Explorer restart without TaskbarCreated reaching us, WorkerW recreated)
        if (worker == IntPtr.Zero || !IsWindow(worker) || surfaces.Any(s => s.Dead || !IsWindow(s.Hwnd) || GetParent(s.Hwnd) != worker))
            needRebuild = true;
        RecomputeOcclusion();          // fallback for moves the events missed
        UpdateFocus();
        foreach (var s in surfaces)     // paused for a while: give the big buffers back
            if (!s.Drawing && !s.Trimmed && Now - s.PausedAt > 5) s.Trim(Color.Hex(world.Config.Background.EdgeColor));
        if (tickCount % reportEvery == 0) Report();
    }

    void HandleSignal()
    {
        var sig = Signal.Take(Config.Dir);
        if (sig == null) return;
        switch (sig.Kind)
        {
            case "think":
                double level = sig.Num("level", 0.85), secs = sig.Num("seconds", 120);
                activity.Think((float)level, secs, DateTime.Now);
                Log.Note($">>> think level={level} for {secs}s");
                break;
            case "insight":
                world.TriggerInsight((float)sig.Num("strength", 1));
                Log.Note(">>> insight");
                break;
            case "debug-suspend":       // test hook: the same path as locking
                SetSuspended(sig.Str("reason") ?? "debug", sig.Bool("on", true));
                break;
            default:
                Log.Note($"⚠ unknown signal: {sig.Kind}");
                break;
        }
    }

    /// Mouse monitor + foreground window monitor = focused (full fps); only with more than one monitor.
    void UpdateFocus()
    {
        if (surfaces.Count < 2) return;
        var focus = new HashSet<IntPtr>();
        if (GetCursorPos(out var pt)) focus.Add(MonitorFromPoint(pt, MONITOR_DEFAULTTONULL));
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && ClassOf(fg) is not ("Progman" or "WorkerW")) focus.Add(MonitorFromWindow(fg, MONITOR_DEFAULTTONULL));
        focus.Remove(IntPtr.Zero);
        if (focus.Count == 0) return;
        bool changed = false;
        foreach (var s in surfaces)
        {
            bool f = focus.Contains(s.Mon.Handle);
            if (f != s.Focused) { s.Focused = f; changed = true; }
        }
        if (changed) { SyncFps(); Log.Note(">>> focus " + string.Join(" ", surfaces.Select(s => $"{s.Mon.Device}:{(s.Focused ? "focus" : "low")}{1 / s.Interval:0}"))); }
    }

    void Report()
    {
        double now = Now, dt = now - lastReport;
        lastReport = now;
        using var p = Process.GetCurrentProcess();
        var cpuT = p.TotalProcessorTime;
        double cpuPct = lastCpu == TimeSpan.Zero || dt <= 0 ? 0 : (cpuT - lastCpu).TotalSeconds / dt * 100;
        lastCpu = cpuT;
        int steps = world.StepCount - lastSteps; lastSteps = world.StepCount;
        var fps = string.Join("/", surfaces.Select(s => { var d = s.Frames - s.LastFrames; s.LastFrames = s.Frames; return (dt > 0 ? d / dt : 0).ToString("0.0"); }));
        string state = suspend.Count > 0 ? $"SUSPENDED({string.Join(",", suspend)})"
            : surfaces.Count > 0 && surfaces.All(s => s.Occluded) ? "OCCLUDED" : "visible";
        if (forceDraw) state += "(forced)";
        Log.Note($"{state,-12} fps=[{fps}] steps/s={(dt > 0 ? steps / dt : 0):0.0} cpu={cpuPct:0.00}%(one core) links={world.LastLinkCount} " +
                 $"act={world.Activity:0.00} ws={p.WorkingSet64 / 1048576.0:0.0}MB private={p.PrivateMemorySize64 / 1048576.0:0.0}MB " +
                 $"events={occEvents}" + (now - lastPauseChange < 1.1 ? " (just switched)" : ""));
    }

    public void Dispose()
    {
        foreach (var hk in hooks) UnhookWinEvent(hk);
        foreach (var s in surfaces) s.Dispose();
        surfaces.Clear();
        // make Explorer repaint where we were (pre-24H2 WorkerWs otherwise keep our last frame)
        if (worker != IntPtr.Zero && IsWindow(worker)) RedrawWindow(worker, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN);
        if (powerNotify != IntPtr.Zero) UnregisterPowerSettingNotification(powerNotify);
        if (hwnd != IntPtr.Zero) { WTSUnRegisterSessionNotification(hwnd); DestroyWindow(hwnd); }
        if (frameTimer != IntPtr.Zero) CloseHandle(frameTimer);
        gpu.Dispose();
        current = null;
    }
}
