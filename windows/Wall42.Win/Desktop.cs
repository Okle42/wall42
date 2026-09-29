using static Wall42.Native;

namespace Wall42;

record struct Monitor(IntPtr Handle, MonitorRect Rect, uint Dpi, string Device);

/// Monitors, and the desktop layer between the wallpaper and the icons.
static class Desktop
{
    public static List<Monitor> Monitors()
    {
        var list = new List<Monitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) =>
        {
            var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfoW(h, ref mi)) return true;
            uint dpi = GetDpiForMonitor(h, 0, out var dx, out _) == 0 ? dx : 96;     // MDT_EFFECTIVE_DPI
            var m = mi.rcMonitor; var w = mi.rcWork;
            list.Add(new Monitor(h, new MonitorRect(m.Left, m.Top, m.Right, m.Bottom, w.Top, w.Bottom, (mi.dwFlags & MONITORINFOF_PRIMARY) != 0), dpi, mi.szDevice));
            return true;
        }, IntPtr.Zero);
        // primary first, like NSScreen.screens[0]; the rest left to right
        return list.OrderByDescending(m => m.Rect.Primary).ThenBy(m => m.Rect.Left).ThenBy(m => m.Rect.Top).ToList();
    }

    /// The window to parent our monitor windows to, so they sit above the wallpaper and below the icons.
    /// 0x052C asks Explorer to split the desktop into a wallpaper layer and an icon layer (the WorkerW trick
    /// every live-wallpaper app uses; there is no official API).
    ///  - Windows 11 24H2+: the WorkerW is a CHILD of Progman, sibling below SHELLDLL_DefView.
    ///  - before 24H2: the WorkerW is a top-level window right behind the one that holds SHELLDLL_DefView.
    public static (IntPtr Worker, string How) FindWorkerW()
    {
        var progman = FindWindowW("Progman", null);
        if (progman == IntPtr.Zero) return (IntPtr.Zero, "no Progman (Explorer not running?)");
        SendMessageTimeoutW(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), SMTO_NORMAL, 1000, out _);

        var child = FindWindowExW(progman, IntPtr.Zero, "WorkerW", null);
        if (child != IntPtr.Zero) return (child, "24H2: WorkerW child of Progman");

        IntPtr worker = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (FindWindowExW(h, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero) return true;
            worker = FindWindowExW(IntPtr.Zero, h, "WorkerW", null);         // the next WorkerW after the icons' window
            return false;
        }, IntPtr.Zero);
        if (worker != IntPtr.Zero) return (worker, "pre-24H2: top-level WorkerW behind the icons");
        return (IntPtr.Zero, "no WorkerW found");
    }

    /// One line per window in the Progman/WorkerW neighbourhood (for the log and the W1 verification).
    public static string Describe(IntPtr worker)
    {
        var progman = FindWindowW("Progman", null);
        var sb = new System.Text.StringBuilder();
        sb.Append($"Progman={progman:X} parentOfWorker={GetParent(worker):X} worker={worker:X}");
        var kids = new List<string>();
        for (var h = FindWindowExW(progman, IntPtr.Zero, null, null); h != IntPtr.Zero; h = FindWindowExW(progman, h, null, null))
            kids.Add($"{ClassOf(h)}:{h:X}");
        sb.Append(" ProgmanChildren(z-order top→bottom)=[" + string.Join(", ", kids) + "]");
        return sb.ToString();
    }
}
