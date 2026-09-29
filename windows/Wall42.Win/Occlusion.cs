using static Wall42.Native;

namespace Wall42;

/// "Is this monitor completely covered?" — the Windows stand-in for NSWindow.occlusionState, which Win32
/// doesn't have for a desktop-layer window. Union of the visible, non-minimised, non-cloaked, opaque
/// top-level windows; the monitor rect minus each of them; nothing left = covered. Order doesn't matter
/// (we are below everything). A single window containing the monitor is reported as full screen.
static class Occlusion
{
    static readonly List<(RECT R, IntPtr H)> wins = new();
    /// the windows counted as cover last time: while everything is paused only THEY can uncover a monitor
    public static readonly HashSet<IntPtr> Counted = new();
    public static readonly HashSet<uint> CountedPids = new();
    static readonly uint self = (uint)Environment.ProcessId;
    static EnumWindowsProc? cb;
    static RECT bounds;                  // union of the monitors: windows outside it are skipped before any DWM call

    public static void Compute(IReadOnlyList<Surface> surfaces, bool[] covered, string[] by)
    {
        wins.Clear();
        Counted.Clear();
        CountedPids.Clear();
        bounds = new RECT(int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);
        foreach (var s in surfaces)
        {
            var m = s.Mon.Rect;
            bounds = new RECT(Math.Min(bounds.Left, m.Left), Math.Min(bounds.Top, m.Top), Math.Max(bounds.Right, m.Right), Math.Max(bounds.Bottom, m.Bottom));
        }
        cb ??= Collect;
        EnumWindows(cb, IntPtr.Zero);
        for (int i = 0; i < surfaces.Count; i++)
        {
            var m = surfaces[i].Mon.Rect;
            var mon = new RECT(m.Left, m.Top, m.Right, m.Bottom);
            (covered[i], by[i]) = Covered(mon);
        }
    }

    static bool Collect(IntPtr h, IntPtr lParam)
    {
        // cheapest tests first: this runs for every top-level window, several times a second at worst
        if (!IsWindowVisible(h) || IsIconic(h)) return true;
        if (!GetWindowRect(h, out var wr) || wr.Right <= bounds.Left || wr.Left >= bounds.Right || wr.Bottom <= bounds.Top || wr.Top >= bounds.Bottom
            || wr.Empty) return true;
        int ex = ExStyle(h);
        // click-through overlays (glows, OSDs, recorder frames) are see-through: never count as cover
        if (SeeThrough(ex)) return true;
        if ((ex & WS_EX_LAYERED) != 0)
        {
            // per-pixel alpha (UpdateLayeredWindow) or partial alpha / colour key: can't be sure it's opaque
            if (!GetLayeredWindowAttributes(h, out _, out var alpha, out var flags)) return true;
            if ((flags & 2) != 0 || ((flags & 1) != 0 && alpha < 255)) return true;      // LWA_ALPHA=1, LWA_COLORKEY=2
        }
        GetWindowThreadProcessId(h, out var pid);
        if (pid == self) return true;
        if (IsCloaked(h)) return true;
        var cls = ClassOf(h);
        if (cls is "Progman" or "WorkerW") return true;
        var r = VisibleRect(h);
        if (r.Empty) return true;
        wins.Add((r, h));
        Counted.Add(h);
        CountedPids.Add(pid);
        return true;
    }

    public static bool SeeThrough(int ex) => (ex & WS_EX_TRANSPARENT) != 0 && (ex & WS_EX_LAYERED) != 0;

    static (bool, string) Covered(RECT mon)
    {
        foreach (var (r, h) in wins)
            if (r.Left <= mon.Left && r.Top <= mon.Top && r.Right >= mon.Right && r.Bottom >= mon.Bottom)
                return (true, "fullscreen:" + ClassOf(h));
        var left = new List<RECT> { mon };
        var next = new List<RECT>(8);
        string last = "";
        foreach (var (r, h) in wins)
        {
            if (r.Right <= mon.Left || r.Left >= mon.Right || r.Bottom <= mon.Top || r.Top >= mon.Bottom) continue;
            next.Clear();
            foreach (var u in left) Subtract(u, r, next);
            (left, next) = (next, left);
            last = ClassOf(h);
            if (left.Count == 0) return (true, "windows:" + last);
            if (left.Count > 512) return (false, "");        // pathological fragmentation: just keep drawing
        }
        return (false, "");
    }

    /// a − b as up to four rects (top band, bottom band, left and right of the middle band)
    static void Subtract(RECT a, RECT b, List<RECT> into)
    {
        if (b.Right <= a.Left || b.Left >= a.Right || b.Bottom <= a.Top || b.Top >= a.Bottom) { into.Add(a); return; }
        if (b.Top > a.Top) into.Add(new RECT(a.Left, a.Top, a.Right, b.Top));
        if (b.Bottom < a.Bottom) into.Add(new RECT(a.Left, b.Bottom, a.Right, a.Bottom));
        int top = Math.Max(a.Top, b.Top), bottom = Math.Min(a.Bottom, b.Bottom);
        if (b.Left > a.Left) into.Add(new RECT(a.Left, top, b.Left, bottom));
        if (b.Right < a.Right) into.Add(new RECT(b.Right, top, a.Right, bottom));
    }
}
