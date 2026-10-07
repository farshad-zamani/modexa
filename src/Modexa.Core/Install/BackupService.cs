using System.IO;
using System.Text.Json;

namespace Modexa.Core.Install;

/// <summary>Record of one file Modexa created or overwrote, so it can be reverted.</summary>
public sealed class BackupEntry
{
    public string RelativePath { get; set; } = "";
    /// <summary>Path of the saved original under the backups dir, or null if the file was newly created.</summary>
    public string? OriginalBackup { get; set; }
    public bool WasCreated { get; set; }


    /// <summary>For a file replaced INSIDE an archive: its path there (original content in <see cref="OriginalBackup"/>).</summary>
    public string? ArchiveEntryPath { get; set; }

    public bool IsArchiveEntry => ArchiveEntryPath != null;
}

/// <summary>
/// Tracks file changes against a game folder and can revert them. Before overwriting a file it
/// copies the original into the backups dir; files Modexa newly creates are recorded so revert can
/// delete them. The log is a JSON manifest per game folder.
/// </summary>
public sealed class BackupSession
{
    private readonly string _gameFolder;
    private readonly string _logPath;
    private readonly List<BackupEntry> _entries = new();

    public BackupSession(string gameFolder)
    {
        _gameFolder = gameFolder;
        AppPaths.EnsureDir(AppPaths.BackupsDir);
        string key = SafeKey(gameFolder);
        _logPath = Path.Combine(AppPaths.BackupsDir, key + ".json");
        _entries = LoadLog(_logPath);
    }

    /// <summary>Call before writing to <paramref name="relativePath"/> inside the game folder.</summary>
    public void TrackBeforeWrite(string relativePath)
    {
        string full = Path.Combine(_gameFolder, relativePath);
        if (_entries.Any(e => !e.IsArchiveEntry
                              && string.Equals(e.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)))
            return; // already have an original/record for this path

        if (File.Exists(full))
        {
            string backupName = $"{SafeKey(relativePath)}.{Guid.NewGuid():N}.bak";
            string backupPath = Path.Combine(AppPaths.BackupsDir, backupName);
            try { File.Copy(full, backupPath, true); }
            catch { backupPath = ""; }
            _entries.Add(new BackupEntry
            {
                RelativePath = relativePath,
                OriginalBackup = string.IsNullOrEmpty(backupPath) ? null : backupPath,
                WasCreated = false
            });
        }
        else
        {
            _entries.Add(new BackupEntry { RelativePath = relativePath, WasCreated = true });
        }
        Save();
    }

    /// <summary>
    /// Call before replacing a file inside an archive (e.g. gameconfig.xml in update.rpf), passing
    /// its current content. Only the first change per entry is recorded, so a revert always returns
    /// to the true original. Content (not TOC positions) is kept: later edits may reorder the TOC.
    /// </summary>
    public void TrackArchiveFile(string archiveRelativePath, string entryPath, byte[] originalContent)
    {
        string entry = entryPath.Replace('\\', '/').Trim('/');
        if (_entries.Any(e => string.Equals(e.ArchiveEntryPath, entry, StringComparison.OrdinalIgnoreCase)
                              && string.Equals(e.RelativePath, archiveRelativePath, StringComparison.OrdinalIgnoreCase)))
            return;
        string backupPath = Path.Combine(AppPaths.BackupsDir, $"{SafeKey(archiveRelativePath + "_" + entry)}.{Guid.NewGuid():N}.bak");
        File.WriteAllBytes(backupPath, originalContent);
        _entries.Add(new BackupEntry
        {
            RelativePath = archiveRelativePath,
            ArchiveEntryPath = entry,
            OriginalBackup = backupPath
        });
        Save();
    }

    /// <summary>Restores every tracked file to its original state (deletes created files).</summary>
    public void RevertAll()
    {
        // Archive entries first: their archive may itself be a file we created and delete below.
        var deletedLater = new HashSet<string>(_entries.Where(x => !x.IsArchiveEntry && x.WasCreated).Select(x => x.RelativePath),
            StringComparer.OrdinalIgnoreCase);
        foreach (var e in _entries.Where(x => x.IsArchiveEntry))
        {
            if (deletedLater.Contains(e.RelativePath)) continue; // the whole archive goes anyway
            string archive = Path.Combine(_gameFolder, e.RelativePath);
            try
            {
                if (!File.Exists(archive)) continue;
                if (e.OriginalBackup == null || !File.Exists(e.OriginalBackup)) continue;
                var ed = Rpf.RpfEditor.Open(archive);
                ed.SetFile(e.ArchiveEntryPath!, File.ReadAllBytes(e.OriginalBackup));
                ed.Commit();
            }
            catch (Exception ex) { Diagnostics.Log.Error("Revert archive entry", ex); /* continue with the rest */ }
        }

        var touchedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _entries.Where(x => !x.IsArchiveEntry))
        {
            string full = Path.Combine(_gameFolder, e.RelativePath);
            try
            {
                if (e.WasCreated)
                {
                    if (File.Exists(full)) File.Delete(full);
                    var dir = Path.GetDirectoryName(full);
                    if (dir != null) touchedDirs.Add(dir);
                }
                else if (!string.IsNullOrEmpty(e.OriginalBackup) && File.Exists(e.OriginalBackup))
                {
                    AppPaths.EnsureDir(Path.GetDirectoryName(full)!);
                    File.Copy(e.OriginalBackup, full, true);
                }
            }
            catch { /* continue reverting the rest */ }
        }

        PruneEmptyDirs(touchedDirs);
        _entries.Clear();
        Save();
    }

    /// <summary>Removes folders Modexa created that are now empty (deepest first, never the game root).</summary>
    private void PruneEmptyDirs(IEnumerable<string> dirs)
    {
        string root = Path.GetFullPath(_gameFolder).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var start in dirs.OrderByDescending(d => d.Length))
        {
            var dir = start;
            while (!string.IsNullOrEmpty(dir))
            {
                string full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
                if (full.Length <= root.Length || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) break;
                try
                {
                    if (!Directory.Exists(full) || Directory.EnumerateFileSystemEntries(full).Any()) break;
                    Directory.Delete(full);
                }
                catch { break; }
                dir = Path.GetDirectoryName(full);
            }
        }
    }

    public bool HasBackups => _entries.Count > 0;

    private void Save()
    {
        try { File.WriteAllText(_logPath, JsonSerializer.Serialize(_entries)); } catch { }
    }

    private static List<BackupEntry> LoadLog(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<List<BackupEntry>>(File.ReadAllText(path)) ?? new();
        }
        catch { }
        return new();
    }

    private static string SafeKey(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s.Replace(Path.DirectorySeparatorChar, '_').Replace(':', '_');
    }
}
