namespace Wall42;

/// Which preset the current config is (the Mac's _current_preset.py): equal after dropping motion.activity and ui,
/// which wall42_set_preset keeps from the old config. Compared after parsing, so 1 vs 1.0 or a missing default
/// field don't count as a difference.
public static class PresetMatch
{
    public static string? Find(Config current, string? dir = null)
    {
        dir ??= Presets.Dir();
        if (dir == null) return null;
        var want = Normal(current);
        foreach (var name in Presets.Names(dir))
        {
            Config? p;
            try { p = Presets.Load(name, out _, dir); } catch { continue; }
            if (p != null && Normal(p) == want) return name;
        }
        return null;
    }

    static string Normal(Config c)
    {
        var n = c.Clone();
        n.Motion.Activity = new();
        n.Ui = new();
        return n.ToJson();
    }
}
