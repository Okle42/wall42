using System.Runtime.InteropServices;

namespace Wall42;

/// Win32 for the tray icon and the control panel (kept apart from Native.cs, which is the renderer's).
static unsafe class UiNative
{
    // ── notification area ──
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public int cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon;
        public fixed char szTip[128];
        public uint dwState, dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_SHOWTIP = 0x80;
    public const uint NOTIFYICON_VERSION_4 = 4;
    public const uint NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401;
    [DllImport("shell32.dll")] public static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);

    // ── menus ──
    public const uint MF_STRING = 0, MF_GRAYED = 1, MF_DISABLED = 2, MF_CHECKED = 8, MF_POPUP = 0x10, MF_SEPARATOR = 0x800, MF_BYCOMMAND = 0;
    public const uint TPM_LEFTALIGN = 0, TPM_RIGHTALIGN = 8, TPM_BOTTOMALIGN = 0x20, TPM_RIGHTBUTTON = 2, TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100;
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] public static extern bool CheckMenuRadioItem(IntPtr menu, uint first, uint last, uint check, uint flags);
    [DllImport("user32.dll")] public static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPos);
    [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] public static extern bool EndMenu();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int idx);
    [DllImport("user32.dll")] public static extern int GetSystemMetricsForDpi(int idx, uint dpi);
    public const int SM_CXSMICON = 49, SM_CXICON = 11, SM_MENUDROPALIGNMENT = 40;

    // uxtheme's unnamed exports (stable since 1903): let Win32 popup menus follow the dark app theme
    [DllImport("uxtheme.dll", EntryPoint = "#135")] public static extern int SetPreferredAppMode(int mode);   // 1 = AllowDark
    [DllImport("uxtheme.dll", EntryPoint = "#136")] public static extern void FlushMenuThemes();

    // ── icons, GDI ──
    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO { public int fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage,
            biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
    [DllImport("user32.dll")] public static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, IntPtr bits);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] public static extern bool GdiFlush();
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike,
        uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] public static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")] public static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int DrawTextW(IntPtr hdc, string text, int len, ref Native.RECT r, uint fmt);
    public const uint SRCCOPY = 0x00CC0020;
    public const uint DT_LEFT = 0, DT_CENTER = 1, DT_RIGHT = 2, DT_VCENTER = 4, DT_SINGLELINE = 0x20, DT_NOPREFIX = 0x800, DT_END_ELLIPSIS = 0x8000,
        DT_WORDBREAK = 0x10;
    public const uint CLEARTYPE_QUALITY = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT { public IntPtr hdc; public int fErase; public Native.RECT rc; public int fRestore, fIncUpdate; public fixed byte rgb[32]; }
    [DllImport("user32.dll")] public static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] public static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] public static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    // ── windows ──
    public const uint WS_OVERLAPPED = 0, WS_CAPTION = 0xC00000, WS_SYSMENU = 0x80000, WS_THICKFRAME = 0x40000, WS_MINIMIZEBOX = 0x20000;
    public const int SW_SHOW = 5, SW_SHOWNA = 8, SW_RESTORE = 9;
    public const uint WM_SETICON = 0x80, WM_PAINT = 0xF, WM_PRINTCLIENT = 0x318, WM_SIZE = 5, WM_GETMINMAXINFO = 0x24, WM_KEYDOWN = 0x100,
        WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203, WM_MOUSEWHEEL = 0x20A,
        WM_CAPTURECHANGED = 0x215, WM_ENTERSIZEMOVE = 0x231, WM_EXITSIZEMOVE = 0x232, WM_COMMAND = 0x111, WM_CONTEXTMENU = 0x7B,
        WM_NULL = 0, WM_APP = 0x8000, WM_SETCURSOR = 0x20, WM_MOUSELEAVE = 0x2A3, WM_ACTIVATE = 6;
    public const uint CS_DBLCLKS = 8;
    [StructLayout(LayoutKind.Sequential)] public struct MINMAXINFO { public Native.POINT reserved, maxSize, maxPosition, minTrackSize, maxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public Native.RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] public static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool AdjustWindowRectExForDpi(ref Native.RECT r, uint style, bool menu, int exStyle, uint dpi);
    [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr GetCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr LoadCursorW(IntPtr inst, IntPtr id);
    [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr cursor);
    public static readonly IntPtr IDC_ARROW = new(32512), IDC_HAND = new(32649);
    public delegate void TimerProc(IntPtr hwnd, uint msg, UIntPtr id, uint time);
    [DllImport("user32.dll")] public static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, TimerProc? proc);

    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_SYSTEMBACKDROP_TYPE = 38;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr ShellExecuteW(IntPtr hwnd, string? op, string file, string? args, string? dir, int show);

    [StructLayout(LayoutKind.Sequential)]
    public struct CHOOSECOLORW
    {
        public int lStructSize; public IntPtr hwndOwner, hInstance; public uint rgbResult; public IntPtr lpCustColors; public uint Flags;
        public IntPtr lCustData, lpfnHook, lpTemplateName;
    }
    public const uint CC_RGBINIT = 1, CC_FULLOPEN = 2, CC_ANYCOLOR = 0x100;
    [DllImport("comdlg32.dll")] public static extern bool ChooseColorW(ref CHOOSECOLORW cc);

    [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    public static void SetText(char* dst, int cap, string s)
    {
        int n = Math.Min(s.Length, cap - 1);
        for (int i = 0; i < n; i++) dst[i] = s[i];
        dst[n] = '\0';
    }

    /// HKCU …\Themes\Personalize: AppsUseLightTheme = 0 ⇒ dark apps; SystemUsesLightTheme = 0 ⇒ dark taskbar.
    public static bool AppsDark() => ReadThemeFlag("AppsUseLightTheme");
    public static bool TaskbarDark() => ReadThemeFlag("SystemUsesLightTheme");
    static bool ReadThemeFlag(string name)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue(name) is int v && v == 0;
        }
        catch { return false; }
    }

    /// Everything the panel/menu touched once and won't touch again goes back to the OS (ShoWork42's
    /// SettingsWindow does the same). What the renderer still uses comes back as soft faults.
    public static void TrimWorkingSet()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        SetProcessWorkingSetSize(Native.GetCurrentProcess(), -1, -1);
    }
}
