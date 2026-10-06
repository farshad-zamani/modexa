namespace Modexa.Core.Games;

/// <summary>Stable identifiers for every title shown on the Modexa home grid.</summary>
public enum GameId
{
    GtaV,           // GTA V Legacy (GTA5.exe, gen8)
    GtaVEnhanced,   // GTA V Enhanced (GTA5_Enhanced.exe, gen9 / 2025+) — separate builds, bundles and mods
    GtaSanAndreas,
    GtaIV,
    RedDeadRedemption1,
    RedDeadRedemption2,
    Cyberpunk2077,
    GtaTrilogy,
    AssettoCorsa,
    EuroTruckSimulator2,
    ForzaHorizon5,
    BeamNgDrive
}

/// <summary>Static metadata about a supported game (name, art key, whether it's active yet).</summary>
public sealed record GameInfo(
    GameId Id,
    string DisplayName,
    string LogoAsset,
    bool Supported);

/// <summary>
/// The catalog driving the home grid. The two GTA V editions are separate titles: their build
/// numbers, prerequisites and mods are not interchangeable. The rest of the active titles install a
/// Mod Runner matched to their version; the remaining ones render as "Coming soon".
/// </summary>
public static class GameCatalog
{
    public static readonly IReadOnlyList<GameInfo> All = new List<GameInfo>
    {
        new(GameId.GtaV,                "Grand Theft Auto V Legacy",     "games/gtav.png",      true),
        new(GameId.GtaVEnhanced,        "Grand Theft Auto V Enhanced",   "games/gtave.png",     true),
        new(GameId.GtaSanAndreas,       "Grand Theft Auto San Andreas",  "games/gtasa.png",     true),
        new(GameId.GtaIV,               "Grand Theft Auto IV",           "games/gtaiv.png",     true),
        new(GameId.RedDeadRedemption1,  "Red Dead Redemption",           "games/rdr1.png",      true),
        new(GameId.RedDeadRedemption2,  "Red Dead Redemption 2",         "games/rdr2.png",      true),
        new(GameId.Cyberpunk2077,       "Cyberpunk 2077",                "games/cyberpunk.png", true),
        new(GameId.GtaTrilogy,          "Grand Theft Auto The Trilogy",  "games/trilogy.png",   false),
        new(GameId.AssettoCorsa,        "Assetto Corsa",                 "games/assetto.png",   false),
        new(GameId.EuroTruckSimulator2, "Euro Truck Simulator 2",        "games/ets2.png",      false),
        new(GameId.ForzaHorizon5,       "Forza Horizon 5",               "games/forza.png",     false),
        new(GameId.BeamNgDrive,         "BeamNG.drive",                  "games/beamng.png",    false),
    };

    public static GameInfo Get(GameId id) => All.First(g => g.Id == id);

    /// <summary>GTA V catalog entry for an edition.</summary>
    public static GameId ForEdition(GameEdition edition)
        => edition == GameEdition.Enhanced ? GameId.GtaVEnhanced : GameId.GtaV;
}
