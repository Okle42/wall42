using System.Runtime.InteropServices;
using static Wall42.Native;
using static Wall42.UiNative;

namespace Wall42;

/// The notification-area icon: the Windows home of MenuBar.swift. Plain Shell_NotifyIcon + TrackPopupMenu
/// (no WinForms: that would add ~15-20 MB to a process that is otherwise just a render loop). The menu is
/// built when it opens and destroyed when it closes; nothing runs while it's closed.
/// Left click (or Enter on the icon) opens the control panel, right click the menu.
sealed unsafe class Tray : IDisposable
{
    public const string ClassName = "wall42.tray";
    const uint WM_TRAY = WM_APP + 1;
    /// test hooks, posted by windows\tools\UiProbe (never used by the app itself):
    /// WM_DEMO_MENU shows the menu at (wParam, lParam) without taking the foreground, closing itself after 3 s;
    /// WM_DEMO_PANEL opens the panel at (wParam, lParam) without activating it. WM_COMMAND runs a menu command.
    public const uint WM_DEMO_MENU = WM_APP + 2, WM_DEMO_PANEL = WM_APP + 3;
    public const int CMD_PAUSE = 10, CMD_PANEL = 11, CMD_FOLDER = 12, CMD_QUIT = 13,
        CMD_ACT_SYSTEM = 20, CMD_ACT_MANUAL = 21, CMD_ACT_OFF = 22, CMD_PRESET = 100;
    static readonly string[] ActivitySources = { "system", "manual", "off" };

    static readonly WndProc proc = Proc;
    static Tray? current;
    static bool classRegistered;

    readonly App app;
    readonly IntPtr hwnd;
    readonly uint taskbarCreated;
    IntPtr icon;
    bool iconPaused, added, failLogged;
    List<string> menuPresets = new();
    readonly UiNative.TimerProc endMenuProc = (_, _, id, _) => { EndMenu(); KillTimer(IntPtr.Zero, id); };

    public Tray(App app)
    {
        this.app = app;
        current = this;
        var inst = GetModuleHandleW(null);
        if (!classRegistered)
        {
            var wc = new WNDCLASSEX { cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc), hInstance = inst, lpszClassName = ClassName };
            if (RegisterClassExW(ref wc) == 0) throw new InvalidOperationException("RegisterClassEx tray " + Marshal.GetLastWin32Error());
            classRegistered = true;
        }
        // a hidden top-level window (message-only windows miss TaskbarCreated, which re-adds the icon)
        hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, ClassName, "wall42 tray", WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        try { SetPreferredAppMode(1); FlushMenuThemes(); } catch { }        // menus follow the dark app theme
        Add();
    }

    public IntPtr Hwnd => hwnd;

    void Add()
    {
        SetIcon(app.ManualPaused, force: true);
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        added = Shell_NotifyIconW(NIM_ADD, ref d);
        d.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref d);
        if (added || !failLogged) Log.Note($"tray icon added={added}");
        failLogged = !added;
    }

    NOTIFYICONDATAW Data(uint flags)
    {
        var d = new NOTIFYICONDATAW { cbSize = sizeof(NOTIFYICONDATAW), hWnd = hwnd, uID = 1, uFlags = flags, uCallbackMessage = WM_TRAY, hIcon = icon };
        SetText(d.szTip, 128, app.ManualPaused ? "wall42（已暫停）" : "wall42");
        return d;
    }

    /// Redraws the icon only when its state changes (grey while manually paused).
    void SetIcon(bool paused, bool force = false)
    {
        if (!force && paused == iconPaused && icon != IntPtr.Zero) return;
        iconPaused = paused;
        var old = icon;
        uint dpi = GetDpiForWindow(hwnd);
        icon = Icons.Make(GetSystemMetricsForDpi(SM_CXSMICON, dpi == 0 ? 96 : dpi), paused);
        if (added)
        {
            var d = Data(NIF_ICON | NIF_TIP | NIF_SHOWTIP);
            Shell_NotifyIconW(NIM_MODIFY, ref d);
        }
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    public void Refresh() => SetIcon(app.ManualPaused);

    /// Started with Windows before the taskbar was ready: NIM_ADD fails and TaskbarCreated may already be
    /// past. The 1 s tick calls this every few seconds until the icon is in.
    public void RetryIfMissing() { if (!added) Add(); }

    static IntPtr Proc(IntPtr h, uint msg, IntPtr wp, IntPtr lp)
    {
        var t = current;
        if (t != null && h == t.hwnd) return t.OnMessage(msg, wp, lp);
        return DefWindowProcW(h, msg, wp, lp);
    }

    IntPtr OnMessage(uint msg, IntPtr wp, IntPtr lp)
    {
        if (msg == taskbarCreated && taskbarCreated != 0)
        {
            added = false;
            Add();                                       // Explorer restarted: the icon is gone
            return IntPtr.Zero;
        }
        switch (msg)
        {
            case WM_TRAY:
                uint ev = (uint)lp & 0xFFFF;
                int x = (short)((long)wp & 0xFFFF), y = (short)(((long)wp >> 16) & 0xFFFF);
                if (ev == WM_CONTEXTMENU) ShowMenu(x, y, demo: false);
                else if (ev is NIN_SELECT or NIN_KEYSELECT) app.ShowPanel();
                return IntPtr.Zero;
            case WM_COMMAND:
                Run((int)((long)wp & 0xFFFF));
                return IntPtr.Zero;
            case WM_DEMO_MENU:
                ShowMenu((int)wp, (int)lp, demo: true);
                return IntPtr.Zero;
            case WM_DEMO_PANEL:
                app.ShowPanel($"{(int)wp},{(int)lp},noactivate");
                return IntPtr.Zero;
            case WM_SETTINGCHANGE:
                if (lp != IntPtr.Zero && Marshal.PtrToStringUni(lp) == "ImmersiveColorSet") try { FlushMenuThemes(); } catch { }   // dark ↔ light
                SetIcon(app.ManualPaused, force: true);
                return DefWindowProcW(hwnd, msg, wp, lp);
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                SetIcon(app.ManualPaused, force: true);          // taskbar DPI may have changed
                return DefWindowProcW(hwnd, msg, wp, lp);
        }
        return DefWindowProcW(hwnd, msg, wp, lp);
    }

    void ShowMenu(int x, int y, bool demo)
    {
        var menu = Build();
        try
        {
            // the documented dance: without it the menu doesn't close when you click elsewhere. (Demo: never
            // take the foreground from whoever has it; the menu closes itself instead.)
            if (!demo) SetForegroundWindow(hwnd);
            else SetTimer(IntPtr.Zero, UIntPtr.Zero, 3000, endMenuProc);
            uint align = GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : TPM_LEFTALIGN;
            app.StartModalFrames();
            int cmd;
            try { cmd = TrackPopupMenuEx(menu, align | TPM_BOTTOMALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, x, y, hwnd, IntPtr.Zero); }
            finally { app.StopModalFrames(); }
            if (!demo) PostMessageW(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (cmd != 0) Run(cmd);
        }
        finally { DestroyMenu(menu); }
        app.TrimSoon("menu closed", 1500);             // the menu's theme/rendering pages aren't needed until next time
    }

    IntPtr Build()
    {
        var m = CreatePopupMenu();
        AppendMenuW(m, MF_STRING | MF_GRAYED, (UIntPtr)1, app.StatusLine());
        AppendMenuW(m, MF_STRING | MF_GRAYED, (UIntPtr)2, app.ActivityLine());
        AppendMenuW(m, MF_SEPARATOR, UIntPtr.Zero, null);

        var presets = CreatePopupMenu();
        menuPresets = Presets.Names().ToList();
        var cur = menuPresets.Count > 0 ? ConfigEdit.MatchPreset(app.Cfg) : null;
        for (int i = 0; i < menuPresets.Count; i++)
            AppendMenuW(presets, MF_STRING | (menuPresets[i] == cur ? MF_CHECKED : 0), (UIntPtr)(CMD_PRESET + i), menuPresets[i]);
        if (menuPresets.Count == 0) AppendMenuW(presets, MF_STRING | MF_GRAYED, (UIntPtr)3, "（找不到 presets 資料夾）");
        AppendMenuW(m, MF_POPUP, (UIntPtr)(ulong)presets, "風格" + (cur != null ? $"：{cur}" : "：自訂"));

        var act = CreatePopupMenu();
        AppendMenuW(act, MF_STRING, (UIntPtr)CMD_ACT_SYSTEM, "系統負載");
        AppendMenuW(act, MF_STRING, (UIntPtr)CMD_ACT_MANUAL, "手動");
        AppendMenuW(act, MF_STRING, (UIntPtr)CMD_ACT_OFF, "關");
        int ai = Array.IndexOf(ActivitySources, app.Cfg.Motion.Activity.Source);
        CheckMenuRadioItem(act, CMD_ACT_SYSTEM, CMD_ACT_OFF, (uint)(CMD_ACT_SYSTEM + Math.Max(0, ai)), MF_BYCOMMAND);
        AppendMenuW(m, MF_POPUP, (UIntPtr)(ulong)act, "忙碌程度");
        AppendMenuW(m, MF_SEPARATOR, UIntPtr.Zero, null);

        AppendMenuW(m, MF_STRING, (UIntPtr)CMD_PAUSE, app.ManualPaused ? "繼續繪製" : "暫停繪製");
        AppendMenuW(m, MF_STRING, (UIntPtr)CMD_PANEL, "開啟控制面板…");
        SetMenuDefaultItem(m, CMD_PANEL, 0);                 // bold: what a left click does
        AppendMenuW(m, MF_STRING, (UIntPtr)CMD_FOLDER, "開啟設定檔資料夾");
        AppendMenuW(m, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenuW(m, MF_STRING, (UIntPtr)CMD_QUIT, "結束 wall42");
        return m;
    }

    void Run(int cmd)
    {
        switch (cmd)
        {
            case CMD_PAUSE:
                app.SetManualPause(!app.ManualPaused);
                Refresh();
                break;
            case CMD_PANEL: app.ShowPanel(); break;
            case CMD_FOLDER: app.OpenConfigFolder(); break;
            case CMD_QUIT: app.Quit(); break;
            case >= CMD_ACT_SYSTEM and <= CMD_ACT_OFF: app.SetActivitySource(ActivitySources[cmd - CMD_ACT_SYSTEM]); break;
            case >= CMD_PRESET:
                // a posted command (test) may come without the menu having been built
                if (menuPresets.Count == 0) menuPresets = Presets.Names().ToList();
                if (cmd - CMD_PRESET < menuPresets.Count) app.ApplyPreset(menuPresets[cmd - CMD_PRESET]);
                break;
        }
    }

    public void Dispose()
    {
        if (added) { var d = Data(0); Shell_NotifyIconW(NIM_DELETE, ref d); added = false; }
        if (icon != IntPtr.Zero) { DestroyIcon(icon); icon = IntPtr.Zero; }
        DestroyWindow(hwnd);
        if (current == this) current = null;
    }
}
