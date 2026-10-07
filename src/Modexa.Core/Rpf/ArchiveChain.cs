using System.IO;

namespace Modexa.Core.Rpf;

/// <summary>
/// Edits a file inside a (possibly nested) game archive, e.g.
/// <c>mods\update\x64\dlcpacks\patchday3ng\dlc.rpf</c> &gt; <c>x64\vehicles.rpf</c> &gt; <c>adder.yft</c>.
/// The top-level archive is made editable in the mods folder first (<see cref="ModsArchives"/>).
/// A nested archive is copied out to a temp file, converted to OPEN if needed, edited, and written
/// back into its parent as a whole — so every level stays a valid, self-contained RPF7.
/// </summary>
public static class ArchiveChain
{
    /// <param name="topArchive">Game-relative top archive ("update/update.rpf", "mods\update\update.rpf", …).</param>
    /// <param name="nested">Archives inside it, outermost first (each relative to its parent).</param>
    /// <param name="createIfMissing">Create missing archives empty (OIV <c>createIfNotExist</c>).</param>
    public static T Edit<T>(string gameFolder, string topArchive, IReadOnlyList<string> nested, Func<RpfEditor, T> edit,
        bool createIfMissing = false, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        string top = ModsArchives.EnsureOpen(gameFolder, topArchive, progress, ct, createIfMissing);
        var (result, _) = EditLevel(top, nested, 0, edit, createIfMissing);
        return result;
    }

    public static void Edit(string gameFolder, string topArchive, IReadOnlyList<string> nested, Action<RpfEditor> edit,
        bool createIfMissing = false, IProgress<int>? progress = null, CancellationToken ct = default)
        => Edit<bool>(gameFolder, topArchive, nested, ed => { edit(ed); return true; }, createIfMissing, progress, ct);

    /// <summary>Returns the edit's result and whether the archive file changed.</summary>
    private static (T result, bool changed) EditLevel<T>(string archiveFile, IReadOnlyList<string> nested, int level,
        Func<RpfEditor, T> edit, bool createIfMissing)
    {
        var ed = RpfEditor.Open(archiveFile);
        if (level == nested.Count)
        {
            var r = edit(ed);
            bool changed = ed.HasChanges;
            ed.Commit();
            return (r, changed);
        }

        string inner = nested[level].Replace('\\', '/').Trim('/');
        string tmpDir = Path.Combine(AppPaths.CacheDir, "rpf");
        AppPaths.EnsureDir(tmpDir);
        string tmp = Path.Combine(tmpDir, Guid.NewGuid().ToString("N") + ".rpf");
        try
        {
            bool converted = false;
            if (ed.FileExists(inner))
            {
                var (name, size) = ed.ExtractNestedArchive(inner, tmp);
                if (RpfArchive.PeekEncryption(tmp) is not (Rpf7.EncryptionOpen or Rpf7.EncryptionNone))
                    converted = RpfArchive.ConvertToOpen(tmp, name, GtaKeys.LoadForPath(archiveFile), size);
            }
            else if (createIfMissing)
            {
                RpfBuilder.CreateOpen(tmp, new Dictionary<string, byte[]>());
                converted = true;
            }
            else
            {
                throw new FileNotFoundException($"'{inner}' was not found inside {Path.GetFileName(archiveFile)}.");
            }

            var (result, innerChanged) = EditLevel(tmp, nested, level + 1, edit, createIfMissing);
            if (!innerChanged && !converted) return (result, false);

            ed.SetNestedArchive(inner, tmp);
            ed.Commit();
            return (result, true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
