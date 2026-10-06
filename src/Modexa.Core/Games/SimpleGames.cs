using System.Diagnostics;
using System.IO;

namespace Modexa.Core.Games;

/// <summary>
/// Describes a "simple" game that Modexa prepares with a single Mod Runner step (everything except
/// GTA V, which has its own richer page). Covers folder validation, optional version detection, and
/// where the game is found via Steam.
/// </summary>
public sealed record SimpleGame(
    GameId Id,
    string[] ExeMarkers,       // relative paths that prove this is the game folder (any one)
    string? VersionExe,        // relative path to read a version from, or null (no version concept)
    bool VersionRequired,      // true = only the matching pack version may be installed
    string[] SteamAppIds)
{
    public bool IsFolder(string folder)
    {
        try { return ExeMarkers.Any(m => File.Exists(Path.Combine(folder, m))); }
        catch { return false; }
    }

    /// <summary>A short version token used to pick the matching pack folder (e.g. "1.0.7.0", "2.31").</summary>
    public string? DetectVersion(string folder)
    {
        if (VersionExe == null) return null;
        try
        {
            string exe = Path.Combine(folder, VersionExe);
            if (!File.Exists(exe)) return null;
            var fvi = FileVersionInfo.GetVersionInfo(exe);
            // Product version is what the community names pack folders after; fall back to file version.
            string? v = (fvi.ProductVersion ?? fvi.FileVersion)?.Trim();
            if (string.IsNullOrWhiteSpace(v)) return null;
            // Keep digits/dots only (strips things like "2.31a" -> "2.31").
            int end = 0;
            while (end < v.Length && (char.IsDigit(v[end]) || v[end] == '.')) end++;
            return end > 0 ? v[..end].TrimEnd('.') : v;
        }
        catch
        {
            return null;
        }
    }
}

public static class SimpleGames
{
    public static readonly IReadOnlyDictionary<GameId, SimpleGame> All = new Dictionary<GameId, SimpleGame>
    {
        [GameId.GtaSanAndreas] = new(GameId.GtaSanAndreas,
            new[] { "gta_sa.exe", "gta-sa.exe", "GTASA.exe" }, null, false,
            new[] { "12120" }),

        [GameId.GtaIV] = new(GameId.GtaIV,
            new[] { "GTAIV.exe", "PlayGTAIV.exe" }, "GTAIV.exe", VersionRequired: true,
            new[] { "12210" }),

        [GameId.RedDeadRedemption1] = new(GameId.RedDeadRedemption1,
            new[] { "RDR.exe", "RDR1.exe" }, null, false,
            new[] { "2668510" }),

        [GameId.RedDeadRedemption2] = new(GameId.RedDeadRedemption2,
            new[] { "RDR2.exe" }, "RDR2.exe", VersionRequired: false,
            new[] { "1174180" }),

        [GameId.Cyberpunk2077] = new(GameId.Cyberpunk2077,
            new[] { @"bin\x64\Cyberpunk2077.exe", "Cyberpunk2077.exe" }, @"bin\x64\Cyberpunk2077.exe",
            VersionRequired: true, new[] { "1091500" }),
    };

    public static bool Has(GameId id) => All.ContainsKey(id);
    public static SimpleGame Get(GameId id) => All[id];
}
