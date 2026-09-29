namespace Wall42.Tests;

public class SessionsTests
{
    // a real-shaped session file (made-up ids and values)
    const string Busy = """{"pid":4242,"sessionId":"aaaa-1111","cwd":"C:\\x","startedAt":1790000000000,"procStart":"134000000000000000","kind":"bg","name":"t","status":"busy","updatedAt":1}""";
    const string Idle = """{"pid":5151,"sessionId":"bbbb-2222","startedAt":1780000000000,"procStart":134000000000000500,"status":"idle"}""";
    const string Key = """{"peerToken":"00","procStartFt":"134000000000000000","pidDomain":"win32:x"}""";

    static Func<int, long?> Procs(params (int Pid, long Created)[] live) =>
        pid => live.Where(p => p.Pid == pid).Select(p => (long?)p.Created).FirstOrDefault();

    sealed class TempDir : IDisposable
    {
        public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wall42-test-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public string Write(string name, string text) { var f = System.IO.Path.Combine(Path, name); File.WriteAllText(f, text); return f; }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    [Fact]
    public void ParsesTheSessionFile()
    {
        var e = ClaudeSessions.Parse(Busy)!.Value;
        Assert.Equal(4242, e.Pid);
        Assert.Equal("aaaa-1111", e.Id);
        Assert.True(e.Busy);
        Assert.Equal(134000000000000000, e.ProcStart);        // string form
        var i = ClaudeSessions.Parse(Idle)!.Value;
        Assert.False(i.Busy);
        Assert.Equal(134000000000000500, i.ProcStart);        // number form
    }

    [Fact]
    public void KeyFilesAndGarbageAreNotSessions()
    {
        Assert.Null(ClaudeSessions.Parse(Key));
        Assert.Null(ClaudeSessions.Parse("not json"));
        Assert.Null(ClaudeSessions.Parse("[1,2]"));
        Assert.Null(ClaudeSessions.Parse("""{"pid":"x"}"""));
        Assert.Null(ClaudeSessions.Parse("""{"pid":-3}"""));
        var noId = ClaudeSessions.Parse("""{"pid":77}""")!.Value;
        Assert.Equal("pid-77", noId.Id);
        Assert.False(noId.Busy);
        Assert.Null(noId.ProcStart);
    }

    [Fact]
    public void PidReuseIsNotTheSession()
    {
        var e = ClaudeSessions.Parse(Busy)!.Value;
        Assert.True(ClaudeSessions.Alive(e, Procs((4242, 134000000000000000))));                  // same process
        Assert.True(ClaudeSessions.Alive(e, Procs((4242, 134000000000000000 + 50_000))));         // rounding
        Assert.False(ClaudeSessions.Alive(e, Procs((4242, 134000000000000000 + 30_000_000))));    // same pid, started 3 s later
        Assert.False(ClaudeSessions.Alive(e, Procs((4242, 133999999000000000))));                 // same pid, older process
        Assert.False(ClaudeSessions.Alive(e, Procs()));                                           // gone
        var noStart = ClaudeSessions.Parse("""{"pid":77,"status":"busy"}""")!.Value;
        Assert.True(ClaudeSessions.Alive(noStart, Procs((77, 1))));                               // no procStart: pid alone
    }

    [Fact]
    public void ScanKeepsLiveOnesOldestFirst()
    {
        using var d = new TempDir();
        d.Write("4242.json", Busy);
        d.Write("5151.json", Idle);
        d.Write("4242.abcdef.key", Key);
        d.Write("9999.json", """{"pid":9999,"sessionId":"dead","procStart":"1","status":"busy"}""");
        d.Write("broken.json", "{");
        var list = ClaudeSessions.Scan(d.Path, Procs((4242, 134000000000000000), (5151, 134000000000000500), (9999, 2_000_000_000)));
        Assert.Equal(new[] { "bbbb-2222", "aaaa-1111" }, list.Select(s => s.Id));      // startedAt order
        Assert.Equal(new[] { false, true }, list.Select(s => s.Busy));
        Assert.Empty(ClaudeSessions.Scan(System.IO.Path.Combine(d.Path, "missing"), Procs()));
    }

    [Fact]
    public void FeedFileForms()
    {
        var n = ClaudeSessions.ParseFeed("""{"count":5,"busy":2}""");
        Assert.Equal(5, n.Count);
        Assert.Equal(2, n.Count(s => s.Busy));
        Assert.Equal("feed-0", n[0].Id);
        Assert.Equal(64, ClaudeSessions.ParseFeed("""{"count":500}""").Count);
        Assert.Empty(ClaudeSessions.ParseFeed("""{"count":-2}"""));
        var l = ClaudeSessions.ParseFeed("""{"sessions":[{"id":"a","busy":true},{"busy":false},{"id":"c"}]}""");
        Assert.Equal(new[] { "a", "feed-1", "c" }, l.Select(s => s.Id));
        Assert.Equal(new[] { true, false, false }, l.Select(s => s.Busy));
        Assert.Empty(ClaudeSessions.ParseFeed("garbage"));
    }

    [Fact]
    public void FeedReadsOnlyWhileDrawingAndEnabled()
    {
        using var d = new TempDir();
        d.Write("4242.json", Busy);
        int probes = 0;
        var feed = new SessionFeed(System.IO.Path.Combine(d.Path, "no-feed.json"), d.Path,
            pid => { probes++; return pid == 4242 ? 134000000000000000 : null; });
        var on = new SessionsConfig { Enabled = true };

        Assert.Null(feed.Poll(null, true, 0));                       // disabled: nothing
        Assert.Null(feed.Poll(new SessionsConfig { Enabled = false }, true, 0));
        Assert.Equal(0, probes);

        var l = feed.Poll(on, true, 10);                             // first read immediately
        Assert.Single(l!);
        Assert.Equal("auto", feed.Source);
        Assert.Equal(1, feed.Busy);
        Assert.Null(feed.Poll(on, true, 11));                        // throttled to every 2 s
        Assert.NotNull(feed.Poll(on, true, 12));
        Assert.Equal(2, probes);

        for (int t = 13; t < 100; t++) Assert.Null(feed.Poll(on, false, t));   // paused: zero work
        Assert.Equal(2, probes);
        Assert.NotNull(feed.Poll(on, true, 100.1));                  // back: read at once, not 2 s later
        Assert.Equal(3, probes);

        var off = feed.Poll(new SessionsConfig { Enabled = false }, true, 101);
        Assert.NotNull(off);
        Assert.Empty(off!);                                          // switched off: cleared once
        Assert.Null(feed.Poll(new SessionsConfig { Enabled = false }, true, 102));
    }

    [Fact]
    public void FeedFileWinsOverAuto()
    {
        using var d = new TempDir();
        d.Write("4242.json", Busy);
        var feedPath = d.Write("feed.json", """{"count":3,"busy":1}""");
        var feed = new SessionFeed(feedPath, d.Path, _ => 134000000000000000);
        var l = feed.Poll(new SessionsConfig { Enabled = true }, true, 0)!;
        Assert.Equal(3, l.Count);
        Assert.Equal("file", feed.Source);
        Assert.Equal("file 3 (busy 1)", feed.SummaryIfChanged());
        Assert.Null(feed.SummaryIfChanged());
        File.Delete(feedPath);
        l = feed.Poll(new SessionsConfig { Enabled = true }, true, 5)!;
        Assert.Single(l);
        Assert.Equal("auto", feed.Source);
        // source=file with no file: nothing, not the auto list
        var fileOnly = new SessionFeed(feedPath, d.Path, _ => 134000000000000000);
        Assert.Empty(fileOnly.Poll(new SessionsConfig { Enabled = true, Source = "file" }, true, 0)!);
    }

    [Fact]
    public void ProbeSeesItselfAndNotAMadeUpPid()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        var created = ClaudeSessions.Parse($$"""{"pid":{{Environment.ProcessId}},"procStart":"{{me.StartTime.ToUniversalTime().ToFileTimeUtc()}}"}""")!.Value;
        Assert.NotNull(ProcessProbe.CreationTime(Environment.ProcessId));
        Assert.True(ClaudeSessions.Alive(created, ProcessProbe.CreationTime));
        Assert.Null(ProcessProbe.CreationTime(0));
    }

    [Fact]
    public void PresetMatchIgnoresActivityAndUi()
    {
        var dir = Presets.Dir();
        Assert.NotNull(dir);
        var neon = Presets.Load("neon", out _, dir)!;
        neon.Motion.Activity.Source = "manual";
        neon.Motion.Activity.ManualLevel = 0.9f;
        neon.Ui.MenuBar = false;
        Assert.Equal("neon", PresetMatch.Find(neon, dir));
        neon.Motion.ParticleCount += 1;
        Assert.Null(PresetMatch.Find(neon, dir));
    }
}
