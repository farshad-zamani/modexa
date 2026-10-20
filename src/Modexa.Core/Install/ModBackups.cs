using System.IO;
using System.Text;

namespace Modexa.Core.Install;

/// <summary>Where a mod's saved originals live, and small helpers used by install / uninstall.</summary>
public static class ModBackups
{
    public static string DirFor(ModInstallRecord record) => Path.Combine(AppPaths.BackupsDir, "mods", record.Id);

    /// <summary>Moves a saved original into another mod's backup folder (that mod now owns it).</summary>
    public static string? HandOver(string? backup, ModInstallRecord heir)
    {
        if (string.IsNullOrEmpty(backup) || !File.Exists(backup)) return null;
        string dir = DirFor(heir);
        AppPaths.EnsureDir(dir);
        string dest = Path.Combine(dir, Path.GetFileName(backup));
        try { File.Move(backup, dest, overwrite: true); return dest; }
        catch
        {
            try { File.Copy(backup, dest, true); return dest; } catch { return backup; }
        }
    }

    /// <summary>Reverses <paramref name="journal"/> inside a saved text original.</summary>
    public static void PatchText(string? backup, EditJournal journal)
    {
        if (string.IsNullOrEmpty(backup) || !File.Exists(backup)) return;
        try
        {
            string? updated = journal.Undo(ReadText(backup));
            if (updated != null) File.WriteAllText(backup, updated, new UTF8Encoding(false));
        }
        catch { /* the backup stays as it was */ }
    }

    /// <summary>True when a saved text original has the same content as <paramref name="text"/> (whitespace aside).</summary>
    public static bool SameText(string? backup, string text)
    {
        if (string.IsNullOrEmpty(backup) || !File.Exists(backup)) return false;
        try { return EditJournal.Normalize(ReadText(backup)) == EditJournal.Normalize(text); }
        catch { return false; }
    }

    /// <summary>True when original bytes have the same text content as <paramref name="text"/> (whitespace aside).</summary>
    public static bool SameText(byte[] original, string text)
    {
        try { return EditJournal.Normalize(Decode(original)) == EditJournal.Normalize(text); }
        catch { return false; }
    }

    public static string ReadText(string path) => Decode(File.ReadAllBytes(path));

    private static string Decode(byte[] b)
    {
        int skip = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(b, skip, b.Length - skip);
    }
}

/// <summary>
/// Fingerprint of a file on disk — tells whether a file is still the one a mod wrote. Content hash
/// for normal files; for big ones (multi-GB archives) the size plus a hash of their first and last
/// megabytes — where every archive edit lands (the table of contents is at the start, new data is
/// appended at the end). The file's time stamp is NOT used: other programs (OpenIV browsing in edit
/// mode, sync / backup tools) can touch it without changing a byte, which used to make an unchanged
/// add-on look "changed by something else" and stay behind on uninstall.
/// </summary>
public static class FileStamp
{
    private const long HashLimit = 32L * 1024 * 1024;
    private const int SampleBytes = 1 << 20;

    public static string? Of(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fi.Length <= HashLimit)
                return $"{fi.Length}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs))}";

            using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buf = new byte[SampleBytes];
            fs.ReadExactly(buf);
            sha.AppendData(buf);
            fs.Position = fi.Length - SampleBytes;
            fs.ReadExactly(buf);
            sha.AppendData(buf);
            return $"{fi.Length}:q{Convert.ToHexString(sha.GetHashAndReset())}";
        }
        catch { return null; }
    }

    /// <summary>
    /// True when the file is still the one <paramref name="stamp"/> was taken of. A stamp from an older
    /// version (size + time, "len:tTICKS") is judged by the size only, since times can change alone.
    /// </summary>
    public static bool Matches(string? stamp, string path)
    {
        if (stamp == null) return false;
        string? current = Of(path);
        if (current == null) return false;
        if (current == stamp) return true;
        int colon = stamp.IndexOf(':');
        if (colon > 0 && stamp.Length > colon + 1 && stamp[colon + 1] == 't')
            return current.StartsWith(stamp[..(colon + 1)], StringComparison.Ordinal);
        return false;
    }
}

/// <summary>Creates folders for a mod's file and remembers which ones the mod created (removed on uninstall).</summary>
public static class InstallDirs
{
    public static void EnsureFor(ModInstallRecord record, string gameFolder, string fullFilePath)
    {
        string root = Path.GetFullPath(gameFolder).TrimEnd(Path.DirectorySeparatorChar);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(fullFilePath));
        var missing = new List<string>();
        while (!string.IsNullOrEmpty(dir) && dir.Length > root.Length && !Directory.Exists(dir))
        {
            missing.Add(dir);
            dir = Path.GetDirectoryName(dir);
        }
        foreach (var m in missing)
        {
            string rel = Path.GetRelativePath(root, m);
            if (!record.CreatedDirs.Contains(rel, StringComparer.OrdinalIgnoreCase)) record.CreatedDirs.Add(rel);
        }
        if (missing.Count > 0) Directory.CreateDirectory(missing[0]);
    }

    /// <summary>Deletes the folders a mod created, deepest first, but only when they are empty.</summary>
    public static void PruneCreated(ModInstallRecord record)
    {
        foreach (var rel in record.CreatedDirs.OrderByDescending(d => d.Length))
        {
            string full = Path.Combine(record.GameFolder, rel);
            try
            {
                if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
                    Directory.Delete(full);
            }
            catch { /* in use or not empty: leave it */ }
        }
    }
}
