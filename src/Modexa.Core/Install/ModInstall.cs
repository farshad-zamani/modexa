using System.IO;
using System.Text.Json;
using Modexa.Core.Format;

namespace Modexa.Core.Install;

/// <summary>One file a mod placed, changed or deleted on disk, with enough info to revert it precisely.</summary>
public sealed class InstalledFile
{
    public string Dest { get; set; } = "";                 // relative to game folder
    public string? OriginalBackup { get; set; }            // saved original, or null if newly created
    public bool WasCreated { get; set; }
    /// <summary>The mod deleted this file (the original is in <see cref="OriginalBackup"/>).</summary>
    public bool WasDeleted { get; set; }
    /// <summary><see cref="FileStamp"/> right after the mod wrote it.</summary>
    public string? WrittenStamp { get; set; }
    /// <summary>For text/XML files the mod edited in place: the individual edits (see <see cref="EditJournal"/>).</summary>
    public EditJournal? Journal { get; set; }
    /// <summary>Set once undone (so a retried uninstall skips it).</summary>
    public bool Reverted { get; set; }
}

/// <summary>Record of one installed mod, used for the installed-mods lists and for uninstall.</summary>
public sealed class ModInstallRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public MxaModType ModType { get; set; }
    public string Game { get; set; } = "GtaV";
    /// <summary>Display name of the game it was installed into (e.g. "Grand Theft Auto V Legacy").</summary>
    public string? GameTitle { get; set; }
    public string GameFolder { get; set; } = "";
    public string? DlcName { get; set; }                   // addon: dlcpacks folder registered
    public string? ProductId { get; set; }                 // FSLM product of a paid package (null otherwise)
    /// <summary>File name the mod was installed from (.oiv / .mxa / .rpf / .zip).</summary>
    public string? Source { get; set; }
    public string? Version { get; set; }
    public string? Author { get; set; }
    public DateTime InstalledUtc { get; set; } = DateTime.UtcNow;
    public List<InstalledFile> Files { get; set; } = new();
    /// <summary>Files changed inside game archives (OIV &lt;archive&gt; operations), for uninstall.</summary>
    public List<ArchiveEdit> ArchiveEdits { get; set; } = new();
    /// <summary>Folders the mod created (game-relative); removed again on uninstall when empty.</summary>
    public List<string> CreatedDirs { get; set; } = new();
    /// <summary>Operations the installer skipped (shown to the user once after installing).</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>Short type label: Modexa (licensed package) / OIV / Add-on / Files.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string KindLabel => ProductId != null || Source?.EndsWith(".mxa", StringComparison.OrdinalIgnoreCase) == true
        ? "Modexa"
        : ModType switch { MxaModType.Oiv => "OIV", MxaModType.Addon => "Add-on", _ => "Files" };
}

/// <summary>Persistent list of installed mods (JSON in the user data folder).</summary>
public static class InstalledModsStore
{
    private static readonly object Gate = new();
    private static string StorePath => AppPaths.InstalledModsFile;

    public static List<ModInstallRecord> All()
    {
        lock (Gate) return Load();
    }

    /// <summary>Mods installed into one game folder, oldest first.</summary>
    public static List<ModInstallRecord> ForFolder(string gameFolder)
        => All().Where(r => SameFolder(r.GameFolder, gameFolder)).OrderBy(r => r.InstalledUtc).ToList();

    public static void Add(ModInstallRecord record)
    {
        lock (Gate)
        {
            var list = Load();
            list.RemoveAll(r => r.Id == record.Id);
            list.Add(record);
            Save(list);
        }
    }

    /// <summary>Replaces stored records with these versions (same Id).</summary>
    public static void Update(IEnumerable<ModInstallRecord> records)
    {
        lock (Gate)
        {
            var list = Load();
            foreach (var r in records)
            {
                int i = list.FindIndex(x => x.Id == r.Id);
                if (i >= 0) list[i] = r;
            }
            Save(list);
        }
    }

    public static void Remove(string id)
    {
        lock (Gate)
        {
            var list = Load();
            list.RemoveAll(r => r.Id == id);
            Save(list);
        }
    }

    public static bool SameFolder(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    private static List<ModInstallRecord> Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<List<ModInstallRecord>>(File.ReadAllText(StorePath)) ?? new();
        }
        catch { }
        return new();
    }

    private static void Save(List<ModInstallRecord> list)
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
