using System.IO;
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

        string root = Path.GetFullPath(gameFolder);
        string modBackupDir = Path.Combine(AppPaths.BackupsDir, "mods", record.Id);

        foreach (var entry in manifest.Files)
        {
            string src = Path.GetFullPath(Path.Combine(extractedDir, entry.Source));
            string dest = Path.GetFullPath(Path.Combine(gameFolder, entry.Dest));

            // Zip-slip guard.
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(src)) continue;

            var rec = new InstalledFile { Dest = entry.Dest };
            if (File.Exists(dest))
            {
                AppPaths.EnsureDir(modBackupDir);
                string backup = Path.Combine(modBackupDir, Guid.NewGuid().ToString("N") + ".bak");
                try { File.Copy(dest, backup, true); rec.OriginalBackup = backup; } catch { }
            }
            else
            {
                rec.WasCreated = true;
            }

            AppPaths.EnsureDir(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, true);
            record.Files.Add(rec);
        }

        return record;
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

    /// <summary>Reverts a single installed mod: restore originals, delete created files.</summary>
    public static void Uninstall(ModInstallRecord record)
    {
        foreach (var f in record.Files)
        {
            string full = Path.Combine(record.GameFolder, f.Dest);
            try
            {
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
            catch { /* keep going */ }
        }
    }
}
