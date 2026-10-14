using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Modexa.Core.Diagnostics;
using Modexa.Core.Engine;
using Modexa.Core.Format;
using Modexa.Core.Rpf;

namespace Modexa.Core.Install;

/// <summary>What an OIV package says about itself (assembly.xml / super.xml metadata).</summary>
public sealed class OivPackageInfo
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    public bool IsSuper { get; set; }
    /// <summary>The package's own id (&lt;package id="{…}"&gt;), when it has one.</summary>
    public string? Id { get; set; }
    /// <summary>SHA-256 of the package definition (assembly.xml / super.xml): equal = the same package.</summary>
    public string Hash { get; set; } = "";
    /// <summary>The package's icon.png, when it has one.</summary>
    public byte[]? Icon { get; set; }
    /// <summary>Top-level game archives the package edits (e.g. "update/update.rpf").</summary>
    public List<string> Archives { get; set; } = new();
    /// <summary>Game-relative files the package writes or deletes outside archives.</summary>
    public List<string> DiskTargets { get; set; } = new();
    /// <summary>Total bytes of content the package installs (drives the progress bar).</summary>
    public long TotalBytes { get; set; }
}

/// <summary>Outcome of applying a package: operations applied vs skipped (unsupported).</summary>
public sealed class OivResult
{
    public List<string> Applied { get; } = new();
    public List<string> Skipped { get; } = new();
}

/// <summary>
/// Install progress: bytes done of total (archive copies included), what is being worked on, and
/// whether that is copying a game archive into mods\ (rather than installing files).
/// </summary>
public readonly record struct OivProgress(long Done, long Total, string? Current, bool Copying = false);

public sealed class OivApplyOptions
{
    /// <summary>
    /// Archives (normalized, e.g. "update/update.rpf") whose mods copy was just made from the game
    /// for this install: their files are still the game's own, so no backup copies are needed.
    /// </summary>
    public IEnumerable<string>? FreshArchives { get; init; }
    public IProgress<OivProgress>? Progress { get; init; }
}

/// <summary>
/// Installs OpenIV packages — available in every tier: <c>.oiv</c> (package format 2.x,
/// <c>assembly.xml</c>) and <c>.oivs</c> "super" packages (<c>super.xml</c>, installed with their
/// default selection). Packages are read straight from the zip, never extracted as a whole.
///
/// Operations, matching OpenIV:
///   * top level: <c>add</c> (anything for update\, x64\ or *.rpf goes to the mods folder — the
///     original game files are never written), <c>delete</c>, <c>xml</c>/<c>text</c> on loose files;
///   * <c>archive</c> (any nesting depth, <c>createIfNotExist</c>): <c>add</c> (binary, resource and
///     nested .rpf files), <c>delete</c>, <c>xml</c> (add First/Last/Before/After, replace, remove)
///     and <c>text</c> (add, insert, replace, delete). Game archives are copied to mods\ and
///     converted to OPEN first.
/// Not supported (reported, never half-applied): <c>pso</c> edits.
///
/// Everything done is recorded on the <see cref="ModInstallRecord"/> — saved originals, file
/// fingerprints, every individual text/XML edit, created folders — which is what lets
/// <see cref="ModUninstaller"/> remove the package exactly. A failure part-way rolls back.
/// </summary>
public static class OivInstaller
{
    /// <summary>Weight of an operation that moves no content (xml/text edits, deletes) in the progress total.</summary>
    private const long OpWeight = 64 * 1024;

    public static bool IsPackageFile(string path) => OivPackage.IsPackageFile(path);

    /// <summary>Reads the package's metadata and which game archives it edits (no changes made).</summary>
    public static OivPackageInfo Inspect(string path)
    {
        using var pkg = OivPackage.Open(path);
        return Inspect(pkg);
    }

    public static OivPackageInfo Inspect(OivPackage pkg)
    {
        var ctx = new Ctx(pkg, Path.GetTempPath(), new ModInstallRecord(), new OivResult(), null);
        var ops = ctx.LoadOperations();
        var info = ctx.Info;
        info.Icon = pkg.TryReadAllBytes("icon.png");
        foreach (var op in ops)
        {
            switch (op.Name.ToLowerInvariant())
            {
                case "archive":
                    string a = ModsArchives.Normalize(op.GetAttribute("path"));
                    if (a.Length > 0 && !info.Archives.Contains(a, StringComparer.OrdinalIgnoreCase)) info.Archives.Add(a);
                    break;
                case "add" or "delete":
                    string dest = op.InnerText.Trim();
                    if (dest.Length > 0) info.DiskTargets.Add(DiskRelative(dest));
                    break;
                case "xml" or "text":
                    string p = op.GetAttribute("path");
                    if (p.Length > 0) info.DiskTargets.Add(DiskRelative(p));
                    break;
            }
        }
        info.TotalBytes = ops.Sum(o => Weight(ctx, o));
        return info;
    }

    /// <summary>
    /// True when <paramref name="record"/> is this same mod file installed earlier (a reinstall).
    /// Decided by the file name only: the parts of a split mod always come as differently named files
    /// (Package.1.oiv, Package.2.oiv…) and are separate mods, whatever their packages say inside.
    /// </summary>
    public static bool IsSamePackage(ModInstallRecord record, string packagePath)
        => record.ModType == MxaModType.Oiv && record.ProductId == null && record.Setup == null
           && string.Equals(record.Source, Path.GetFileName(packagePath.TrimEnd('\\', '/')), StringComparison.OrdinalIgnoreCase);

    /// <summary>Applies a package (an .oiv/.oivs file or an extracted folder) — see the other overload.</summary>
    public static OivResult Apply(string path, string gameFolder, ModInstallRecord record, OivApplyOptions? options = null)
    {
        using var pkg = OivPackage.Open(path);
        return Apply(pkg, gameFolder, record, options);
    }

    /// <summary>
    /// Applies a package to <paramref name="gameFolder"/>, recording everything on
    /// <paramref name="record"/> (also fills its name/version/author/id from the package).
    /// </summary>
    public static OivResult Apply(OivPackage pkg, string gameFolder, ModInstallRecord record, OivApplyOptions? options = null)
    {
        var result = new OivResult();
        var ctx = new Ctx(pkg, gameFolder, record, result, options?.Progress);
        record.GameFolder = gameFolder;
        record.ModType = MxaModType.Oiv;
        foreach (var a in options?.FreshArchives ?? Array.Empty<string>())
            ctx.MarkFresh(ModsArchives.Normalize(a));
        try
        {
            var ops = ctx.LoadOperations();
            if (!string.IsNullOrWhiteSpace(ctx.Info.Name)) record.Name = ctx.Info.Name;
            record.Version ??= ctx.Info.Version;
            record.Author ??= ctx.Info.Author;
            record.PackageId = ctx.Info.Id;
            record.PackageHash = ctx.Info.Hash;
            ctx.Total = ops.Sum(o => Weight(ctx, o)) + CopyBytes(ops, gameFolder);
            ctx.Report(force: true);
            foreach (var op in ops)
                RunTopLevel(ctx, op);
            SaveIcon(pkg, record);
        }
        catch (Exception ex)
        {
            Log.Error("OIV install failed, rolling back", ex);
            Rollback(record);
            if (ex is ModInstallException) throw;
            if (ex is ModsArchiveException mae) throw new ModInstallException(ModInstallException.NotPrepared, mae.Message, mae);
            throw new ModInstallException("failed", ex.Message, ex);
        }

        foreach (var s in result.Skipped)
        {
            Log.Info("OIV skipped: " + s);
            record.Warnings.Add(s);
        }
        ctx.Done = ctx.Total;
        ctx.Report(force: true);
        return result;
    }

    /// <summary>Bytes of game archives that will be copied into mods\ for these operations.</summary>
    private static long CopyBytes(IEnumerable<XmlElement> ops, string gameFolder)
        => ops.Where(o => o.Name.Equals("archive", StringComparison.OrdinalIgnoreCase))
            .Select(o => ModsArchives.Normalize(o.GetAttribute("path")))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Sum(p => ModsArchives.CopySize(gameFolder, p));

    /// <summary>
    /// Disk space the install needs: copies of the game archives it edits plus the content it
    /// writes (both land in the game folder).
    /// </summary>
    public static long SpaceNeeded(OivPackageInfo info, string gameFolder)
        => info.TotalBytes + info.Archives.Sum(a => ModsArchives.CopySize(gameFolder, a));

    /// <summary>Undoes a half-applied install; archive copies made for it (and unused by others) are deleted.</summary>
    private static void Rollback(ModInstallRecord record)
    {
        var errors = new List<string>();
        var others = InstalledModsStore.ForFolder(record.GameFolder).Where(r => r.Id != record.Id).ToList();
        var released = record.CreatedArchives.Where(a => !ModsArchives.InUse(record.GameFolder, a, others))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        try { ArchiveEditRecorder.Revert(record, Array.Empty<ModInstallRecord>(), errors, released); } catch { }
        try { LooseFileInstaller.Uninstall(record); } catch { }
        foreach (var a in released)
            try { ModsArchives.Release(record.GameFolder, a); } catch { }
        try { string dir = ModBackups.DirFor(record); if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        record.Files.Clear();
        record.ArchiveEdits.Clear();
        record.CreatedArchives.Clear();
    }

    /// <summary>Keeps the package icon next to the mod's backups (shown in the installed lists).</summary>
    private static void SaveIcon(OivPackage pkg, ModInstallRecord record)
    {
        try
        {
            var icon = pkg.TryReadAllBytes("icon.png");
            if (icon == null || icon.Length == 0 || icon.Length > 2 * 1024 * 1024) return;
            string dir = ModBackups.DirFor(record);
            AppPaths.EnsureDir(dir);
            string file = Path.Combine(dir, "icon.png");
            File.WriteAllBytes(file, icon);
            record.IconFile = file;
        }
        catch { /* cosmetic */ }
    }

    // ---- package loading ---------------------------------------------------------------------

    private sealed class Ctx
    {
        public readonly OivPackage Package;
        public readonly string GameFolder;
        public readonly ModInstallRecord Record;
        public readonly OivResult Result;
        public readonly ArchiveEditRecorder Recorder;
        public readonly OivPackageInfo Info = new();
        private readonly HashSet<string> _fresh = new(StringComparer.OrdinalIgnoreCase);
        private readonly IProgress<OivProgress>? _progress;
        private long _lastTick;

        public long Total, Done;
        public string? Current;
        public bool Copying;

        public Ctx(OivPackage package, string gameFolder, ModInstallRecord record, OivResult result, IProgress<OivProgress>? progress)
        {
            Package = package;
            GameFolder = Path.GetFullPath(gameFolder);
            Record = record;
            Result = result;
            Recorder = new ArchiveEditRecorder(record);
            _progress = progress;
        }

        public bool IsFresh(string archive) => _fresh.Contains(archive);

        public void MarkFresh(string archive)
        {
            _fresh.Add(archive);
            if (!Record.CreatedArchives.Contains(archive, StringComparer.OrdinalIgnoreCase)) Record.CreatedArchives.Add(archive);
        }

        /// <summary>Reports progress, at most ~8 times a second.</summary>
        public void Report(bool force = false)
        {
            if (_progress == null) return;
            long now = Environment.TickCount64;
            if (!force && now - _lastTick < 120) return;
            _lastTick = now;
            _progress.Report(new OivProgress(Math.Min(Done, Total), Total, Current, Copying));
        }

        /// <summary>The operation elements of the package, in order.</summary>
        public List<XmlElement> LoadOperations()
        {
            var ops = new List<XmlElement>();

            if (Package.Exists("assembly.xml"))
            {
                byte[] raw = Package.ReadAllBytes("assembly.xml");
                Info.Hash = Convert.ToHexString(SHA256.HashData(raw));
                var doc = Load(raw, "assembly.xml");
                var pkg = doc.DocumentElement;
                string target = pkg?.GetAttribute("target") ?? "";
                if (target.Length > 0 && !target.Equals("Five", StringComparison.OrdinalIgnoreCase))
                    throw new ModInstallException(ModInstallException.Unsupported, $"This OIV package is for another game ({target}).");
                Info.Id = NullIfEmpty(pkg?.GetAttribute("id"));
                ReadMetadata(pkg?.SelectSingleNode("metadata"));
                var content = pkg?.SelectSingleNode("content")
                              ?? throw new ModInstallException(ModInstallException.BadPackage, "No <content> in assembly.xml.");
                ops.AddRange(content.ChildNodes.OfType<XmlElement>());
                return ops;
            }

            if (Package.Exists("super.xml"))
            {
                byte[] raw = Package.ReadAllBytes("super.xml");
                Info.Hash = Convert.ToHexString(SHA256.HashData(raw));
                var doc = Load(raw, "super.xml");
                var root = doc.DocumentElement;
                if (root == null || root.Name != "superpackage")
                    throw new ModInstallException(ModInstallException.BadPackage, "Invalid super.xml.");
                Info.IsSuper = true;
                Info.Id = NullIfEmpty(root.GetAttribute("id"));
                ReadMetadata(root.SelectSingleNode("metadata"));

                // Default selection: required modules, default-on modules, each group's default option.
                var items = new List<XmlNode>();
                foreach (XmlElement m in root.SelectNodes("modules/module")!.OfType<XmlElement>())
                    if (IsTrue(m.GetAttribute("required")) || IsTrue(m.GetAttribute("default"))) items.Add(m);
                foreach (XmlElement g in root.SelectNodes("groups/group")!.OfType<XmlElement>())
                {
                    string def = g.GetAttribute("default");
                    if (string.IsNullOrEmpty(def) || def.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;
                    var opt = g.SelectNodes("option")!.OfType<XmlElement>()
                        .FirstOrDefault(o => o.GetAttribute("id").Equals(def, StringComparison.OrdinalIgnoreCase));
                    if (opt != null) items.Add(opt);
                }

                foreach (var item in items)
                {
                    var install = item.SelectSingleNode("install");
                    if (install == null) continue;
                    foreach (XmlElement i in install.ChildNodes.OfType<XmlElement>())
                    {
                        if (i.Name == "content") ops.AddRange(i.ChildNodes.OfType<XmlElement>());
                        else if (i.Name == "folder") ops.AddRange(FolderAsAdds(doc, i.GetAttribute("source")));
                    }
                }
                return ops;
            }

            throw new ModInstallException(ModInstallException.BadPackage, "Not an OIV package (no assembly.xml or super.xml).");
        }

        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static XmlDocument Load(byte[] raw, string name)
        {
            var doc = new XmlDocument();
            try
            {
                using var ms = new MemoryStream(raw);
                doc.Load(ms);
            }
            catch (XmlException ex) { throw new ModInstallException(ModInstallException.BadPackage, $"{name} is not valid XML: {ex.Message}", ex); }
            return doc;
        }

        private void ReadMetadata(XmlNode? meta)
        {
            if (meta == null) return;
            Info.Name = meta.SelectSingleNode("name")?.InnerText.Trim() ?? "";
            var v = meta.SelectSingleNode("version");
            if (v != null)
            {
                string major = v.SelectSingleNode("major")?.InnerText.Trim() ?? "";
                string minor = v.SelectSingleNode("minor")?.InnerText.Trim() ?? "";
                string tag = v.SelectSingleNode("tag")?.InnerText.Trim() ?? "";
                string num = major.Length > 0 ? (minor.Length > 0 ? $"{major}.{minor}" : major) : "";
                Info.Version = (num + (tag.Length > 0 && !tag.Equals("Version", StringComparison.OrdinalIgnoreCase) ? " " + tag : "")).Trim();
                if (Info.Version.Length == 0) Info.Version = null;
            }
            var author = meta.SelectSingleNode("author/displayName")?.InnerText.Trim();
            Info.Author = string.IsNullOrEmpty(author) ? null : author;
            var desc = meta.SelectSingleNode("description")?.InnerText.Trim();
            Info.Description = string.IsNullOrEmpty(desc) ? null : desc;
        }

        /// <summary>An .oivs &lt;folder source&gt;: copy the folder's tree to the game root.</summary>
        private IEnumerable<XmlElement> FolderAsAdds(XmlDocument doc, string source)
        {
            if (string.IsNullOrWhiteSpace(source)) yield break;
            string rel = source.Replace('\\', '/').Trim('/');
            foreach (var inside in Package.FilesUnder("content/" + rel))
            {
                var add = doc.CreateElement("add");
                add.SetAttribute("source", rel + "/" + inside);
                add.InnerText = inside.Replace('/', '\\');
                yield return add;
            }
        }

        /// <summary>Package path of a content file (the package refuses paths that escape it).</summary>
        public static string ContentPath(string source) => "content/" + source.Replace('\\', '/').Trim().TrimStart('/');

        /// <summary>
        /// Game-relative destination for a file on disk. Anything under update\ or x64\ or any .rpf
        /// is redirected to the mods folder, exactly like OpenIV, so original game files stay vanilla.
        /// </summary>
        public string DiskTarget(string dest)
        {
            string result = DiskRelative(dest);
            string full = Path.GetFullPath(Path.Combine(GameFolder, result));
            if (!full.StartsWith(GameFolder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new ModInstallException(ModInstallException.BadPackage, $"Destination escapes the game folder: {dest}");
            return result;
        }
    }

    private static string DiskRelative(string dest)
    {
        string rel = dest.Replace('/', '\\').Trim().TrimStart('\\');
        bool mods = false;
        if (rel.StartsWith("mods\\", StringComparison.OrdinalIgnoreCase)) { rel = rel[5..]; mods = true; }
        mods |= rel.StartsWith("update", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("x64", StringComparison.OrdinalIgnoreCase)
                || rel.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase);
        return mods ? Path.Combine("mods", rel) : rel;
    }

    private static bool IsTrue(string s) => bool.TryParse(s, out bool b) && b;

    /// <summary>Progress weight of an operation: the bytes it installs (nested archives included).</summary>
    private static long Weight(Ctx ctx, XmlElement op)
    {
        switch (op.Name.ToLowerInvariant())
        {
            case "add":
                string source = op.GetAttribute("source");
                long len = source.Length == 0 ? 0 : ctx.Package.Length(Ctx.ContentPath(source));
                return Math.Max(len, 0) + 1;
            case "archive":
                return op.ChildNodes.OfType<XmlElement>().Sum(c => Weight(ctx, c));
            case "defragmentation":
                return 0;
            default:
                return OpWeight;
        }
    }

    // ---- top level ---------------------------------------------------------------------------

    private static void RunTopLevel(Ctx ctx, XmlElement op)
    {
        switch (op.Name.ToLowerInvariant())
        {
            case "add":
                DiskAdd(ctx, op);
                break;
            case "delete":
                DiskDelete(ctx, op);
                ctx.Done += OpWeight;
                break;
            case "archive":
                RunArchive(ctx, op, top: op.GetAttribute("path"), nested: new List<string>(),
                    create: IsTrue(op.GetAttribute("createIfNotExist")));
                break;
            case "xml":
            case "text":
                DiskEdit(ctx, op);
                ctx.Done += OpWeight;
                break;
            case "defragmentation":
                break; // our edits never fragment beyond what the game reads fine
            default:
                ctx.Result.Skipped.Add($"Unsupported operation <{op.Name}>");
                ctx.Done += OpWeight;
                break;
        }
        ctx.Report();
    }

    private static void DiskAdd(Ctx ctx, XmlElement op)
    {
        string source = op.GetAttribute("source");
        string dest = op.InnerText.Trim();
        if (source.Length == 0 || dest.Length == 0) { ctx.Result.Skipped.Add("<add> without source or destination"); return; }

        string src = Ctx.ContentPath(source);
        long len = ctx.Package.Length(src);
        if (len < 0) throw new ModInstallException(ModInstallException.BadPackage, $"The package is missing {source}.");

        string rel = ctx.DiskTarget(dest);
        ctx.Current = rel;
        ctx.Report(force: true);
        LooseFileInstaller.CopyTracked(ctx.Record, () => ctx.Package.OpenRead(src), rel);
        ctx.Done += len + 1;
        ctx.Result.Applied.Add($"add {rel}");
    }

    private static void DiskDelete(Ctx ctx, XmlElement op)
    {
        string target = op.InnerText.Trim();
        if (target.Length == 0) return;
        string rel = ctx.DiskTarget(target);
        string full = Path.Combine(ctx.GameFolder, rel);
        if (!File.Exists(full)) return;
        var rec = LooseFileInstaller.Track(ctx.Record, rel); // keeps the original so uninstall puts it back
        File.Delete(full);
        if (!rec.WasCreated) rec.WasDeleted = true;
        rec.WrittenStamp = null;
        ctx.Result.Applied.Add($"delete {rel}");
    }

    private static void DiskEdit(Ctx ctx, XmlElement op)
    {
        string path = op.GetAttribute("path");
        if (path.Length == 0) { ctx.Result.Skipped.Add($"<{op.Name}> without path"); return; }
        string rel = ctx.DiskTarget(path);
        string full = Path.Combine(ctx.GameFolder, rel);
        bool xml = op.Name.Equals("xml", StringComparison.OrdinalIgnoreCase);
        bool exists = File.Exists(full);
        if (!exists && (xml || !IsTrue(op.GetAttribute("createIfNotExist"))))
        {
            ctx.Result.Skipped.Add($"{op.Name} target not found: {path}");
            return;
        }

        string original = exists ? ModBackups.ReadText(full) : "";
        var journal = new EditJournal();
        string updated = xml ? ApplyXmlOps(op, original, ctx.Result, journal) : ApplyTextOps(op, original, journal);
        if (updated == original && exists) return;

        var rec = LooseFileInstaller.Track(ctx.Record, rel);
        if (!rec.WasCreated) rec.Journal = Merge(rec.Journal, journal);
        InstallDirs.EnsureFor(ctx.Record, ctx.GameFolder, full);
        File.WriteAllText(full, updated, new UTF8Encoding(false));
        rec.WrittenStamp = FileStamp.Of(full);
        ctx.Result.Applied.Add($"{op.Name} edit {rel}");
    }

    private static EditJournal Merge(EditJournal? a, EditJournal b)
    {
        if (a == null) return b;
        if (b.XmlAdded != null) (a.XmlAdded ??= new()).AddRange(b.XmlAdded);
        if (b.XmlRemoved != null) (a.XmlRemoved ??= new()).AddRange(b.XmlRemoved);
        if (b.XmlReplaced != null) (a.XmlReplaced ??= new()).AddRange(b.XmlReplaced);
        if (b.LinesAdded != null) (a.LinesAdded ??= new()).AddRange(b.LinesAdded);
        if (b.LinesReplaced != null) (a.LinesReplaced ??= new()).AddRange(b.LinesReplaced);
        if (b.LinesDeleted != null) (a.LinesDeleted ??= new()).AddRange(b.LinesDeleted);
        return a;
    }

    // ---- archives ----------------------------------------------------------------------------

    private static void RunArchive(Ctx ctx, XmlElement node, string top, List<string> nested, bool create)
    {
        string type = node.GetAttribute("type");
        if (type.Length > 0 && !type.Equals("RPF7", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Result.Skipped.Add($"Archive type {type} is not supported ({node.GetAttribute("path")})");
            ctx.Done += Weight(ctx, node);
            return;
        }
        if (string.IsNullOrWhiteSpace(top)) { ctx.Result.Skipped.Add("<archive> without path"); return; }

        var children = node.ChildNodes.OfType<XmlElement>().ToList();
        var fileOps = children.Where(c => !c.Name.Equals("archive", StringComparison.OrdinalIgnoreCase)).ToList();
        string topNorm = ModsArchives.Normalize(top);

        // One editing session for this archive's own operations…
        if (fileOps.Count > 0 || create)
        {
            // The game's archive is copied to mods\ first. A copy made right now still holds the
            // game's own files: the game archive itself is their backup.
            long copySize = ModsArchives.CopySize(ctx.GameFolder, topNorm);
            if (copySize > 0) ctx.MarkFresh(topNorm);

            ctx.Current = nested.Count == 0 ? topNorm : topNorm + " > " + string.Join(" > ", nested);
            ctx.Copying = copySize > 0;
            ctx.Report(force: true);
            long start = ctx.Done, streamBase = 0;
            var copied = new InlineProgress<long>(n => { ctx.Done = start + n; ctx.Report(); });
            var written = new InlineProgress<long>(n => { ctx.Done = streamBase + n; ctx.Report(); });
            ArchiveChain.Edit(ctx.GameFolder, topNorm, nested, ed =>
            {
                ctx.Copying = false;
                ctx.Done = start + copySize;
                ctx.Report(force: true);
                foreach (var op in fileOps) RunArchiveOp(ctx, ed, topNorm, nested, op);
                streamBase = ctx.Done; // large files are counted while the commit copies them
            }, createIfMissing: create, observer: ctx.Recorder.Observe(topNorm), written: written, copied: copied);
            ctx.Done = start + copySize + fileOps.Sum(o => Weight(ctx, o));
            ctx.Report(force: true);
        }

        // …then each nested archive in its own session.
        foreach (var inner in children.Where(c => c.Name.Equals("archive", StringComparison.OrdinalIgnoreCase)))
        {
            string innerPath = inner.GetAttribute("path").Replace('\\', '/').Trim('/');
            if (innerPath.Length == 0) { ctx.Result.Skipped.Add("nested <archive> without path"); continue; }
            RunArchive(ctx, inner, top, new List<string>(nested) { innerPath }, IsTrue(inner.GetAttribute("createIfNotExist")));
        }
    }

    /// <summary>Applies one operation inside an archive (large files are streamed in at commit).</summary>
    private static void RunArchiveOp(Ctx ctx, RpfEditor ed, string top, List<string> nested, XmlElement op)
    {
        string where = nested.Count == 0 ? top : top + " > " + string.Join(" > ", nested);
        bool vanilla = nested.Count == 0 && ctx.IsFresh(top);
        switch (op.Name.ToLowerInvariant())
        {
            case "add":
            {
                string source = op.GetAttribute("source");
                string entry = op.InnerText.Trim().Replace('\\', '/').Trim('/');
                if (source.Length == 0 || entry.Length == 0) { ctx.Result.Skipped.Add($"<add> without source or destination in {where}"); return; }
                string src = Ctx.ContentPath(source);
                long size = ctx.Package.Length(src);
                if (size < 0) throw new ModInstallException(ModInstallException.BadPackage, $"The package is missing {source}.");
                if (size > uint.MaxValue)
                {
                    ctx.Result.Skipped.Add($"{source} is too large to store inside an archive");
                    ctx.Done += size + 1;
                    return;
                }
                ctx.Recorder.Touch(top, nested, ed, entry, vanilla);
                ed.SetFileFrom(entry, size, () => ctx.Package.OpenRead(src));
                ctx.Result.Applied.Add($"add {where}/{entry}");
                if (ed.IsStreamed(entry)) return;
                ctx.Done += size + 1;
                ctx.Report();
                return;
            }
            case "delete":
            {
                string entry = op.InnerText.Trim().Replace('\\', '/').Trim('/');
                ctx.Done += OpWeight;
                if (entry.Length == 0 || !ed.FileExists(entry)) return;
                ctx.Recorder.Touch(top, nested, ed, entry, vanilla);
                ed.DeleteFile(entry);
                ctx.Result.Applied.Add($"delete {where}/{entry}");
                return;
            }
            case "xml":
            case "text":
            {
                ctx.Done += OpWeight;
                string entry = op.GetAttribute("path").Replace('\\', '/').Trim('/');
                bool xml = op.Name.Equals("xml", StringComparison.OrdinalIgnoreCase);
                if (entry.Length == 0) { ctx.Result.Skipped.Add($"<{op.Name}> without path in {where}"); return; }
                bool exists = ed.FileExists(entry);
                if (!exists && (xml || !IsTrue(op.GetAttribute("createIfNotExist"))))
                {
                    ctx.Result.Skipped.Add($"{op.Name} target not in archive: {where}/{entry}");
                    return;
                }
                if (exists && !ed.IsBinary(entry)) { ctx.Result.Skipped.Add($"{entry} is a binary resource, not text"); return; }

                string original = exists ? ed.ReadText(entry) : "";
                var journal = new EditJournal();
                string updated = xml ? ApplyXmlOps(op, original, ctx.Result, journal) : ApplyTextOps(op, original, journal);
                if (updated != original || !exists)
                {
                    var edit = ctx.Recorder.Touch(top, nested, ed, entry, vanilla);
                    ed.SetText(entry, updated);
                    if (!edit.WasCreated) edit.Journal = Merge(edit.Journal, journal);
                    ctx.Result.Applied.Add($"{op.Name} edit {where}/{entry}");
                }
                return;
            }
            case "defragmentation":
                return;
            default:
                ctx.Result.Skipped.Add($"Unsupported operation <{op.Name}> in {where}");
                ctx.Done += OpWeight;
                return;
        }
    }

    // ---- text / xml edits --------------------------------------------------------------------

    /// <summary>Applies OIV &lt;text&gt; line commands (add / insert / replace / delete), journaling each.</summary>
    private static string ApplyTextOps(XmlElement textNode, string content, EditJournal journal)
    {
        bool crlf = content.Contains("\r\n");
        var lines = content.Length == 0 ? new List<string>() : content.Replace("\r\n", "\n").Split('\n').ToList();
        // Appending after a trailing newline should not leave an empty line in between.
        bool trailingNewline = lines.Count > 0 && lines[^1].Length == 0;
        if (trailingNewline) lines.RemoveAt(lines.Count - 1);
        bool changed = false;

        static bool Matches(string line, string condition, string pattern) => condition.ToLowerInvariant() switch
        {
            "startwith" or "startswith" => line.TrimStart().StartsWith(pattern.Trim(), StringComparison.Ordinal),
            "mask" => Regex.IsMatch(line, "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$"),
            "contains" => line.Contains(pattern, StringComparison.Ordinal),
            _ => line.Trim() == pattern.Trim() // Equal
        };

        foreach (XmlElement op in textNode.ChildNodes.OfType<XmlElement>())
        {
            string line = op.GetAttribute("line");
            string text = op.InnerText;

            switch (op.Name.ToLowerInvariant())
            {
                case "add":
                    if (lines.Contains(text)) break; // idempotent re-install
                    lines.Add(text);
                    (journal.LinesAdded ??= new()).Add(text);
                    changed = true;
                    break;
                case "insert":
                {
                    // OpenIV defaults: where="Before", condition="Mask".
                    string condition = op.GetAttribute("condition") is { Length: > 0 } c ? c : "Mask";
                    bool before = !op.GetAttribute("where").Equals("After", StringComparison.OrdinalIgnoreCase);
                    for (int i = 0; i < lines.Count; i++)
                    {
                        if (!Matches(lines[i], condition, line)) continue;
                        int at = before ? i : i + 1;
                        // Already there from an earlier install of the same package: don't add twice.
                        if (before ? at > 0 && lines[at - 1] == text : at < lines.Count && lines[at] == text) break;
                        lines.Insert(at, text);
                        (journal.LinesAdded ??= new()).Add(text);
                        changed = true;
                        break;
                    }
                    break;
                }
                case "replace":
                {
                    string condition = op.GetAttribute("condition") is { Length: > 0 } c ? c : "Equal";
                    for (int i = 0; i < lines.Count; i++)
                        if (Matches(lines[i], condition, line) && lines[i] != text)
                        {
                            (journal.LinesReplaced ??= new()).Add(new LineChange { Old = lines[i], New = text, Index = i });
                            lines[i] = text;
                            changed = true;
                        }
                    break;
                }
                case "delete":
                {
                    string condition = op.GetAttribute("condition") is { Length: > 0 } c ? c : "Equal";
                    for (int i = lines.Count - 1; i >= 0; i--)
                        if (Matches(lines[i], condition, text))
                        {
                            (journal.LinesDeleted ??= new()).Insert(0, new LineChange { Old = lines[i], Index = i });
                            lines.RemoveAt(i);
                            changed = true;
                        }
                    break;
                }
            }
        }

        if (!changed) return content;
        return string.Join(crlf ? "\r\n" : "\n", lines) + (trailingNewline || content.Length == 0 ? (crlf ? "\r\n" : "\n") : "");
    }

    private static string ApplyXmlOps(XmlElement xmlNode, string xml, OivResult result, EditJournal journal)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xml);
        bool changed = false;

        foreach (XmlElement op in xmlNode.ChildNodes.OfType<XmlElement>())
        {
            string xpath = op.GetAttribute("xpath");
            if (xpath.Length == 0) { result.Skipped.Add("xml operation without xpath"); continue; }

            XmlNode? target;
            try { target = doc.SelectSingleNode(xpath); }
            catch (System.Xml.XPath.XPathException) { result.Skipped.Add($"invalid xpath: {xpath}"); continue; }

            switch (op.Name.ToLowerInvariant())
            {
                case "add":
                {
                    if (target == null) { result.Skipped.Add($"xpath not found: {xpath}"); break; }
                    string append = op.GetAttribute("append");
                    bool sibling = append.Equals("Before", StringComparison.OrdinalIgnoreCase) || append.Equals("After", StringComparison.OrdinalIgnoreCase);
                    var parent = sibling ? target.ParentNode : target;
                    if (parent == null) { result.Skipped.Add($"xpath has no parent: {xpath}"); break; }
                    string parentPath = sibling ? XPathOf(parent) : xpath;
                    bool first = append.Equals("First", StringComparison.OrdinalIgnoreCase);

                    var toAdd = op.ChildNodes.OfType<XmlElement>().ToList();
                    if (first || append.Equals("After", StringComparison.OrdinalIgnoreCase)) toAdd.Reverse();
                    string indent = IndentOf(parent);
                    foreach (var element in toAdd)
                    {
                        var imported = doc.ImportNode(element, true);
                        string norm = EditJournal.Normalize(imported.OuterXml);
                        if (parent.ChildNodes.OfType<XmlElement>().Any(c => EditJournal.Normalize(c.OuterXml) == norm))
                            continue; // already present (idempotent; and never "own" another mod's identical entry)

                        if (sibling)
                        {
                            if (append.Equals("Before", StringComparison.OrdinalIgnoreCase)) parent.InsertBefore(imported, target);
                            else parent.InsertAfter(imported, target);
                        }
                        else if (first && parent.FirstChild != null) parent.InsertBefore(imported, parent.FirstChild);
                        else
                        {
                            var lastEl = parent.ChildNodes.OfType<XmlElement>().LastOrDefault();
                            if (lastEl != null) parent.InsertAfter(imported, lastEl);
                            else parent.AppendChild(imported);
                        }
                        if (indent.Length > 0) parent.InsertBefore(doc.CreateWhitespace(indent), imported);
                        (journal.XmlAdded ??= new()).Add(new XmlAddition { ParentXPath = parentPath, Xml = imported.OuterXml });
                        changed = true;
                    }
                    break;
                }
                case "remove":
                    if (target?.ParentNode is { } rp)
                    {
                        int index = rp.ChildNodes.OfType<XmlElement>().ToList().IndexOf((target as XmlElement)!);
                        (journal.XmlRemoved ??= new()).Add(new XmlAddition { ParentXPath = XPathOf(rp), Xml = target.OuterXml, Index = index });
                        rp.RemoveChild(target);
                        changed = true;
                    }
                    break;
                case "replace":
                {
                    if (target?.ParentNode == null) { result.Skipped.Add($"xpath not found: {xpath}"); break; }
                    var replacement = op.ChildNodes.OfType<XmlElement>().FirstOrDefault();
                    if (replacement == null) { result.Skipped.Add($"xml replace without a node: {xpath}"); break; }
                    var imported = doc.ImportNode(replacement, true);
                    var parent = target.ParentNode;
                    (journal.XmlReplaced ??= new()).Add(new XmlChange { ParentXPath = XPathOf(parent), OldXml = target.OuterXml, NewXml = imported.OuterXml });
                    parent.ReplaceChild(imported, target);
                    changed = true;
                    break;
                }
                default:
                    result.Skipped.Add($"xml operation <{op.Name}> is not supported");
                    break;
            }
        }

        return changed ? XmlFiles.Save(doc) : xml;
    }

    /// <summary>A positional XPath to a node (e.g. /CVehicleModelInfo/InitDatas[1]) so it can be found again.</summary>
    private static string XPathOf(XmlNode node)
    {
        var parts = new List<string>();
        for (var n = node; n != null && n.NodeType == XmlNodeType.Element; n = n.ParentNode)
        {
            int index = 1;
            for (var s = n.PreviousSibling; s != null; s = s.PreviousSibling)
                if (s.NodeType == XmlNodeType.Element && s.Name == n.Name) index++;
            parts.Insert(0, $"{n.Name}[{index}]");
        }
        return "/" + string.Join("/", parts);
    }

    /// <summary>The whitespace used before the last child element (to keep the file's layout).</summary>
    private static string IndentOf(XmlNode parent)
    {
        var last = parent.ChildNodes.OfType<XmlElement>().LastOrDefault();
        return last?.PreviousSibling is { NodeType: XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace } ws ? ws.Value ?? "" : "";
    }
}
