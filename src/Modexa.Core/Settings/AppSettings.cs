using System.IO;
using System.Text;
using System.Text.Json;

namespace Modexa.Core.Settings;

/// <summary>User preferences in %LocalAppData%\Modexa (language, remembered game folders, window).</summary>
public sealed class AppSettings
{
    /// <summary>"en" or "fa". Null means "ask on first run".</summary>
    public string? Language { get; set; }

    /// <summary>GTA V Legacy folder.</summary>
    public string? GtaVFolder { get; set; }
    /// <summary>GTA V Enhanced folder (a separate install from Legacy).</summary>
    public string? GtaVEnhancedFolder { get; set; }
    public string? GtaSaFolder { get; set; }

    /// <summary>Remembered folders for the single-step games, keyed by <see cref="Games.GameId"/> name.</summary>
    public Dictionary<string, string> GameFolders { get; set; } = new();

    public string? GetGtaVFolder(Games.GameEdition edition)
        => edition == Games.GameEdition.Enhanced ? GtaVEnhancedFolder : GtaVFolder;

    public void SetGtaVFolder(Games.GameEdition edition, string? folder)
    {
        if (edition == Games.GameEdition.Enhanced) GtaVEnhancedFolder = folder;
        else GtaVFolder = folder;
    }

    public string? GetSimpleFolder(Games.GameId game)
    {
        // San Andreas keeps its dedicated field for backward compatibility.
        if (game == Games.GameId.GtaSanAndreas) return GtaSaFolder;
        return GameFolders.TryGetValue(game.ToString(), out var f) ? f : null;
    }

    public void SetSimpleFolder(Games.GameId game, string? folder)
    {
        if (game == Games.GameId.GtaSanAndreas) { GtaSaFolder = folder; return; }
        if (string.IsNullOrWhiteSpace(folder)) GameFolders.Remove(game.ToString());
        else GameFolders[game.ToString()] = folder;
    }

    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 760;
    public bool WindowMaximized { get; set; }

    // ---- persistence -------------------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s != null) return s;
            }
        }
        catch
        {
            // Corrupt settings should never block startup; fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOpts);
            AppPaths.WriteAllTextAtomic(AppPaths.SettingsFile, json, Encoding.UTF8);
        }
        catch
        {
            // Best-effort; a failed settings write must not crash the app.
        }
    }
}
