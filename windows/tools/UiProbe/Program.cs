using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Test driver for wall42's tray menu and control panel. Only ever touches windows that belong to <pid>
// (our own wall42 instance): posts the menu commands / test messages to them and PrintWindows them.
// Never moves, focuses or captures anyone else's window.
//
//   UiProbe list  <pid>                        our windows (class, hwnd, rect, visible)
//   UiProbe cmd   <pid> <id>                   run a tray menu command (Tray.CMD_*: 10 pause, 11 panel, 13 quit, 20-22 activity, 100+i preset i)
//   UiProbe menu  <pid> <x> <y> <png> [keys]   show the tray menu at x,y without taking the foreground, PrintWindow it
//                                              (keys: e.g. "down,down,down,right" to open a submenu first; each popup → png, png.1…)
//   UiProbe popups <pid> <png> [vk]            PrintWindow our open popup menus (the panel's preset list), then send vk (hex, 1B = Esc)
//   UiProbe dialog <pid> <png>                 PrintWindow our open dialog (colour picker), then close it (Cancel)
//   UiProbe panel <pid> <x> <y>                open the panel at x,y without activating it
//   UiProbe shot  <pid> <png>                  PrintWindow the panel
//   UiProbe click <pid> <x> <y> [right]        click in the panel (client pixels)
//   UiProbe drag  <pid> <x1> <y1> <x2> <y2>    drag in the panel
//   UiProbe key   <pid> <vk>                   key press in the panel (hex or decimal)
//   UiProbe wheel <pid> <delta>                mouse wheel in the panel
//   UiProbe close <pid>                        close the panel (WM_CLOSE)
//   UiProbe mem   <pid>                        working set / private bytes / cpu seconds
static class P
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; public override string ToString() => $"({L},{T})-({R},{B}) {R - L}x{B - T}"; }
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

    const uint WM_APP = 0x8000, WM_COMMAND = 0x111, WM_CLOSE = 0x10, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101,
        WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_MOUSEWHEEL = 0x20A;

    static string Cls(IntPtr h) { var s = new StringBuilder(128); GetClassNameW(h, s, 128); return s.ToString(); }

    static List<IntPtr> Windows(uint pid, string? cls = null, bool visibleOnly = false)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var p);
            if (p == pid && (cls == null || Cls(h) == cls) && (!visibleOnly || IsWindowVisible(h))) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static IntPtr One(uint pid, string cls) => Windows(pid, cls).FirstOrDefault() is var h && h != IntPtr.Zero ? h : throw new Exception($"no {cls} window in pid {pid}");
    static IntPtr XY(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

    static void Shot(IntPtr h, string png)
    {
        GetWindowRect(h, out var r);
        using var bmp = new Bitmap(Math.Max(1, r.R - r.L), Math.Max(1, r.B - r.T));
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            bool ok = PrintWindow(h, hdc, 2);                // PW_RENDERFULLCONTENT
            g.ReleaseHdc(hdc);
            Console.WriteLine($"PrintWindow {Cls(h)} {h:X} {r} ok={ok} -> {png}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(png))!);
        bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
    }

    static int Main(string[] a)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);        // real pixel rects, like wall42
        uint pid = uint.Parse(a[1]);
        switch (a[0])
        {
            case "list":
                foreach (var h in Windows(pid)) { GetWindowRect(h, out var r); Console.WriteLine($"{Cls(h),-16} {h:X} vis={IsWindowVisible(h)} {r}"); }
                break;
            case "cmd":
                PostMessageW(One(pid, "wall42.tray"), WM_COMMAND, (IntPtr)int.Parse(a[2]), IntPtr.Zero);
                break;
            case "menu":
            {
                var tray = One(pid, "wall42.tray");
                PostMessageW(tray, WM_APP + 2, (IntPtr)int.Parse(a[2]), (IntPtr)int.Parse(a[3]));
                Thread.Sleep(700);
                var menus = Windows(pid, "#32768", true);
                if (a.Length > 5 && menus.Count > 0)
                {
                    foreach (var k in a[5].Split(','))
                    {
                        int vk = k switch { "down" => 0x28, "up" => 0x26, "right" => 0x27, "left" => 0x25, _ => Convert.ToInt32(k, 16) };
                        PostMessageW(menus[0], WM_KEYDOWN, (IntPtr)vk, IntPtr.Zero);
                        Thread.Sleep(120);
                    }
                    Thread.Sleep(500);
                    menus = Windows(pid, "#32768", true);
                }
                for (int i = 0; i < menus.Count; i++) Shot(menus[i], i == 0 ? a[4] : a[4].Replace(".png", $".{i}.png"));
                if (menus.Count == 0) Console.WriteLine("no menu window found");
                break;
            }
            case "popups":                          // popup menus of ours that are open now (e.g. the panel's preset list)
            {
                var menus = Windows(pid, "#32768", true);
                for (int i = 0; i < menus.Count; i++) Shot(menus[i], i == 0 ? a[2] : a[2].Replace(".png", $".{i}.png"));
                if (menus.Count == 0) Console.WriteLine("no menu window found");
                if (a.Length > 3 && menus.Count > 0) PostMessageW(menus[0], WM_KEYDOWN, (IntPtr)Convert.ToInt32(a[3], 16), IntPtr.Zero);   // e.g. 1B = Esc
                break;
            }
            case "dialog":                          // our open dialog (the colour picker): PrintWindow, then close it (= Cancel)
            {
                var d = Windows(pid, "#32770", true);
                if (d.Count == 0) { Console.WriteLine("no dialog found"); break; }
                Shot(d[0], a[2]);
                PostMessageW(d[0], WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                break;
            }
            case "panel":
                PostMessageW(One(pid, "wall42.tray"), WM_APP + 3, (IntPtr)int.Parse(a[2]), (IntPtr)int.Parse(a[3]));
                break;
            case "shot":
                Shot(One(pid, "wall42.panel"), a[2]);
                break;
            case "click":
            {
                var h = One(pid, "wall42.panel");
                var p = XY(int.Parse(a[2]), int.Parse(a[3]));
                bool right = a.Length > 4 && a[4] == "right";
                PostMessageW(h, right ? WM_RBUTTONDOWN : WM_LBUTTONDOWN, (IntPtr)(right ? 2 : 1), p);
                PostMessageW(h, right ? WM_RBUTTONUP : WM_LBUTTONUP, IntPtr.Zero, p);
                break;
            }
            case "drag":
            {
                var h = One(pid, "wall42.panel");
                int x1 = int.Parse(a[2]), y1 = int.Parse(a[3]), x2 = int.Parse(a[4]), y2 = int.Parse(a[5]);
                PostMessageW(h, WM_LBUTTONDOWN, (IntPtr)1, XY(x1, y1));
                for (int i = 1; i <= 10; i++) { PostMessageW(h, WM_MOUSEMOVE, (IntPtr)1, XY(x1 + (x2 - x1) * i / 10, y1 + (y2 - y1) * i / 10)); Thread.Sleep(15); }
                PostMessageW(h, WM_LBUTTONUP, IntPtr.Zero, XY(x2, y2));
                break;
            }
            case "key":
            {
                var h = One(pid, "wall42.panel");
                int vk = a[2].StartsWith("0x") ? Convert.ToInt32(a[2], 16) : int.Parse(a[2]);
                PostMessageW(h, WM_KEYDOWN, (IntPtr)vk, IntPtr.Zero);
                PostMessageW(h, WM_KEYUP, (IntPtr)vk, IntPtr.Zero);
                break;
            }
            case "wheel":
                PostMessageW(One(pid, "wall42.panel"), WM_MOUSEWHEEL, (IntPtr)(int.Parse(a[2]) << 16), IntPtr.Zero);
                break;
            case "close":
                PostMessageW(One(pid, "wall42.panel"), WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                break;
            case "mem":
            {
                using var p = Process.GetProcessById((int)pid);
                Console.WriteLine($"ws={p.WorkingSet64 / 1048576.0:0.0}MB private={p.PrivateMemorySize64 / 1048576.0:0.0}MB cpu={p.TotalProcessorTime.TotalSeconds:0.000}s");
                break;
            }
            default:
                Console.WriteLine("unknown command");
                return 1;
        }
        return 0;
    }
}
