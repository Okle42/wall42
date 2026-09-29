namespace Wall42;

/// The shared style presets (repo presets\*.json, the same files the Mac uses).
public static class Presets
{
    /// WALL42_REPO wins (like the Mac); else walk up from the exe until a folder has presets\*.json
    /// (a dev build sits in windows\Wall42.Win\bin\…); else the installed copy under %LOCALAPPDATA%\wall42.
    public static string? Dir()
    {
        var repo = Environment.GetEnvironmentVariable("WALL42_REPO");
        if (!string.IsNullOrWhiteSpace(repo) && HasPresets(Path.Combine(repo, "presets"))) return Path.Combine(repo, "presets");
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (HasPresets(Path.Combine(d.FullName, "presets"))) return Path.Combine(d.FullName, "presets");
        var home = Environment.GetEnvironmentVariable("WALL42_HOME");
        var installed = Path.Combine(string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wall42") : home, "presets");
        return HasPresets(installed) ? installed : null;
    }

    static bool HasPresets(string dir) => Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.json").Any();

    public static IReadOnlyList<string> Names(string? dir = null)
    {
        dir ??= Dir();
        if (dir == null) return Array.Empty<string>();
        return Directory.EnumerateFiles(dir, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    public static Config? Load(string name, out List<string> warnings, string? dir = null)
    {
        warnings = new();
        dir ??= Dir();
        var f = dir == null ? null : Path.Combine(dir, name + ".json");
        if (f == null || !File.Exists(f)) return null;
        return Config.Parse(Config.ReadShared(f), out warnings);
    }
}
