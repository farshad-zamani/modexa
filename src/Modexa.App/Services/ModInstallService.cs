using System.Diagnostics;
using System.IO;
using System.Windows;
using Modexa.App.Views;
using Modexa.Core;
using Modexa.Core.Diagnostics;
using Modexa.Core.Engine;
using Modexa.Core.Format;
using Modexa.Core.Games;
using Modexa.Core.I18n;
using Modexa.Core.Install;
using Modexa.Core.Licensing;
using Modexa.Core.Rpf;

namespace Modexa.App.Services;

public sealed record ModResult(bool Success, string Message, ModInstallRecord? Record = null, bool Cancelled = false);

/// <summary>Progress of an install: a localized phase key and, when known, a percentage.</summary>
public readonly record struct InstallPhase(string Key, int? Percent = null)
{
    /// <summary>Phase key that tells the page to hide its busy overlay (a dialog is about to show).</summary>
    public const string Hide = "__hide";

    public static implicit operator InstallPhase(string key) => new(key);
}

/// <summary>The game a mod is installed into (always chosen on that game's page).</summary>
public sealed record GameTarget(GameId Id, string Folder, string Title, GameEdition Edition)
{
    public bool IsGtaV => Id is GameId.GtaV or GameId.GtaVEnhanced;

    /// <summary>The game key written into .mxa headers by the packer.</summary>
    public string MxaKey => Id switch
    {
        GameId.GtaV or GameId.GtaVEnhanced => "GtaV",
        GameId.GtaSanAndreas => "GtaSa",
        GameId.GtaIV => "GtaIV",
        GameId.RedDeadRedemption1 => "Rdr1",
        GameId.RedDeadRedemption2 => "Rdr2",
        GameId.Cyberpunk2077 => "Cp2077",
        _ => Id.ToString()
    };

    public bool Accepts(MxaInfo info)
        => string.Equals(info.Game, MxaKey, StringComparison.OrdinalIgnoreCase)
           || string.Equals(info.Game, Id.ToString(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Every install and uninstall, driven from a game's page:
///
///   .oiv / .oivs (all tiers)  -> public OIV installer, fully recorded for an exact uninstall.
///   .mxa (licensed)           -> license for THIS product -> engine module -> content key -> engine installs.
///   .rpf / .zip / folder (Pro)-> Pro engine.
///   uninstall (all tiers)     -> <see cref="ModUninstaller"/> plays the install record back in reverse.
///
/// UI prompts run on the UI thread; all file work runs on worker threads.
/// </summary>
public static class ModInstallService
{
    public static readonly string[] OivExtensions = { ".oiv", ".oivs" };
    public static readonly string[] RawExtensions = { ".rpf", ".zip" };

    public static bool IsInstallable(string path, bool gtaV)
        => path.EndsWith(MxaFile.Extension, StringComparison.OrdinalIgnoreCase)
           || (gtaV && (OivExtensions.Concat(RawExtensions).Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase)) || Directory.Exists(path)));

    /// <summary>Routes a file to the right installer for <paramref name="target"/>.</summary>
    public static Task<ModResult> InstallAsync(string path, GameTarget target, Window owner, IProgress<InstallPhase>? phase)
    {
        if (path.EndsWith(MxaFile.Extension, StringComparison.OrdinalIgnoreCase))
            return InstallPackageAsync(path, target, owner, phase);
        if (!target.IsGtaV)
            return Task.FromResult(Fail("Mods_UnsupportedHere"));
        if (OivInstaller.IsPackageFile(path))
            return InstallOivAsync(path, target, phase);
        return InstallRawAsync(path, target, owner, phase);
    }

    // ---- OIV (Free) ------------------------------------------------------------------------------

    public static async Task<ModResult> InstallOivAsync(string path, GameTarget target, IProgress<InstallPhase>? phase)
    {
        if (await Task.Run(() => GameProcess.IsRunning(target.Folder))) return Fail("Mods_GameRunning");

        phase?.Report("Oiv_Reading");
        string dir;
        OivPackageInfo info;
        try
        {
            dir = await Task.Run(() => OivInstaller.ExtractToTemp(path));
        }
        catch (Exception ex) when (ex is ModInstallException or IOException or UnauthorizedAccessException)
        {
            return new ModResult(false, Loc.Instance.Format("Oiv_Invalid", ex.Message));
        }

        try
        {
            try { info = await Task.Run(() => OivInstaller.Inspect(dir)); }
            catch (ModInstallException ex) { return new ModResult(false, Loc.Instance.Format("Oiv_Invalid", ex.Message)); }

            string name = string.IsNullOrWhiteSpace(info.Name) ? Path.GetFileNameWithoutExtension(path) : info.Name;

            // Confirm, showing what the package says about itself.
            phase?.Report(new InstallPhase(InstallPhase.Hide)); // the confirmation dialog needs the page
            var lines = new List<string> { Loc.Instance.Format("Oiv_Confirm", name, target.Title) };
            if (!string.IsNullOrWhiteSpace(info.Version)) lines.Add(Loc.Instance.Format("Oiv_Version", info.Version));
            if (!string.IsNullOrWhiteSpace(info.Author)) lines.Add(Loc.Instance.Format("Oiv_Author", info.Author));
            if (!string.IsNullOrWhiteSpace(info.Description))
                lines.Add("\n" + (info.Description!.Length > 400 ? info.Description[..400] + "…" : info.Description));
            if (!DialogWindow.Prompt(string.Join("\n", lines), Loc.Instance["Common_Install"], Loc.Instance["Common_Cancel"], DialogKind.Question))
                return new ModResult(false, "", Cancelled: true);

            // Same package already installed here? Offer a clean reinstall.
            var existing = InstalledModsStore.ForFolder(target.Folder)
                .FirstOrDefault(r => r.ModType == MxaModType.Oiv && r.ProductId == null
                                     && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (!DialogWindow.Confirm(Loc.Instance.Format("Oiv_Reinstall", name))) return new ModResult(false, "", Cancelled: true);
                phase?.Report("Mods_Uninstalling");
                var un = await Task.Run(() => ModUninstaller.Uninstall(existing));
                if (!un.Success) return new ModResult(false, UninstallMessage(un));
            }

            // Game archives the package edits are copied to mods\ first — with visible progress.
            foreach (var archive in info.Archives)
            {
                bool inGame = await Task.Run(() => File.Exists(ModsArchives.GamePath(target.Folder, archive)));
                if (!inGame) continue; // created by the package itself (createIfNotExist)
                string? error = await EnsureModsArchiveAsync(target.Folder, archive, phase);
                if (error != null) return new ModResult(false, error);
            }

            phase?.Report("Dl_Installing");
            var record = NewRecord(target, path, name);
            await Task.Run(() =>
            {
                OivInstaller.Apply(dir, target.Folder, record);
                InstalledModsStore.Add(record);
            });
            return Success(record);
        }
        catch (ModInstallException ex)
        {
            Log.Error("OIV install", ex);
            return new ModResult(false, InstallMessage(ex));
        }
        catch (ModsArchiveException ex)
        {
            Log.Error("OIV install", ex);
            return new ModResult(false, Loc.Instance.Format("Mods_NotPrepared", ModsArchiveMessages.For(ex)));
        }
        catch (Exception ex)
        {
            Log.Error("OIV install", ex);
            return new ModResult(false, $"{Loc.Instance["Dl_Failed"]}\n{ex.Message}");
        }
        finally
        {
            string d = dir;
            _ = Task.Run(() => OivInstaller.TryDeleteDir(d));
        }
    }

    // ---- licensed .mxa ---------------------------------------------------------------------------

    public static async Task<ModResult> InstallPackageAsync(string mxaPath, GameTarget target, Window owner, IProgress<InstallPhase>? phase)
    {
        if (!await Task.Run(() => MxaFile.IsMxaFile(mxaPath)))
            return Fail("Mxa_NotPackage");

        MxaInfo info;
        try { info = await Task.Run(() => MxaFile.ReadInfo(mxaPath)); }
        catch { return Fail("Mxa_Corrupt"); }

        // The package must be for this game (and this GTA V edition, when it names one).
        if (!target.Accepts(info))
            return new ModResult(false, Loc.Instance.Format("Mxa_WrongGame", info.Game, target.Title));
        if (target.IsGtaV && Enum.TryParse<GameEdition>(info.Edition, true, out var ed) && ed != GameEdition.Unknown && ed != target.Edition)
            return new ModResult(false, Loc.Instance.Format("Mxa_WrongEdition", ed == GameEdition.Enhanced ? "GTA V Enhanced" : "GTA V Legacy"));
        if (await Task.Run(() => GameProcess.IsRunning(target.Folder))) return Fail("Mods_GameRunning");

        // 1) A license for this product. Each paid mod is its own product with its own key.
        var license = LicenseStore.ForProduct(info.ProductId);
        if (license == null)
        {
            var dlg = new LicenseWindow(LicensePurpose.Product, prompt: info.Name, productId: info.ProductId) { Owner = owner };
            if (dlg.ShowDialog() != true)
                return Fail("License_Required");
            ThemeService.Raise(dlg.ActivatedTier); // first license: Free -> Plus
            license = LicenseStore.ForProduct(info.ProductId);
            if (license == null) return Fail("License_Required");
        }

        try
        {
            // 2) The engine (Plus build is enough; a Pro user already has the superset).
            phase?.Report("Engine_Downloading");
            string engineKey = LicenseStore.Best()?.Key ?? license.Key;
            var engine = await Task.Run(() => EngineModule.EnsureAsync(engineKey, LicenseTier.Plus));

            // 3) The product's content key — the server only issues it if the license covers the product.
            phase?.Report("Dl_Verifying");
            byte[] contentKey = await Task.Run(() => EngineModule.GetContentKeyAsync(info.ProductId, license.Key));

            // 4) Add-ons / OIVs edit mods\update\update.rpf: create it now, with visible progress.
            if (target.IsGtaV && info.ModType is MxaModType.Addon or MxaModType.Oiv)
            {
                string? error = await EnsureModsArchiveAsync(target.Folder, ModsArchives.UpdateRpf, phase);
                if (error != null) return new ModResult(false, error);
            }

            // 5) Decrypt + install inside the engine, on a worker thread.
            phase?.Report("Dl_Installing");
            string work = NewWorkDir("unpack");
            try
            {
                var record = await Task.Run(() =>
                {
                    var rec = engine.InstallPackage(mxaPath, contentKey, target.Folder, info.Game, work);
                    Stamp(rec, target, mxaPath, info.Name);
                    rec.Author ??= string.IsNullOrWhiteSpace(info.Author) ? null : info.Author;
                    InstalledModsStore.Add(rec);
                    // Machine-bound entitlement: the ASI keeps this content active only on this PC.
                    try { Modexa.Core.Drm.EntitlementService.RegisterInstall(target.Folder, rec, info.ProductId); } catch { }
                    return rec;
                });
                return Success(record);
            }
            finally
            {
                CleanupLater(work);
            }
        }
        catch (LicenseGateException ex)
        {
            Log.Error("Install gate", ex);
            return new ModResult(false, LicenseWindow.GateMessage(ex));
        }
        catch (ModInstallException ex)
        {
            Log.Error("Install", ex);
            return new ModResult(false, InstallMessage(ex));
        }
        catch (Exception ex)
        {
            Log.Error("Install", ex);
            return new ModResult(false, $"{Loc.Instance["Dl_Failed"]}\n{ex.Message}");
        }
    }

    // ---- Pro: any other mod ----------------------------------------------------------------------

    /// <summary>
    /// Pro-only: installs an add-on dlc.rpf, a .zip or a folder. Requires the Pro build of the
    /// engine — which the server only gives to PRO licenses.
    /// </summary>
    public static async Task<ModResult> InstallRawAsync(string path, GameTarget target, Window owner, IProgress<InstallPhase>? phase)
    {
        if (await Task.Run(() => GameProcess.IsRunning(target.Folder))) return Fail("Mods_GameRunning");
        var pro = await GetProEngineAsync(owner, phase);
        if (pro == null) return Fail("Pro_Required");

        if (Path.GetExtension(path).Equals(".rpf", StringComparison.OrdinalIgnoreCase))
        {
            string? error = await EnsureModsArchiveAsync(target.Folder, ModsArchives.UpdateRpf, phase);
            if (error != null) return new ModResult(false, error);
        }

        phase?.Report("Dl_Installing");
        string work = NewWorkDir("raw");
        try
        {
            var record = await Task.Run(() =>
            {
                var rec = pro.InstallRaw(path, target.Folder, "GtaV", work);
                Stamp(rec, target, path, rec.Name);
                InstalledModsStore.Add(rec);
                return rec;
            });
            return Success(record);
        }
        catch (ModInstallException ex)
        {
            Log.Error("Raw install", ex);
            return new ModResult(false, InstallMessage(ex));
        }
        catch (Exception ex)
        {
            Log.Error("Raw install", ex);
            return new ModResult(false, $"{Loc.Instance["Dl_Failed"]}\n{ex.Message}");
        }
        finally
        {
            CleanupLater(work);
        }
    }

    /// <summary>The loaded Pro engine, downloading it for an existing PRO key or offering activation.</summary>
    private static async Task<IProModEngine?> GetProEngineAsync(Window owner, IProgress<InstallPhase>? phase)
    {
        if (EngineModule.Load() is IProModEngine loaded) return loaded;

        var best = LicenseStore.Best();
        if (best?.Tier == LicenseTier.Pro)
        {
            try
            {
                phase?.Report("Engine_Downloading");
                var e = await Task.Run(() => EngineModule.EnsureAsync(best.Key, LicenseTier.Pro));
                if (e is IProModEngine p) { ThemeService.Apply(LicenseTier.Pro); return p; }
            }
            catch (LicenseGateException ex)
            {
                DialogWindow.Show($"{Loc.Instance["Pro_EngineFailed"]}\n{LicenseWindow.GateMessage(ex)}", DialogKind.Warning);
                return null;
            }
        }

        phase?.Report(new InstallPhase(InstallPhase.Hide));
        var dlg = new LicenseWindow(LicensePurpose.Pro) { Owner = owner };
        if (dlg.ShowDialog() != true || dlg.ActivatedTier != LicenseTier.Pro) return null;
        ThemeService.Apply(LicenseTier.Pro);
        return EngineModule.Load() as IProModEngine;
    }

    // ---- uninstall (all tiers) -------------------------------------------------------------------

    /// <summary>Removes a mod exactly as it was installed. Returns a message for the user.</summary>
    public static async Task<ModResult> UninstallAsync(ModInstallRecord record)
    {
        if (await Task.Run(() => GameProcess.IsRunning(record.GameFolder))) return Fail("Mods_GameRunning");
        try
        {
            var result = await Task.Run(() => ModUninstaller.Uninstall(record));
            return result.Success
                ? new ModResult(true, Loc.Instance.Format("Mods_Uninstalled", record.Name))
                : new ModResult(false, UninstallMessage(result));
        }
        catch (Exception ex)
        {
            Log.Error("Uninstall", ex);
            return new ModResult(false, $"{Loc.Instance["Dl_Failed"]}\n{ex.Message}");
        }
    }

    private static string UninstallMessage(UninstallResult result)
        => Loc.Instance.Format("Mods_UninstallIncomplete", string.Join("\n", result.Errors.Take(6).Select(e => "• " + e)));

    // ---- helpers ---------------------------------------------------------------------------------

    private static ModResult Fail(string key) => new(false, Loc.Instance[key]);

    private static ModInstallRecord NewRecord(GameTarget target, string source, string name) => new()
    {
        Name = name,
        Game = target.MxaKey,
        GameTitle = target.Title,
        GameFolder = target.Folder,
        Source = Path.GetFileName(source.TrimEnd('\\', '/')),
        InstalledUtc = DateTime.UtcNow
    };

    private static void Stamp(ModInstallRecord rec, GameTarget target, string source, string? name)
    {
        rec.GameTitle = target.Title;
        rec.GameFolder = target.Folder;
        rec.Source = Path.GetFileName(source.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(rec.Name) && !string.IsNullOrWhiteSpace(name)) rec.Name = name!;
        rec.InstalledUtc = DateTime.UtcNow;
    }

    /// <summary>"Done", plus anything the installer had to skip (unsupported package parts).</summary>
    private static ModResult Success(ModInstallRecord record)
    {
        string done = Loc.Instance.Format("Mods_Installed_Ok", record.Name);
        if (record.Warnings.Count == 0) return new ModResult(true, done, record);
        var shown = record.Warnings.Take(8).Select(w => "• " + w).ToList();
        if (record.Warnings.Count > shown.Count) shown.Add($"… (+{record.Warnings.Count - shown.Count})");
        return new ModResult(true, done + "\n\n" + Loc.Instance.Format("Mods_Warnings", string.Join("\n", shown)), record);
    }

    /// <summary>
    /// Creates mods\&lt;archive&gt; (copy of the game's, converted to OPEN) when it is missing.
    /// Returns a user-facing error, or null when the archive is ready.
    /// </summary>
    private static async Task<string?> EnsureModsArchiveAsync(string gameFolder, string archive, IProgress<InstallPhase>? phase)
    {
        if (await Task.Run(() => ModsArchives.IsReady(gameFolder, archive))) return null;
        phase?.Report(new InstallPhase("Prep_CreatingModsRpf", 0));
        var copy = new Progress<int>(p => phase?.Report(new InstallPhase("Prep_CreatingModsRpf", p)));
        try
        {
            await Task.Run(() => ModsArchives.EnsureOpen(gameFolder, archive, copy));
            return null;
        }
        catch (ModsArchiveException ex)
        {
            Log.Error("Create mods archive", ex);
            return Loc.Instance.Format("Mods_NotPrepared", ModsArchiveMessages.For(ex));
        }
    }

    private static string InstallMessage(ModInstallException ex) => ex.Code switch
    {
        ModInstallException.NotPrepared => Loc.Instance.Format("Mods_NotPrepared",
            ex.InnerException is ModsArchiveException mae ? ModsArchiveMessages.For(mae) : ex.Message),
        ModInstallException.Unsupported => $"{Loc.Instance["Mods_Unsupported"]}\n{ex.Message}",
        ModInstallException.BadPackage => Loc.Instance.Format("Oiv_Invalid", ex.Message),
        _ => $"{Loc.Instance["Dl_Failed"]}\n{ex.Message}"
    };

    private static string NewWorkDir(string kind)
    {
        string dir = Path.Combine(AppPaths.CacheDir, kind, Guid.NewGuid().ToString("N"));
        AppPaths.EnsureDir(dir);
        return dir;
    }

    private static void CleanupLater(string dir)
        => _ = Task.Run(() => { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } });

    /// <summary>Opens a folder in Windows Explorer.</summary>
    public static void OpenFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        try { Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{folder}\"", UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Open folder", ex); }
    }
}

/// <summary>Is the game running from this folder? (Files would be locked mid-install.)</summary>
public static class GameProcess
{
    public static bool IsRunning(string gameFolder)
    {
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in new[] { gameFolder, Path.Combine(gameFolder, "bin", "x64") })
                if (Directory.Exists(dir))
                    foreach (var exe in Directory.EnumerateFiles(dir, "*.exe"))
                        names.Add(Path.GetFileNameWithoutExtension(exe));
            // Launchers/updaters can stay open; the game executables can't.
            names.RemoveWhere(n => n.Contains("Launcher", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("Setup", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("LanguageSelect", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("PlayGTAV", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("unins", StringComparison.OrdinalIgnoreCase));
            return Process.GetProcesses().Any(p =>
            {
                try { return names.Contains(p.ProcessName); }
                finally { p.Dispose(); }
            });
        }
        catch { return false; }
    }
}
