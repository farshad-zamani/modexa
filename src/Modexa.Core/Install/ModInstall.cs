using System.IO;
using System.Text.Json;
using Modexa.Core.Format;

namespace Modexa.Core.Install;

/// <summary>One file a paid mod placed, with enough info to revert it precisely.</summary>
public sealed class InstalledFile
{
    public string Dest { get; set; } = "";                 // relative to game folder
    public string? OriginalBackup { get; set; }            // saved original, or null if newly created
    public bool WasCreated { get; set; }
}

/// <summary>Record of one installed paid mod, used for the "installed mods" view and uninstall.</summary>
public sealed class ModInstallRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public MxaModType ModType { get; set; }
    public string Game { get; set; } = "GtaV";
    public string GameFolder { get; set; } = "";
    public string? DlcName { get; set; }                   // addon: dlcpacks folder registered
    public string? ProductId { get; set; }                 // FSLM product of a paid package (null for Pro raw installs)
    public DateTime InstalledUtc { get; set; } = DateTime.UtcNow;
    public List<InstalledFile> Files { get; set; } = new();
}

/// <summary>Persistent list of installed paid mods (JSON in the user data folder).</summary>
public static class InstalledModsStore
{
    private static readonly object Gate = new();
    private static string StorePath => AppPaths.InstalledModsFile;

    public static List<ModInstallRecord> All()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<List<ModInstallRecord>>(File.ReadAllText(StorePath)) ?? new();
        }
        catch { }
        return new();
    }

    public static void Add(ModInstallRecord record)
    {
        lock (Gate)
        {
            var list = All();
            list.Add(record);
            Save(list);
        }
    }

    public static void Remove(string id)
    {
        lock (Gate)
        {
            var list = All();
            list.RemoveAll(r => r.Id == id);
            Save(list);
        }
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
