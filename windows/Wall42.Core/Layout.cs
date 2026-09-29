using System.Numerics;

namespace Wall42;

/// A monitor as Windows reports it (per-monitor-v2 aware: physical pixels in the virtual-screen space).
public record struct MonitorRect(int Left, int Top, int Right, int Bottom, int WorkTop, int WorkBottom, bool Primary)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// Monitors → world slots. The world is the union of the monitor rects in physical pixels, divided by one
/// scale (the primary monitor's DPI scale) so world units behave like Mac points: presets written in points
/// look the same size, and mixed-DPI monitors still line up edge to edge (one space, one divisor).
public static class Layout
{
    public static (List<ScreenSlot> Slots, float MainArea, int UnionLeft, int UnionTop) Build(IReadOnlyList<MonitorRect> mons, float unitScale)
    {
        if (mons.Count == 0) return (new(), 1, 0, 0);
        int ul = mons.Min(m => m.Left), ut = mons.Min(m => m.Top);
        float s = MathF.Max(0.1f, unitScale);
        var slots = mons.Select(m => new ScreenSlot(
            new Vector2((m.Left - ul) / s, (m.Top - ut) / s),
            new Vector2(m.Width / s, m.Height / s),
            (m.WorkTop - ut) / s,
            (m.WorkBottom - ut) / s)).ToList();
        var main = mons.FirstOrDefault(m => m.Primary);
        if (main.Width == 0) main = mons[0];
        return (slots, main.Width / s * (main.Height / s), ul, ut);
    }
}
