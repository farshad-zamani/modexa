using System.IO;
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

    public static string AddEntry(string xml, string dlcName)
    {
        var (doc, paths) = Parse(xml);
        if (paths == null || FindItem(paths, dlcName) != null) return xml;

        // Match the file's indentation: copy the whitespace that precedes the last <Item>.
        var lastItem = paths.ChildNodes.Cast<XmlNode>().LastOrDefault(n => n.NodeType == XmlNodeType.Element);
        string indent = lastItem?.PreviousSibling is { NodeType: XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace } ws
            ? ws.Value ?? "\n\t\t"
            : "\n\t\t";
        var item = doc.CreateElement("Item");
        item.InnerText = $"dlcpacks:/{dlcName}/";
        if (lastItem != null)
        {
            paths.InsertAfter(item, lastItem);
            paths.InsertBefore(doc.CreateWhitespace(indent), item);
        }
        else
        {
            paths.AppendChild(item);
        }
        return XmlFiles.Save(doc);
    }

    public static string RemoveEntry(string xml, string dlcName)
    {
        var (doc, paths) = Parse(xml);
        var node = paths == null ? null : FindItem(paths, dlcName);
        if (node == null) return xml;
        if (node.PreviousSibling is { NodeType: XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace } ws)
            paths!.RemoveChild(ws);
        paths!.RemoveChild(node);
        return XmlFiles.Save(doc);
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
