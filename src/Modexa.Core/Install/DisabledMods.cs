using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Modexa.Core.Format;

namespace Modexa.Core.Install;

/// <summary>
/// A mod the user turned off. Disabling is a normal, exact uninstall (the game is back to how it was
/// without the mod) plus a reference to the mod file, so enabling simply installs it again — the
/// same tested paths as any install and uninstall, nothing half-hidden inside the game folder.
/// </summary>
public sealed class DisabledMod
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public MxaModType ModType { get; set; }
    /// <summary>Short type label of the installed mod (OIV / Modexa / Add-on / Files).</summary>
    public string Kind { get; set; } = "";
    public string Game { get; set; } = "GtaV";
    public string? GameTitle { get; set; }
    public string GameFolder { get; set; } = "";
    public string? ProductId { get; set; }
    /// <summary>File name it was installed from (kept on the reinstall, so split mods stay apart).</summary>
    public string? Source { get; set; }
    public string? Version { get; set; }
    public string? Author { get; set; }
    /// <summary>Where the mod file was when it was disabled.</summary>
    public string? OriginalPath { get; set; }
    /// <summary>Modexa's own link to the mod file (a hard link: no extra space), when one could be made.</summary>
    public string? KeptPath { get; set; }
    public string? IconFile { get; set; }
    public DateTime InstalledUtc { get; set; }
    public DateTime DisabledUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The mod file to install it from again: Modexa's link, else the original (null when both are gone).</summary>
    public string? AvailablePath()
    {
        foreach (var p in new[] { KeptPath, OriginalPath })
            if (!string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p))) return p;
        return null;
    }
}

/// <summary>Persistent list of disabled mods (JSON next to the installed-mods list).</summary>
public static class DisabledModsStore
{
    private static readonly object Gate = new();
    private static string StorePath => Path.Combine(AppPaths.DataDir, "disabled-mods.json");

    /// <summary>Folder holding a disabled mod's kept file and icon.</summary>
    public static string DirFor(string id) => Path.Combine(AppPaths.DataDir, "disabled", id);

    public static List<DisabledMod> All()
    {
        lock (Gate) return Load();
    }

    public static List<DisabledMod> ForFolder(string gameFolder)
        => All().Where(d => InstalledModsStore.SameFolder(d.GameFolder, gameFolder)).OrderBy(d => d.InstalledUtc).ToList();

    public static void Add(DisabledMod mod)
    {
        lock (Gate)
        {
            var list = Load();
            list.RemoveAll(d => d.Id == mod.Id);
            list.Add(mod);
            Save(list);
        }
    }

    /// <summary>Forgets a disabled mod and deletes Modexa's kept copy of it (never the user's own file).</summary>
    public static void Remove(string id)
    {
        lock (Gate)
        {
            var list = Load();
            list.RemoveAll(d => d.Id == id);
            Save(list);
        }
        try { string dir = DirFor(id); if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    /// <summary>A mod installed again from the same file replaces its disabled entry.</summary>
    public static void RemoveSameSource(string gameFolder, string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return;
        foreach (var d in ForFolder(gameFolder).Where(d => string.Equals(d.Source, source, StringComparison.OrdinalIgnoreCase)))
            Remove(d.Id);
    }

    /// <summary>Other disabled parts of the same split mod (same name and author, same game folder).</summary>
    public static List<DisabledMod> PartsOf(DisabledMod mod)
        => ForFolder(mod.GameFolder)
            .Where(d => d.ModType == MxaModType.Oiv && mod.ModType == MxaModType.Oiv && d.ProductId == null
                        && string.Equals(d.Name, mod.Name, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(d.Author ?? "", mod.Author ?? "", StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// Keeps the mod file for later without copying it: a hard link in Modexa's data folder (same
    /// drive only; then it survives the user deleting or moving the download). Returns null when no
    /// link could be made — the original path is then used.
    /// </summary>
    public static string? Keep(string id, string sourceFile)
    {
        try
        {
            if (!File.Exists(sourceFile)) return null;
            string dir = DirFor(id);
            AppPaths.EnsureDir(dir);
            string dest = Path.Combine(dir, Path.GetFileName(sourceFile));
            if (File.Exists(dest)) File.Delete(dest);
            return CreateHardLink(dest, sourceFile, IntPtr.Zero) ? dest : null;
        }
        catch { return null; }
    }

    /// <summary>Copies the mod's icon next to the kept file (the mod's backups folder goes away on uninstall).</summary>
    public static string? KeepIcon(string id, string? iconFile)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(iconFile) || !File.Exists(iconFile)) return null;
            string dir = DirFor(id);
            AppPaths.EnsureDir(dir);
            string dest = Path.Combine(dir, "icon.png");
            File.Copy(iconFile, dest, true);
            return dest;
        }
        catch { return null; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private static List<DisabledMod> Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<List<DisabledMod>>(File.ReadAllText(StorePath)) ?? new();
        }
        catch { }
        return new();
    }

    private static void Save(List<DisabledMod> list)
    {
        try
        {
            AppPaths.WriteAllTextAtomic(StorePath,
                JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }),
                System.Text.Encoding.UTF8);
        }
        catch { }
    }
}
