using System.IO;
using Modexa.Core.Diagnostics;
using Modexa.Core.Prepare;
using Modexa.Core.Rpf;

namespace Modexa.Core.Install;

/// <summary>Are an installed mod's files still in the game?</summary>
public enum InstallHealth
{
    /// <summary>Everything Modexa put there is still there (or there is nothing it could check).</summary>
    Ok,
    /// <summary>Some of it is gone.</summary>
    Partial,
    /// <summary>All of it is gone (the game folder was restored, moved or the mod was deleted by hand).</summary>
    Missing
}

/// <summary>
/// Checks the mods Modexa installed against the game folder: a mod whose files were removed by another
/// program, a backup restore, or by hand is shown as missing in the lists instead of as installed.
/// </summary>
public static class InstallChecks
{
    /// <summary>Health of each record (by id). Reads the game folders and, once per archive, its table of contents.</summary>
    public static Dictionary<string, InstallHealth> Check(IEnumerable<ModInstallRecord> records)
    {
        var result = new Dictionary<string, InstallHealth>();
        var archives = new Dictionary<string, RpfEditor?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var r in records)
            {
                InstallHealth health;
                try
                {
                    health = r.ProductId != null
                        ? FolderChanges.Engine()?.CheckHealth(r) ?? InstallHealth.Ok // purchased content is kept by the engine
                        : CheckRecord(r, archives);
                }
                catch (Exception ex)
                {
                    Log.Error("Health check", ex);
                    health = InstallHealth.Ok;
                }
                result[r.Id] = health;
            }
        }
        finally
        {
            foreach (var a in archives.Values) a?.Dispose();
        }
        return result;
    }

    public static InstallHealth Check(ModInstallRecord record) => Check(new[] { record })[record.Id];

    /// <summary>The check on what is on disk: files, mods-folder archives and the files created inside them.</summary>
    public static InstallHealth CheckRecord(ModInstallRecord r, Dictionary<string, RpfEditor?>? archives = null)
        => CheckRecord(r, rel => IsThere(r.GameFolder, rel), archives);

    /// <summary>The same check with a custom "is this file there" (the engine knows where it keeps purchased files).</summary>
    public static InstallHealth CheckRecord(ModInstallRecord r, Func<string, bool> fileThere, Dictionary<string, RpfEditor?>? archives = null)
    {
        if (string.IsNullOrWhiteSpace(r.GameFolder) || !Directory.Exists(r.GameFolder)) return InstallHealth.Missing;

        int total = 0, missing = 0;
        foreach (var f in r.Files.Where(f => !f.Reverted && !f.WasDeleted && !string.IsNullOrWhiteSpace(f.Dest)))
        {
            total++;
            if (!fileThere(f.Dest)) missing++;
        }

        bool own = archives == null;
        archives ??= new Dictionary<string, RpfEditor?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var g in r.ArchiveEdits.Where(e => !e.Reverted).GroupBy(e => e.Archive, StringComparer.OrdinalIgnoreCase))
            {
                string path = ModsArchives.ModsPath(r.GameFolder, g.Key);
                if (!File.Exists(path))
                {
                    total += g.Count();
                    missing += g.Count();
                    continue;
                }
                // Files the mod created at the top of that archive: still in it?
                var created = g.Where(e => e.WasCreated && e.Nested.Count == 0 && !e.Entry.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)).ToList();
                if (created.Count == 0) { total++; continue; }
                var ed = OpenOnce(archives, path);
                foreach (var e in created)
                {
                    total++;
                    if (ed != null && !ed.FileExists(e.Entry)) missing++;
                }
            }
        }
        finally
        {
            if (own) foreach (var a in archives.Values) a?.Dispose();
        }

        return total == 0 || missing == 0 ? InstallHealth.Ok : missing >= total ? InstallHealth.Missing : InstallHealth.Partial;
    }

    private static RpfEditor? OpenOnce(Dictionary<string, RpfEditor?> cache, string path)
    {
        if (cache.TryGetValue(path, out var ed)) return ed;
        try { ed = RpfEditor.Open(path); }
        catch { ed = null; } // can't read it right now: don't claim anything is missing
        cache[path] = ed;
        return ed;
    }

    /// <summary>A file of a mod is there — or parked by "clean launch" next to where it belongs.</summary>
    public static bool IsThere(string gameFolder, string relative)
    {
        string full = Path.Combine(gameFolder, relative);
        return File.Exists(full) || File.Exists(full + CleanLaunchService.DisabledSuffix) || Directory.Exists(full);
    }
}
