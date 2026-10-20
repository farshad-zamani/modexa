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
}

/// <summary>Pro-only capabilities (present only in the Pro build of the module).</summary>
public interface IProModEngine : IModEngine
{
    /// <summary>Lists what a mod the user gave holds (nothing is installed).</summary>
    ModPlan Plan(string path, string workDir, IProgress<int>? progress = null);

    /// <summary>Installs one listed item of kind <see cref="ModPlanKind.Mod"/>.</summary>
    ModInstallRecord Install(ModPlan plan, ModPlanItem item, string gameFolder, string game);

    /// <summary>
    /// Installs an item of kind <see cref="ModPlanKind.Manual"/> into the place the user gives (as the
    /// mod's author describes it). Throws <see cref="ModInstallException"/> when that place is not certain.
    /// </summary>
    ModInstallRecord InstallAt(ModPlan plan, ModPlanItem item, string place, string gameFolder, string game);
}

public static class ModEngineContract
{
    public const int ApiVersion = 4;
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
    /// <summary>Installing it needs the editable mods\update\update.rpf (made first, with progress).</summary>
    public bool NeedsModsArchive { get; init; }
    /// <summary>The engine's own data about the item.</summary>
    public object? Tag { get; init; }
}

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
