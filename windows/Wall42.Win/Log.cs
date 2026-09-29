namespace Wall42;

/// %LOCALAPPDATA%\wall42\wall42.log (the Mac logs to ~/Library/Logs/wall42.log). WALL42_LOG moves it.
/// Rotates to wall42.old.log at 2 MB so a resident process never grows it forever.
static class Log
{
    public static readonly string PathName = Environment.GetEnvironmentVariable("WALL42_LOG") is { Length: > 0 } p ? p
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wall42", "wall42.log");

    public static void Note(string s)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
            var fi = new FileInfo(PathName);
            if (fi.Exists && fi.Length > 2_000_000)
                File.Move(PathName, System.IO.Path.ChangeExtension(PathName, ".old.log"), overwrite: true);
            File.AppendAllText(PathName, $"[{DateTime.Now:HH:mm:ss}] {s}{Environment.NewLine}");
        }
        catch { }                      // logging must never take the wallpaper down
    }
}
