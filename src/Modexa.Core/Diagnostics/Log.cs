using System.IO;
using System.Text;

namespace Modexa.Core.Diagnostics;

/// <summary>
/// Tiny append-only log in %LocalAppData%\Modexa\logs (one file per day, last 7 kept). Used for
/// crash reports so a closed window always leaves a trace the user can send us.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    // Invariant culture: on a Persian-calendar Windows the file would otherwise be named 1405-07-15.
    public static string CurrentFile => Path.Combine(AppPaths.LogsDir,
        "modexa-" + DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string context, Exception ex) => Write("ERROR", $"{context}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                AppPaths.EnsureDir(AppPaths.LogsDir);
                File.AppendAllText(CurrentFile,
                    DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + $" [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never throw.
        }
    }

    /// <summary>Deletes logs older than a week.</summary>
    public static void Prune()
    {
        try
        {
            if (!Directory.Exists(AppPaths.LogsDir)) return;
            foreach (var f in Directory.GetFiles(AppPaths.LogsDir, "modexa-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7))
                    File.Delete(f);
        }
        catch { }
    }
}
