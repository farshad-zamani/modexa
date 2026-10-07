using System.IO;
using System.IO.Compression;
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
    /// <summary>Top-level game archives the package edits (e.g. "update/update.rpf").</summary>
    public List<string> Archives { get; set; } = new();
}

/// <summary>Outcome of applying a package: operations applied vs skipped (unsupported).</summary>
public sealed class OivResult
{
    public List<string> Applied { get; } = new();
    public List<string> Skipped { get; } = new();
}

/// <summary>
/// Installs OpenIV packages — available in every tier: <c>.oiv</c> (package format 2.x,
/// <c>assembly.xml</c>) and <c>.oivs</c> "super" packages (<c>super.xml</c>, installed with their
/// default selection).
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
    private const long MaxArchiveEntryBytes = int.MaxValue - 64 * 1024;

    public static bool IsPackageFile(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".oiv" or ".oivs";

    /// <summary>Extracts an .oiv/.oivs (zip) to a temp folder, or returns the folder if already extracted.</summary>
    public static string ExtractToTemp(string packagePath)
    {
        if (Directory.Exists(packagePath)) return packagePath;
        string dir = Path.Combine(AppPaths.CacheDir, "oiv", Guid.NewGuid().ToString("N"));
        AppPaths.EnsureDir(dir);
        try { ZipFile.ExtractToDirectory(packagePath, dir); }
        catch (InvalidDataException ex)
        {
            TryDeleteDir(dir);
            throw new ModInstallException(ModInstallException.BadPackage, "The file is not a valid OIV package (zip).", ex);
        }
        return dir;
    }

    public static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    /// <summary>Reads the package's metadata and which game archives it edits (no changes made).</summary>
    public static OivPackageInfo Inspect(string packageDir)
    {
        var ctx = new Ctx(packageDir, packageDir, new ModInstallRecord(), new OivResult());
        var ops = ctx.LoadOperations();
        ctx.Info.Archives = ops.Where(o => o.Name.Equals("archive", StringComparison.OrdinalIgnoreCase))
            .Select(o => ModsArchives.Normalize(o.GetAttribute("path")))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ctx.Info;
    }

    /// <summary>
    /// Applies an extracted package to <paramref name="gameFolder"/>, recording everything on
    /// <paramref name="record"/> (also fills its name/version/author from the package).
    /// </summary>
    public static OivResult Apply(string packageDir, string gameFolder, ModInstallRecord record)
    {
        var result = new OivResult();
        var ctx = new Ctx(packageDir, gameFolder, record, result);
        record.GameFolder = gameFolder;
        record.ModType = MxaModType.Oiv;
        try
        {
            var ops = ctx.LoadOperations();
            if (!string.IsNullOrWhiteSpace(ctx.Info.Name)) record.Name = ctx.Info.Name;
            record.Version ??= ctx.Info.Version;
            record.Author ??= ctx.Info.Author;
            foreach (var op in ops)
                RunTopLevel(ctx, op);
        }
        catch (Exception ex)
        {
            Log.Error("OIV install failed, rolling back", ex);
            try { ArchiveEditRecorder.Revert(record); } catch { }
            try { LooseFileInstaller.Uninstall(record); } catch { }
            record.Files.Clear();
            record.ArchiveEdits.Clear();
            if (ex is ModInstallException) throw;
            throw new ModInstallException("failed", ex.Message, ex);
        }

        foreach (var s in result.Skipped)
        {
            Log.Info("OIV skipped: " + s);
            record.Warnings.Add(s);
        }
        return result;
    }

    // ---- package loading ---------------------------------------------------------------------

    private sealed class Ctx
    {
        public readonly string PackageDir, ContentDir, GameFolder;
        public readonly ModInstallRecord Record;
        public readonly OivResult Result;
        public readonly ArchiveEditRecorder Recorder;
        public readonly OivPackageInfo Info = new();

        public Ctx(string packageDir, string gameFolder, ModInstallRecord record, OivResult result)
        {
            PackageDir = packageDir;
            ContentDir = Path.Combine(packageDir, "content");
            GameFolder = Path.GetFullPath(gameFolder);
            Record = record;
            Result = result;
            Recorder = new ArchiveEditRecorder(record);
        }

        /// <summary>The operation elements of the package, in order.</summary>
        public List<XmlElement> LoadOperations()
        {
            string assembly = Path.Combine(PackageDir, "assembly.xml");
            string super = Path.Combine(PackageDir, "super.xml");
            var ops = new List<XmlElement>();

            if (File.Exists(assembly))
            {
                var doc = Load(assembly);
                var pkg = doc.DocumentElement;
                string target = pkg?.GetAttribute("target") ?? "";
                if (target.Length > 0 && !target.Equals("Five", StringComparison.OrdinalIgnoreCase))
                    throw new ModInstallException(ModInstallException.Unsupported, $"This OIV package is for another game ({target}).");
                ReadMetadata(pkg?.SelectSingleNode("metadata"));
                var content = pkg?.SelectSingleNode("content")
                              ?? throw new ModInstallException(ModInstallException.BadPackage, "No <content> in assembly.xml.");
                ops.AddRange(content.ChildNodes.OfType<XmlElement>());
                return ops;
            }

            if (File.Exists(super))
            {
                var doc = Load(super);
                var root = doc.DocumentElement;
                if (root == null || root.Name != "superpackage")
                    throw new ModInstallException(ModInstallException.BadPackage, "Invalid super.xml.");
                Info.IsSuper = true;
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

        private static XmlDocument Load(string path)
        {
            var doc = new XmlDocument();
            try { doc.Load(path); }
            catch (XmlException ex) { throw new ModInstallException(ModInstallException.BadPackage, $"{Path.GetFileName(path)} is not valid XML: {ex.Message}", ex); }
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
            string rel = source.Replace('/', '\\').Trim('\\');
            string baseDir = SafeContentPath(rel);
            if (!Directory.Exists(baseDir)) yield break;
            foreach (var file in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
            {
                string inside = Path.GetRelativePath(baseDir, file);
                var add = doc.CreateElement("add");
                add.SetAttribute("source", Path.Combine(rel, inside));
                add.InnerText = inside;
                yield return add;
            }
        }

        /// <summary>A content file path, refusing anything that escapes the package's content folder.</summary>
        public string SafeContentPath(string source)
        {
            string full = Path.GetFullPath(Path.Combine(ContentDir, source.Replace('/', '\\').TrimStart('\\')));
            string root = Path.GetFullPath(ContentDir).TrimEnd('\\') + "\\";
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new ModInstallException(ModInstallException.BadPackage, $"Invalid source path: {source}");
            return full;
        }

        /// <summary>
        /// Game-relative destination for a file on disk. Anything under update\ or x64\ or any .rpf
        /// is redirected to the mods folder, exactly like OpenIV, so original game files stay vanilla.
        /// </summary>
        public string DiskTarget(string dest)
        {
            string rel = dest.Replace('/', '\\').Trim().TrimStart('\\');
            bool mods = false;
            if (rel.StartsWith("mods\\", StringComparison.OrdinalIgnoreCase)) { rel = rel[5..]; mods = true; }
            mods |= rel.StartsWith("update", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("x64", StringComparison.OrdinalIgnoreCase)
                    || rel.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase);
            string result = mods ? Path.Combine("mods", rel) : rel;

            string full = Path.GetFullPath(Path.Combine(GameFolder, result));
            if (!full.StartsWith(GameFolder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new ModInstallException(ModInstallException.BadPackage, $"Destination escapes the game folder: {dest}");
            return result;
        }
    }

    private static bool IsTrue(string s) => bool.TryParse(s, out bool b) && b;

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
                break;
            case "archive":
                RunArchive(ctx, op, top: op.GetAttribute("path"), nested: new List<string>(),
                    create: IsTrue(op.GetAttribute("createIfNotExist")));
                break;
            case "xml":
            case "text":
                DiskEdit(ctx, op);
                break;
            case "defragmentation":
                break; // our edits never fragment beyond what the game reads fine
            default:
                ctx.Result.Skipped.Add($"Unsupported operation <{op.Name}>");
                break;
        }
    }

    private static void DiskAdd(Ctx ctx, XmlElement op)
    {
        string source = op.GetAttribute("source");
        string dest = op.InnerText.Trim();
        if (source.Length == 0 || dest.Length == 0) { ctx.Result.Skipped.Add("<add> without source or destination"); return; }

        string src = ctx.SafeContentPath(source);
        if (!File.Exists(src)) throw new ModInstallException(ModInstallException.BadPackage, $"The package is missing {source}.");

        string rel = ctx.DiskTarget(dest);
        LooseFileInstaller.CopyTracked(ctx.Record, src, rel);
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
            return;
        }
        if (string.IsNullOrWhiteSpace(top)) { ctx.Result.Skipped.Add("<archive> without path"); return; }

        var children = node.ChildNodes.OfType<XmlElement>().ToList();
        var fileOps = children.Where(c => !c.Name.Equals("archive", StringComparison.OrdinalIgnoreCase)).ToList();
        string topNorm = ModsArchives.Normalize(top);

        // One editing session for this archive's own operations…
        if (fileOps.Count > 0 || create)
        {
            bool topExisted = File.Exists(ModsArchives.ModsPath(ctx.GameFolder, topNorm));
            ArchiveChain.Edit(ctx.GameFolder, topNorm, nested, ed =>
            {
                foreach (var op in fileOps) RunArchiveOp(ctx, ed, topNorm, nested, op);
                ctx.Recorder.Seal(topNorm, nested, ed);
            }, createIfMissing: create);
            TrackCreatedArchive(ctx, topNorm, topExisted);
        }

        // …then each nested archive in its own session.
        foreach (var inner in children.Where(c => c.Name.Equals("archive", StringComparison.OrdinalIgnoreCase)))
        {
            string innerPath = inner.GetAttribute("path").Replace('\\', '/').Trim('/');
            if (innerPath.Length == 0) { ctx.Result.Skipped.Add("nested <archive> without path"); continue; }
            RunArchive(ctx, inner, top, new List<string>(nested) { innerPath }, IsTrue(inner.GetAttribute("createIfNotExist")));
        }
    }

    /// <summary>
    /// A brand-new archive the package created (createIfNotExist, no game original) belongs to the
    /// mod: uninstall deletes it. Copies of game archives (mods\update\update.rpf…) are shared and
    /// stay; the edits inside them are undone instead.
    /// </summary>
    private static void TrackCreatedArchive(Ctx ctx, string topNorm, bool existed)
    {
        if (existed || File.Exists(ModsArchives.GamePath(ctx.GameFolder, topNorm))) return;
        string rel = Path.Combine("mods", topNorm.Replace('/', '\\'));
        if (ctx.Record.Files.Any(f => string.Equals(f.Dest, rel, StringComparison.OrdinalIgnoreCase))) return;
        ctx.Record.Files.Add(new InstalledFile { Dest = rel, WasCreated = true });
    }

    private static void RunArchiveOp(Ctx ctx, RpfEditor ed, string top, List<string> nested, XmlElement op)
    {
        string where = nested.Count == 0 ? top : top + " > " + string.Join(" > ", nested);
        switch (op.Name.ToLowerInvariant())
        {
            case "add":
            {
                string source = op.GetAttribute("source");
                string entry = op.InnerText.Trim().Replace('\\', '/').Trim('/');
                if (source.Length == 0 || entry.Length == 0) { ctx.Result.Skipped.Add($"<add> without source or destination in {where}"); return; }
                string src = ctx.SafeContentPath(source);
                if (!File.Exists(src)) throw new ModInstallException(ModInstallException.BadPackage, $"The package is missing {source}.");
                long size = new FileInfo(src).Length;
                if (size > MaxArchiveEntryBytes)
                {
                    ctx.Result.Skipped.Add($"{source} is too large to store inside an archive");
                    return;
                }
                ctx.Recorder.Touch(top, nested, ed, entry);
                if (entry.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)) ed.SetNestedArchive(entry, src);
                else ed.SetFile(entry, File.ReadAllBytes(src));
                ctx.Result.Applied.Add($"add {where}/{entry}");
                break;
            }
            case "delete":
            {
                string entry = op.InnerText.Trim().Replace('\\', '/').Trim('/');
                if (entry.Length == 0 || !ed.FileExists(entry)) return;
                ctx.Recorder.Touch(top, nested, ed, entry);
                ed.DeleteFile(entry);
                ctx.Result.Applied.Add($"delete {where}/{entry}");
                break;
            }
            case "xml":
            case "text":
            {
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
                var edit = ctx.Recorder.Touch(top, nested, ed, entry);
                var journal = new EditJournal();
                string updated = xml ? ApplyXmlOps(op, original, ctx.Result, journal) : ApplyTextOps(op, original, journal);
                if (updated != original || !exists)
                {
                    ed.SetText(entry, updated);
                    if (!edit.WasCreated) edit.Journal = Merge(edit.Journal, journal);
                    ctx.Result.Applied.Add($"{op.Name} edit {where}/{entry}");
                }
                break;
            }
            case "defragmentation":
                break;
            default:
                ctx.Result.Skipped.Add($"Unsupported operation <{op.Name}> in {where}");
                break;
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
