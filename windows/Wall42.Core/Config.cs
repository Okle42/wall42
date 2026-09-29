using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wall42;

// Same JSON schema as Sources/Config.swift, so presets and config files move between Mac and Windows.
// Unlike Codable, every field here is optional: a missing key keeps the Mac default, and a key with the
// wrong type is skipped with a warning instead of throwing the whole file away (Mac README pitfall).
// Fields whose *absence* means something on the Mac (link.color, secondaryFps, sessions…) stay nullable.

public sealed class BackgroundConfig
{
    public string Mode { get; set; } = "gradient";          // "solid" | "gradient" (per screen, radial) | "vertical" (whole world, top→bottom)
    public string SolidColor { get; set; } = "#000000";
    public string CenterColor { get; set; } = "#0F041D";
    public string EdgeColor { get; set; } = "#000000";
    public float Radius { get; set; } = 1.02f;
}

public sealed class LinkConfig
{
    public bool Enabled { get; set; } = true;
    public float Distance { get; set; } = 168;
    public float Opacity { get; set; } = 0.40f;
    public float Boost { get; set; } = 1.35f;
    public bool OnlyNodes { get; set; }                       // constellations: only the big "node" particles link
    public string Mode { get; set; } = "proximity";          // proximity | traffic | attention
    public int TargetCount { get; set; } = 60;
    public float LifeMin { get; set; } = 1.1f;
    public float LifeMax { get; set; } = 2.6f;
    public string? Color { get; set; }                        // null = midpoint of colorA/colorB
}

public sealed class BokehConfig
{
    public float Ratio { get; set; } = 0.15f;
    public float SizeMin { get; set; } = 34;
    public float SizeMax { get; set; } = 72;
    public float Speed { get; set; } = 18;
    public float Dimming { get; set; } = 0.22f;
}

public sealed class PulseConfig
{
    public float Speed { get; set; } = 0.35f;
    public float Strength { get; set; } = 0.9f;
    public float Width { get; set; } = 0.004f;
}

public sealed class ActivityConfig
{
    public string Source { get; set; } = "system";           // system | manual | off
    public float ManualLevel { get; set; }
    public float Smoothing { get; set; } = 0.85f;
    public float MinLoad { get; set; } = 0.08f;
    public float MaxLoad { get; set; } = 0.75f;
}

public sealed class SessionsConfig
{
    public bool? Enabled { get; set; }
    public string? Source { get; set; }                       // auto | file
    public float? Size { get; set; }                          // null = max(nodeSizeMax, 8) × 1.15
}

public sealed class MotionConfig
{
    public string Effect { get; set; } = "floating";         // floating | snow | sand
    public int ParticleCount { get; set; } = 140;             // per primary-screen area (density)
    public int Fps { get; set; } = 30;
    public int? SecondaryFps { get; set; }                    // null = fps / 2
    public string ColorA { get; set; } = "#1ADBF5";
    public string ColorB { get; set; } = "#FC3D99";
    public float Speed { get; set; } = 11;
    public float SizeMin { get; set; } = 7;
    public float SizeMax { get; set; } = 13;
    public float NodeRatio { get; set; } = 0.20f;
    public float NodeSizeMin { get; set; } = 17;
    public float NodeSizeMax { get; set; } = 27;
    public float Brightness { get; set; } = 1.0f;
    public float BreathSpeed { get; set; } = 0.7f;
    public float Glow { get; set; } = 1.0f;
    public string Blend { get; set; } = "additive";          // additive | normal
    public float TwinkleAmount { get; set; } = 0.45f;
    public float TwinkleVariance { get; set; } = 0.6f;
    public float SizeBias { get; set; } = 2.2f;
    public LinkConfig Link { get; set; } = new();
    public BokehConfig Bokeh { get; set; } = new();
    public PulseConfig Pulse { get; set; } = new();
    public ActivityConfig Activity { get; set; } = new();
    public float Softness { get; set; }
    public float Wind { get; set; } = 8;
    public int Streams { get; set; } = 1;
    public SessionsConfig? Sessions { get; set; }
}

public sealed class UIConfig
{
    public bool MenuBar { get; set; } = true;                 // Windows: the tray icon (later step)
}

public sealed class Config
{
    public BackgroundConfig Background { get; set; } = new();
    public MotionConfig Motion { get; set; } = new();
    public UIConfig Ui { get; set; } = new();

    static readonly JsonSerializerOptions writeOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    static readonly JsonDocumentOptions readOpts = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public string ToJson() => JsonSerializer.Serialize(this, writeOpts);
    public Config Clone() => Parse(ToJson(), out _);

    /// Tolerant parse. Never throws: garbage gives the defaults plus a warning.
    public static Config Parse(string json, out List<string> warnings)
    {
        warnings = new();
        var c = new Config();
        try
        {
            using var doc = JsonDocument.Parse(json, readOpts);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { warnings.Add("root is not an object"); return c; }
            Bind(doc.RootElement, c, "", warnings);
        }
        catch (JsonException e) { warnings.Add("not valid JSON: " + e.Message); }
        return c;
    }

    static void Bind(JsonElement obj, object target, string path, List<string> warnings)
    {
        foreach (var p in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite) continue;
            var key = char.ToLowerInvariant(p.Name[0]) + p.Name[1..];
            if (!obj.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) continue;
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            object? val = null;
            if (t == typeof(float) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)) val = (float)d;
            else if (t == typeof(int) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n)
                     && Math.Abs(n) < int.MaxValue) val = (int)Math.Round(n);
            else if (t == typeof(bool) && v.ValueKind is JsonValueKind.True or JsonValueKind.False) val = v.GetBoolean();
            else if (t == typeof(string) && v.ValueKind == JsonValueKind.String) val = v.GetString();
            else if (t.IsClass && t != typeof(string) && v.ValueKind == JsonValueKind.Object)
            {
                // nested section: start from its defaults so a partial section keeps the rest
                val = p.GetValue(target) ?? Activator.CreateInstance(t)!;
                Bind(v, val, path + key + ".", warnings);
            }
            if (val != null) p.SetValue(target, val);
            else warnings.Add($"{path}{key}: unexpected {v.ValueKind}, kept default");
        }
    }

    // ── files ──────────────────────────────────────────────────────────

    /// %APPDATA%\wall42 — also where the control signals live. WALL42_CONFIG moves only config.json,
    /// never the signal folder (Mac: otherwise a test config would stop hearing MCP commands).
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "wall42");

    public static string FilePath
    {
        get
        {
            var p = Environment.GetEnvironmentVariable("WALL42_CONFIG");
            return string.IsNullOrWhiteSpace(p) ? Path.Combine(Dir, "config.json") : Path.GetFullPath(Environment.ExpandEnvironmentVariables(p));
        }
    }

    /// Reads the config; a missing file is written out with the defaults so there is something to edit.
    public static Config Load(out string? warning, string? path = null)
    {
        path ??= FilePath;
        warning = null;
        if (!File.Exists(path))
        {
            var c = new Config();
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, c.ToJson()); } catch { }
            return c;
        }
        string text;
        try { text = ReadShared(path); }
        catch (Exception e) { warning = "cannot read config, using defaults: " + e.Message; return new Config(); }
        var cfg = Parse(text, out var w);
        if (w.Count > 0) warning = "config: " + string.Join("; ", w);
        return cfg;
    }

    /// Editors save by truncate+write; share everything so we never block them and never throw on a race.
    public static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    /// Atomic save (temp + replace), for the control panel later.
    public void Save(string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson());
        File.Move(tmp, path, overwrite: true);
    }
}

/// mtime poll, once a second like the Mac: no FileSystemWatcher thread, and editors that replace
/// the file (new inode) are handled the same as in-place writes.
public sealed class ConfigWatcher
{
    readonly string path;
    DateTime stamp;
    public ConfigWatcher(string path) { this.path = path; stamp = Stamp(); }
    DateTime Stamp() { try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; } catch { return DateTime.MinValue; } }
    public bool Changed()
    {
        var s = Stamp();
        if (s == stamp || s == DateTime.MinValue) return false;
        stamp = s;
        return true;
    }
    /// after our own save, so it doesn't bounce back as a reload
    public void Touch() => stamp = Stamp();
}

public static class Color
{
    /// "#RRGGBB" → sRGB components 0..1 (not linearised, same as Mac). Bad input is black, not a crash.
    public static Vector4 Hex(string? s)
    {
        var t = (s ?? "").Trim();
        if (t.StartsWith('#')) t = t[1..];
        if (t.Length != 6 || !uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v)) return new(0, 0, 0, 1);
        return new(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f, 1);
    }
}
