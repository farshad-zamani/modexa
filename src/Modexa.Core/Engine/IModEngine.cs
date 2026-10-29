using Modexa.Core.Install;
using Modexa.Core.Licensing;

namespace Modexa.Core.Engine;

/// <summary>
/// Contract of the license-gated engine module (Modexa.Support.dll). The public app contains NO
/// package crypto, RPF writer, OIV importer or add-on installer — those live only in the module,
/// which the server hands out after verifying a license. A patched Free exe therefore has nothing
/// to unlock: without a genuine license there is no code that can install paid content.
///
/// The server builds the module in two flavors from the same source:
///   * Plus — installs the client's own .mxa packages (<see cref="IModEngine"/> only).
///   * Pro  — additionally installs arbitrary third-party mods (<see cref="IProModEngine"/>).
/// A Plus module physically lacks the Pro code, so flipping a tier flag in the app gains nothing.
/// </summary>
public interface IModEngine
{
    /// <summary>Must equal <see cref="ModEngineContract.ApiVersion"/>; older modules are re-downloaded.</summary>
    int ApiVersion { get; }

    /// <summary><see cref="LicenseTier.Plus"/> or <see cref="LicenseTier.Pro"/>.</summary>
    LicenseTier Flavor { get; }

    /// <summary>
    /// Decrypts a .mxa with its server-issued content key and installs it (loose files, add-on pack
    /// + dlclist registration, or embedded OIV). Throws <see cref="ModInstallException"/>.
    /// </summary>
    ModInstallRecord InstallPackage(string mxaPath, byte[] contentKey, string gameFolder, string game, string workDir,
        IProgress<EngineProgress>? progress = null);

    /// <summary>Reverts an install: restores originals, deletes created files, unregisters add-ons.</summary>
    void Uninstall(ModInstallRecord record);

    /// <summary>True when the game has an OPEN mods\update\update.rpf (add-ons can be registered).</summary>
    bool IsAddonReady(string gameFolder);

    /// <summary>
    /// Modexa is about to change a game folder (installing, removing, preparing). Only the outermost of
    /// nested changes reaches the engine (see <see cref="Install.FolderChanges"/>).
    /// </summary>
    void BeginChanges(string gameFolder);

    /// <summary>Modexa finished changing a game folder.</summary>
    void EndChanges(string gameFolder);

    /// <summary>An installed mod is about to be removed.</summary>
    void Removing(ModInstallRecord record);

    /// <summary>Checks the licenses of the installed purchased mods with the shop: one notice per product.</summary>
    IReadOnlyList<ProductNotice> CheckLicenses(LicenseCheckRequest request);
}

/// <summary>Pro-only capabilities (present only in the Pro build of the module).</summary>
public interface IProModEngine : IModEngine
{
    /// <summary>Lists what a mod the user gave holds (nothing is installed).</summary>
    /// <param name="askPassword">Asked for a protected archive's password (its file name → password, null = cancel).</param>
    ModPlan Plan(string path, string workDir, IProgress<int>? progress = null, Func<string, string?>? askPassword = null);

    /// <summary>Installs one listed item of kind <see cref="ModPlanKind.Mod"/>.</summary>
    ModInstallRecord Install(ModPlan plan, ModPlanItem item, string gameFolder, string game);

    /// <summary>
    /// Installs an item into the place the user gives (as the mod's author describes it): an item of
    /// kind <see cref="ModPlanKind.Manual"/>, or a <see cref="ModPlanKind.Mod"/> the user places
    /// elsewhere. Throws <see cref="ModInstallException"/> when that place is not certain.
    /// </summary>
    ModInstallRecord InstallAt(ModPlan plan, ModPlanItem item, string place, string gameFolder, string game);
}

public static class ModEngineContract
{
    public const int ApiVersion = 6;
}

/// <summary>One stored license key (a purchased mod's key names its product; null for a Pro key).</summary>
public sealed record LicenseKeyRef(string Key, string? ProductId);

/// <summary>What <see cref="IModEngine.CheckLicenses"/> checks, and how to reach the shop.</summary>
public sealed class LicenseCheckRequest
{
    public IReadOnlyList<LicenseKeyRef> Keys { get; init; } = Array.Empty<LicenseKeyRef>();
    public string ApiUrl { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public string DeviceId { get; init; } = "";
}

public enum ProductLicenseState
{
    /// <summary>A license covers it.</summary>
    Active,
    /// <summary>Covered, but its time ends within a few days.</summary>
    EndsSoon,
    /// <summary>No license covers it any more: it is off in the game until a new license is entered.</summary>
    Ended,
    /// <summary>Ended, and the time to enter a new license has passed: remove it from the game.</summary>
    RemoveNow,
    /// <summary>The shop could not be reached: nothing changed.</summary>
    Unknown
}

/// <summary>The license state of one purchased mod (product).</summary>
public sealed class ProductNotice
{
    public string ProductId { get; init; } = "";
    public ProductLicenseState State { get; init; }
    /// <summary>When the license time ends (null = no end, or not known).</summary>
    public DateTime? EndsUtc { get; init; }
    /// <summary>Ended: until when a new license can be entered before the mod is removed.</summary>
    public DateTime? RemoveAfterUtc { get; init; }
    /// <summary>Stored keys of this product the shop no longer accepts (safe to forget).</summary>
    public IReadOnlyList<string> EndedKeys { get; init; } = Array.Empty<string>();
}

/// <summary>What a listed item is, for the app.</summary>
public enum ModPlanKind
{
    /// <summary>An OpenIV package (installed by the app's own package installer).</summary>
    Package,
    /// <summary>A Modexa package (.mxa).</summary>
    Licensed,
    /// <summary>A mod the engine installs.</summary>
    Mod,
    /// <summary>Files whose place the user gives by hand.</summary>
    Manual,
    /// <summary>Shown only (with the reason in <see cref="ModPlanItem.Detail"/>).</summary>
    Refused
}

/// <summary>One item found in what the user gave.</summary>
public sealed class ModPlanItem
{
    public ModPlanKind Kind { get; init; }
    public string Title { get; init; } = "";
    /// <summary>Short label shown next to the title.</summary>
    public string? Badge { get; init; }
    /// <summary>One line about it (what goes where, or why it is refused).</summary>
    public string? Detail { get; init; }
    /// <summary>Where it sits in what the user gave ("…\Optional Add-Ons\4. Parallax Roads").</summary>
    public string? Location { get; init; }
    /// <summary>Package / .mxa file, for the app to install.</summary>
    public string? Path { get; init; }
    /// <summary>What goes where ("AddonSpawner.asi" → "game folder"), one line each.</summary>
    public IReadOnlyList<ModPlanTarget> Targets { get; init; } = Array.Empty<ModPlanTarget>();
    /// <summary>The user may give another place for it (a small edit button).</summary>
    public bool CanPlace { get; init; }
    /// <summary>Installing it needs the editable mods\update\update.rpf (made first, with progress).</summary>
    public bool NeedsModsArchive { get; init; }
    /// <summary>The engine's own data about the item.</summary>
    public object? Tag { get; init; }
}

/// <summary>One line of a listed item: a file or folder, and where it goes.</summary>
public sealed record ModPlanTarget(string Name, string Destination);

/// <summary>Everything found in what the user gave.</summary>
public sealed class ModPlan
{
    public List<ModPlanItem> Items { get; init; } = new();
}

public enum EngineStage { Unpacking, Installing }

/// <summary>Progress of a package install: the stage, bytes done of total, and what is being worked on.</summary>
public readonly record struct EngineProgress(EngineStage Stage, long Done, long Total, string? Current = null);

/// <summary>Install failure with a stable code the app maps to a localized message.</summary>
public sealed class ModInstallException : Exception
{
    public const string NotPrepared = "not_prepared";
    public const string Unsupported = "unsupported";
    public const string BadPackage = "bad_package";

    public string Code { get; }

    public ModInstallException(string code, string message, Exception? inner = null) : base(message, inner)
        => Code = code;
}
