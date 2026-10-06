using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modexa.Core.Games;

namespace Modexa.Core.Prepare;

/// <summary>The kind of prerequisite a bundle installs.</summary>
public enum PrepareStep
{
    ModRunner,   // ASI loader + ScriptHookV (+ .NET/Lua runtimes) + OpenRPF on Enhanced -> game root
    GameConfig,  // limit adjusters -> game root; gameconfig.xml -> mods\update\update.rpf\common\data
    Menu,        // trainer/menu (Menyoo) -> game root
    SanAndreas   // SA: ASI loader + CLEO + Mod Loader -> game root
}

/// <summary>Kept for manifest compatibility (v1). New bundles use <see cref="BundleEntry.Maps"/>.</summary>
public enum InstallTarget
{
    GameRoot,
    ModsUpdateCommonData
}

/// <summary>Copies everything under <see cref="From"/> inside the archive to <see cref="To"/> in the game folder.</summary>
public sealed class BundleMap
{
    /// <summary>Archive path. Ending in '/' = a folder (its contents are copied); otherwise a single file.</summary>
    [JsonPropertyName("from")] public string From { get; set; } = "";

    /// <summary>Destination relative to the game root ("" = root). For a single file, the target file path.</summary>
    [JsonPropertyName("to")] public string To { get; set; } = "";
}

/// <summary>
/// A gameconfig.xml pack: <see cref="Folder"/> contains one sub-folder per variant (traffic density,
/// "more mods" vs "less mods"…), each holding a gameconfig.xml. The user picks one.
/// </summary>
public sealed class GameConfigSpec
{
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";
    [JsonPropertyName("default")] public string? Default { get; set; }
    /// <summary>Game build the configs were made for (a different build gets a warning, not a block).</summary>
    [JsonPropertyName("forBuild")] public int? ForBuild { get; set; }
}

/// <summary>One downloadable bundle (.zip or .rar) and how to install it.</summary>
public sealed class BundleEntry
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }

    /// <summary>Archive password, if the pack is protected (WTMod packs use the site name).</summary>
    [JsonPropertyName("password")] public string? Password { get; set; }

    [JsonPropertyName("target")] public InstallTarget Target { get; set; } = InstallTarget.GameRoot;

    /// <summary>Optional: match a specific game build; null = any build of the edition.</summary>
    [JsonPropertyName("build")] public int? Build { get; set; }

    /// <summary>Optional: "Legacy" or "Enhanced"; null = any edition.</summary>
    [JsonPropertyName("edition")] public string? Edition { get; set; }

    /// <summary>File mappings. Empty = extract the whole archive into the game root (v1 behaviour).</summary>
    [JsonPropertyName("maps")] public List<BundleMap> Maps { get; set; } = new();

    /// <summary>Folders to create in the game root (e.g. "mods").</summary>
    [JsonPropertyName("createDirs")] public List<string> CreateDirs { get; set; } = new();

    /// <summary>Optional gameconfig.xml variants to install into mods\update\update.rpf.</summary>
    [JsonPropertyName("gameConfig")] public GameConfigSpec? GameConfig { get; set; }

    /// <summary>Also install Modexa.asi (the paid-mod entitlement gate) — set on Mod Runner bundles.</summary>
    [JsonPropertyName("modexaAsi")] public bool ModexaAsi { get; set; }

    /// <summary>Also install the bundled OpenIV mods-folder enabler (Legacy GTA V only).</summary>
    [JsonPropertyName("openIv")] public bool OpenIv { get; set; }

    /// <summary>
    /// When a map's <c>from</c> contains a <c>*</c> segment matched by the game's version, refuse to
    /// install a non-matching version (used for CP2077 / GTA IV / RDR2 where the runner is per-build).
    /// </summary>
    [JsonPropertyName("requireVersionMatch")] public bool RequireVersionMatch { get; set; }
}

/// <summary>A step with one or more version-specific bundle candidates.</summary>
public sealed class StepEntry
{
    [JsonPropertyName("step")] public PrepareStep Step { get; set; }

    /// <summary>Which game this step is for; null applies to both GTA V editions (back-compat).</summary>
    [JsonPropertyName("game")] public GameId? Game { get; set; }

    [JsonPropertyName("bundles")] public List<BundleEntry> Bundles { get; set; } = new();
}

/// <summary>
/// Maps each prepare step to version/edition-specific download bundles. A built-in default ships
/// with the app (the free WTMod packs) and can be overridden by a remote JSON, so new game builds or
/// new pack layouts are supported without an app update.
/// </summary>
public sealed class PrepareManifest
{
    /// <summary>Manifests older than this layout are ignored in favour of the built-in default.</summary>
    public const int CurrentVersion = 2;

    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
    [JsonPropertyName("steps")] public List<StepEntry> Steps { get; set; } = new();

    private const string RemoteUrl = Remote.Endpoints.PrepareManifest;

    public static PrepareManifest Empty() => new();

    // ---- built-in default: the free WTMod.com packs ------------------------------------------

    private const string ModRunnerUrl = "https://dl.wtmod.com/mods/Gta-V/Mod-Runner-Pack-GtaV-WTMod.com.rar";
    private const string MenyooUrl = "https://dl.wtmod.com/mods/Gta-V/Menyoo-Trainer-GTAV-WTMod.com.rar";
    private const string GameConfigUrl = "https://dl.wtmod.com/mods/Gta-V/Game-Config-Pack-GtaV-WTMod.com.rar";

    private const string RunnerFolder = "Mod Runner Pack GtaV/Mod Runner For GtaV Legacy v3889 & Enhanced v1158.13 and older versions/";
    private const string GameConfigRoot = "Game Config For Add-on Mods Gta V/";
    private const string WtModPassword = "WTMod.com"; // standard password of the WTMod.com archives

    // Mod Runner packs for the other supported games (contents copy into the game root).
    private const string SaRunnerUrl = "https://dl.wtmod.com/mods/Gta-Sa/Mod-Runner-Gta-sa-WTMod.com.rar";
    private const string IvRunnerUrl = "https://dl.wtmod.com/mods/Gta-IV/Mod-Runner-Pack-GTA-IV-WTMod.com.rar";
    private const string Rdr1RunnerUrl = "https://dl.wtmod.com/mods/RDR1/Mod-Runner-RDR1-v1.3-WTMod.com.rar";
    private const string Rdr2RunnerUrl = "https://dl.wtmod.com/mods/RDR2/Mod-Runner-RDR2-WTMod.com.rar";
    private const string Cp2077RunnerUrl = "https://dl.wtmod.com/mods/CP2077/Cyberpunk-2077-Mod-Runner-Pack-WTMod.com.rar";

    private static StepEntry SimpleRunner(GameId game, string url, string from, bool requireVersion = false) => new()
    {
        Step = PrepareStep.ModRunner,
        Game = game,
        Bundles = { new BundleEntry { Url = url, Password = WtModPassword, RequireVersionMatch = requireVersion, Maps = { new BundleMap { From = from, To = "" } } } }
    };

    public static PrepareManifest Default() => new()
    {
        Version = CurrentVersion,
        Steps =
        {
            new StepEntry
            {
                Step = PrepareStep.ModRunner,
                Bundles =
                {
                    new BundleEntry
                    {
                        Url = ModRunnerUrl, Password = WtModPassword, Edition = "Legacy", ModexaAsi = true, OpenIv = true, CreateDirs = { "mods" },
                        Maps = { new BundleMap { From = RunnerFolder, To = "" } }
                    },
                    new BundleEntry
                    {
                        // Enhanced also needs OpenRPF.asi so the game reads the mods folder. The pack's
                        // GTA5.exe stub only exists to make OpenIV recognise Enhanced — Modexa doesn't need it.
                        Url = ModRunnerUrl, Password = WtModPassword, Edition = "Enhanced", ModexaAsi = true, CreateDirs = { "mods" },
                        Maps =
                        {
                            new BundleMap { From = RunnerFolder, To = "" },
                            new BundleMap { From = "Mod Runner Pack GtaV/Enhanced OpenIV Fix/OpenRPF.asi", To = "OpenRPF.asi" }
                        }
                    }
                }
            },
            new StepEntry
            {
                Step = PrepareStep.GameConfig,
                Bundles =
                {
                    new BundleEntry
                    {
                        Url = GameConfigUrl, Password = WtModPassword, Edition = "Legacy",
                        Maps = { new BundleMap { From = GameConfigRoot + "GTA V Legacy/1/", To = "" } }, // heap + packfile adjusters
                        GameConfig = new GameConfigSpec
                        {
                            Folder = GameConfigRoot + "GTA V Legacy/2/Gta Config v37 for v 1.0.3889",
                            Default = "For More Mods/Stock Traffic (Means Gta base)",
                            ForBuild = 3889
                        }
                    },
                    new BundleEntry
                    {
                        Url = GameConfigUrl, Password = WtModPassword, Edition = "Enhanced",
                        GameConfig = new GameConfigSpec
                        {
                            Folder = GameConfigRoot + "GTA V Enhanced/Gta Config Enhanced v3 for v 1.0.1158",
                            Default = "Stock Traffic (Means Gta base)",
                            ForBuild = 1158
                        }
                    }
                }
            },
            new StepEntry
            {
                Step = PrepareStep.Menu,
                Bundles =
                {
                    new BundleEntry
                    {
                        // Menyoo is for both GTA V editions.
                        Url = MenyooUrl, Password = WtModPassword,
                        Maps =
                        {
                            new BundleMap { From = "Menyoo 2.4.3/Menyoo.asi", To = "Menyoo.asi" },
                            new BundleMap { From = "Menyoo 2.4.3/menyooStuff/", To = "menyooStuff" }
                        }
                    }
                }
            },

            // ---- Other supported games (single Mod Runner step each) ----
            SimpleRunner(GameId.GtaSanAndreas, SaRunnerUrl, "Mod Runner Gta sa/"),
            SimpleRunner(GameId.RedDeadRedemption1, Rdr1RunnerUrl, "Mod Runner RDR1 */"),
            SimpleRunner(GameId.GtaIV, IvRunnerUrl, "Mod Runner Gta IV */", requireVersion: true),
            SimpleRunner(GameId.RedDeadRedemption2, Rdr2RunnerUrl, "Mod Runner RDR2/*/", requireVersion: true),
            SimpleRunner(GameId.Cyberpunk2077, Cp2077RunnerUrl, "Mod Runner Pack Cyberpunk 2077 Core Mods/*/", requireVersion: true),
        }
    };

    private static bool AppliesToGtaV(GameId g) => g is GameId.GtaV or GameId.GtaVEnhanced;

    /// <summary>Finds the best bundle for a game/step given the detected edition/build, or null.</summary>
    public BundleEntry? Resolve(GameId game, PrepareStep step, GameEdition edition, int build)
    {
        var entry = Steps.FirstOrDefault(s => s.Step == step
            && (s.Game == game || (s.Game == null && AppliesToGtaV(game))));
        if (entry == null) return null;

        bool EditionMatches(BundleEntry b) =>
            string.IsNullOrWhiteSpace(b.Edition)
            || string.Equals(b.Edition, edition.ToString(), StringComparison.OrdinalIgnoreCase);

        // Exact build + edition, then edition-only. Never fall back to the other edition's bundle.
        return entry.Bundles.FirstOrDefault(b => EditionMatches(b) && b.Build == build)
            ?? entry.Bundles.FirstOrDefault(b => EditionMatches(b) && b.Build == null)
            ?? entry.Bundles.FirstOrDefault(EditionMatches);
    }

    /// <summary>True if the manifest has any bundle for this game/step.</summary>
    public bool HasStep(GameId game, PrepareStep step)
        => Steps.Any(s => s.Step == step && (s.Game == game || (s.Game == null && AppliesToGtaV(game))));

    /// <summary>The cached remote manifest if present and current, else the built-in default.</summary>
    public static PrepareManifest LoadLocal()
    {
        try
        {
            string path = Path.Combine(AppPaths.CacheDir, "prepare-manifest.json");
            if (File.Exists(path))
            {
                var m = JsonSerializer.Deserialize<PrepareManifest>(File.ReadAllText(path));
                if (m is { Version: >= CurrentVersion } && m.Steps.Count > 0) return m;
            }
        }
        catch { }
        return Default();
    }

    /// <summary>Fetches the remote manifest (if hosted) and caches it; otherwise the local one.</summary>
    public static async Task<PrepareManifest> RefreshAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Modexa/1.0");
            string json = await http.GetStringAsync(RemoteUrl).ConfigureAwait(false);
            var m = JsonSerializer.Deserialize<PrepareManifest>(json);
            if (m is { Version: >= CurrentVersion } && m.Steps.Count > 0)
            {
                AppPaths.EnsureDir(AppPaths.CacheDir);
                File.WriteAllText(Path.Combine(AppPaths.CacheDir, "prepare-manifest.json"), json);
                return m;
            }
        }
        catch { }
        return LoadLocal();
    }
}
