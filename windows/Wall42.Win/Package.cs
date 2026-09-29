using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Wall42;

/// Installing, updating and uninstalling (per user, no admin), like the Mac install.sh / uninstall.sh:
///   wall42.exe                     packed download (pack.ps1): asks first (TaskDialog), then installs itself.
///                                  The installed copy, a dev build, or --run: the wallpaper layer as always.
///   wall42.exe --install [--quiet] copy to %LOCALAPPDATA%\wall42\bin, presets to %LOCALAPPDATA%\wall42\presets,
///                                  %APPDATA%\wall42\config.json only if missing, HKCU Run, Settings → Apps, start
///   wall42.exe --uninstall [--quiet]  stop it, remove Run + Settings → Apps, delete bin and the log;
///                                  config, presets (and later wallpaper backups) stay, like the Mac
///   wall42.exe --version
/// Never touches the system wallpaper: we draw above it, and when we stop Explorer shows it again as it was.
///
/// Test overrides (so an e2e never touches the real install): WALL42_HOME (instead of %LOCALAPPDATA%\wall42),
/// WALL42_DATA (instead of %APPDATA%\wall42, in Config.Dir), WALL42_REG_ROOT (instead of
/// HKCU\Software\Microsoft\Windows\CurrentVersion, for both Run and Uninstall).
static class Package
{
    public const string Name = "wall42";
    const string Exe = "wall42.exe", PresetPrefix = "presets/";

    public static string Version =>
        typeof(Package).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(Package).Assembly.GetName().Version?.ToString(3) ?? "0";

    /// A packed exe (pack.ps1) carries the presets inside; a dev build doesn't and never shows the install prompt.
    static bool IsPacked => typeof(Package).Assembly.GetManifestResourceNames().Any(n => n.StartsWith(PresetPrefix));

    public static string Home => Environment.GetEnvironmentVariable("WALL42_HOME") is { Length: > 0 } h ? Path.GetFullPath(h)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
    static string Bin => Path.Combine(Home, "bin");
    static string PresetDir => Path.Combine(Home, "presets");
    static string InstalledExe => Path.Combine(Bin, Exe);
    static bool RunningInstalled => SamePath(Environment.ProcessPath!, InstalledExe);

    /// Program.Main calls this first: an exit code, or null = run the wallpaper layer.
    public static int? Dispatch(string[] args)
    {
        bool quiet = args.Contains("--quiet");
        switch (args.FirstOrDefault())
        {
            case "--version": return Report(() => Version, quiet: true);
            case "--install": return Report(Install, quiet);
            case "--uninstall": return Report(Uninstall, quiet);
            case "--run": return null;
            case null:
                // the downloaded exe asks; the installed copy (autostart starts it with no arguments) just runs
                if (IsPacked && !RunningInstalled && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WALL42_SNAPSHOT"))) return FirstRun();
                return null;
            default:
                return Report(() => throw new ArgumentException($"unknown argument {args[0]}; use --install, --uninstall, --version or --run"), quiet);
        }
    }

    // ── install / update ───────────────────────────────────────────

    public static string Install()
    {
        var before = InstalledVersion;
        var stopped = StopInstalled();
        Directory.CreateDirectory(Bin);
        if (IsPacked)
        {
            if (!RunningInstalled) CopyAtomic(Environment.ProcessPath!, InstalledExe);
            // a packed install is one file; whatever an older dev install left (dlls, json) goes
            foreach (var f in Directory.GetFiles(Bin))
                if (!Path.GetFileName(f).Equals(Exe, StringComparison.OrdinalIgnoreCase)) try { File.Delete(f); } catch { }
        }
        else
        {
            var here = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');
            if (!SamePath(here, Bin)) CopyBuild(here, Bin);
        }
        int presets = InstallPresets();
        var cfg = Config.FilePath;
        string config;
        if (File.Exists(cfg)) config = $"kept {cfg}";
        else { new Config().Save(cfg); config = $"created {cfg}"; }
        SetAutostart(true);
        Register();
        // ShellExecute: the resident process must not inherit our stdout (a caller reading it would wait forever);
        // cwd = Home, never bin, so nothing holds the bin folder open when an uninstall wants to delete it
        Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true, WorkingDirectory = Home });
        return (before == null ? $"installed {Version}" : $"updated {before} -> {Version}") + $" to {Bin}; presets: {presets} in {PresetDir}; " +
               $"config: {config}; autostart: on; Settings -> Apps: listed; " + (stopped > 0 ? "restarted" : "started") + "; system wallpaper: untouched";
    }

    /// The shipped presets, overwritten on every install/update (they are ours); presets the user added stay.
    static int InstallPresets()
    {
        Directory.CreateDirectory(PresetDir);
        int n = 0;
        if (IsPacked)
        {
            var asm = typeof(Package).Assembly;
            foreach (var r in asm.GetManifestResourceNames().Where(r => r.StartsWith(PresetPrefix)))
            {
                using var s = asm.GetManifestResourceStream(r)!;
                WriteAtomic(s, Path.Combine(PresetDir, r[PresetPrefix.Length..]));
                n++;
            }
        }
        else if (Presets.Dir() is { } src && !SamePath(src, PresetDir))
        {
            foreach (var f in Directory.GetFiles(src, "*.json")) { CopyAtomic(f, Path.Combine(PresetDir, Path.GetFileName(f))); n++; }
        }
        return n;
    }

    // ── uninstall ──────────────────────────────────────────────────

    /// Everything Install did, undone — except the user's things: config.json and the signal folder
    /// (%APPDATA%\wall42) and the presets folder stay for a later reinstall, like the Mac uninstall.sh.
    public static string Uninstall()
    {
        var stopped = StopInstalled();
        SetAutostart(false);
        Unregister();
        // the status file (MCP) goes too; synced wallpaper PNGs and the backup stay: one may be the system wallpaper now
        foreach (var f in new[] { "wall42.log", "wall42.old.log", "status.json" }) try { File.Delete(Path.Combine(Home, f)); } catch { }
        string files;
        if (!Directory.Exists(Bin)) files = "no files";
        else if (RunningInstalled)
        {
            // an exe can't delete itself: a hidden cmd does it once we have exited, retrying for ~15 s
            // (a big single-file exe that was just scanned or is still being unmapped stays locked for a moment).
            // Our std handles are made non-inheritable first: a caller reading our stdout must not wait for the cmd too.
            foreach (var std in new[] { -10, -11, -12 }) SetHandleInformation(GetStdHandle(std), HANDLE_FLAG_INHERIT, 0);
            Process.Start(new ProcessStartInfo("cmd.exe",
                    $"/d /c for /l %i in (1,1,15) do @if exist \"{Bin}\" (ping -n 2 127.0.0.1 >nul & rmdir /s /q \"{Bin}\" 2>nul)")
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() });
            files = $"{Bin} removed after exit";
        }
        else { Directory.Delete(Bin, recursive: true); files = $"{Bin} removed"; }
        return $"stopped: {stopped}; autostart: off; Settings -> Apps: removed; {files}; kept: {Config.Dir} (config), {PresetDir} (presets); system wallpaper: untouched";
    }

    // ── the running copy ───────────────────────────────────────────

    /// wall42 processes started from the install folder. A dev build or a test copy running elsewhere is none of our business.
    static List<Process> Installed()
    {
        var list = new List<Process>();
        foreach (var p in Process.GetProcessesByName(Name))
        {
            if (p.Id == Environment.ProcessId) continue;
            if (ImagePath(p.Id) is { } f && SamePath(f, InstalledExe)) list.Add(p); else p.Dispose();
        }
        return list;
    }

    /// WM_CLOSE to its hidden main window first (a clean exit: the log line, WorkerW repaint on older Windows); kill if it doesn't go.
    static int StopInstalled()
    {
        var procs = Installed();
        foreach (var p in procs)
        {
            foreach (var h in MainWindows((uint)p.Id)) PostMessageW(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            try { if (!p.WaitForExit(5000)) { p.Kill(); p.WaitForExit(3000); } } catch { }
            p.Dispose();
        }
        return procs.Count;
    }

    static List<IntPtr> MainWindows(uint pid)
    {
        var found = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var owner);
            if (owner == pid)
            {
                var sb = new System.Text.StringBuilder(64);
                if (GetClassNameW(h, sb, sb.Capacity) > 0 && sb.ToString() == "wall42.main") found.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ── files ──────────────────────────────────────────────────────

    static void CopyAtomic(string from, string to)
    {
        using var s = File.OpenRead(from);
        WriteAtomic(s, to);
    }

    static void WriteAtomic(Stream s, string to)
    {
        var tmp = to + ".new";
        using (var f = File.Create(tmp)) s.CopyTo(f);
        File.Move(tmp, to, overwrite: true);                     // never a half-written file
    }

    /// Dev install (dotnet build output, several files): the top-level files but the pdbs. Subfolders are not
    /// needed (runtimes\browser, or a win-x64\ publish output that pack.ps1 leaves next to it).
    static void CopyBuild(string from, string to)
    {
        foreach (var f in Directory.GetFiles(from))
            if (!f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) CopyAtomic(f, Path.Combine(to, Path.GetFileName(f)));
    }

    // ── registry: HKCU Run (autostart) and HKCU Uninstall (Settings → Apps) ──

    static string RegRoot =>
        Environment.GetEnvironmentVariable("WALL42_REG_ROOT") is { Length: > 0 } r ? r : @"Software\Microsoft\Windows\CurrentVersion";
    static string RunKey => RegRoot + @"\Run";
    static string UninstallKey => RegRoot + @"\Uninstall\" + Name;

    static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Name, $"\"{InstalledExe}\"");
        else if (k.GetValue(Name) != null) k.DeleteValue(Name);
    }

    static void Register()
    {
        using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
        k.SetValue("DisplayName", Name);
        k.SetValue("DisplayVersion", Version);
        k.SetValue("Publisher", Name);
        k.SetValue("DisplayIcon", InstalledExe);
        k.SetValue("InstallLocation", Bin);
        k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        k.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        var kb = Directory.EnumerateFiles(Bin, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024;
        k.SetValue("EstimatedSize", (int)Math.Min(kb, int.MaxValue), RegistryValueKind.DWord);
    }

    static void Unregister()
    {
        using var root = Registry.CurrentUser.OpenSubKey(RegRoot + @"\Uninstall", writable: true);
        if (root?.OpenSubKey(Name) != null) root.DeleteSubKeyTree(Name);
    }

    static string? InstalledVersion
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return File.Exists(InstalledExe) ? k?.GetValue("DisplayVersion") as string ?? "?" : null;
        }
    }

    // ── double-click on the downloaded exe ─────────────────────────

    /// Ask before touching anything: what gets installed where, and that the wallpaper itself is left alone.
    static int FirstRun()
    {
        var installed = InstalledVersion;
        if (installed == Version)
        {
            if (Installed().Count == 0) Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true, WorkingDirectory = Home });
            Dialog.Info(Name, "wall42 已經安裝好了", $"版本 {Version}，正在桌面上跑（被視窗蓋住時會自動停畫）。\n設定檔：{Config.FilePath}（存檔即生效）");
            return 0;
        }
        var text = "在桌布之上、桌面圖示之下畫會動的粒子；桌面被視窗蓋住時完全停畫，幾乎不耗電。\n\n安裝會：\n" +
                   $"• 把程式放到 {Bin}（只有你這個帳號，不需要系統管理員）\n" +
                   $"• 內建樣式（preset）放到 {PresetDir}\n" +
                   $"• 設定檔 {Config.FilePath} 不存在時才建立預設值；已有的設定不動\n" +
                   "• 開機時自動啟動，並列在「設定 > 應用程式」裡可以解除安裝\n\n" +
                   "不會更改你的系統桌布：粒子畫在桌布上面，結束或解除安裝後就是原本的桌布。";
        var go = Dialog.Ask(Name, installed == null ? $"安裝 wall42 {Version}？" : $"把 wall42 從 {installed} 更新到 {Version}？",
                            text, installed == null ? "安裝" : "更新");
        if (!go) return 0;
        try
        {
            Install();
            Dialog.Info(Name, installed == null ? "安裝好了" : "更新好了",
                        $"粒子已經在桌面上跑了（桌面被視窗蓋住時看不到，也不會耗電）。\n設定檔：{Config.FilePath}（存檔即生效）\n" +
                        "要移除：「設定 > 應用程式」→ wall42 → 解除安裝（設定檔會保留）。");
            return 0;
        }
        catch (Exception e)
        {
            Dialog.Error(Name, "安裝失敗", e.Message);
            return 1;
        }
    }

    /// WinExe: attach to the calling console (if any) so the answer shows there. Started from Settings → Apps
    /// (no console to attach to), the answer goes into a dialog unless --quiet.
    static int Report(Func<string> action, bool quiet)
    {
        bool console = AttachConsole(ATTACH_PARENT_PROCESS);
        try
        {
            var r = action();
            Console.WriteLine(r);
            if (!console && !quiet) Dialog.Info(Name, "wall42", r);
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            if (!console && !quiet) Dialog.Error(Name, "wall42", e.Message);
            return 1;
        }
    }

    // ── paths ──────────────────────────────────────────────────────

    /// %TEMP% is often an 8.3 short path (C:\Users\ABCDEF~1\…) while a process reports its long path: compare long forms.
    static bool SamePath(string a, string b) =>
        string.Equals(LongPath(a).TrimEnd('\\'), LongPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    static string LongPath(string p)
    {
        var full = Path.GetFullPath(p);
        var sb = new System.Text.StringBuilder(1024);
        var n = GetLongPathNameW(full, sb, (uint)sb.Capacity);
        return n > 0 && n < sb.Capacity ? sb.ToString() : full;              // a path that doesn't exist stays as is
    }

    static string? ImagePath(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            uint n = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref n) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    const uint WM_CLOSE = 0x0010, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, ATTACH_PARENT_PROCESS = unchecked((uint)-1);
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hwnd, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetLongPathNameW(string shortPath, System.Text.StringBuilder longPath, uint n);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, System.Text.StringBuilder s, ref uint n);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] static extern bool SetHandleInformation(IntPtr h, uint mask, uint flags);
    const uint HANDLE_FLAG_INHERIT = 1;
}
