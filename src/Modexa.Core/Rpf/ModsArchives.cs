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

/// <summary>
/// GTA V "mods folder" archives — what OpenIV's "Copy to mods folder" does, automated: the game's
/// archive (e.g. <c>update\update.rpf</c>) is copied to <c>mods\update\update.rpf</c> and its TOC
/// converted from NG/AES to OPEN so it can be edited. The original game file is never touched; with
/// OpenIV.asi (Legacy) / OpenRPF.asi (Enhanced) installed the game loads the mods copy instead.
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

    public static string ModsPath(string gameFolder, string relativePath)
        => Path.Combine(gameFolder, "mods", Normalize(relativePath).Replace('/', Path.DirectorySeparatorChar));

    public static string GamePath(string gameFolder, string relativePath)
        => Path.Combine(gameFolder, Normalize(relativePath).Replace('/', Path.DirectorySeparatorChar));

    /// <summary>True when the mods copy exists and is already OPEN (editable).</summary>
    public static bool IsReady(string gameFolder, string relativePath = UpdateRpf)
    {
        string p = ModsPath(gameFolder, relativePath);
        return File.Exists(p) && RpfArchive.PeekEncryption(p) is Rpf7.EncryptionOpen or Rpf7.EncryptionNone;
    }

    /// <summary>
    /// Makes sure <c>mods\&lt;relativePath&gt;</c> exists and is OPEN, creating it from the game's
    /// archive when needed (multi-GB copy for update.rpf: run on a worker thread). The creation is
    /// recorded in the game folder's backup log, so "Revert" removes it again.
    /// </summary>
    /// <param name="createEmptyIfMissing">The game has no such archive: create an empty OPEN one (OIV <c>createIfNotExist</c>).</param>
    /// <returns>Full path of the editable archive.</returns>
    public static string EnsureOpen(string gameFolder, string relativePath, IProgress<int>? progress = null,
        CancellationToken ct = default, bool createEmptyIfMissing = false)
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
                string modsRel = Path.Combine("mods", rel.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(src))
                {
                    if (!createEmptyIfMissing)
                        throw new ModsArchiveException(ModsArchiveException.SourceMissing, $"The game has no {rel.Replace('/', '\\')}.");
                    backup.TrackBeforeWrite(modsRel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    RpfBuilder.CreateOpen(dest, new Dictionary<string, byte[]>());
                    return dest;
                }

                var keys = LoadKeys(gameFolder); // fail fast, before a multi-GB copy
                long size = new FileInfo(src).Length;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                EnsureSpace(dest, size);

                backup.TrackBeforeWrite(modsRel);
                string tmp = dest + ".modexa-tmp";
                try
                {
                    CopyWithProgress(src, tmp, size, progress, ct);
                    RpfArchive.ConvertToOpen(tmp, Path.GetFileName(src), keys, size);
                    File.Move(tmp, dest);
                }
                catch
                {
                    TryDelete(tmp);
                    throw;
                }
                progress?.Report(100);
                return dest;
            }
            catch (Exception ex) when (ex is not (ModsArchiveException or OperationCanceledException))
            {
                throw new ModsArchiveException(ModsArchiveException.Failed, ex.Message, ex);
            }
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

    private static void CopyWithProgress(string src, string dest, long size, IProgress<int>? progress, CancellationToken ct)
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
