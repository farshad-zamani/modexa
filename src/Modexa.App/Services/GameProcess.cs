using System.Diagnostics;
using System.IO;
using Modexa.App.Views;
using Modexa.Core.I18n;
using Modexa.Core.Install;

namespace Modexa.App.Services;

/// <summary>The game of a folder as a running program: is it open, and close it (like "End task").</summary>
public static class GameProcess
{
    /// <summary>Is the game running from this folder? (Its files are locked while it runs.)</summary>
    public static bool IsRunning(string gameFolder)
    {
        var found = Find(gameFolder);
        foreach (var p in found) p.Dispose();
        return found.Count > 0;
    }

    private static List<Process> Find(string gameFolder)
    {
        var result = new List<Process>();
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in new[] { gameFolder, Path.Combine(gameFolder, "bin", "x64") })
                if (Directory.Exists(dir))
                    foreach (var exe in Directory.EnumerateFiles(dir, "*.exe"))
                        names.Add(Path.GetFileNameWithoutExtension(exe));
            // Launchers/updaters can stay open; the game executables can't.
            names.RemoveWhere(n => n.Contains("Launcher", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("Setup", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("LanguageSelect", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("PlayGTAV", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("unins", StringComparison.OrdinalIgnoreCase));
            string root = Path.GetFullPath(gameFolder).TrimEnd('\\', '/') + "\\";
            foreach (var p in Process.GetProcesses())
            {
                if (!names.Contains(p.ProcessName)) { p.Dispose(); continue; }
                // The same program from another folder is another game: tell by its path when it can be read.
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { /* protected process: go by its name */ }
                if (path != null && !Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) { p.Dispose(); continue; }
                result.Add(p);
            }
        }
        catch { }
        return result;
    }

    /// <summary>Ends the game's programs (like Task Manager's "End task") and waits for them to go. True when none is left.</summary>
    public static bool Close(string gameFolder)
    {
        foreach (var p in Find(gameFolder))
        {
            try { p.Kill(entireProcessTree: true); }
            catch { /* already gone, or not allowed */ }
            try { p.WaitForExit(8000); } catch { }
            p.Dispose();
        }
        bool closed = !IsRunning(gameFolder);
        // Whatever the game had open (purchased content) is put away again now that it is gone.
        if (closed) FolderChanges.Settled(gameFolder);
        return closed;
    }

    /// <summary>
    /// When the game is open, offers to close it (one click). True when the game is not running — it
    /// wasn't, or it was closed now; false when the user kept it open or it couldn't be closed.
    /// </summary>
    public static async Task<bool> EnsureClosedAsync(string gameFolder)
    {
        if (!await Task.Run(() => IsRunning(gameFolder))) return true;
        if (!DialogWindow.Prompt(Loc.Instance["Mods_GameRunning_Ask"], Loc.Instance["Game_Close"], Loc.Instance["Common_Cancel"], DialogKind.Warning))
            return false;
        if (await Task.Run(() => Close(gameFolder))) return true;
        DialogWindow.Show(Loc.Instance["Game_Close_Failed"], DialogKind.Warning);
        return false;
    }
}
