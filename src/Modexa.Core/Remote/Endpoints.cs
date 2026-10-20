using Modexa.Core.Licensing;

namespace Modexa.Core.Remote;

/// <summary>
/// Every server URL the app talks to, in one place.
///
///   * The engine module is a plain per-tier download (static hosting). It carries no secret and is
///     integrity-pinned by SHA-256 (see <see cref="Engine.EngineHashes"/>), so a tampered or wrong
///     file is rejected. A patched app could fetch it, but that only unlocks installing *free*
///     third-party mods — it cannot decrypt any paid .mxa.
///   * The real paid-mod protection is <see cref="ContentKey"/>: it must be a LICENSE-GATED endpoint
///     that releases a product's decryption key only after verifying the buyer's license. See
///     docs/DEPLOYMENT.md and secret/SERVER.md.
/// </summary>
public static class Endpoints
{
    /// <summary>FSLM license API (activate / verify).</summary>
    public const string LicenseApi = "https://rockstargame.ir/";

    /// <summary>Per-tier engine download (static). The app picks by the license tier.</summary>
    public const string EnginePlus = "https://dl.wtmod.com/mods/apps/modexa/plus/Modexa.Support.dll";
    public const string EnginePro = "https://dl.wtmod.com/mods/apps/modexa/pro/Modexa.Support.dll";

    public static string EngineFor(LicenseTier tier) => tier == LicenseTier.Pro ? EnginePro : EnginePlus;

    /// <summary>License-gated per-product content key (POST) used to open a purchased .mxa.</summary>
    public const string ContentKey = "https://rockstargame.ir/wp-json/modexa/key";

    /// <summary>Remote prepare-bundle manifest (GET, optional; a built-in default is used otherwise).</summary>
    public const string PrepareManifest = "https://rockstargame.ir/modexa-prepare.json";

    /// <summary>Where users buy licenses.</summary>
    public const string Shop = "https://rockstargame.ir/shop/";

    /// <summary>Where players get game updates (suggested when a game is too old for the GameConfig pack).</summary>
    public const string GameUpdates = "https://gameever.ir/";

    /// <summary>Product pages promoted on the GTA V pages.</summary>
    public const string BackupManagerPro = "https://rockstargame.ir/shop/games/virtual-products/applications/gta-v-backup-manager-pro/";
    public const string MenyooFa = "https://rockstargame.ir/shop/games/virtual-products/applications/menyoofa-persian-menyoo-%d9%85%d9%86%db%8c%d9%88-%d9%81%d8%a7%d8%b1%d8%b3%db%8c/";
}
