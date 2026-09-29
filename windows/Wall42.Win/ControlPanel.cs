using System.Runtime.InteropServices;
using static Wall42.Native;
using static Wall42.UiNative;

namespace Wall42;

/// The control panel: ControlPanel.swift on Windows. Data-driven like the Mac (one line per knob, see
/// Groups()), but drawn by hand into one window — no WinForms, no child controls — so it costs a DIB and
/// three fonts while open and nothing once closed (the window, canvas and fonts are destroyed and the
/// working set trimmed). Dragging applies to the wallpaper immediately; config.json is written through
/// ConfigEdit (unknown keys kept, atomic) 400 ms after the last change and when the panel closes.
/// Follows the Windows app theme (dark/light), also when it changes while open.
sealed unsafe class ControlPanel
{
    public const string ClassName = "wall42.panel";
    const uint TIMER_SAVE = 1;

    // ── rows ─────────────────────────────────────────────────────────

    abstract class Row { public string Label = ""; }
    sealed class Slider : Row
    {
        public double Min, Max; public int Dp; public bool Log; public string Path = "";
        public Func<Config, double> Get = _ => 0; public Action<ConfigEdit, double>? Also;
    }
    sealed class Toggle : Row { public string Path = ""; public Func<Config, bool> Get = _ => false; }
    sealed class Choice : Row { public string Path = ""; public string[] Values = [], Titles = []; public Func<Config, string> Get = _ => ""; }
    sealed class ColorRow : Row { public string Path = ""; public Func<Config, string?> Get = _ => null; public Func<Config, string>? Auto; }
    sealed class PresetRow : Row { }
    sealed class ButtonRow : Row { public string Text = ""; public Action<ControlPanel> Act = _ => { }; }
    sealed record Group(string Title, Row[] Rows);

    static Slider S(string label, string path, double min, double max, int dp, Func<Config, double> get, Action<ConfigEdit, double>? also = null, bool log = false)
        => new() { Label = label, Path = path, Min = min, Max = max, Dp = dp, Get = get, Also = also, Log = log };
    static Choice C(string label, string path, string[] values, string[] titles, Func<Config, string> get)
        => new() { Label = label, Path = path, Values = values, Titles = titles, Get = get };

    /// What the panel shows. Same groups and ranges as the Mac panel, plus fps, background mode/colours and a
    /// log-scale particle count (sand is 2400; the Mac slider stops at 600).
    static Group[] Groups() =>
    [
        new("風格", [new PresetRow { Label = "風格" }]),
        new("整體", [
            S("亮度", "motion.brightness", 0, 2, 2, c => c.Motion.Brightness),
            S("發光感", "motion.glow", 0, 1, 2, c => c.Motion.Glow),
            C("混合", "motion.blend", ["additive", "normal"], ["疊加發光", "一般"], c => c.Motion.Blend),
            C("效果", "motion.effect", ["floating", "snow", "sand"], ["漂浮", "下雪", "流沙"], c => c.Motion.Effect),
            S("幀率", "motion.fps", 10, 60, 0, c => c.Motion.Fps),
            new Toggle { Label = "Session 光點", Path = "motion.sessions.enabled", Get = c => c.Motion.Sessions?.Enabled ?? false },
        ]),
        new("粒子", [
            S("數量", "motion.particleCount", 20, 3000, 0, c => c.Motion.ParticleCount, log: true),
            S("最小", "motion.sizeMin", 1, 30, 1, c => c.Motion.SizeMin),
            S("最大", "motion.sizeMax", 1, 40, 1, c => c.Motion.SizeMax),
            S("節點比例", "motion.nodeRatio", 0, 0.6, 2, c => c.Motion.NodeRatio),
            S("節點大小", "motion.nodeSizeMax", 2, 60, 1, c => c.Motion.NodeSizeMax,
                (e, v) => e.SetNumber("motion.nodeSizeMin", v * 0.62, 1)),          // keep the min/max ratio (Mac)
            S("漂浮速度", "motion.speed", 0, 40, 1, c => c.Motion.Speed),
            S("閃爍幅度", "motion.twinkleAmount", 0, 1, 2, c => c.Motion.TwinkleAmount),
            S("閃爍速度", "motion.breathSpeed", 0, 2.5, 2, c => c.Motion.BreathSpeed),
            S("閃爍差異", "motion.twinkleVariance", 0, 1, 2, c => c.Motion.TwinkleVariance),
        ]),
        new("連線", [
            new Toggle { Label = "啟用", Path = "motion.link.enabled", Get = c => c.Motion.Link.Enabled },
            C("模式", "motion.link.mode", ["proximity", "traffic", "attention"], ["距離", "流量", "注意力"], c => c.Motion.Link.Mode),
            new Toggle { Label = "只連節點", Path = "motion.link.onlyNodes", Get = c => c.Motion.Link.OnlyNodes },
            S("亮度", "motion.link.opacity", 0, 1, 2, c => c.Motion.Link.Opacity),
            S("加亮", "motion.link.boost", 0.2, 3, 2, c => c.Motion.Link.Boost),
            S("距離", "motion.link.distance", 40, 500, 0, c => c.Motion.Link.Distance),
            S("同時傳輸", "motion.link.targetCount", 5, 250, 0, c => c.Motion.Link.TargetCount),
        ]),
        new("脈衝", [
            S("強度", "motion.pulse.strength", 0, 3, 2, c => c.Motion.Pulse.Strength),
            S("速度", "motion.pulse.speed", 0, 2, 2, c => c.Motion.Pulse.Speed),
            S("寬度", "motion.pulse.width", 0.0005, 0.02, 4, c => c.Motion.Pulse.Width),
        ]),
        new("散景", [
            S("比例", "motion.bokeh.ratio", 0, 0.4, 3, c => c.Motion.Bokeh.Ratio),
            S("大小", "motion.bokeh.sizeMax", 5, 120, 0, c => c.Motion.Bokeh.SizeMax,
                (e, v) => e.SetNumber("motion.bokeh.sizeMin", v * 0.48, 1)),
            S("暗度", "motion.bokeh.dimming", 0, 1, 2, c => c.Motion.Bokeh.Dimming),
        ]),
        new("顏色", [
            new ColorRow { Label = "粒子 A", Path = "motion.colorA", Get = c => c.Motion.ColorA },
            new ColorRow { Label = "粒子 B", Path = "motion.colorB", Get = c => c.Motion.ColorB },
            new ColorRow { Label = "連線", Path = "motion.link.color", Get = c => c.Motion.Link.Color,
                Auto = c => Canvas.Hex(Canvas.Mix(Canvas.ParseHex(c.Motion.ColorA), Canvas.ParseHex(c.Motion.ColorB), 0.5f)) },
        ]),
        new("背景", [
            C("模式", "background.mode", ["solid", "gradient", "vertical"], ["純色", "放射漸層", "垂直漸層"], c => c.Background.Mode),
            new ColorRow { Label = "純色", Path = "background.solidColor", Get = c => c.Background.SolidColor },
            new ColorRow { Label = "中心／下", Path = "background.centerColor", Get = c => c.Background.CenterColor },
            new ColorRow { Label = "邊緣／上", Path = "background.edgeColor", Get = c => c.Background.EdgeColor },
            S("漸層範圍", "background.radius", 0.2, 2, 2, c => c.Background.Radius),
        ]),
        new("忙碌程度", [
            C("來源", "motion.activity.source", ["system", "manual", "off"], ["系統負載", "手動", "關"], c => c.Motion.Activity.Source),
            S("手動值", "motion.activity.manualLevel", 0, 1, 2, c => c.Motion.Activity.ManualLevel),
        ]),
        new("", [new ButtonRow { Text = "開啟設定檔資料夾", Act = p => p.app.OpenConfigFolder() }]),
    ];

    // ── theme ────────────────────────────────────────────────────────

    sealed record Theme(bool Dark, uint Back, uint Text, uint Secondary, uint Input, uint Border, uint Track, uint Accent, uint OnAccent,
        uint Knob, uint KnobBorder, uint Warn)
    {
        /// WALL42_THEME=light|dark overrides (tests; the Windows setting is never touched)
        public static Theme Current() => (Environment.GetEnvironmentVariable("WALL42_THEME") switch { "light" => false, "dark" => true, _ => AppsDark() })
            ? new(true, 0x202020, 0xFFFFFF, 0x9E9E9E, 0x2D2D2D, 0x3C3C3C, 0x5A5A5A, 0x4CC2FF, 0x000000, 0x454545, 0x505050, 0xFCE100)
            : new(false, 0xF3F3F3, 0x1B1B1B, 0x606060, 0xFDFDFD, 0xD6D6D6, 0xB8B8B8, 0x005FB8, 0xFFFFFF, 0xFFFFFF, 0xCCCCCC, 0x9D5D00);
    }

    // ── state ────────────────────────────────────────────────────────

    static readonly WndProc wndProc = Proc;
    static ControlPanel? current;
    static bool classRegistered;
    static IntPtr custColors;                                  // ChooseColor's 16 custom colours, kept for the session

    public event Action? Closed;
    readonly App app;
    readonly Group[] groups = Groups();
    IntPtr hwnd;
    Theme th = Theme.Current();
    float scale = 1;
    Canvas? canvas;
    IntPtr fontBody, fontTitle, fontValue;
    ConfigEdit? ed;
    string? readOnly;                                          // why edits are off (config.json isn't valid JSON)
    Config cfg;
    string? basePreset;                                        // the style the edits started from ("reset" goes back to it)
    bool presetMatches, dirty;

    sealed class Item { public Row Row = null!; public int Y, H; }
    readonly List<Item> items = new();
    readonly List<(string title, int y)> titles = new();
    int contentH, scrollY;
    Item? drag;                                                // slider being dragged
    Item? focus;                                               // last slider touched: arrow keys move it
    bool dragThumb; int dragThumbFrom, dragScrollFrom;

    public ControlPanel(App app)
    {
        this.app = app;
        cfg = app.Cfg;
        Load();
        basePreset = ConfigEdit.MatchPreset(cfg);
        presetMatches = basePreset != null;
    }

    void Load()
    {
        ed = ConfigEdit.Open(Config.FilePath, out var err);
        readOnly = ed == null ? "config.json 不是有效的 JSON：面板不會覆寫它，請先修正檔案。" : null;
        cfg = ed?.ToConfig() ?? app.Cfg;
        if (err != null) Log.Note("⚠ panel: " + err);
    }

    /// The file changed under us (the tray menu, a hand edit, a preset): show what's there now.
    public void Reload()
    {
        if (hwnd == IntPtr.Zero) return;
        KillTimer(hwnd, (UIntPtr)TIMER_SAVE);
        dirty = false;
        Load();
        Layout(ClientW);                                       // the read-only banner may have come or gone
        var m = ConfigEdit.MatchPreset(cfg);
        if (m != null) basePreset = m;
        presetMatches = m != null && m == basePreset;
        Invalidate();
    }

    // ── window ───────────────────────────────────────────────────────

    int D(float dip) => (int)MathF.Round(dip * scale);

    public void Show(string how)
    {
        bool activate = !how.Contains("noactivate");
        if (hwnd != IntPtr.Zero)
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            if (activate) SetForegroundWindow(hwnd);
            return;
        }
        var inst = GetModuleHandleW(null);
        if (!classRegistered)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc), hInstance = inst,
                lpszClassName = ClassName, hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
            };
            if (RegisterClassExW(ref wc) == 0) throw new InvalidOperationException("RegisterClassEx panel " + Marshal.GetLastWin32Error());
            classRegistered = true;
        }
        // where: next to the tray (bottom right of the monitor with the mouse), or where a test asked
        POINT at;
        int? atX = null, atY = null;
        var parts = how.Split(',');
        if (parts.Length >= 2 && int.TryParse(parts[0], out var px) && int.TryParse(parts[1], out var py)) { atX = px; atY = py; at = new POINT(px, py); }
        else GetCursorPos(out at);
        var mon = MonitorFromPoint(at, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = sizeof(MONITORINFO) };
        GetMonitorInfoW(mon, ref mi);
        GetDpiForMonitor(mon, 0, out uint dpi, out _);
        scale = (dpi == 0 ? 96 : dpi) / 96f;
        Layout(D(400));
        uint style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX;
        int cw = D(400), ch = Math.Min(contentH, Math.Min((int)(mi.rcWork.Height * 0.86), D(760)));
        var r = new RECT(0, 0, cw, ch);
        AdjustWindowRectExForDpi(ref r, style, false, 0, dpi == 0 ? 96 : dpi);
        int ww = r.Width, wh = r.Height;
        int x = atX ?? mi.rcWork.Right - ww - D(12), y = atY ?? mi.rcWork.Bottom - wh - D(12);
        current = this;
        hwnd = CreateWindowExW(0, ClassName, "wall42 參數", style, x, y, ww, wh, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { current = null; throw new InvalidOperationException("CreateWindowEx panel " + Marshal.GetLastWin32Error()); }
        ApplyTheme();
        MakeFonts();
        var icon = Icons.Make(GetSystemMetricsForDpi(SM_CXSMICON, dpi), false);
        var big = Icons.Make(GetSystemMetricsForDpi(SM_CXICON, dpi), false);
        SendMessageW(hwnd, WM_SETICON, IntPtr.Zero, icon);
        SendMessageW(hwnd, WM_SETICON, new IntPtr(1), big);
        smallIcon = icon; bigIcon = big;
        ShowWindow(hwnd, activate ? SW_SHOW : SW_SHOWNA);
        if (activate) SetForegroundWindow(hwnd);
        Log.Note($"panel open {ww}x{wh} at {x},{y} dpi={dpi} dark={th.Dark} preset={basePreset ?? "-"}{(readOnly != null ? " READ-ONLY" : "")}");
    }

    IntPtr smallIcon, bigIcon;

    void ApplyTheme()
    {
        th = Theme.Current();
        int dark = th.Dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    void MakeFonts()
    {
        FreeFonts();
        fontBody = CreateFontW(-D(13), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, CLEARTYPE_QUALITY, 0, "Microsoft JhengHei UI");
        fontTitle = CreateFontW(-D(12), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, CLEARTYPE_QUALITY, 0, "Microsoft JhengHei UI");
        fontValue = CreateFontW(-D(12), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");
    }

    void FreeFonts()
    {
        foreach (var f in new[] { fontBody, fontTitle, fontValue }) if (f != IntPtr.Zero) DeleteObject(f);
        fontBody = fontTitle = fontValue = IntPtr.Zero;
    }

    /// Closes and releases everything; trim = give the pages back afterwards (not when the app is exiting).
    public void Close(bool trim = true)
    {
        if (hwnd == IntPtr.Zero) return;
        if (dirty) Save();
        KillTimer(hwnd, (UIntPtr)TIMER_SAVE);
        var h = hwnd;
        hwnd = IntPtr.Zero;
        DestroyWindow(h);
        canvas?.Dispose(); canvas = null;
        FreeFonts();
        if (smallIcon != IntPtr.Zero) DestroyIcon(smallIcon);
        if (bigIcon != IntPtr.Zero) DestroyIcon(bigIcon);
        smallIcon = bigIcon = IntPtr.Zero;
        items.Clear(); titles.Clear();
        ed = null;
        if (current == this) current = null;
        Log.Note("panel closed");
        Closed?.Invoke();
        if (trim) app.TrimSoon("panel closed", 300);        // after the window's last messages are through
    }

    void Invalidate() { if (hwnd != IntPtr.Zero) InvalidateRect(hwnd, IntPtr.Zero, false); }

    // ── layout (content coordinates, physical pixels) ────────────────

    int Pad => D(16);
    int LabelW => D(88);
    int ValueW => D(50);
    int RowH => D(34);
    int ControlX => Pad + LabelW + D(8);

    void Layout(int width)
    {
        items.Clear(); titles.Clear();
        int y = D(8);
        if (readOnly != null) y += D(44);
        foreach (var g in groups)
        {
            if (g.Title.Length > 0) { y += D(10); titles.Add((g.Title, y)); y += D(24); }
            else y += D(14);
            foreach (var r in g.Rows) { items.Add(new Item { Row = r, Y = y, H = RowH }); y += RowH; }
        }
        contentH = y + D(14);
    }

    int ClientW => canvas?.W ?? D(400);
    int ClientH => canvas?.H ?? 1;
    int MaxScroll => Math.Max(0, contentH - ClientH);
    int SliderX => ControlX + D(8);
    int SliderW => ClientW - SliderX - Pad - ValueW - D(10);

    // ── painting ─────────────────────────────────────────────────────

    void Render()
    {
        if (GetClientRect(hwnd, out var rc) && (canvas == null || canvas.W != Math.Max(1, rc.Width) || canvas.H != Math.Max(1, rc.Height)))
        {
            canvas?.Dispose();
            canvas = new Canvas(rc.Width, rc.Height);
            Layout(canvas.W);
        }
        var c = canvas!;
        scrollY = Math.Clamp(scrollY, 0, MaxScroll);
        c.Clear(th.Back);
        var text = new List<Action>();          // GDI text after all the pixel shapes

        if (readOnly != null)
        {
            int y = D(8) - scrollY;
            c.RoundRect(Pad, y, ClientW - 2 * Pad, D(36), D(6), th.Input);
            text.Add(() => c.Text("⚠ " + readOnly, fontBody, th.Warn, Pad + D(10), y, ClientW - 2 * Pad - D(20), D(36)));
        }
        foreach (var (title, ty) in titles)
        {
            int y = ty - scrollY;
            if (y > ClientH || y + D(24) < 0) continue;
            text.Add(() => c.Text(title, fontTitle, th.Secondary, Pad, y, ClientW - 2 * Pad, D(24)));
        }
        foreach (var it in items)
        {
            int y = it.Y - scrollY;
            if (y > ClientH || y + it.H < 0) continue;
            DrawRow(c, it.Row, y, it.H, text);
        }
        // scrollbar: a thin thumb on the right, only when there is something to scroll
        if (MaxScroll > 0)
        {
            var (ty, th2) = Thumb();
            c.RoundRect(ClientW - D(7), ty, D(4), th2, D(2), th.Secondary, 0.55f);
        }
        c.Flush();
        foreach (var t in text) t();
    }

    (int y, int h) Thumb()
    {
        int track = ClientH - D(8);
        int h = Math.Max(D(24), (int)((long)track * ClientH / Math.Max(1, contentH)));
        int y = D(4) + (int)((long)(track - h) * scrollY / Math.Max(1, MaxScroll));
        return (y, h);
    }

    void DrawRow(Canvas c, Row row, int y, int h, List<Action> text)
    {
        int cx = ControlX, cw = ClientW - ControlX - Pad, mid = y + h / 2;
        bool enabled = readOnly == null;
        if (row is not (PresetRow or ButtonRow)) text.Add(() => c.Text(row.Label, fontBody, th.Text, Pad, y, LabelW, h));
        switch (row)
        {
            case Slider s:
            {
                double v = s.Get(cfg);
                float t = (float)ToPos(s, v);
                int x0 = SliderX, w = SliderW;
                c.RoundRect(x0, mid - D(2), w, D(4), D(2), th.Track);
                c.RoundRect(x0, mid - D(2), w * t, D(4), D(2), enabled ? th.Accent : th.Secondary);
                float kx = x0 + w * t;
                c.Circle(kx, mid, D(10), th.KnobBorder);
                c.Circle(kx, mid, D(9), th.Knob);
                c.Circle(kx, mid, drag?.Row == s ? D(4.5f) : D(6), enabled ? th.Accent : th.Secondary);
                string val = v.ToString("F" + s.Dp, System.Globalization.CultureInfo.InvariantCulture);
                text.Add(() => c.Text(val, fontValue, th.Secondary, ClientW - Pad - ValueW, y, ValueW, h, DT_RIGHT));
                break;
            }
            case Toggle tg:
            {
                bool on = tg.Get(cfg);
                int w = D(40), hh = D(20), x = cx, top = mid - hh / 2;
                if (on)
                {
                    c.RoundRect(x, top, w, hh, hh / 2f, enabled ? th.Accent : th.Secondary);
                    c.Circle(x + w - hh / 2f, mid, D(6), th.OnAccent);
                }
                else
                {
                    c.RoundRect(x, top, w, hh, hh / 2f, th.Secondary);
                    c.RoundRect(x + D(1), top + D(1), w - D(2), hh - D(2), hh / 2f - D(1), th.Back);
                    c.Circle(x + hh / 2f, mid, D(5), th.Secondary);
                }
                text.Add(() => c.Text(on ? "開" : "關", fontBody, th.Secondary, x + w + D(10), y, D(40), h));
                break;
            }
            case Choice ch:
            {
                string v = ch.Get(cfg);
                int n = ch.Values.Length, hh = D(26), top = mid - hh / 2;
                c.RoundRect(cx, top, cw, hh, D(5), th.Border);
                c.RoundRect(cx + D(1), top + D(1), cw - D(2), hh - D(2), D(4), th.Input);
                for (int i = 0; i < n; i++)
                {
                    int sx = cx + cw * i / n, sw = cw * (i + 1) / n - cw * i / n;
                    bool sel = ch.Values[i] == v;
                    if (sel) c.RoundRect(sx + D(2), top + D(2), sw - D(4), hh - D(4), D(3), enabled ? th.Accent : th.Secondary);
                    string title = ch.Titles[i];
                    uint col = sel ? th.OnAccent : th.Text;
                    text.Add(() => c.Text(title, fontBody, col, sx, top, sw, hh, DT_CENTER));
                }
                break;
            }
            case ColorRow cr:
            {
                string? v = cr.Get(cfg);
                string shown = v ?? cr.Auto?.Invoke(cfg) ?? "#000000";
                int w = D(44), hh = D(22), top = mid - hh / 2;
                c.RoundRect(cx, top, w, hh, D(4), th.Border);
                c.RoundRect(cx + D(1), top + D(1), w - D(2), hh - D(2), D(3), Canvas.ParseHex(shown));
                string label = v == null ? "自動" : cr.Auto != null ? v.ToUpperInvariant() + "　（右鍵：改回自動）" : v.ToUpperInvariant();
                text.Add(() => c.Text(label, fontValue, th.Secondary, cx + w + D(10), y, cw - w - D(10), h));
                break;
            }
            case PresetRow:
            {
                int bw = D(64), gap = D(8), dw = ClientW - 2 * Pad - bw - gap, hh = D(28), top = mid - hh / 2;
                c.RoundRect(Pad, top, dw, hh, D(5), th.Border);
                c.RoundRect(Pad + D(1), top + D(1), dw - D(2), hh - D(2), D(4), th.Input);
                c.RoundRect(Pad + dw + gap, top, bw, hh, D(5), th.Border);
                c.RoundRect(Pad + dw + gap + D(1), top + D(1), bw - D(2), hh - D(2), D(4), th.Input);
                float vx = Pad + dw - D(20), vy = mid - D(1.5f);            // chevron
                c.Capsule(vx - D(4), vy - D(2), vx, vy + D(2), D(1.5f), th.Secondary);
                c.Capsule(vx, vy + D(2), vx + D(4), vy - D(2), D(1.5f), th.Secondary);
                string name = basePreset == null ? "自訂" : presetMatches ? basePreset : basePreset + "（已修改）";
                text.Add(() =>
                {
                    c.Text(name, fontBody, th.Text, Pad + D(10), top, dw - D(36), hh);
                    c.Text("重設", fontBody, basePreset != null ? th.Text : th.Secondary, Pad + dw + gap, top, bw, hh, DT_CENTER);
                });
                break;
            }
            case ButtonRow b:
            {
                int hh = D(30), top = mid - hh / 2, w = ClientW - 2 * Pad;
                c.RoundRect(Pad, top, w, hh, D(5), th.Border);
                c.RoundRect(Pad + D(1), top + D(1), w - D(2), hh - D(2), D(4), th.Input);
                text.Add(() => c.Text(b.Text, fontBody, th.Text, Pad, top, w, hh, DT_CENTER));
                break;
            }
        }
    }

    static double ToPos(Slider s, double v)
    {
        double t = s.Log ? Math.Log(Math.Max(v, s.Min) / s.Min) / Math.Log(s.Max / s.Min) : (v - s.Min) / (s.Max - s.Min);
        return Math.Clamp(t, 0, 1);
    }

    static double FromPos(Slider s, double t)
    {
        t = Math.Clamp(t, 0, 1);
        double v = s.Log ? s.Min * Math.Pow(s.Max / s.Min, t) : s.Min + t * (s.Max - s.Min);
        if (s.Log && v >= 200) v = Math.Round(v / 10) * 10;          // 1230, not 1234: the top of a log scale is coarse anyway
        return Math.Round(v, s.Dp, MidpointRounding.AwayFromZero);
    }

    // ── input ────────────────────────────────────────────────────────

    Item? HitRow(int y)
    {
        int cy = y + scrollY;
        foreach (var it in items) if (cy >= it.Y && cy < it.Y + it.H) return it;
        return null;
    }

    void OnDown(int x, int y, bool right)
    {
        if (MaxScroll > 0 && x >= ClientW - D(12))
        {
            var (ty, th2) = Thumb();
            if (y >= ty && y < ty + th2) { dragThumb = true; dragThumbFrom = y; dragScrollFrom = scrollY; SetCapture(hwnd); return; }
            ScrollBy(y < ty ? -ClientH + RowH : ClientH - RowH);
            return;
        }
        var it = HitRow(y);
        if (it == null) return;
        if (it.Row is ButtonRow b) { if (!right) b.Act(this); return; }
        if (it.Row is PresetRow) { if (!right) PresetClick(x, it); return; }
        if (readOnly != null || ed == null) return;
        int cx = ControlX, cw = ClientW - ControlX - Pad;
        switch (it.Row)
        {
            case Slider s when !right && x >= SliderX - D(12) && x <= SliderX + SliderW + D(12):
                drag = it; focus = it;
                SetCapture(hwnd);
                SlideTo(s, x);
                break;
            case Toggle tg when !right && x < cx + D(90):
                Change(e => e.SetBool(tg.Path, !tg.Get(cfg)));
                break;
            case Choice ch when !right && x >= cx && x < cx + cw:
                int i = Math.Clamp((x - cx) * ch.Values.Length / Math.Max(1, cw), 0, ch.Values.Length - 1);
                if (ch.Values[i] != ch.Get(cfg)) Change(e => e.SetString(ch.Path, ch.Values[i]));
                break;
            case ColorRow cr when x >= cx:
                if (right) { if (cr.Auto != null && cr.Get(cfg) != null) Change(e => e.Remove(cr.Path)); }
                else PickColor(cr);
                break;
        }
    }

    void SlideTo(Slider s, int x)
    {
        double v = FromPos(s, (x - SliderX) / (double)Math.Max(1, SliderW));
        if (Math.Abs(v - s.Get(cfg)) < Math.Pow(10, -s.Dp) / 2) { Invalidate(); return; }
        Change(e => { e.SetNumber(s.Path, v, s.Dp); s.Also?.Invoke(e, v); });
    }

    void Step(Slider s, int dir)
    {
        double v = s.Get(cfg);
        double t = ToPos(s, v) + dir * 0.01;
        double nv = FromPos(s, t);
        if (nv == Math.Round(v, s.Dp)) nv = Math.Round(v + dir * Math.Pow(10, -s.Dp), s.Dp);
        nv = Math.Clamp(nv, s.Min, s.Max);
        Change(e => { e.SetNumber(s.Path, nv, s.Dp); s.Also?.Invoke(e, nv); });
    }

    void ScrollBy(int dy)
    {
        int n = Math.Clamp(scrollY + dy, 0, MaxScroll);
        if (n != scrollY) { scrollY = n; Invalidate(); }
    }

    /// One edit: into the tree, straight to the wallpaper, onto disk when things settle.
    void Change(Action<ConfigEdit> edit)
    {
        if (ed == null) return;
        edit(ed);
        cfg = ed.ToConfig();
        app.ApplyLive(cfg);
        presetMatches = false;
        dirty = true;
        SetTimer(hwnd, (UIntPtr)TIMER_SAVE, 400, IntPtr.Zero);
        Invalidate();
    }

    void Save()
    {
        KillTimer(hwnd, (UIntPtr)TIMER_SAVE);
        if (!dirty || ed == null) return;
        try
        {
            ed.Save(Config.FilePath);
            app.SavedByUi();
            dirty = false;
            presetMatches = basePreset != null && ConfigEdit.MatchPreset(cfg) == basePreset;
            Log.Note($"panel saved: world particles={app.Cfg.Motion.ParticleCount} fps={app.Cfg.Motion.Fps} effect={app.Cfg.Motion.Effect}");
        }
        catch (Exception e) { Log.Note("⚠ panel cannot save: " + e.Message); }
        Invalidate();
    }

    void PresetClick(int x, Item it)
    {
        int bw = D(64), gap = D(8), dw = ClientW - 2 * Pad - bw - gap;
        if (x >= Pad + dw + gap)
        {
            if (basePreset != null) app.ApplyPreset(basePreset);    // reset: back to the style the edits started from
            return;
        }
        var names = Presets.Names().ToList();
        if (names.Count == 0) return;
        var m = CreatePopupMenu();
        string? cur = presetMatches ? basePreset : null;
        for (int i = 0; i < names.Count; i++) AppendMenuW(m, MF_STRING | (names[i] == cur ? MF_CHECKED : 0), (UIntPtr)(i + 1), names[i]);
        var pt = new POINT(Pad, it.Y - scrollY + it.H / 2 + D(14));
        ClientToScreen(hwnd, ref pt);
        app.StartModalFrames();
        int cmd;
        try { cmd = TrackPopupMenuEx(m, TPM_LEFTALIGN | TPM_RETURNCMD | TPM_NONOTIFY, pt.X, pt.Y, hwnd, IntPtr.Zero); }
        finally { app.StopModalFrames(); DestroyMenu(m); }
        if (cmd <= 0) return;
        if (dirty) Save();
        basePreset = names[cmd - 1];
        app.ApplyPreset(basePreset);                            // → EditConfig → Reload() refreshes us
    }

    void PickColor(ColorRow cr)
    {
        if (custColors == IntPtr.Zero) custColors = Marshal.AllocHGlobal(16 * 4);
        // the palette of the current style as custom colours
        var seed = new[] { cfg.Motion.ColorA, cfg.Motion.ColorB, cfg.Motion.Link.Color, cfg.Background.CenterColor, cfg.Background.EdgeColor, cfg.Background.SolidColor };
        for (int i = 0; i < 16; i++) ((uint*)custColors)[i] = i < seed.Length && seed[i] != null ? Canvas.Rgb2Ref(Canvas.ParseHex(seed[i])) : 0xFFFFFF;
        string cur = cr.Get(cfg) ?? cr.Auto?.Invoke(cfg) ?? "#000000";
        var cc = new CHOOSECOLORW
        {
            lStructSize = sizeof(CHOOSECOLORW), hwndOwner = hwnd, rgbResult = Canvas.Rgb2Ref(Canvas.ParseHex(cur)),
            lpCustColors = custColors, Flags = CC_RGBINIT | CC_FULLOPEN | CC_ANYCOLOR,
        };
        app.StartModalFrames();
        bool ok;
        try { ok = ChooseColorW(ref cc); }
        finally { app.StopModalFrames(); }
        if (!ok || hwnd == IntPtr.Zero) return;
        var hex = Canvas.Hex(Canvas.Ref2Rgb(cc.rgbResult));
        if (!string.Equals(hex, cr.Get(cfg), StringComparison.OrdinalIgnoreCase)) Change(e => e.SetString(cr.Path, hex));
    }

    // ── messages ─────────────────────────────────────────────────────

    static IntPtr Proc(IntPtr h, uint msg, IntPtr wp, IntPtr lp)
    {
        var p = current;
        if (p != null && (p.hwnd == h || p.hwnd == IntPtr.Zero)) return p.OnMessage(h, msg, wp, lp);
        return DefWindowProcW(h, msg, wp, lp);
    }

    static int X(IntPtr lp) => (short)((long)lp & 0xFFFF);
    static int Y(IntPtr lp) => (short)(((long)lp >> 16) & 0xFFFF);

    IntPtr OnMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp)
    {
        switch (msg)
        {
            case WM_PAINT:
            {
                var dc = BeginPaint(h, out var ps);
                Render();
                canvas!.BlitTo(dc, 0, 0);
                EndPaint(h, ref ps);
                return IntPtr.Zero;
            }
            case WM_PRINTCLIENT:                                   // PrintWindow (screenshots)
                Render();
                canvas!.BlitTo(wp, 0, 0);
                return IntPtr.Zero;
            case WM_ERASEBKGND: return new IntPtr(1);
            case WM_SIZE:
                Invalidate();
                return IntPtr.Zero;
            case WM_GETMINMAXINFO:
                ((MINMAXINFO*)lp)->minTrackSize = new POINT(D(340), D(220));
                return IntPtr.Zero;
            case WM_LBUTTONDOWN: case WM_LBUTTONDBLCLK: OnDown(X(lp), Y(lp), false); return IntPtr.Zero;
            case 0x204: OnDown(X(lp), Y(lp), true); return IntPtr.Zero;              // WM_RBUTTONDOWN
            case WM_MOUSEMOVE:
                if (dragThumb)
                {
                    var (_, th2) = Thumb();
                    int track = ClientH - D(8) - th2;
                    int ny = dragScrollFrom + (int)((long)(Y(lp) - dragThumbFrom) * MaxScroll / Math.Max(1, track));
                    ScrollBy(ny - scrollY);
                }
                else if (drag?.Row is Slider s) SlideTo(s, X(lp));
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                if (drag != null || dragThumb) { drag = null; dragThumb = false; ReleaseCapture(); Invalidate(); }
                return IntPtr.Zero;
            case WM_CAPTURECHANGED:
                if (drag != null || dragThumb) { drag = null; dragThumb = false; Invalidate(); }
                return IntPtr.Zero;
            case WM_MOUSEWHEEL:
                ScrollBy(-(short)(((long)wp >> 16) & 0xFFFF) * RowH * 3 / 120);
                return IntPtr.Zero;
            case WM_KEYDOWN:
                switch ((int)wp)
                {
                    case 0x1B: Close(); break;                                          // Esc
                    case 0x25: case 0x27:                                               // ← →
                        if (focus?.Row is Slider fs && readOnly == null) Step(fs, (int)wp == 0x27 ? 1 : -1);
                        break;
                    case 0x26: ScrollBy(-RowH); break;
                    case 0x28: ScrollBy(RowH); break;
                    case 0x21: ScrollBy(-ClientH + RowH); break;                        // PgUp
                    case 0x22: ScrollBy(ClientH - RowH); break;
                    case 0x24: ScrollBy(-contentH); break;                               // Home
                    case 0x23: ScrollBy(contentH); break;
                }
                return IntPtr.Zero;
            case WM_TIMER:
                if ((uint)wp == TIMER_SAVE) Save();
                return IntPtr.Zero;
            case WM_ENTERSIZEMOVE: app.StartModalFrames(); return IntPtr.Zero;
            case WM_EXITSIZEMOVE: app.StopModalFrames(); return IntPtr.Zero;
            case WM_DPICHANGED:
            {
                scale = ((uint)wp & 0xFFFF) / 96f;
                MakeFonts();
                var r = (RECT*)lp;
                SetWindowPos(h, IntPtr.Zero, r->Left, r->Top, r->Width, r->Height, SWP_NOZORDER | SWP_NOACTIVATE);
                canvas?.Dispose(); canvas = null;
                Invalidate();
                return IntPtr.Zero;
            }
            case WM_SETTINGCHANGE:
                if (lp != IntPtr.Zero && Marshal.PtrToStringUni(lp) == "ImmersiveColorSet") { ApplyTheme(); Invalidate(); }
                break;
            case WM_CLOSE:
                Close();
                return IntPtr.Zero;
        }
        return DefWindowProcW(h, msg, wp, lp);
    }

    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
}
