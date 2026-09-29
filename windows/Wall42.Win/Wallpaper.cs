using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Wall42.Native;

namespace Wall42;

/// The system wallpaper (IDesktopWallpaper, per monitor). wall42 itself never needs it — it draws above the
/// wallpaper — but "sync" copies the current frame into it so the Settings preview and the gap between boot and
/// wall42 starting show the same picture (the Mac's sync-wallpaper.sh / .sync-request).
///
/// Safety: WALL42_SYNC_DRY=1 (or {"dry":true} in the request) does everything except the final SetWallpaper /
/// SetPosition calls. The wallpaper as it was before our first sync is logged every time and kept once in
/// backup\original-wallpaper.json so it can be restored ({"kind":"restore-wallpaper"} or wall42.exe --restore-wallpaper).
static class Wallpaper
{
    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint index);
        uint GetMonitorDevicePathCount();
        RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
        void SetPosition(int position);
        int GetPosition();
        // (slideshow methods follow in the vtable; not declared, not used)
    }

    [ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")] class DesktopWallpaperClass { }

    const int POS_TILE = 1, POS_FILL = 4, POS_SPAN = 5;
    static readonly string[] PosNames = { "center", "tile", "stretch", "fit", "fill", "span" };

    public static bool EnvDry => Program.EnvBool("WALL42_SYNC_DRY");
    public static string BackupFile => Path.Combine(Paths.StateDir, "backup", "original-wallpaper.json");

    public sealed record MonitorWallpaper(string Id, RECT Rect, string Path);
    public sealed record State(List<MonitorWallpaper> Monitors, int Position, uint Color);

    static IDesktopWallpaper Open() => (IDesktopWallpaper)new DesktopWallpaperClass();

    /// Read only: every attached monitor's wallpaper, the position mode and the background colour.
    public static State Read()
    {
        var dw = Open();
        try
        {
            var list = new List<MonitorWallpaper>();
            uint n = dw.GetMonitorDevicePathCount();
            for (uint i = 0; i < n; i++)
            {
                string id;
                try { id = dw.GetMonitorDevicePathAt(i); } catch { continue; }
                RECT r;
                try { r = dw.GetMonitorRECT(id); } catch { continue; }        // not attached to the desktop
                string p = "";
                try { p = dw.GetWallpaper(id) ?? ""; } catch { }
                list.Add(new(id, r, p));
            }
            int pos = -1; uint color = 0;
            try { pos = dw.GetPosition(); } catch { }
            try { color = dw.GetBackgroundColor(); } catch { }
            return new(list, pos, color);
        }
        finally { Marshal.ReleaseComObject(dw); }
    }

    public static string Describe(State s) =>
        $"position={(s.Position is >= 0 and < 6 ? PosNames[s.Position] : s.Position.ToString())} color=#{s.Color & 0xFF:X2}{(s.Color >> 8) & 0xFF:X2}{(s.Color >> 16) & 0xFF:X2} " +
        string.Join(" ", s.Monitors.Select(m => $"[{m.Rect.Width}x{m.Rect.Height}@{m.Rect.Left},{m.Rect.Top} \"{(m.Path.Length == 0 ? "(none)" : m.Path)}\"]"));

    /// First sync only (like the Mac): never overwrite, or our own PNG would become "the original".
    static void BackupOnce(State s)
    {
        if (File.Exists(BackupFile)) return;
        if (s.Monitors.Any(m => IsOurs(m.Path))) return;
        var o = new JsonObject
        {
            ["savedAt"] = DateTime.Now.ToString("s"),
            ["position"] = s.Position,
            ["color"] = s.Color,
            ["monitors"] = new JsonArray(s.Monitors.Select(m => (JsonNode)new JsonObject
            {
                ["id"] = m.Id, ["path"] = m.Path,
                ["rect"] = new JsonArray(m.Rect.Left, m.Rect.Top, m.Rect.Right, m.Rect.Bottom),
            }).ToArray()),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(BackupFile)!);
        File.WriteAllText(BackupFile, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Log.Note($"wallpaper: original saved -> {BackupFile}");
    }

    static bool IsOurs(string p) => p.Length > 0 && Path.GetFullPath(p).StartsWith(Path.GetFullPath(Paths.StateDir), StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(p).StartsWith("wallpaper_", StringComparison.OrdinalIgnoreCase);

    /// A → B → A: the same path again may be served from the shell's cache without repainting (the Mac has the same issue).
    public static string NextSlot()
    {
        var f = Path.Combine(Paths.StateDir, ".slot");
        string cur = "";
        try { cur = File.ReadAllText(f).Trim(); } catch { }
        var slot = cur == "a" ? "b" : "a";
        Directory.CreateDirectory(Paths.StateDir);
        File.WriteAllText(f, slot);
        foreach (var old in Directory.EnumerateFiles(Paths.StateDir, $"wallpaper_{(slot == "a" ? "b" : "a")}*.png"))
            try { File.Delete(old); } catch { }
        return slot;
    }

    /// The per-monitor images are already written; point the system at them. Returns what was (or would be) set.
    public static List<string> Apply(IReadOnlyList<(RECT Rect, string Png)> shots, bool dry)
    {
        var before = Read();
        Log.Note($"wallpaper: current {Describe(before)}");            // enough to restore by hand, every time
        BackupOnce(before);
        var done = new List<string>();
        var dw = dry ? null : Open();
        try
        {
            foreach (var (rect, png) in shots)
            {
                var id = before.Monitors.FirstOrDefault(m => m.Rect.Left == rect.Left && m.Rect.Top == rect.Top
                    && m.Rect.Width == rect.Width && m.Rect.Height == rect.Height)?.Id;
                // one monitor that we couldn't match (odd DPI virtualisation): the only one there is
                if (id == null && shots.Count == 1 && before.Monitors.Count == 1) id = before.Monitors[0].Id;
                if (id == null) { Log.Note($"⚠ wallpaper: no system monitor at {rect}, skipped"); continue; }
                if (dry) Log.Note($"wallpaper (dry-run): would SetWallpaper({Short(id)}, {png})");
                else dw!.SetWallpaper(id, png);
                done.Add(png);
            }
            // our images are exactly monitor-sized: any mode but tile/span shows them 1:1
            if (before.Position is POS_TILE or POS_SPAN)
            {
                if (dry) Log.Note($"wallpaper (dry-run): would SetPosition(fill) (was {PosNames[before.Position]})");
                else dw!.SetPosition(POS_FILL);
            }
        }
        finally { if (dw != null) Marshal.ReleaseComObject(dw); }
        return done;
    }

    /// Back to what backup\original-wallpaper.json recorded (paths per monitor, position, colour).
    public static bool Restore(bool dry)
    {
        if (!File.Exists(BackupFile)) { Log.Note("wallpaper restore: no backup (never synced), nothing to do"); return false; }
        var o = JsonNode.Parse(File.ReadAllText(BackupFile))!.AsObject();
        var dw = dry ? null : Open();
        try
        {
            var now = Read();
            Log.Note($"wallpaper restore: current {Describe(now)}");
            foreach (var m in o["monitors"]!.AsArray())
            {
                string id = (string)m!["id"]!, path = (string?)m["path"] ?? "";
                if (!now.Monitors.Any(x => x.Id == id)) { Log.Note($"wallpaper restore: monitor {Short(id)} not attached, skipped"); continue; }
                if (dry) Log.Note($"wallpaper restore (dry-run): would SetWallpaper({Short(id)}, \"{path}\")");
                else dw!.SetWallpaper(id, path);
            }
            int pos = (int?)o["position"] ?? -1;
            uint color = (uint?)o["color"] ?? 0;
            if (dry) Log.Note($"wallpaper restore (dry-run): would SetBackgroundColor(0x{color:X6}) SetPosition({pos})");
            else
            {
                dw!.SetBackgroundColor(color);
                if (pos >= 0 && pos != now.Position) dw.SetPosition(pos);
            }
        }
        finally { if (dw != null) Marshal.ReleaseComObject(dw); }
        Log.Note($"wallpaper restore: done{(dry ? " (dry-run)" : "")}");
        return true;
    }

    /// \\?\DISPLAY#…#{guid} is long; the log only needs something recognisable
    static string Short(string id) => id.Length > 40 ? id[..20] + "…" + id[^12..] : id;
}
