using System.IO;
using Modexa.Core.Diagnostics;

namespace Modexa.Core.Install;

/// <summary>Outcome of an uninstall.</summary>
public sealed class UninstallResult
{
    /// <summary>Things that could not be undone (e.g. a file locked by the running game).</summary>
    public List<string> Errors { get; } = new();
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// Removes an installed mod exactly — for every tier, no engine needed. The install record says how
/// the mod was installed (files copied/replaced/deleted, edits inside game archives, folders created,
/// add-on registration), and the uninstall plays that back in reverse:
///
///  * archive edits: restored exactly when untouched since; otherwise this mod's own text/XML edits
///    are reversed one by one (shared files like dlclist.xml keep other mods' entries);
///  * disk files: restored / deleted when still the mod's version; when a mod installed later has
///    overwritten the same file, that mod inherits the saved original instead (so removing mods in
///    any order always ends at the true original);
///  * add-on registration removed, folders the mod created deleted when empty, backups cleaned up.
///
/// If anything fails (typically the game is running and holds a file), what was undone is marked,
/// the record is kept and the uninstall can simply be run again.
/// </summary>
public static class ModUninstaller
{
    /// <summary>Uninstalls every mod in a game folder, newest first. Returns what could not be undone.</summary>
    public static List<string> UninstallAll(string gameFolder)
    {
        var errors = new List<string>();
        foreach (var r in Enumerable.Reverse(InstalledModsStore.ForFolder(gameFolder)))
        {
            // Reload: earlier uninstalls may have handed originals over to this record.
            var current = InstalledModsStore.All().FirstOrDefault(x => x.Id == r.Id) ?? r;
            errors.AddRange(Uninstall(current).Errors.Select(e => $"{current.Name}: {e}"));
        }
        return errors;
    }

    public static UninstallResult Uninstall(ModInstallRecord record)
    {
        var result = new UninstallResult();
        var all = InstalledModsStore.All();
        // Use the stored version: other uninstalls may have handed originals over to it since the
        // caller loaded its copy.
        record = all.FirstOrDefault(x => x.Id == record.Id) ?? record;
        var later = all.Where(r => r.Id != record.Id
                                   && InstalledModsStore.SameFolder(r.GameFolder, record.GameFolder)
                                   && r.InstalledUtc >= record.InstalledUtc)
                       .OrderBy(r => r.InstalledUtc)
                       .ToList();

        // 1) Edits inside game archives (newest first).
        ArchiveEditRecorder.Revert(record, later, result.Errors);

        // 2) Files on disk (newest first).
        LooseFileInstaller.RevertFiles(record, later, result.Errors);

        // 3) Add-on registration — unless another installed mod still uses the same pack name.
        if (!string.IsNullOrWhiteSpace(record.DlcName)
            && !all.Any(r => r.Id != record.Id && InstalledModsStore.SameFolder(r.GameFolder, record.GameFolder)
                             && string.Equals(r.DlcName, record.DlcName, StringComparison.OrdinalIgnoreCase)))
        {
            try { DlcList.Unregister(record.GameFolder, record.DlcName!); }
            catch (Exception ex) { result.Errors.Add($"dlclist.xml: {ex.Message}"); }
        }

        // 4) Paid-content protection list.
        try { Drm.EntitlementService.UnregisterInstall(record.GameFolder, record, all); } catch { }

        // 5) Folders the mod created.
        InstallDirs.PruneCreated(record);

        // Later mods may have inherited originals / had their saved copies patched.
        if (later.Count > 0) InstalledModsStore.Update(later);

        if (result.Success)
        {
            InstalledModsStore.Remove(record.Id);
            try
            {
                string dir = ModBackups.DirFor(record);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { /* stale backups are harmless */ }
            Log.Info($"Uninstalled '{record.Name}' from {record.GameFolder}");
        }
        else
        {
            InstalledModsStore.Update(new[] { record }); // keeps the progress for a retry
            Log.Info($"Uninstall of '{record.Name}' incomplete: {string.Join("; ", result.Errors)}");
        }
        return result;
    }
}
