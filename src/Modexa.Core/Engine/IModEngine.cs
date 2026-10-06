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
    ModInstallRecord InstallPackage(string mxaPath, byte[] contentKey, string gameFolder, string game, string workDir);

    /// <summary>Reverts an install: restores originals, deletes created files, unregisters add-ons.</summary>
    void Uninstall(ModInstallRecord record);

    /// <summary>True when the game has an OPEN mods\update\update.rpf (add-ons can be registered).</summary>
    bool IsAddonReady(string gameFolder);
}

/// <summary>Pro-only capabilities (present only in the Pro build of the module).</summary>
public interface IProModEngine : IModEngine
{
    /// <summary>Installs a third-party mod: .oiv/.oivs, an add-on dlc.rpf, a .zip or a folder.</summary>
    ModInstallRecord InstallRaw(string path, string gameFolder, string game, string workDir);
}

public static class ModEngineContract
{
    public const int ApiVersion = 1;
}

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
