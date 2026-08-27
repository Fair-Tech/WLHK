using System.Text;

namespace Wlhk.Core;

/// <summary>
/// Minimal always-on diagnostic log. Startup is the one path we cannot debug
/// interactively (it runs at logon, before anyone is watching), so the launch
/// sequence and every failure is recorded to a small rolling file next to the
/// config.
/// </summary>
public static class Log
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();
    private static string? _path;

    public static string Path => _path ??= ResolvePath();

    private static string ResolvePath()
    {
        try
        {
            string portable = System.IO.Path.Combine(AppContext.BaseDirectory, "WLHK_data");
            if (Directory.Exists(portable))
                return System.IO.Path.Combine(portable, "wlhk.log");

            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WLHK");
            Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, "wlhk.log");
        }
        catch
        {
            return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wlhk.log");
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Trim();
                File.AppendAllText(Path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch { /* logging must never break the app */ }
    }

    public static void Error(string context, Exception ex) =>
        Write($"ERROR {context}: {ex.GetType().Name}: {ex.Message}");

    /// <summary>Keep the tail of the file when it grows past the cap.</summary>
    private static void Trim()
    {
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists || info.Length <= MaxBytes) return;
            var lines = File.ReadAllLines(Path);
            File.WriteAllLines(Path, lines.Skip(lines.Length / 2));
        }
        catch { }
    }
}
