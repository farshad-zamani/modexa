using System.IO;
using Modexa.Core.Diagnostics;
using Modexa.Core.Rpf;

namespace Modexa.Core.Install;

/// <summary>Outcome of an uninstall.</summary>
public sealed class UninstallResult
{
    /// <summary>Things that could not be undone (e.g. a file locked by the running game).</summary>
    public List<string> Errors { get; } = new();
    /// <summary>Disk space given back (archive copies deleted, archives compacted).</summary>
    public long FreedBytes { get; set; }
    /// <summary>Files the mod wrote that something else changed since: left in place (game-relative).</summary>
    public List<string> Kept { get; } = new();
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// Removes an installed mod exactly — for every tier, no engine needed. The install record says how
/// the mod was installed (files copied/replaced/deleted, edits inside game archives, archive copies
/// made, folders created, add-on registration), and the uninstall plays that back in reverse:
///
///  * archive copies in mods\ that only this mod used (and nobody changed since) are deleted as a
///    whole — the game reads its own original again, and the space is given back;
///  * in archives other mods still use, only this mod's files are put back: restored exactly when
///    untouched since; shared text/XML files (dlclist.xml…) get only this mod's own edits reversed;
///    a file a later mod replaced is handed to that mod with the true original;
///  * disk files: restored / deleted when still the mod's version; when a mod installed later has
///    overwritten the same file, that mod inherits the saved original instead (so removing mods in
///    any order always ends at the true original);
///  * add-on registration removed, folders the mod created deleted when empty, backups cleaned up.
///
/// No other mod's files are ever touched. If anything fails (typically the game is running and
/// holds a file), what was undone is marked, the record is kept and the uninstall can simply be
/// run again.
/// </summary>
public static class ModUninstaller
{
    /// <summary>Uninstalls every mod in a game folder, newest first. Returns what could not be undone.</summary>
    public static List<string> UninstallAll(string gameFolder, IProgress<string>? step = null)
    {
        var errors = new List<string>();
        foreach (var r in Enumerable.Reverse(InstalledModsStore.ForFolder(gameFolder)))
        {
            // Reload: earlier uninstalls may have handed originals over to this record.
            var current = InstalledModsStore.Get(r.Id) ?? r;
            step?.Report(current.Name);
            errors.AddRange(Uninstall(current, step).Errors.Select(e => $"{current.Name}: {e}"));
        }
        return errors;
    }

    public static UninstallResult Uninstall(ModInstallRecord record, IProgress<string>? step = null)
    {
        var result = new UninstallResult();
        var all = InstalledModsStore.All();
        // Use the stored version: other uninstalls may have handed originals over to it since the
        // caller loaded its copy.
        record = all.FirstOrDefault(x => x.Id == record.Id) ?? record;
        string folder = record.GameFolder;
        var others = all.Where(r => r.Id != record.Id && InstalledModsStore.SameFolder(r.GameFolder, folder)).ToList();
        var later = others.Where(r => r.InstalledUtc >= record.InstalledUtc).OrderBy(r => r.InstalledUtc).ToList();

        // Archive copies only this mod used go away as a whole (nothing inside needs undoing).
        var released = record.Archives.Where(a => ModsArchives.CanRelease(folder, a, others))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // An archive the mod itself put on disk (e.g. its own dlcpacks\…\dlc.rpf) and then edited is
        // deleted as a whole below: undoing the edits inside it first would only make it look
        // "changed by something else" and leave it behind.
        foreach (var f in record.Files.Where(f => f.WasCreated && !f.Reverted && f.Dest.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)))
        {
            if (!FileStamp.Matches(f.WrittenStamp, Path.Combine(folder, f.Dest))) continue;
            foreach (var e in record.ArchiveEdits.Where(e => !e.Reverted && SameDiskFile(e.Archive, f.Dest)))
                e.Reverted = true;
        }

        // 1) Edits inside game archives (newest first).
        ArchiveEditRecorder.Revert(record, later, result.Errors, released, step);

        // 2) Files on disk (newest first).
        LooseFileInstaller.RevertFiles(record, later, result.Errors, result.Kept);

        // 3) Add-on registration — unless another installed mod still uses the same pack name, or
        //    update.rpf itself is about to be deleted.
        if (!released.Contains(ModsArchives.UpdateRpf))
        {
            var dlcs = record.AllDlcs.Where(d => !others.Any(r => r.AllDlcs.Contains(d, StringComparer.OrdinalIgnoreCase))).ToList();
            try { DlcList.UnregisterMany(folder, dlcs); }
            catch (Exception ex) { result.Errors.Add($"dlclist.xml: {ex.Message}"); }
        }

        // 4) Paid-content protection list.
        try { Drm.EntitlementService.UnregisterInstall(folder, record, all); } catch { }

        // 5) The archive copies, then folders the mod created.
        foreach (var a in released)
        {
            try
            {
                long size = new FileInfo(ModsArchives.ModsPath(folder, a)).Length;
                step?.Report(a);
                ModsArchives.Release(folder, a);
                result.FreedBytes += size;
                foreach (var e in record.ArchiveEdits.Where(e => ModsArchives.Same(e.Archive, a))) e.Reverted = true;
            }
            catch (Exception ex) { result.Errors.Add($"{a}: {ex.Message}"); }
        }
        InstallDirs.PruneCreated(record);

        // 6) Archives other mods still use keep their size after files are put back (every write
        //    appends): rewrite them without the dead space when that is worth it.
        if (result.Success)
            foreach (var a in record.Archives.Where(a => !released.Contains(a)))
            {
                try
                {
                    step?.Report(a);
                    result.FreedBytes += ModsArchives.CompactIfWasteful(folder, a);
                }
                catch (Exception ex) { Log.Error($"Compact {a}", ex); /* only space, never correctness */ }
            }

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
            Log.Info($"Uninstalled '{record.Name}' from {folder} ({released.Count} archive copies removed, {result.FreedBytes / (1024 * 1024)} MB freed)");
        }
        else
        {
            InstalledModsStore.Update(new[] { record }); // keeps the progress for a retry
            Log.Info($"Uninstall of '{record.Name}' incomplete: {string.Join("; ", result.Errors)}");
        }
        if (result.Kept.Count > 0) Log.Info($"Uninstall of '{record.Name}' left files changed since: {string.Join("; ", result.Kept)}");
        return result;
    }

    /// <summary>Does an archive edit ("update/x64/dlcpacks/x/dlc.rpf") point at this disk file ("mods\update\…\dlc.rpf")?</summary>
    internal static bool SameDiskFile(string archive, string dest)
        => string.Equals(ModsArchives.ModsRelative(archive).TrimStart('\\'), dest.Replace('/', '\\').TrimStart('\\'), StringComparison.OrdinalIgnoreCase);
}
