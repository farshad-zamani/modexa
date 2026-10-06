using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Modexa.Core.Games;

/// <summary>A located game install (folder + edition + detected version + how it was found).</summary>
public sealed record GameInstall(
    GameId Game,
    string Folder,
    GameEdition Edition,
    GameVersion? Version,
    string Source);

/// <summary>
/// Finds game install folders from the registry, Steam/Epic manifests, then validates each
/// candidate by the presence of the real executable + a sanity marker. GTA V is fully supported;
/// GTA SA is detected by folder markers. Users can always pick a folder manually.
/// </summary>
public static class GameDetector
{
    // Steam app ids.
    private const string SteamGtaVLegacy = "271590";
    private const string SteamGtaVEnhanced = "3240220";
    private const string SteamGtaSa = "12120";

    /// <summary>GTA V installs of one edition only (Legacy and Enhanced are separate titles).</summary>
    public static IReadOnlyList<GameInstall> DetectGtaV(GameEdition edition)
        => DetectGtaV().Where(i => i.Edition == edition).ToList();

    /// <summary>True if <paramref name="folder"/> is a GTA V install of exactly this edition.</summary>
    public static bool IsGtaVFolder(string folder, GameEdition edition)
        => IsGtaVFolder(folder) && EditionOf(folder) == edition;

    /// <summary>Edition of a GTA V folder by its executable (Enhanced ships GTA5_Enhanced.exe).</summary>
    public static GameEdition EditionOf(string folder)
    {
        try
        {
            if (File.Exists(Path.Combine(folder, "GTA5_Enhanced.exe"))) return GameEdition.Enhanced;
            if (File.Exists(Path.Combine(folder, "GTA5.exe"))) return GameEdition.Legacy;
        }
        catch { }
        return GameEdition.Unknown;
    }

    /// <summary>All GTA V installs found on this machine (Legacy and/or Enhanced), de-duplicated.</summary>
    public static IReadOnlyList<GameInstall> DetectGtaV()
    {
        var found = new List<GameInstall>();
        void Add(string? folder, string source)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            folder = folder.Trim();
            if (!IsGtaVFolder(folder)) return;
            if (found.Any(f => PathEquals(f.Folder, folder!))) return;

            var version = GameVersion.Detect(folder);
            var edition = version?.Edition ?? EditionOf(folder);
            found.Add(new GameInstall(GameCatalog.ForEdition(edition), folder, edition, version, source));
        }

        // Registry (Rockstar). Enhanced registers under its own key on the Rockstar launcher.
        Add(ReadRegistry(@"SOFTWARE\WOW6432Node\Rockstar Games\Grand Theft Auto V", "InstallFolder"), "Registry");
        Add(ReadRegistry(@"SOFTWARE\WOW6432Node\Rockstar Games\GTAV", "InstallFolderSteam"), "Registry(Steam)");
        Add(ReadRegistry(@"SOFTWARE\Rockstar Games\Grand Theft Auto V", "InstallFolder"), "Registry");
        Add(ReadRegistry(@"SOFTWARE\WOW6432Node\Rockstar Games\GTA V Enhanced", "InstallFolder"), "Registry");
        Add(ReadRegistry(@"SOFTWARE\WOW6432Node\Rockstar Games\Grand Theft Auto V Enhanced", "InstallFolder"), "Registry");

        // Steam libraries.
        foreach (var lib in SteamLibraries())
        {
            Add(SteamAppInstallDir(lib, SteamGtaVEnhanced), "Steam");
            Add(SteamAppInstallDir(lib, SteamGtaVLegacy), "Steam");
        }

        // Epic.
        foreach (var inst in EpicInstalls())
            Add(inst, "Epic");

        return found;
    }

    /// <summary>Best-effort install folder for a "simple" game (Steam common dir validated by its exe).</summary>
    public static string? DetectSimple(GameId id)
    {
        if (!SimpleGames.Has(id)) return null;
        var def = SimpleGames.Get(id);
        try
        {
            foreach (var lib in SteamLibraries())
                foreach (var appId in def.SteamAppIds)
                {
                    string? dir = SteamCommonDir(lib, appId);
                    if (!string.IsNullOrWhiteSpace(dir) && def.IsFolder(dir!)) return dir;
                }
        }
        catch { }
        return null;
    }

    /// <summary>The Steam install directory for an app id (no game-specific validation), or null.</summary>
    private static string? SteamCommonDir(string steamApps, string appId)
    {
        try
        {
            string acf = Path.Combine(steamApps, $"appmanifest_{appId}.acf");
            if (!File.Exists(acf)) return null;
            string text = File.ReadAllText(acf);
            var state = Regex.Match(text, "\"StateFlags\"\\s*\"(\\d+)\"");
            if (state.Success && int.TryParse(state.Groups[1].Value, out int flags) && (flags & 4) == 0) return null;
            var dir = Regex.Match(text, "\"installdir\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (!dir.Success) return null;
            return Path.Combine(steamApps, "common", dir.Groups[1].Value);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Best-effort GTA San Andreas folder (Steam/registry); null if not found.</summary>
    public static GameInstall? DetectGtaSa()
    {
        string? Validate(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            folder = folder.Trim();
            return IsGtaSaFolder(folder) ? folder : null;
        }

        foreach (var lib in SteamLibraries())
        {
            var f = Validate(SteamAppInstallDir(lib, SteamGtaSa));
            if (f != null) return new GameInstall(GameId.GtaSanAndreas, f, GameEdition.Unknown, null, "Steam");
        }
        var reg = Validate(ReadRegistry(@"SOFTWARE\WOW6432Node\Rockstar Games\GTA San Andreas", "InstallFolder"));
        if (reg != null) return new GameInstall(GameId.GtaSanAndreas, reg, GameEdition.Unknown, null, "Registry");

        return null;
    }

    // ---- Validation --------------------------------------------------------------------------

    public static bool IsGtaVFolder(string folder)
    {
        try
        {
            bool hasExe = File.Exists(Path.Combine(folder, "GTA5.exe"))
                       || File.Exists(Path.Combine(folder, "GTA5_Enhanced.exe"));
            bool hasMarker = File.Exists(Path.Combine(folder, "update", "update.rpf"))
                          || File.Exists(Path.Combine(folder, "x64a.rpf"));
            return hasExe && hasMarker;
        }
        catch { return false; }
    }

    public static bool IsGtaSaFolder(string folder)
    {
        try
        {
            return File.Exists(Path.Combine(folder, "gta_sa.exe"))
                || File.Exists(Path.Combine(folder, "gta-sa.exe"))
                || File.Exists(Path.Combine(folder, "GTASA.exe"));
        }
        catch { return false; }
    }

    // ---- Registry ----------------------------------------------------------------------------

    private static string? ReadRegistry(string subKey, string valueName)
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(subKey, false);
                var val = key?.GetValue(valueName)?.ToString();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
            catch { /* try the other view */ }
        }
        return null;
    }

    // ---- Steam -------------------------------------------------------------------------------

    private static string? SteamRoot()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", false);
            var p = key?.GetValue("SteamPath")?.ToString();
            if (!string.IsNullOrWhiteSpace(p)) return p.Replace('/', '\\');
        }
        catch { }
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Valve\Steam", false);
            var p = key?.GetValue("InstallPath")?.ToString();
            if (!string.IsNullOrWhiteSpace(p)) return p;
        }
        catch { }
        return null;
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var root = SteamRoot();
        if (string.IsNullOrWhiteSpace(root)) yield break;

        yield return Path.Combine(root, "steamapps");

        string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); } catch { yield break; }

        // Each library has a "path" "...". Grab them all.
        foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase))
        {
            var p = m.Groups[1].Value.Replace("\\\\", "\\");
            var sa = Path.Combine(p, "steamapps");
            if (Directory.Exists(sa)) yield return sa;
        }
    }

    private static string? SteamAppInstallDir(string steamApps, string appId)
    {
        try
        {
            string acf = Path.Combine(steamApps, $"appmanifest_{appId}.acf");
            if (!File.Exists(acf)) return null;
            string text = File.ReadAllText(acf);

            // Skip entries that aren't fully installed (StateFlags bit 4 = fully installed).
            var state = Regex.Match(text, "\"StateFlags\"\\s*\"(\\d+)\"");
            if (state.Success && int.TryParse(state.Groups[1].Value, out int flags) && (flags & 4) == 0)
                return null;

            var dir = Regex.Match(text, "\"installdir\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (!dir.Success) return null;

            string common = Path.Combine(steamApps, "common", dir.Groups[1].Value);
            // GTA V Enhanced on Steam nests the game under a GTAV subfolder in some installs.
            if (IsGtaVFolder(common)) return common;
            string nested = Path.Combine(common, "GTAV");
            if (IsGtaVFolder(nested)) return nested;
            return common;
        }
        catch
        {
            return null;
        }
    }

    // ---- Epic --------------------------------------------------------------------------------

    private static IEnumerable<string> EpicInstalls()
    {
        string manifestsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");

        if (!Directory.Exists(manifestsDir)) yield break;

        string[] files;
        try { files = Directory.GetFiles(manifestsDir, "*.item"); }
        catch { yield break; }

        foreach (var file in files)
        {
            string? loc = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                var name = root.TryGetProperty("DisplayName", out var dn) ? dn.GetString() ?? "" : "";
                if (name.Contains("Grand Theft Auto", StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("InstallLocation", out var il))
                {
                    loc = il.GetString();
                }
            }
            catch { }
            if (!string.IsNullOrWhiteSpace(loc)) yield return loc!;
        }
    }

    private static bool PathEquals(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\'),
                Path.GetFullPath(b).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
