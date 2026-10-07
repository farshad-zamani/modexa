using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Modexa.Core.Diagnostics;
using Modexa.Core.Rpf;

namespace Modexa.Core.Install;

/// <summary>One file a mod changed INSIDE a game archive, with what is needed to undo it.</summary>
public sealed class ArchiveEdit
{
    /// <summary>Top-level archive, game-relative without the "mods\" prefix (e.g. "update/update.rpf").</summary>
    public string Archive { get; set; } = "";
    /// <summary>Nested archives inside it, outermost first.</summary>
    public List<string> Nested { get; set; } = new();
    /// <summary>Path of the file inside the innermost archive.</summary>
    public string Entry { get; set; } = "";
    public bool WasCreated { get; set; }
    /// <summary>The original content (as read from the archive), saved under the mod's backup folder.</summary>
    public string? OriginalBackup { get; set; }
    /// <summary>SHA-256 of the content right after the mod's change; null when the mod deleted the file.</summary>
    public string? WrittenSha256 { get; set; }
    /// <summary>The individual text/XML changes, undone one by one when the file was changed again since.</summary>
    public EditJournal? Journal { get; set; }
    /// <summary>Set once undone (so a retried uninstall skips it).</summary>
    public bool Reverted { get; set; }

}

public sealed class XmlAddition
{
    public string ParentXPath { get; set; } = "";
    public string Xml { get; set; } = "";
    /// <summary>Element index under the parent (for re-inserting a removed node at its place); -1 = unknown.</summary>
    public int Index { get; set; } = -1;
}

public sealed class XmlChange
{
    public string ParentXPath { get; set; } = "";
    public string OldXml { get; set; } = "";
    public string NewXml { get; set; } = "";
}

public sealed class LineChange
{
    public string Old { get; set; } = "";
    public string New { get; set; } = "";
    public int Index { get; set; } = -1;
}

/// <summary>
/// The individual edits a mod made to a text/XML file. Used when the file was changed again by
/// something else after the install: instead of restoring the old copy (which would wipe the other
/// change), each edit is reversed on its own — added nodes/lines removed, replaced ones put back,
/// removed ones re-inserted.
/// </summary>
public sealed class EditJournal
{
    public List<XmlAddition>? XmlAdded { get; set; }
    public List<XmlAddition>? XmlRemoved { get; set; }
    public List<XmlChange>? XmlReplaced { get; set; }
    public List<string>? LinesAdded { get; set; }
    public List<LineChange>? LinesReplaced { get; set; }
    public List<LineChange>? LinesDeleted { get; set; }

    public bool IsEmpty => (XmlAdded?.Count ?? 0) + (XmlRemoved?.Count ?? 0) + (XmlReplaced?.Count ?? 0)
                           + (LinesAdded?.Count ?? 0) + (LinesReplaced?.Count ?? 0) + (LinesDeleted?.Count ?? 0) == 0;

    /// <summary>Reverses this journal on <paramref name="content"/>; null when nothing could be changed.</summary>
    public string? Undo(string content)
    {
        bool xml = (XmlAdded?.Count ?? 0) + (XmlRemoved?.Count ?? 0) + (XmlReplaced?.Count ?? 0) > 0;
        return xml ? UndoXml(content) : UndoText(content);
    }

    private string? UndoXml(string content)
    {
        try
        {
            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.LoadXml(content);
            bool changed = false;

            foreach (var r in Enumerable.Reverse(XmlReplaced ?? new()))
            {
                var parent = doc.SelectSingleNode(r.ParentXPath);
                var hit = parent == null ? null : FindChild(parent, r.NewXml);
                if (hit == null) continue;
                var old = Import(doc, r.OldXml);
                if (old == null) continue;
                parent!.ReplaceChild(old, hit);
                changed = true;
            }
            foreach (var a in Enumerable.Reverse(XmlAdded ?? new()))
            {
                var parent = doc.SelectSingleNode(a.ParentXPath);
                var hit = parent == null ? null : FindChild(parent, a.Xml);
                if (hit == null) continue;
                if (hit.PreviousSibling is { NodeType: XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace } ws)
                    parent!.RemoveChild(ws);
                parent!.RemoveChild(hit);
                changed = true;
            }
            foreach (var r in Enumerable.Reverse(XmlRemoved ?? new()))
            {
                var parent = doc.SelectSingleNode(r.ParentXPath);
                if (parent == null || FindChild(parent, r.Xml) != null) continue;
                var node = Import(doc, r.Xml);
                if (node == null) continue;
                var elements = parent.ChildNodes.OfType<XmlElement>().ToList();
                if (r.Index >= 0 && r.Index < elements.Count) parent.InsertBefore(node, elements[r.Index]);
                else parent.AppendChild(node);
                changed = true;
            }
            return changed ? XmlFiles.Save(doc) : null;
        }
        catch (Exception ex)
        {
            Log.Error("Undo xml edits", ex);
            return null;
        }
    }

    private string? UndoText(string content)
    {
        bool crlf = content.Contains("\r\n");
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        bool changed = false;

        foreach (var l in Enumerable.Reverse(LinesAdded ?? new()))
        {
            int i = lines.FindLastIndex(x => x == l);
            if (i >= 0) { lines.RemoveAt(i); changed = true; }
        }
        foreach (var r in Enumerable.Reverse(LinesReplaced ?? new()))
        {
            int i = r.Index >= 0 && r.Index < lines.Count && lines[r.Index] == r.New ? r.Index : lines.IndexOf(r.New);
            if (i >= 0) { lines[i] = r.Old; changed = true; }
        }
        foreach (var d in Enumerable.Reverse(LinesDeleted ?? new()))
        {
            int at = d.Index < 0 ? lines.Count : Math.Min(d.Index, lines.Count);
            lines.Insert(at, d.Old);
            changed = true;
        }
        return changed ? string.Join(crlf ? "\r\n" : "\n", lines) : null;
    }

    private static XmlNode? FindChild(XmlNode parent, string xml)
    {
        string want = Normalize(xml);
        return parent.ChildNodes.Cast<XmlNode>().LastOrDefault(n => n.NodeType == XmlNodeType.Element && Normalize(n.OuterXml) == want);
    }

    private static XmlNode? Import(XmlDocument doc, string xml)
    {
        try
        {
            var frag = doc.CreateDocumentFragment();
            frag.InnerXml = xml;
            return frag.FirstChild;
        }
        catch { return null; }
    }

    public static string Normalize(string xml) => string.Concat(xml.Where(c => !char.IsWhiteSpace(c)));
}

/// <summary>
/// Records archive edits for a mod install and reverts them on uninstall. A file the mod fully owns
/// (unchanged since the install) is restored exactly; a file another mod changed afterwards either
/// hands its original over to that mod (so the true original comes back when it is removed too) or,
/// for shared text/XML files such as dlclist.xml, gets only this mod's own edits reversed.
/// </summary>
public sealed class ArchiveEditRecorder
{
    private readonly ModInstallRecord _record;
    private readonly string _backupDir;

    public ArchiveEditRecorder(ModInstallRecord record)
    {
        _record = record;
        _backupDir = ModBackups.DirFor(record);
    }

    /// <summary>Call before the first change to <paramref name="entry"/> in this editing session.</summary>
    public ArchiveEdit Touch(string archive, IReadOnlyList<string> nested, RpfEditor ed, string entry)
    {
        string arch = ModsArchives.Normalize(archive);
        string ent = NormalizeEntry(entry);
        var existing = _record.ArchiveEdits.FirstOrDefault(e => Same(e, arch, nested, ent));
        if (existing != null) return existing;

        var edit = new ArchiveEdit { Archive = arch, Nested = nested.Select(NormalizeEntry).ToList(), Entry = ent };
        if (ed.FileExists(ent))
        {
            AppPaths.EnsureDir(_backupDir);
            string bak = Path.Combine(_backupDir, Guid.NewGuid().ToString("N") + ".bak");
            File.WriteAllBytes(bak, ed.ReadFile(ent));
            edit.OriginalBackup = bak;
        }
        else
        {
            edit.WasCreated = true;
        }
        _record.ArchiveEdits.Add(edit);
        return edit;
    }

    /// <summary>Call after the session's changes are staged (before commit): fingerprints the result.</summary>
    public void Seal(string archive, IReadOnlyList<string> nested, RpfEditor ed)
    {
        string arch = ModsArchives.Normalize(archive);
        foreach (var e in _record.ArchiveEdits.Where(e => e.Archive == arch && SameChain(e.Nested, nested)))
            e.WrittenSha256 = ed.FileExists(e.Entry) ? Sha(ed.ReadFile(e.Entry)) : null;
    }

    // ---- revert --------------------------------------------------------------------------------

    /// <summary>Undoes every archive edit of an installed mod (no later mods to hand originals to).</summary>
    public static void Revert(ModInstallRecord record) => Revert(record, Array.Empty<ModInstallRecord>(), new List<string>());

    /// <summary>
    /// Undoes every archive edit of <paramref name="record"/>, newest first, one editing session per
    /// archive. <paramref name="later"/> are mods installed after it into the same game (oldest first).
    /// Problems are added to <paramref name="errors"/>; successfully undone edits are marked.
    /// </summary>
    public static void Revert(ModInstallRecord record, IReadOnlyList<ModInstallRecord> later, List<string> errors)
    {
        var pending = record.ArchiveEdits.Where(e => !e.Reverted).Reverse().ToList();
        foreach (var g in pending.GroupBy(e => e.Archive + "|" + string.Join(">", e.Nested), StringComparer.OrdinalIgnoreCase))
        {
            var first = g.First();
            if (!File.Exists(ModsArchives.ModsPath(record.GameFolder, first.Archive)))
            {
                foreach (var e in g) e.Reverted = true; // the whole archive is gone (game reverted)
                continue;
            }
            try
            {
                ArchiveChain.Edit(record.GameFolder, first.Archive, first.Nested, ed =>
                {
                    foreach (var e in g) RevertOne(ed, e, record, later);
                });
                foreach (var e in g) e.Reverted = true;
            }
            catch (Exception ex)
            {
                Log.Error($"Uninstall: archive edits in {first.Archive}", ex);
                errors.Add($"{first.Archive}: {ex.Message}");
            }
        }
    }

    private static void RevertOne(RpfEditor ed, ArchiveEdit e, ModInstallRecord record, IReadOnlyList<ModInstallRecord> later)
    {
        bool exists = ed.FileExists(e.Entry);
        string? current = exists ? Sha(ed.ReadFile(e.Entry)) : null;

        if (current == e.WrittenSha256)
        {
            // Nobody touched it since: restore exactly.
            if (e.WasCreated) { if (exists) ed.DeleteFile(e.Entry); }
            else if (e.OriginalBackup != null && File.Exists(e.OriginalBackup)) ed.SetFile(e.Entry, File.ReadAllBytes(e.OriginalBackup));
            return;
        }

        // A later mod replaced the same file: it now owns the original (restored when it is removed).
        var heir = later.SelectMany(r => r.ArchiveEdits.Select(x => (r, x)))
            .FirstOrDefault(p => !p.x.Reverted && Same(p.x, e.Archive, e.Nested, e.Entry));
        var journal = e.Journal;
        if (heir.x != null && (journal == null || journal.IsEmpty))
        {
            heir.x.WasCreated = e.WasCreated;
            heir.x.OriginalBackup = ModBackups.HandOver(e.OriginalBackup, heir.r);
            e.OriginalBackup = null;
            return;
        }

        if (!exists) return;
        if (journal != null && !journal.IsEmpty)
        {
            string? text = journal.Undo(ed.ReadText(e.Entry));
            // Back to exactly what we saved (other mods' edits already taken out)? Then restore the
            // saved bytes — same content, original formatting.
            if (text != null && ModBackups.SameText(e.OriginalBackup, text)) ed.SetFile(e.Entry, File.ReadAllBytes(e.OriginalBackup!));
            else if (text != null) ed.SetText(e.Entry, text);

            // Later mods saved this file *with* our edits as "their original": take our edits out of
            // those copies too, or removing them later would bring our edits (e.g. a dlclist entry
            // for a pack that no longer exists) back.
            foreach (var (_, x) in later.SelectMany(r => r.ArchiveEdits.Select(x => (r, x)))
                         .Where(p => !p.x.Reverted && Same(p.x, e.Archive, e.Nested, e.Entry)))
                ModBackups.PatchText(x.OriginalBackup, journal);
        }
        else
        {
            Log.Info($"Uninstall: {e.Entry} was changed by something else since; left as is.");
        }
    }

    private static string NormalizeEntry(string p) => p.Replace('\\', '/').Trim('/');

    private static bool Same(ArchiveEdit e, string archive, IReadOnlyList<string> nested, string entry)
        => string.Equals(e.Archive, archive, StringComparison.OrdinalIgnoreCase)
           && SameChain(e.Nested, nested)
           && string.Equals(e.Entry, entry, StringComparison.OrdinalIgnoreCase);

    private static bool SameChain(IReadOnlyList<string> a, IReadOnlyList<string> b)
        => a.Count == b.Count && a.Zip(b).All(p => string.Equals(NormalizeEntry(p.First), NormalizeEntry(p.Second), StringComparison.OrdinalIgnoreCase));

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}

/// <summary>Serializes an XmlDocument back to text the way the game files are written (UTF-8, no re-indent).</summary>
public static class XmlFiles
{
    public static string Save(XmlDocument doc)
    {
        using var sw = new Utf8StringWriter();
        using (var xw = XmlWriter.Create(sw, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false }))
            doc.Save(xw);
        // Keep the file's own declaration exactly (e.g. encoding="UTF-8"), not the writer's.
        return doc.FirstChild is XmlDeclaration decl ? decl.OuterXml + sw : sw.ToString();
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
