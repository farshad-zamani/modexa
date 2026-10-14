using System.IO;
using Modexa.Core.Install;

namespace Modexa.Core.Rpf;

/// <summary>Why an editable mods-folder archive could not be made.</summary>
public sealed class ModsArchiveException : Exception
{
    public const string NoKeys = "no_keys";            // game exe missing / unknown version
    public const string NoSpace = "no_space";          // not enough free disk space for the copy
    public const string SourceMissing = "missing";     // the game has no such archive
    public const string Failed = "failed";             // I/O or format error

    public string Code { get; }
    public ModsArchiveException(string code, string message, Exception? inner = null) : base(message, inner) => Code = code;
}

/// <summary>A mods-folder archive Modexa made that no installed mod uses any more.</summary>
public sealed record UnusedArchive(string Archive, long Bytes);

/// <summary>
/// GTA V "mods folder" archives — what OpenIV's "Copy to mods folder" does, automated: the game's
/// archive (e.g. <c>update\update.rpf</c>) is copied to <c>mods\update\update.rpf</c> and its TOC
/// converted from NG/AES to OPEN so it can be edited. The original game file is never touched; with
/// OpenIV.asi (Legacy) / OpenRPF.asi (Enhanced) installed the game loads the mods copy instead.
///
/// Every copy Modexa makes is remembered (with a fingerprint after each Modexa write). When the
/// last mod that used a copy is uninstalled, the copy is deleted again — the game then reads its
/// own original — unless something else (e.g. OpenIV) changed it in the meantime.
/// </summary>
public static class ModsArchives
{
    public const string UpdateRpf = "update/update.rpf";

    private static readonly object Gate = new();

    /// <summary>"mods\update\update.rpf" / "\update\update.rpf" -> "update/update.rpf".</summary>
    public static string Normalize(string relativePath)
    {
        string p = relativePath.Replace('\\', '/').Trim().TrimStart('/');
        if (p.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)) p = p[5..];
        return p;
    }

    public static bool Same(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static string ModsPath(string gameFolder, string relativePath)
        => Path.Combine(gameFolder, "mods", Normalize(relativePath).Replace('/', Path.DirectorySeparatorChar));

    public static string GamePath(string gameFolder, string relativePath)
        => Path.Combine(gameFolder, Normalize(relativePath).Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Game-relative path of the mods copy, e.g. <c>mods\update\update.rpf</c>.</summary>
    public static string ModsRelative(string relativePath)
        => Path.Combine("mods", Normalize(relativePath).Replace('/', Path.DirectorySeparatorChar));

    /// <summary>True when the mods copy exists and is already OPEN (editable).</summary>
    public static bool IsReady(string gameFolder, string relativePath = UpdateRpf)
    {
        string p = ModsPath(gameFolder, relativePath);
        return File.Exists(p) && RpfArchive.PeekEncryption(p) is Rpf7.EncryptionOpen or Rpf7.EncryptionNone;
    }

    /// <summary>Bytes that <see cref="EnsureOpen"/> would copy for this archive (0 when it is ready or the game has none).</summary>
    public static long CopySize(string gameFolder, string relativePath)
    {
        if (IsReady(gameFolder, relativePath) || File.Exists(ModsPath(gameFolder, relativePath))) return 0;
        var fi = new FileInfo(GamePath(gameFolder, relativePath));
        return fi.Exists ? fi.Length : 0;
    }

    /// <summary>
    /// Makes sure <c>mods\&lt;relativePath&gt;</c> exists and is OPEN, creating it from the game's
    /// archive when needed (multi-GB copy for update.rpf: run on a worker thread). The creation is
    /// recorded in the game folder's backup log, so "Revert" removes it again.
    /// </summary>
    /// <param name="createEmptyIfMissing">The game has no such archive: create an empty OPEN one (OIV <c>createIfNotExist</c>).</param>
    /// <param name="copied">Bytes copied so far (for an overall progress across several archives).</param>
    /// <returns>Full path of the editable archive.</returns>
    public static string EnsureOpen(string gameFolder, string relativePath, IProgress<int>? progress = null,
        CancellationToken ct = default, bool createEmptyIfMissing = false, IProgress<long>? copied = null)
    {
        string rel = Normalize(relativePath);
        if (!rel.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
            throw new ModsArchiveException(ModsArchiveException.Failed, $"'{relativePath}' is not an RPF archive.");

        string dest = ModsPath(gameFolder, rel);
        string root = Path.GetFullPath(Path.Combine(gameFolder, "mods")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(dest).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ModsArchiveException(ModsArchiveException.Failed, $"'{relativePath}' points outside the game folder.");

        lock (Gate)
        {
            try
            {
                if (File.Exists(dest))
                {
                    var enc = RpfArchive.PeekEncryption(dest)
                              ?? throw new ModsArchiveException(ModsArchiveException.Failed, $"mods\\{rel} is not a valid RPF archive.");
                    if (enc is Rpf7.EncryptionOpen or Rpf7.EncryptionNone) return dest;

                    // A plain copy someone made earlier (still NG): convert it in place.
                    RpfArchive.ConvertToOpen(dest, Path.GetFileName(dest), LoadKeys(gameFolder));
                    return dest;
                }

                string src = GamePath(gameFolder, rel);
                var backup = new BackupSession(gameFolder);
                string modsRel = ModsRelative(rel);

                if (!File.Exists(src))
                {
                    if (!createEmptyIfMissing)
                        throw new ModsArchiveException(ModsArchiveException.SourceMissing, $"The game has no {rel.Replace('/', '\\')}.");
                    backup.TrackBeforeWrite(modsRel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    RpfBuilder.CreateOpen(dest, new Dictionary<string, byte[]>());
                    backup.Restamp(modsRel);
                    return dest;
                }

                // Keys are only needed for an encrypted original (fail fast, before a multi-GB copy).
                bool encrypted = RpfArchive.PeekEncryption(src) is not (Rpf7.EncryptionOpen or Rpf7.EncryptionNone);
                var keys = encrypted ? LoadKeys(gameFolder) : null;
                long size = new FileInfo(src).Length;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                EnsureSpace(dest, size);

                backup.TrackBeforeWrite(modsRel);
                string tmp = dest + ".modexa-tmp";
                try
                {
                    CopyWithProgress(src, tmp, size, progress, copied, ct);
                    if (keys != null) RpfArchive.ConvertToOpen(tmp, Path.GetFileName(src), keys, size);
                    File.Move(tmp, dest);
                }
                catch
                {
                    TryDelete(tmp);
                    backup.Forget(modsRel);
                    throw;
                }
                backup.Restamp(modsRel);
                progress?.Report(100);
                return dest;
            }
            catch (Exception ex) when (ex is not (ModsArchiveException or OperationCanceledException))
            {
                throw new ModsArchiveException(ModsArchiveException.Failed, ex.Message, ex);
            }
        }
    }

    /// <summary>Call after Modexa wrote to a mods archive: remembers its fingerprint.</summary>
    public static void NoteWrite(string gameFolder, string relativePath)
    {
        try { new BackupSession(gameFolder).Restamp(ModsRelative(relativePath)); } catch { }
    }

    // ---- ownership / clean-up --------------------------------------------------------------------

    /// <summary>Mods-folder archives Modexa made in this game folder (normalized, e.g. "update/update.rpf").</summary>
    public static List<string> CreatedCopies(string gameFolder)
        => new BackupSession(gameFolder).CreatedFiles()
            .Where(r => r.StartsWith("mods\\", StringComparison.OrdinalIgnoreCase) && r.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>True when one of <paramref name="records"/> (or older prepare data) still needs this mods archive.</summary>
    public static bool InUse(string gameFolder, string archive, IEnumerable<ModInstallRecord> records, BackupSession? backup = null)
    {
        string a = Normalize(archive);
        foreach (var r in records)
        {
            if (!InstalledModsStore.SameFolder(r.GameFolder, gameFolder)) continue;
            if (r.ArchiveEdits.Any(e => !e.Reverted && Same(e.Archive, a))) return true;
            if (r.AllDlcs.Any() && Same(a, UpdateRpf)) return true;
            if (r.Files.Any(f => !f.Reverted && f.Dest.StartsWith("mods", StringComparison.OrdinalIgnoreCase) && Same(f.Dest, a))) return true;
        }
        backup ??= new BackupSession(gameFolder);
        return backup.HasEntriesInside(ModsRelative(a));
    }

    /// <summary>
    /// Can Modexa's copy of <paramref name="archive"/> be deleted now? It must be a copy Modexa made,
    /// unused by <paramref name="records"/>, and unchanged since Modexa last wrote it.
    /// </summary>
    public static bool CanRelease(string gameFolder, string archive, IEnumerable<ModInstallRecord> records)
    {
        string rel = ModsRelative(archive);
        var backup = new BackupSession(gameFolder);
        if (!backup.Created(rel)) return false;
        string full = Path.Combine(gameFolder, rel);
        if (!File.Exists(full) || InUse(gameFolder, archive, records, backup)) return false;
        string? stamp = backup.StampOf(rel);
        return stamp != null && stamp == FileStamp.Of(full);
    }

    /// <summary>Deletes Modexa's copy of an archive (the game falls back to its own original).</summary>
    public static void Release(string gameFolder, string archive)
    {
        string rel = ModsRelative(archive);
        string full = Path.Combine(gameFolder, rel);
        lock (Gate)
        {
            if (File.Exists(full)) File.Delete(full);
            new BackupSession(gameFolder).Forget(rel);
            PruneEmptyDirs(Path.Combine(gameFolder, "mods"), Path.GetDirectoryName(full));
        }
    }

    /// <summary>Copies Modexa made that no installed mod uses (shown to the user to free the space).</summary>
    public static List<UnusedArchive> Unused(string gameFolder)
    {
        var records = InstalledModsStore.ForFolder(gameFolder);
        var backup = new BackupSession(gameFolder);
        var list = new List<UnusedArchive>();
        foreach (var a in CreatedCopies(gameFolder))
        {
            var fi = new FileInfo(ModsPath(gameFolder, a));
            if (fi.Exists && !InUse(gameFolder, a, records, backup)) list.Add(new UnusedArchive(a, fi.Length));
        }
        return list;
    }

    /// <summary>
    /// Rewrites an archive without its dead space when that is worth it (each replace appends and
    /// leaves the old copy behind). Returns the bytes freed (0 when skipped).
    /// </summary>
    public static long CompactIfWasteful(string gameFolder, string archive, IProgress<long>? copied = null)
    {
        string full = ModsPath(gameFolder, archive);
        if (!File.Exists(full)) return 0;
        lock (Gate)
        {
            using var ed = RpfEditor.Open(full);
            long dead = ed.DeadBytes(), size = new FileInfo(full).Length;
            if (dead < 128L * 1024 * 1024 || dead < size / 5) return 0;
            try { EnsureSpace(full, size - dead); }
            catch (ModsArchiveException) { return 0; }
            ed.Compact(copied);
            NoteWrite(gameFolder, archive);
            return dead;
        }
    }

    private static void PruneEmptyDirs(string stopAt, string? dir)
    {
        string stop = Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar);
        while (!string.IsNullOrEmpty(dir))
        {
            string full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Length <= stop.Length || !full.StartsWith(stop, StringComparison.OrdinalIgnoreCase)) break;
            try
            {
                if (!Directory.Exists(full) || Directory.EnumerateFileSystemEntries(full).Any()) break;
                Directory.Delete(full);
            }
            catch { break; }
            dir = Path.GetDirectoryName(full);
        }
    }

    private static GtaKeys LoadKeys(string gameFolder)
    {
        try { return GtaKeys.Load(gameFolder); }
        catch (GtaKeysException ex) { throw new ModsArchiveException(ModsArchiveException.NoKeys, ex.Message, ex); }
    }

    private static void EnsureSpace(string dest, long needed)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dest))!);
            long margin = 256L * 1024 * 1024;
            if (drive.IsReady && drive.AvailableFreeSpace < needed + margin)
                throw new ModsArchiveException(ModsArchiveException.NoSpace,
                    $"Not enough free space on {drive.Name}: {needed / (1024 * 1024)} MB needed, {drive.AvailableFreeSpace / (1024 * 1024)} MB free.");
        }
        catch (ArgumentException) { /* network path etc.: let the copy report it */ }
    }

    private static void CopyWithProgress(string src, string dest, long size, IProgress<int>? progress, IProgress<long>? copied, CancellationToken ct)
    {
        const int Buffer = 4 << 20;
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer, FileOptions.SequentialScan);
        using var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, Buffer, FileOptions.SequentialScan);
        output.SetLength(size);
        output.Position = 0;
        var buf = new byte[Buffer];
        long done = 0;
        int last = -1;
        int n;
        while ((n = input.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            output.Write(buf, 0, n);
            done += n;
            copied?.Report(done);
            int pct = size > 0 ? (int)(done * 100 / size) : 100;
            if (pct != last && pct < 100) { last = pct; progress?.Report(pct); }
        }
        output.Flush(true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
