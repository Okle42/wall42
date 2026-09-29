using System.Runtime.InteropServices;
using System.Text;

// usage: Inspect [capture.png]
// Prints the Progman/WorkerW tree with z-order, styles of wall42's windows, and what WindowFromPoint hits.
// Never moves, focuses or changes any window.
static class P
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; public override string ToString() => $"({L},{T})-({R},{B})"; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string? c, string? t);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr p, IntPtr a, string? c, string? t);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint f);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr ChildWindowFromPointEx(IntPtr p, POINT pt, uint flags);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

    static string Cls(IntPtr h) { var s = new StringBuilder(128); GetClassNameW(h, s, 128); return s.ToString(); }
    static string Desc(IntPtr h)
    {
        GetWindowRect(h, out var r);
        GetWindowThreadProcessId(h, out var pid);
        long ex = (long)GetWindowLongPtrW(h, -20), st = (long)GetWindowLongPtrW(h, -16);
        return $"{Cls(h)} {h:X} pid={pid} vis={IsWindowVisible(h)} rect={r} style={st:X8} ex={ex:X8}";
    }

    static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var progman = FindWindowW("Progman", null);
        Console.WriteLine("Progman: " + Desc(progman));
        Console.WriteLine("Progman direct children, z-order top -> bottom:");
        int z = 0;
        for (var h = FindWindowExW(progman, IntPtr.Zero, null, null); h != IntPtr.Zero; h = FindWindowExW(progman, h, null, null))
        {
            Console.WriteLine($"  [{z++}] " + Desc(h));
            for (var c = FindWindowExW(h, IntPtr.Zero, null, null); c != IntPtr.Zero; c = FindWindowExW(h, c, null, null))
                Console.WriteLine("        child " + Desc(c));
        }
        var ours = new List<IntPtr>();
        EnumChildWindows(progman, (h, _) => { if (Cls(h) == "wall42.surface") ours.Add(h); return true; }, IntPtr.Zero);
        Console.WriteLine($"wall42 surfaces under Progman: {ours.Count}");
        foreach (var h in ours)
        {
            long ex = (long)GetWindowLongPtrW(h, -20), st = (long)GetWindowLongPtrW(h, -16);
            var parent = GetParent(h);
            Console.WriteLine($"  {h:X} parent={Cls(parent)} {parent:X} grandparent={Cls(GetParent(parent))} {GetParent(parent):X} " +
                              $"WS_CHILD={(st & 0x40000000) != 0} WS_DISABLED={(st & 0x08000000) != 0} " +
                              $"WS_EX_TRANSPARENT={(ex & 0x20) != 0} WS_EX_NOACTIVATE={(ex & 0x8000000) != 0} WS_EX_TOOLWINDOW={(ex & 0x80) != 0}");
        }
        Console.WriteLine("foreground: " + Desc(GetForegroundWindow()));
        foreach (var s in Screen.AllScreens)
            foreach (var (fx, fy) in new[] { (0.5, 0.5), (0.03, 0.05), (0.97, 0.9) })
            {
                var p = new POINT { X = s.Bounds.Left + (int)(s.Bounds.Width * fx), Y = s.Bounds.Top + (int)(s.Bounds.Height * fy) };
                var h = WindowFromPoint(p);
                Console.WriteLine($"  WindowFromPoint({p.X},{p.Y}) = {Cls(h)} {h:X} root={Cls(GetAncestor(h, 2))} isWall42={Cls(h) == "wall42.surface"}");
                // the desktop part of the hit test: inside Progman the topmost visible, enabled child wins (then its child)
                var c1 = ChildWindowFromPointEx(progman, p, 1 | 2);
                var c2 = c1 == IntPtr.Zero || c1 == progman ? c1 : ChildWindowFromPointEx(c1, p, 1 | 2);
                Console.WriteLine($"    inside Progman: {Cls(c1)} {c1:X} -> {Cls(c2)} {c2:X}");
            }
        // what is under the maximised windows: the desktop layer itself (no window is moved or activated)
        var icons = FindWindowExW(FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null), IntPtr.Zero, "SysListView32", null);
        if (icons != IntPtr.Zero)
        {
            GetWindowRect(icons, out var ir);
            Console.WriteLine("icons list: " + Desc(icons) + $" covers ({ir.L},{ir.T})-({ir.R},{ir.B})");
        }
        if (args.Length > 0)
        {
            GetWindowRect(progman, out var r);
            using var bmp = new Bitmap(r.R - r.L, r.B - r.T);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                bool ok = PrintWindow(progman, hdc, 2);        // PW_RENDERFULLCONTENT: includes DirectX/flip content
                g.ReleaseHdc(hdc);
                Console.WriteLine($"PrintWindow(Progman) ok={ok}");
            }
            bmp.Save(args[0], System.Drawing.Imaging.ImageFormat.Png);
        }
        return 0;
    }
}
