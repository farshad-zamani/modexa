using System.IO;
using System.Text;
using Modexa.Core.Format;

namespace Modexa.Core.Install;

/// <summary>
/// Installs "loose"/copy-paste mods: copies each file from an extracted payload to its mapped
/// destination inside the game folder, backing up any originals so the mod can be uninstalled
/// cleanly. Also used by the addon installer for the non-rpf files of a pack.
/// </summary>
public static class LooseFileInstaller
{
    public static ModInstallRecord Install(MxaManifest manifest, string extractedDir, string gameFolder, string game)
    {
        if (!Directory.Exists(gameFolder)) throw new DirectoryNotFoundException(gameFolder);

        var record = new ModInstallRecord
        {
            Name = manifest.Name,
            ModType = manifest.ModType,
            Game = game,
            GameFolder = gameFolder,
            DlcName = manifest.DlcName
        };

        string root = Path.GetFullPath(gameFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        try
        {
            foreach (var entry in manifest.Files)
            {
                string src = Path.GetFullPath(Path.Combine(extractedDir, entry.Source));
                string dest = Path.GetFullPath(Path.Combine(gameFolder, entry.Dest));

                // Zip-slip guard.
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(src)) continue;

                CopyTracked(record, src, entry.Dest);
            }
        }
        catch
        {
            // Never leave a half-installed mod behind (it would not be in the installed list).
            Uninstall(record);
            throw;
        }

        return record;
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destRel"/> (game-relative) and records it:
    /// the original is saved first (once per mod) and the written file is fingerprinted.
    /// </summary>
    public static void CopyTracked(ModInstallRecord record, string source, string destRel)
    {
        string dest = Path.Combine(record.GameFolder, destRel);
        var rec = Track(record, destRel);
        InstallDirs.EnsureFor(record, record.GameFolder, dest);
        File.Copy(source, dest, true);
        rec.WrittenStamp = FileStamp.Of(dest);
    }

    /// <summary>Records a disk file before the mod changes it (original saved once per mod).</summary>
    public static InstalledFile Track(ModInstallRecord record, string destRel)
    {
        var existing = record.Files.FirstOrDefault(f => string.Equals(f.Dest, destRel, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        string dest = Path.Combine(record.GameFolder, destRel);
        var rec = new InstalledFile { Dest = destRel };
        if (File.Exists(dest))
        {
            // Without a saved original the file could never be restored: refuse rather than lose it.
            string dir = ModBackups.DirFor(record);
            AppPaths.EnsureDir(dir);
            string backup = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".bak");
            File.Copy(dest, backup, true);
            rec.OriginalBackup = backup;
        }
        else
        {
            rec.WasCreated = true;
        }
        // Recorded before the write: if the write fails half-way, the rollback still cleans it up.
        record.Files.Add(rec);
        return rec;
    }

    /// <summary>
    /// Installs every file under <paramref name="sourceFolder"/> into the game folder at the same
    /// relative path (used by Pro "install any mod" for a plain folder/zip of loose files).
    /// </summary>
    public static ModInstallRecord InstallFolder(string sourceFolder, string gameFolder, string game, string name)
    {
        var manifest = new MxaManifest { ModType = MxaModType.Loose, Name = name };
        foreach (var f in Directory.GetFiles(sourceFolder, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceFolder, f).Replace('\\', '/');
            manifest.Files.Add(new MxaFileEntry { Source = rel, Dest = rel });
        }
        return Install(manifest, sourceFolder, gameFolder, game);
    }

    /// <summary>
    /// Reverts a mod's disk files (no other mods considered — see <see cref="ModUninstaller"/> for the
    /// full uninstall that also hands originals over to mods installed later).
    /// </summary>
    public static void Uninstall(ModInstallRecord record)
    {
        var errors = new List<string>();
        RevertFiles(record, Array.Empty<ModInstallRecord>(), errors);
        InstallDirs.PruneCreated(record);
    }

    internal static void RevertFiles(ModInstallRecord record, IReadOnlyList<ModInstallRecord> later, List<string> errors)
    {
        for (int i = record.Files.Count - 1; i >= 0; i--)
        {
            var f = record.Files[i];
            if (f.Reverted) continue;
            string full = Path.Combine(record.GameFolder, f.Dest);
            try
            {
                RevertFile(record, f, full, later);
                f.Reverted = true;
            }
            catch (Exception ex)
            {
                errors.Add($"{f.Dest}: {ex.Message}");
            }
        }
    }

    private static void RevertFile(ModInstallRecord record, InstalledFile f, string full, IReadOnlyList<ModInstallRecord> later)
    {
        string? current = FileStamp.Of(full);
        // Unchanged since the mod wrote (or deleted) it? (No stamp = a created archive the mod owns.)
        bool untouched = f.WasDeleted ? current == null : f.WrittenStamp == null || current == f.WrittenStamp;

        if (!untouched)
        {
            // A mod installed later wrote the same file: it takes over our original.
            var heir = later.SelectMany(r => r.Files.Select(x => (r, x)))
                .FirstOrDefault(p => !p.x.Reverted && string.Equals(p.x.Dest, f.Dest, StringComparison.OrdinalIgnoreCase));
            if (f.Journal is { IsEmpty: false } j)
            {
                if (File.Exists(full))
                {
                    string? updated = j.Undo(ModBackups.ReadText(full));
                    if (updated != null && ModBackups.SameText(f.OriginalBackup, updated)) File.Copy(f.OriginalBackup!, full, true);
                    else if (updated != null) File.WriteAllText(full, updated, new UTF8Encoding(false));
                }
                if (heir.x != null) ModBackups.PatchText(heir.x.OriginalBackup, j);
                return;
            }
            if (heir.x != null)
            {
                heir.x.WasCreated = f.WasCreated;
                heir.x.OriginalBackup = ModBackups.HandOver(f.OriginalBackup, heir.r);
                f.OriginalBackup = null;
                return;
            }
            // Changed by something else (the user, a game update): don't destroy that change.
            return;
        }

        if (f.WasCreated)
        {
            if (File.Exists(full)) File.Delete(full);
        }
        else if (!string.IsNullOrEmpty(f.OriginalBackup) && File.Exists(f.OriginalBackup))
        {
            AppPaths.EnsureDir(Path.GetDirectoryName(full)!);
            File.Copy(f.OriginalBackup, full, true);
        }
    }
}
