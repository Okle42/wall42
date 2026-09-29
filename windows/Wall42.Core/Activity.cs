using System.Runtime.InteropServices;
using System.Text.Json;

namespace Wall42;

/// Whole-machine CPU load 0..1 from GetSystemTimes deltas (the Windows counterpart of host_statistics).
public sealed class CpuLoad
{
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    long lastIdle, lastKernel, lastUser;
    bool primed;

    /// null on the first call (no baseline yet), like the Mac.
    public float? Sample()
    {
        if (!OperatingSystem.IsWindows() || !GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        // kernel time includes idle time
        long di = idle - lastIdle, dk = kernel - lastKernel, du = user - lastUser;
        bool had = primed;
        (lastIdle, lastKernel, lastUser, primed) = (idle, kernel, user, true);
        long total = dk + du;
        if (!had || total <= 0) return null;
        return Math.Clamp((float)(total - di) / total, 0, 1);
    }
}

/// Turns the system load (or a manual value, or an MCP "think") into the 0..1 activity, smoothed.
/// Pure: the caller passes the load sample and the time, so it is testable.
public sealed class ActivityController
{
    float smoothed;
    (float Level, DateTime Until)? think;
    public float Value => smoothed;
    public bool Thinking => think != null;

    /// MCP think: raise busyness for a while; it expires by itself (the AI may forget to switch it off).
    public void Think(float level, double seconds, DateTime now) =>
        think = (Math.Clamp(level, 0, 1), now.AddSeconds(Math.Clamp(seconds, 5, 3600)));

    public void Release() => think = null;

    /// Called once a second. Returns the new activity and whether a think just expired.
    public (float Activity, bool ThinkExpired) Update(ActivityConfig a, float? load, DateTime now)
    {
        if (think is { } t)
        {
            if (now < t.Until)
            {
                smoothed = smoothed * 0.55f + t.Level * 0.45f;     // quick but not a jump
                return (smoothed, false);
            }
            think = null;
            return (Tick(a, load), true);
        }
        return (Tick(a, load), false);
    }

    float Tick(ActivityConfig a, float? load)
    {
        float target;
        switch (a.Source)
        {
            case "off": target = 0; break;
            case "manual": target = Math.Clamp(a.ManualLevel, 0, 1); break;
            default:
                if (load is not { } l) return smoothed;
                float lo = a.MinLoad, hi = MathF.Max(a.MaxLoad, a.MinLoad + 0.01f);
                target = Math.Clamp((l - lo) / (hi - lo), 0, 1);
                break;
        }
        float k = Math.Clamp(a.Smoothing, 0, 0.99f);
        smoothed = smoothed * k + target * (1 - k);
        return smoothed;
    }
}

/// %APPDATA%\wall42\.signal — the file signal the MCP server writes (atomically). Read once, then deleted.
/// {"kind":"think","level":0.9,"seconds":120} | {"kind":"insight","strength":1.0} | {"kind":"debug-suspend",…}
public sealed record Signal(string Kind, JsonElement Body)
{
    public static string PathIn(string dir) => System.IO.Path.Combine(dir, ".signal");

    public static Signal? Take(string dir)
    {
        var f = PathIn(dir);
        if (!File.Exists(f)) return null;
        string text;
        try { text = Config.ReadShared(f); File.Delete(f); } catch { return null; }
        return Parse(text);
    }

    public static Signal? Parse(string text)
    {
        try
        {
            using var d = JsonDocument.Parse(text);
            if (d.RootElement.ValueKind != JsonValueKind.Object || !d.RootElement.TryGetProperty("kind", out var k)
                || k.ValueKind != JsonValueKind.String) return null;
            return new Signal(k.GetString()!, d.RootElement.Clone());
        }
        catch (JsonException) { return null; }
    }

    public double Num(string key, double fallback) =>
        Body.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
    public string? Str(string key) =>
        Body.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public bool Bool(string key, bool fallback) =>
        Body.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
}
