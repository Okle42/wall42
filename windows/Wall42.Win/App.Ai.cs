using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Wall42.Native;

namespace Wall42;

/// The AI-facing half of the resident process (kept out of App.cs): the Claude session points' data, the status file
/// the MCP server reads (wall42_status), the wallpaper sync request, and the signals that belong to those.
/// Everything here runs from the 1 s tick and is built to cost nothing while every monitor is paused:
/// sessions are not read at all, the status file is only rebuilt every 5 s and only written when it changed.
sealed unsafe partial class App
{
    readonly SessionFeed sessionFeed = new(Paths.SessionsFeed, Paths.ClaudeSessionsDir);
    string? presetName;
    Config? presetFor;
    readonly DateTime startedAt = DateTime.Now;
    readonly long procStart = SelfCreationTime();
    JsonObject? lastSync;
    readonly string syncRequest = Paths.SyncRequest;      // resolved once: the tick only stats it

    // status file bookkeeping
    string lastStatusKey = "", lastStatusVolatile = "";
    double lastStatusWrite = double.NegativeInfinity, lastStatusBuild = double.NegativeInfinity, statusCpuAt;
    double statusCpu, statusCpuPct, statusFps;
    int[] statusFrames = Array.Empty<int>();
    const double StatusEvery = 5, StatusVolatileEvery = 30;

    static long SelfCreationTime() => ProcessProbe.CreationTime(Environment.ProcessId) ?? 0;

    /// Called from Tick after the regular signal.
    void AiTick()
    {
        long t0 = Stopwatch.GetTimestamp();
        UpdateSessions();
        if (File.Exists(syncRequest)) SyncWallpaper();
        WriteStatus(false);
        aiTicks += Stopwatch.GetTimestamp() - t0;
    }

    long aiTicks;     // cumulative time in AiTick (logged at exit, to keep the paused cost honest)

    void UpdateSessions()
    {
        bool drawing = surfaces.Any(s => s.Drawing);
        var list = sessionFeed.Poll(world.Config.Motion.Sessions, drawing, Now);
        if (list == null) return;
        world.SetSessions(list);
        if (sessionFeed.SummaryIfChanged() is { } s) Log.Note(">>> sessions " + s);
    }

    /// Signals owned by this file. false = not ours.
    bool HandleAiSignal(Signal sig)
    {
        switch (sig.Kind)
        {
            case "restore-wallpaper":
                bool dry = Wallpaper.EnvDry || sig.Bool("dry", false);
                try { Wallpaper.Restore(dry); } catch (Exception e) { Log.Note("⚠ wallpaper restore failed: " + e.Message); }
                return true;
            case "status":      // write the status file now (tests, or an MCP that wants fresh numbers)
                WriteStatus(true);
                return true;
        }
        return false;
    }

    // ── wallpaper sync ──────────────────────────────────────────────

    /// The frame as it is now, per monitor, rendered offscreen at the monitor's size (the World isn't stepped:
    /// this is exactly what was last shown). Works while paused too.
    void SyncWallpaper()
    {
        bool dry = Wallpaper.EnvDry;
        try
        {
            var body = Config.ReadShared(syncRequest);
            File.Delete(syncRequest);
            // an empty file (touch) is the normal request; {"dry":true} asks for a dry run
            if (body.Trim().Length > 0)
            {
                using var d = JsonDocument.Parse(body);
                if (d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("dry", out var v) && v.ValueKind == JsonValueKind.True) dry = true;
            }
        }
        catch (JsonException) { }
        catch (Exception e) { Log.Note("⚠ wallpaper sync: cannot take the request: " + e.Message); return; }
        var sw = Stopwatch.StartNew();
        Log.Note($"wallpaper sync: capturing {surfaces.Count} monitor(s){(dry ? " (dry-run)" : "")}");
        try
        {
            if (surfaces.Count == 0) { Log.Note("⚠ wallpaper sync: no surfaces"); return; }
            var slot = Wallpaper.NextSlot();
            bool normal = world.Config.Motion.Blend == "normal";
            var shots = new List<(RECT, string)>();
            for (int i = 0; i < surfaces.Count; i++)
            {
                var s = surfaces[i];
                int w = s.Mon.Rect.Width, h = s.Mon.Rect.Height;
                var u = world.UniformsFor(new(w, h), s.Slot.Origin, s.Slot.Size, unit);
                var rgba = gpu.RenderOffscreen(world, w, h, u, normal);
                var png = Path.Combine(Paths.StateDir, $"wallpaper_{slot}_{i}.png");
                Png.Write(png, rgba, w, h);
                shots.Add((new RECT(s.Mon.Rect.Left, s.Mon.Rect.Top, s.Mon.Rect.Right, s.Mon.Rect.Bottom), png));
            }
            var set = Wallpaper.Apply(shots, dry);
            Log.Note($"wallpaper sync: done{(dry ? " (dry-run)" : "")} {set.Count}/{shots.Count} -> {string.Join(", ", set.Select(Path.GetFileName))} {sw.ElapsedMilliseconds}ms");
            lastSync = new JsonObject
            {
                ["at"] = DateTime.Now.ToString("s"), ["dryRun"] = dry, ["slot"] = slot,
                ["files"] = new JsonArray(shots.Select(x => (JsonNode)x.Item2).ToArray()), ["applied"] = set.Count,
            };
        }
        catch (Exception e)
        {
            Log.Note("⚠ wallpaper sync failed: " + e.Message);
            lastSync = new JsonObject { ["at"] = DateTime.Now.ToString("s"), ["error"] = e.Message };
        }
        // the frame buffers (w×h×4 per monitor, plus the PNG stream) sit on the large object heap: a resident
        // process that syncs once shouldn't carry them until some later GC happens to run
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        WriteStatus(true);
    }

    // ── status file (what wall42_status reports) ────────────────────

    /// %LOCALAPPDATA%\wall42\status.json. Rebuilt at most every 5 s; written only when a meaningful field changed,
    /// or when only the measured numbers moved and 30 s have passed. Atomic (temp + replace).
    void WriteStatus(bool force, bool exiting = false)
    {
        double now = Now;
        if (!force && now - lastStatusBuild < StatusEvery) return;
        double dt = now - statusCpuAt;
        lastStatusBuild = now;
        if (!ReferenceEquals(presetFor, world.Config))
        {
            presetFor = world.Config;
            try { presetName = PresetMatch.Find(world.Config); } catch { presetName = null; }
        }
        // cheap gate (no allocation): nothing that matters moved and the 30 s refresh isn't due → no JSON at all.
        // While paused this is all the status file costs: one hash every 5 s.
        if (!force)
        {
            var hc = new HashCode();
            hc.Add(suspend.Count); hc.Add(forceDraw); hc.Add(world.Config); hc.Add(presetName); hc.Add(lastSync);
            foreach (var sf in surfaces) { hc.Add(sf.Drawing); hc.Add(sf.Occluded); hc.Add(sf.Trimmed); hc.Add(sf.Interval); }
            hc.Add(world.Sessions.Count); hc.Add(sessionFeed.Busy); hc.Add(sessionFeed.Source); hc.Add(activity.Thinking);
            hc.Add((int)MathF.Round(world.Activity * 20)); hc.Add(world.LastLinkCount / 10);
            int fp = hc.ToHashCode();
            if (fp == lastFingerprint && now - lastStatusWrite < StatusVolatileEvery) return;
            lastFingerprint = fp;
        }
        var (cpuT, ws, priv) = SelfUsage();
        // measured over >= 5 s windows; a forced write in between reuses the last window (else it's noise)
        bool measure = statusCpuAt == 0 || dt >= StatusEvery;
        if (statusFrames.Length != surfaces.Count) { statusFrames = surfaces.Select(s => s.Frames).ToArray(); measure = statusCpuAt == 0; }
        if (measure)
        {
            statusCpuPct = statusCpuAt == 0 ? 0 : (cpuT - statusCpu) / dt * 100;
            (statusCpu, statusCpuAt) = (cpuT, now);
        }
        double cpuPct = statusCpuPct;

        bool anyDrawing = surfaces.Any(s => s.Drawing);
        string state = exiting ? "stopped" : suspend.Count > 0 ? "SUSPENDED" : surfaces.Count > 0 && surfaces.All(s => s.Occluded) ? "OCCLUDED"
            : anyDrawing ? "visible" : "paused";
        var monitors = new JsonArray();
        double fpsMax = measure ? 0 : statusFps;
        for (int i = 0; i < surfaces.Count; i++)
        {
            var s = surfaces[i];
            if (measure)
            {
                double fps = dt > 0 && dt < 60 ? Math.Max(0, s.Frames - statusFrames[i]) / dt : 0;
                statusFrames[i] = s.Frames;
                fpsMax = Math.Max(fpsMax, fps);
            }
            monitors.Add(new JsonObject
            {
                ["device"] = s.Mon.Device, ["primary"] = s.Mon.Rect.Primary,
                ["rect"] = $"{s.Mon.Rect.Width}x{s.Mon.Rect.Height}@{s.Mon.Rect.Left},{s.Mon.Rect.Top}",
                ["drawing"] = s.Drawing, ["occluded"] = s.Occluded, ["coveredBy"] = s.Occluded ? s.CoveredBy : null,
                ["trimmed"] = s.Trimmed, ["targetFps"] = Math.Round(1 / s.Interval),
            });
        }
        var m = world.Config.Motion;
        var o = new JsonObject
        {
            ["running"] = !exiting, ["pid"] = Environment.ProcessId, ["procStart"] = procStart.ToString(),
            ["startedAt"] = startedAt.ToString("s"),
            ["state"] = state, ["occluded"] = state == "OCCLUDED", ["paused"] = !anyDrawing,
            ["suspend"] = new JsonArray(suspend.OrderBy(x => x).Select(x => (JsonNode)x).ToArray()),
            ["forced"] = forceDraw, ["monitors"] = monitors,
            ["preset"] = presetName, ["effect"] = m.Effect, ["particleCount"] = m.ParticleCount, ["drawCount"] = world.DrawCount,
            ["targetFps"] = m.Fps, ["thinking"] = activity.Thinking, ["activitySource"] = m.Activity.Source,
            ["sessions"] = new JsonObject
            {
                ["enabled"] = m.Sessions?.Enabled ?? false, ["source"] = sessionFeed.Source,
                ["count"] = world.Sessions.Count, ["busy"] = world.Sessions.Count(x => x.Busy),
            },
            ["config"] = Config.FilePath, ["signalDir"] = Paths.SignalDir,
            ["syncDryRun"] = Wallpaper.EnvDry,
            ["lastSync"] = lastSync?.DeepClone(),
        };
        statusFps = fpsMax;
        string key = o.ToJsonString();
        // measured numbers: rounded so a paused process doesn't look "changed" every time
        var vol = new JsonObject
        {
            ["fps"] = Math.Round(fpsMax, 1), ["cpuPercent"] = Math.Round(cpuPct, 2),
            ["cpuPercentMachine"] = Math.Round(cpuPct / Environment.ProcessorCount, 3),
            ["memoryMB"] = Math.Round(ws, 1), ["privateMB"] = Math.Round(priv, 1),
            ["links"] = world.LastLinkCount, ["activity"] = Math.Round(world.Activity, 2),
        };
        string volKey = $"{Math.Round(fpsMax)}|{Math.Round(cpuPct, 1)}|{Math.Round(ws)}|{world.LastLinkCount}|{Math.Round(world.Activity, 2)}";
        bool meaningful = key != lastStatusKey;
        if (!force && !meaningful && (volKey == lastStatusVolatile || now - lastStatusWrite < StatusVolatileEvery)) return;
        foreach (var kv in vol) o[kv.Key] = kv.Value?.DeepClone();
        o["updatedAt"] = DateTime.Now.ToString("s");
        o["at"] = DateTime.Now.ToString("HH:mm:ss");
        try
        {
            Directory.CreateDirectory(Paths.StateDir);
            var tmp = Paths.StatusFile + ".tmp";
            File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, Paths.StatusFile, overwrite: true);
            (lastStatusKey, lastStatusVolatile, lastStatusWrite) = (key, volKey, now);
            statusWrites++;
        }
        catch (Exception e) { Log.Note("⚠ status file: " + e.Message); }
    }

    int statusWrites, lastFingerprint;

    void AiExit()
    {
        try { WriteStatus(true, exiting: true); } catch { }
        Log.Note($"ai: status writes={statusWrites} aiTick={aiTicks * 1000.0 / Stopwatch.Frequency:0.0}ms over {tickCount} ticks");
    }
}
