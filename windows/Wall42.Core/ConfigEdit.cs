using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wall42;

/// Edits config.json as a JSON tree, not through Config: keys we don't know (added by a newer version, by
/// hand, by another tool) survive, untouched values keep their exact text, key order stays. Used by the
/// tray menu and the control panel; the renderer still only ever reads through Config.Parse.
public sealed class ConfigEdit
{
    static readonly JsonDocumentOptions docOpts = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    static readonly JsonSerializerOptions writeOpts = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public JsonObject Root { get; private set; }

    public ConfigEdit(JsonObject root) => Root = root;

    /// null when the file exists but isn't a JSON object: then nobody should overwrite it (the user is
    /// probably in the middle of editing it by hand). A missing file is an empty object.
    public static ConfigEdit? Open(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path)) return new ConfigEdit(new JsonObject());
        try
        {
            var node = JsonNode.Parse(Config.ReadShared(path), null, docOpts);
            if (node is JsonObject o) return new ConfigEdit(o);
            error = "設定檔的最外層不是物件";
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { error = e.Message; }
        return null;
    }

    public static JsonObject? ParseObject(string json)
    {
        try { return JsonNode.Parse(json, null, docOpts) as JsonObject; } catch (JsonException) { return null; }
    }

    public string ToJson() => Root.ToJsonString(writeOpts);

    /// What the renderer will see after this edit.
    public Config ToConfig() => Config.Parse(ToJson(), out _);

    /// Atomic: temp file + replace, so the 1 s hot-reload poll never reads half a file.
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson() + Environment.NewLine);
        File.Move(tmp, path, overwrite: true);
    }

    public JsonNode? Get(string path)
    {
        JsonNode? n = Root;
        foreach (var k in path.Split('.'))
        {
            if (n is not JsonObject o || !o.TryGetPropertyValue(k, out n)) return null;
        }
        return n;
    }

    /// "motion.link.distance" = v, creating missing (or non-object) sections on the way.
    public void Set(string path, JsonNode? value)
    {
        var keys = path.Split('.');
        var o = Root;
        for (int i = 0; i < keys.Length - 1; i++)
        {
            if (o[keys[i]] is not JsonObject child) { child = new JsonObject(); o[keys[i]] = child; }
            o = child;
        }
        o[keys[^1]] = value;
    }

    /// Numbers are written rounded to what the control shows: 0.45, not 0.44999998807907104.
    public void SetNumber(string path, double v, int decimals)
    {
        v = Math.Round(v, decimals, MidpointRounding.AwayFromZero);
        Set(path, decimals == 0 ? JsonValue.Create((long)v) : JsonValue.Create(v));
    }

    /// Back to "not set" (for keys whose absence means something, e.g. link.color = automatic).
    public void Remove(string path)
    {
        int dot = path.LastIndexOf('.');
        if ((dot < 0 ? Root : Get(path[..dot])) is JsonObject o) o.Remove(path[(dot + 1)..]);
    }

    public void SetBool(string path, bool v) => Set(path, JsonValue.Create(v));
    public void SetString(string path, string v) => Set(path, JsonValue.Create(v));

    /// Switching style: the preset's background and motion replace ours, but the user's activity source
    /// (Mac: "don't let a style file wash it away"), the ui section and every key the preset doesn't
    /// have (e.g. settings of other features) stay.
    public void ApplyPreset(JsonObject preset)
    {
        var activity = Get("motion.activity")?.DeepClone();
        foreach (var (k, v) in preset)
        {
            if (k == "ui") continue;
            Root[k] = v?.DeepClone();
        }
        if (activity != null) Set("motion.activity", activity);
    }

    /// Which preset the config currently is (activity and ui ignored, like the Mac's currentPresetName),
    /// compared on the parsed values so formatting and omitted defaults don't matter.
    public static string? MatchPreset(Config current, string? dir = null)
    {
        var want = Normalized(current);
        foreach (var name in Presets.Names(dir))
        {
            var p = Presets.Load(name, out _, dir);
            if (p != null && Normalized(p) == want) return name;
        }
        return null;
    }

    static string Normalized(Config c)
    {
        var n = c.Clone();
        n.Motion.Activity = new ActivityConfig();
        n.Ui = new UIConfig();
        return n.ToJson();
    }
}
