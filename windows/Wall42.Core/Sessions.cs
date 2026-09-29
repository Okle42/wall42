using System.Runtime.InteropServices;
using System.Text.Json;

namespace Wall42;

/// Where the AI-facing files live. The signal folder is fixed at %APPDATA%\wall42 (like the Mac's ~/.config/wall42):
/// it does NOT follow WALL42_CONFIG, otherwise a test config would stop hearing the MCP. WALL42_SIGNAL_DIR moves it
/// explicitly (tests next to other running instances). The state folder (status.json, wallpaper PNGs) sits next to
/// the log, %LOCALAPPDATA%\wall42, and moves with WALL42_LOG.
public static class Paths
{
    public static string SignalDir => Environment.GetEnvironmentVariable("WALL42_SIGNAL_DIR") is { Length: > 0 } d
        ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(d)) : Config.Dir;

    public static string LogFile => Environment.GetEnvironmentVariable("WALL42_LOG") is { Length: > 0 } p ? Path.GetFullPath(p)
        : Path.Combine(Home, "wall42.log");

    /// %LOCALAPPDATA%wall42, or WALL42_HOME (the installer's sandbox) — the same folder Log.cs and Package use.
    public static string Home => Environment.GetEnvironmentVariable("WALL42_HOME") is { Length: > 0 } h ? Path.GetFullPath(h)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wall42");

    public static string StateDir => Path.GetDirectoryName(LogFile)!;
    public static string StatusFile => Path.Combine(StateDir, "status.json");
    public static string SessionsFeed => Path.Combine(SignalDir, "sessions.json");
    public static string SyncRequest => Path.Combine(SignalDir, ".sync-request");
    public static string ClaudeSessionsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");
}

/// Is this pid alive, and when was it created? (FILETIME, 100 ns since 1601 — the unit Claude Code writes as procStart)
public static class ProcessProbe
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, STILL_ACTIVE = 259;

    /// Creation time of a running process, null when it is gone (or not ours to see). A handle held by someone
    /// else keeps an exited process openable, so the exit code is checked too.
    public static long? CreationTime(int pid)
    {
        if (!OperatingSystem.IsWindows() || pid <= 0) return null;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (!GetExitCodeProcess(h, out var code) || code != STILL_ACTIVE) return null;
            return GetProcessTimes(h, out var created, out _, out _, out _) ? created : null;
        }
        finally { CloseHandle(h); }
    }
}

/// The session points' data: %APPDATA%\wall42\sessions.json (fed by the MCP) wins; otherwise Claude Code's own
/// %USERPROFILE%\.claude\sessions\<pid>.json files. Pure parsing + an injected process probe, so it is testable.
public static class ClaudeSessions
{
    public readonly record struct Entry(string Id, bool Busy, double StartedAt, int Pid, long? ProcStart);

    /// Same pid, different creation time = the pid was reused by an unrelated process after the session died.
    /// Claude Code writes the exact FILETIME; allow 10 ms in case some writer rounds.
    public const long ProcStartTolerance = 100_000;

    /// One <pid>.json. null for anything that isn't a session file (the .key files, garbage, no pid).
    public static Entry? Parse(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var o = d.RootElement;
            if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty("pid", out var p)) return null;
            int pid = p.ValueKind switch
            {
                JsonValueKind.Number when p.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(p.GetString(), out var n) => n,
                _ => 0,
            };
            if (pid <= 0) return null;
            string id = o.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() is { Length: > 0 } sid
                ? sid : $"pid-{pid}";
            bool busy = o.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String && st.GetString() == "busy";
            double started = o.TryGetProperty("startedAt", out var sa) && sa.ValueKind == JsonValueKind.Number ? sa.GetDouble() : 0;
            return new Entry(id, busy, started, pid, Long(o, "procStart") ?? Long(o, "procStartFt"));
        }
        catch (JsonException) { return null; }
    }

    static long? Long(JsonElement o, string key)
    {
        if (!o.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var m)) return m;
        return null;
    }

    /// Alive = the pid runs AND (when the file says when it started) it is the same process.
    public static bool Alive(Entry e, Func<int, long?> creationTime)
    {
        if (creationTime(e.Pid) is not { } created) return false;
        return e.ProcStart is not { } want || Math.Abs(created - want) <= ProcStartTolerance;
    }

    /// Every live session in the folder, oldest first (the Mac orders by startedAt too).
    public static List<World.SessionInfo> Scan(string dir, Func<int, long?> creationTime)
    {
        var found = new List<Entry>();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.json").ToList(); }
        catch (IOException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
        foreach (var f in files)
        {
            string text;
            try { text = Config.ReadShared(f); } catch { continue; }
            if (Parse(text) is { } e && Alive(e, creationTime)) found.Add(e);
        }
        return found.OrderBy(e => e.StartedAt).ThenBy(e => e.Id, StringComparer.Ordinal)
            .Select(e => new World.SessionInfo(e.Id, e.Busy)).ToList();
    }

    /// The MCP feed: {"count":5,"busy":2} or {"sessions":[{"id":"a","busy":true}]}. Garbage = no sessions.
    public static List<World.SessionInfo> ParseFeed(string json)
    {
        var list = new List<World.SessionInfo>();
        try
        {
            using var d = JsonDocument.Parse(json);
            var o = d.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return list;
            if (o.TryGetProperty("sessions", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                int k = 0;
                foreach (var e in arr.EnumerateArray())
                {
                    if (list.Count >= 64) break;
                    string id = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String
                        ? i.GetString()! : $"feed-{k}";
                    bool busy = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("busy", out var b) && b.ValueKind == JsonValueKind.True;
                    list.Add(new(id, busy));
                    k++;
                }
                return list;
            }
            int n = Math.Clamp(Int(o, "count"), 0, 64), busyN = Math.Max(0, Int(o, "busy"));
            for (int i = 0; i < n; i++) list.Add(new($"feed-{i}", i < busyN));
        }
        catch (JsonException) { }
        return list;
    }

    static int Int(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)
            ? (int)Math.Clamp(d, int.MinValue, int.MaxValue) : 0;
}

/// Decides when to read and what to hand the World. Reads at most every 2 s, and only when the caller says we are
/// drawing with sessions enabled — while paused it does nothing at all (the tick doesn't even list the folder).
public sealed class SessionFeed
{
    readonly Func<int, long?> probe;
    readonly string feedPath, claudeDir;
    double lastRead = double.NegativeInfinity;
    bool wasActive;
    public string Summary { get; private set; } = "";
    public string Source { get; private set; } = "off";
    public int Count { get; private set; }
    public int Busy { get; private set; }
    public double Interval { get; set; } = 2.0;

    public SessionFeed(string feedPath, string claudeDir, Func<int, long?>? probe = null)
    {
        this.feedPath = feedPath; this.claudeDir = claudeDir;
        this.probe = probe ?? ProcessProbe.CreationTime;
    }

    /// Returns the new list when it is time to read (null = nothing to do). `now` in seconds.
    public List<World.SessionInfo>? Poll(SessionsConfig? sc, bool drawing, double now)
    {
        bool enabled = sc?.Enabled ?? false;
        if (!enabled)
        {
            bool had = wasActive || Count > 0;
            wasActive = false;
            if (!had) return null;
            (Source, Count, Busy) = ("off", 0, 0);
            return new();            // switched off: clear once
        }
        if (!drawing) { wasActive = false; return null; }
        // coming back from a pause (or just enabled): read now, the list may be minutes old
        if (wasActive && now - lastRead < Interval) return null;
        wasActive = true;
        lastRead = now;
        List<World.SessionInfo> list;
        if (sc!.Source == "file" || File.Exists(feedPath))
        {
            Source = "file";
            string text = "";
            try { text = Config.ReadShared(feedPath); } catch { }
            list = ClaudeSessions.ParseFeed(text);
        }
        else
        {
            Source = "auto";
            list = ClaudeSessions.Scan(claudeDir, probe);
        }
        Count = list.Count;
        Busy = list.Count(s => s.Busy);
        return list;
    }

    /// "auto 3 (busy 1)" — changes only when the log should say something.
    public string? SummaryIfChanged()
    {
        var s = $"{Source} {Count} (busy {Busy})";
        if (s == Summary) return null;
        Summary = s;
        return s;
    }
}
