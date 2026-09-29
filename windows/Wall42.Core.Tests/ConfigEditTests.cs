using System.Text.Json.Nodes;

namespace Wall42.Tests;

/// The tray menu and the control panel write config.json through ConfigEdit: what they don't touch must survive.
public class ConfigEditTests
{
    const string Sample = """
        {
          // hand-written, with a comment and a trailing comma
          "background": { "mode": "gradient", "centerColor": "#070A10" },
          "motion": {
            "particleCount": 420,
            "brightness": 0.42445773,
            "futureKnob": 7,
            "activity": { "source": "manual", "manualLevel": 0.6 },
          },
          "ui": { "menuBar": true },
          "wallpaperSync": { "enabled": true, "note": "說明" }
        }
        """;

    static ConfigEdit Open(string json) => new(ConfigEdit.ParseObject(json)!);

    [Fact]
    public void EditsKeepUnknownKeysOrderAndUntouchedNumbers()
    {
        var ed = Open(Sample);
        ed.SetNumber("motion.particleCount", 160.4, 0);
        ed.SetNumber("motion.link.distance", 212.3456, 0);       // missing section is created
        ed.SetNumber("motion.pulse.width", 0.0042252, 4);
        var json = ed.ToJson();
        var o = JsonNode.Parse(json)!;
        Assert.Equal(160, (long)o["motion"]!["particleCount"]!);
        Assert.Equal(212, (long)o["motion"]!["link"]!["distance"]!);
        Assert.Equal(0.0042, (double)o["motion"]!["pulse"]!["width"]!);
        Assert.Contains("\"brightness\": 0.42445773", json);          // untouched: exact text
        Assert.Equal(7, (int)o["motion"]!["futureKnob"]!);
        Assert.Equal("說明", (string)o["wallpaperSync"]!["note"]!);   // readable, not \uXXXX
        Assert.Contains("說明", json);
        var keys = o.AsObject().Select(kv => kv.Key).ToList();
        Assert.Equal(new[] { "background", "motion", "ui", "wallpaperSync" }, keys);
        Assert.Equal(160, ed.ToConfig().Motion.ParticleCount);
    }

    [Fact]
    public void ApplyPresetKeepsActivityUiAndForeignSections()
    {
        var ed = Open(Sample);
        var neon = ConfigEdit.ParseObject(File.ReadAllText(Path.Combine(Presets.Dir()!, "neon.json")))!;
        ed.ApplyPreset(neon);
        var c = ed.ToConfig();
        Assert.Equal(Presets.Load("neon", out _)!.Motion.ParticleCount, c.Motion.ParticleCount);
        Assert.Equal("manual", c.Motion.Activity.Source);                 // the user's activity survives the style switch
        Assert.Equal(0.6f, c.Motion.Activity.ManualLevel);
        Assert.True(ed.Root["wallpaperSync"] is JsonObject);               // not a style key: kept
        Assert.True(ed.Root["ui"] is JsonObject);
        Assert.Equal("neon", ConfigEdit.MatchPreset(c));
    }

    [Fact]
    public void MatchPresetIgnoresActivityAndFormatting()
    {
        var k = Presets.Load("kang", out _)!;
        k.Motion.Activity.Source = "off";
        k.Ui.MenuBar = false;
        Assert.Equal("kang", ConfigEdit.MatchPreset(k));
        k.Motion.ParticleCount++;
        Assert.Null(ConfigEdit.MatchPreset(k));
    }

    [Fact]
    public void RemoveMakesAKeyAutomaticAgain()
    {
        var ed = Open("""{ "motion": { "link": { "color": "#112233", "distance": 100 } } }""");
        Assert.Equal("#112233", ed.ToConfig().Motion.Link.Color);
        ed.Remove("motion.link.color");
        Assert.Null(ed.ToConfig().Motion.Link.Color);
        Assert.Equal(100, ed.ToConfig().Motion.Link.Distance);
        ed.Remove("no.such.path");                                        // harmless
    }

    [Fact]
    public void BrokenFileIsNeverOpenedForWriting()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wall42-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, "{ \"motion\": { \"particleCount\": ");     // someone mid-edit
            Assert.Null(ConfigEdit.Open(path, out var err));
            Assert.NotNull(err);
            File.WriteAllText(path, "[1,2]");
            Assert.Null(ConfigEdit.Open(path, out _));
            var missing = ConfigEdit.Open(Path.Combine(dir, "none.json"), out _);
            Assert.NotNull(missing);                                              // missing = start empty
            // atomic save round trip
            var ed = Open(Sample);
            ed.SetBool("motion.sessions.enabled", true);
            ed.Save(path);
            Assert.False(File.Exists(path + ".tmp"));
            var back = ConfigEdit.Open(path, out _)!;
            Assert.True(back.ToConfig().Motion.Sessions?.Enabled);
            Assert.Equal(7, (int)back.Root["motion"]!["futureKnob"]!);
        }
        finally { Directory.Delete(dir, true); }
    }
}
