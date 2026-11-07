using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using Modexa.Core.Rpf;

namespace Modexa.Core.Install;

/// <summary>
/// Add-on pack registration in GTA V's <c>common\data\dlclist.xml</c> inside the editable
/// <c>mods\update\update.rpf</c> (created automatically when missing). Idempotent.
/// </summary>
public static class DlcList
{
    public const string EntryPath = "common/data/dlclist.xml";
    private const string PathsXPath = "/SMandatoryPacksData/Paths";

    public static string DlcPacksDir(string gameFolder)
        => Path.Combine(gameFolder, "mods", "update", "x64", "dlcpacks");

    /// <summary>Adds <c>dlcpacks:/name/</c>. Returns false if it was already registered.</summary>
    public static bool Register(string gameFolder, string dlcName, IProgress<int>? progress = null, CancellationToken ct = default)
        => Edit(gameFolder, xml => AddEntry(xml, dlcName), progress, ct);

    /// <summary>Adds several packs in one edit of update.rpf (an add-on pack of many DLCs). True if any was new.</summary>
    public static bool RegisterMany(string gameFolder, IReadOnlyCollection<string> dlcNames, IProgress<int>? progress = null, CancellationToken ct = default)
        => dlcNames.Count > 0 && Edit(gameFolder, xml => dlcNames.Aggregate(xml, AddEntry), progress, ct);

    /// <summary>Removes several packs in one edit. Does nothing when there is no editable update.rpf.</summary>
    public static bool UnregisterMany(string gameFolder, IReadOnlyCollection<string> dlcNames)
    {
        if (dlcNames.Count == 0 || !File.Exists(ModsArchives.ModsPath(gameFolder, ModsArchives.UpdateRpf))) return false;
        return Edit(gameFolder, xml => dlcNames.Aggregate(xml, RemoveEntry), null, default);
    }

    /// <summary>Removes <c>dlcpacks:/name/</c>. Does nothing when there is no editable update.rpf.</summary>
    public static bool Unregister(string gameFolder, string dlcName)
    {
        if (!File.Exists(ModsArchives.ModsPath(gameFolder, ModsArchives.UpdateRpf))) return false;
        return Edit(gameFolder, xml => RemoveEntry(xml, dlcName), null, default);
    }

    public static bool IsRegistered(string gameFolder, string dlcName)
    {
        if (!ModsArchives.IsReady(gameFolder)) return false;
        var ed = RpfEditor.Open(ModsArchives.ModsPath(gameFolder, ModsArchives.UpdateRpf));
        return ed.FileExists(EntryPath) && FindItem(Parse(ed.ReadText(EntryPath)).paths, dlcName) != null;
    }

    private static bool Edit(string gameFolder, Func<string, string> change, IProgress<int>? progress, CancellationToken ct)
    {
        // update.rpf can briefly lock under antivirus / Windows Search right after a write.
        int delay = 100;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return ArchiveChain.Edit(gameFolder, ModsArchives.UpdateRpf, Array.Empty<string>(), ed =>
                {
                    if (!ed.FileExists(EntryPath)) throw new FileNotFoundException("dlclist.xml was not found inside update.rpf.");
                    string xml = ed.ReadText(EntryPath);
                    string updated = change(xml);
                    if (updated == xml) return false;
                    ed.SetText(EntryPath, updated);
                    return true;
                }, progress: progress, ct: ct);
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, 1000);
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, 1000);
            }
        }
    }

    // dlclist.xml is edited as TEXT, never re-serialised: every other byte of the user's file (spacing,
    // tabs, line endings, a lowercase <item>, comments, the order of the lines) stays exactly as it was.

    private static readonly Regex ItemEnd = new(@"</Item\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string AddEntry(string xml, string dlcName)
    {
        var (_, paths) = Parse(xml);
        if (paths == null || FindItem(paths, dlcName) != null) return xml;
        string nl = xml.Contains("\r\n") ? "\r\n" : "\n";
        string item = $"<Item>dlcpacks:/{dlcName}/</Item>";

        int close = xml.IndexOf("</Paths>", StringComparison.Ordinal);
        if (close < 0)
        {
            // <Paths/> or <Paths></Paths> style: open it up.
            var empty = Regex.Match(xml, @"<Paths\s*/>");
            if (!empty.Success) return xml;
            return xml[..empty.Index] + "<Paths>" + nl + "\t\t" + item + nl + "\t</Paths>" + xml[(empty.Index + empty.Length)..];
        }

        // After the last <Item> before </Paths>, on a new line with that line's own indentation.
        Match? last = null;
        foreach (Match m in ItemEnd.Matches(xml[..close])) last = m;
        if (last == null)
        {
            int lineStart = LineStart(xml, close);
            string closeIndent = xml[lineStart..close].All(c => c is ' ' or '\t') ? xml[lineStart..close] : "";
            return xml[..lineStart] + closeIndent + "\t" + item + nl + xml[lineStart..];
        }
        int end = last.Index + last.Length;
        string indent = LeadingWhitespace(xml, last.Index);
        int eol = xml.IndexOf('\n', end);
        bool restIsBlank = eol >= 0 && eol < close && xml[end..eol].All(c => c is ' ' or '\t' or '\r');
        if (restIsBlank)
            return xml[..(eol + 1)] + indent + item + nl + xml[(eol + 1)..];
        return xml[..end] + nl + indent + item + xml[end..];
    }

    public static string RemoveEntry(string xml, string dlcName)
    {
        var (_, paths) = Parse(xml);
        if (paths == null || FindItem(paths, dlcName) == null) return xml;

        // The element, wherever it sits; when it is alone on its line, the whole line goes with it.
        string name = Regex.Escape(dlcName.Trim().Trim('/', '\\'));
        var element = new Regex($@"<Item\s*>\s*dlcpacks:[/\\]{name}[/\\]?\s*</Item\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        string result = xml;
        for (var m = element.Match(result); m.Success; m = element.Match(result))
        {
            int ls = LineStart(result, m.Index);
            int e = m.Index + m.Length;
            int eol = result.IndexOf('\n', e);
            string before = result[ls..m.Index];
            string after = eol < 0 ? result[e..] : result[e..eol];
            bool alone = before.All(c => c is ' ' or '\t') && after.All(c => c is ' ' or '\t' or '\r');
            result = alone && eol >= 0 ? result.Remove(ls, eol + 1 - ls) : result.Remove(m.Index, m.Length);
        }
        return result;
    }

    private static int LineStart(string s, int index)
    {
        int i = s.LastIndexOf('\n', Math.Max(index - 1, 0));
        return i < 0 || index == 0 ? 0 : i + 1;
    }

    private static string LeadingWhitespace(string s, int index)
    {
        int ls = LineStart(s, index);
        string lead = s[ls..index];
        return lead.All(c => c is ' ' or '\t') ? lead : "\t\t";
    }
    private static (XmlDocument doc, XmlNode? paths) Parse(string xml)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xml);
        return (doc, doc.SelectSingleNode(PathsXPath));
    }

    private static XmlNode? FindItem(XmlNode? paths, string dlcName)
    {
        if (paths == null) return null;
        string want = $"dlcpacks:/{dlcName}".TrimEnd('/');
        foreach (XmlNode child in paths.ChildNodes)
        {
            if (!string.Equals(child.Name, "Item", StringComparison.Ordinal)) continue;
            string v = child.InnerText.Trim().Replace('\\', '/').TrimEnd('/');
            if (string.Equals(v, want, StringComparison.OrdinalIgnoreCase)) return child;
        }
        return null;
    }
}
