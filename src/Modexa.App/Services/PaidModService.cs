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
using WinForms = System.Windows.Forms;

namespace Modexa.App.Services;

public sealed record PaidModResult(bool Success, string Message, ModInstallRecord? Record = null);

/// <summary>
/// Orchestrates installs. The app itself contains no package crypto or game-archive writer; the
/// sequence is:
///
///   .mxa (Plus):  license for THIS product (ask once, stored) -> engine module (license-gated
///                 download) -> content key for the product (license-gated, cached) -> engine installs.
///   raw mod (Pro): Pro engine required (only the Pro build of the module implements it).
///
/// The first activated license turns Free into Plus; a PRO key + verified Pro engine turns Pro.
/// UI prompts run on the UI thread; all file work runs on worker threads.
/// </summary>
public static class PaidModService
{
    public static async Task<PaidModResult> InstallAsync(string mxaPath, Window owner, IProgress<string>? phase = null)
    {
        if (!await Task.Run(() => MxaFile.IsMxaFile(mxaPath)))
            return Fail("Mxa_NotPackage");

        MxaInfo info;
        try { info = await Task.Run(() => MxaFile.ReadInfo(mxaPath)); }
        catch { return Fail("Mxa_Corrupt"); }

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

            // 4) Target folder (GTA V edition aware).
            string? gameFolder = await ResolveGameFolderAsync(info.Game, info.Edition);
            if (string.IsNullOrWhiteSpace(gameFolder))
                return Fail("Dl_NoGameFolder");

            // 5) Decrypt + install inside the engine, on a worker thread.
            phase?.Report("Dl_Installing");
            string work = NewWorkDir("unpack");
            try
            {
                var record = await Task.Run(() =>
                {
                    var rec = engine.InstallPackage(mxaPath, contentKey, gameFolder!, info.Game, work);
                    InstalledModsStore.Add(rec);
                    // Machine-bound entitlement: the ASI keeps this content active only on this PC.
                    try { Modexa.Core.Drm.EntitlementService.RegisterInstall(gameFolder!, rec, info.ProductId); } catch { }
                    return rec;
                });
                return new PaidModResult(true, Loc.Instance["Common_Done"], record);
            }
            finally
            {
                CleanupLater(work);
            }
        }
        catch (LicenseGateException ex)
        {
            Log.Error("Install gate", ex);
            return new PaidModResult(false, LicenseWindow.GateMessage(ex));
        }
        catch (ModInstallException ex)
        {
            Log.Error("Install", ex);
            return new PaidModResult(false, InstallMessage(ex));
        }
        catch (Exception ex)
        {
            Log.Error("Install", ex);
            return new PaidModResult(false, $"{Loc.Instance["Dl_Failed"]}\n{ex.Message}");
        }
    }

    /// <summary>
    /// Pro-only: installs ANY mod from any source (.oiv/.oivs, add-on dlc.rpf, .zip or a folder).
    /// Requires the Pro build of the engine — which the server only gives to PRO licenses.
    /// </summary>
    public static async Task<PaidModResult> InstallRawAsync(string path, Window owner, IProgress<string>? phase = null)
    {
        var pro = await GetProEngineAsync(owner, phase);
        if (pro == null) return Fail("Pro_Required");

        string? gameFolder = await ResolveGameFolderAsync("GtaV", "");
        if (string.IsNullOrWhiteSpace(gameFolder))
            return Fail("Dl_NoGameFolder");

        phase?.Report("Dl_Installing");
        string work = NewWorkDir("raw");
        try
        {
            var record = await Task.Run(() =>
            {
                var rec = pro.InstallRaw(path, gameFolder!, "GtaV", work);
                InstalledModsStore.Add(rec);
                return rec;
            });
            return new PaidModResult(true, Loc.Instance["Common_Done"], record);
        }
        catch (ModInstallException ex)
        {
            Log.Error("Raw install", ex);
            return new PaidModResult(false, InstallMessage(ex));
        }
        catch (Exception ex)
        {
            Log.Error("Raw install", ex);
            return new PaidModResult(false, $"{Loc.Instance["Dl_Failed"]}\n{ex.Message}");
        }
        finally
        {
            CleanupLater(work);
        }
    }

    /// <summary>The loaded Pro engine, downloading it for an existing PRO key or offering activation.</summary>
    private static async Task<IProModEngine?> GetProEngineAsync(Window owner, IProgress<string>? phase)
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

        var dlg = new LicenseWindow(LicensePurpose.Pro) { Owner = owner };
        if (dlg.ShowDialog() != true || dlg.ActivatedTier != LicenseTier.Pro) return null;
        ThemeService.Apply(LicenseTier.Pro);
        return EngineModule.Load() as IProModEngine;
    }

    /// <summary>Uninstalls a recorded mod (files + add-on registration through the engine).</summary>
    public static void Uninstall(ModInstallRecord record)
    {
        var engine = EngineModule.Load();
        if (engine != null) engine.Uninstall(record);
        else LooseFileInstaller.Uninstall(record); // engine gone (license ended): still remove the files
        InstalledModsStore.Remove(record.Id);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static PaidModResult Fail(string key) => new(false, Loc.Instance[key]);

    private static string InstallMessage(ModInstallException ex) => ex.Code switch
    {
        ModInstallException.NotPrepared => Loc.Instance["Mods_NotPrepared"],
        ModInstallException.Unsupported => Loc.Instance["Mods_Unsupported"],
        ModInstallException.BadPackage => Loc.Instance["Gate_NotEntitled"],
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

    /// <summary>
    /// Saved folder -> auto-detect (worker thread) -> ask the user (UI thread). For GTA V the
    /// package's edition decides; packages for "any" edition ask when both editions are installed.
    /// </summary>
    private static async Task<string?> ResolveGameFolderAsync(string game, string? editionText)
    {
        var app = (App)Application.Current;

        if (string.Equals(game, "GtaSa", StringComparison.OrdinalIgnoreCase))
        {
            string? saved = app.Settings.GtaSaFolder;
            if (!string.IsNullOrWhiteSpace(saved) && await Task.Run(() => GameDetector.IsGtaSaFolder(saved!)))
                return saved;
            return await BrowseForAsync("Folder_Select_GtaSa", GameDetector.IsGtaSaFolder, f => app.Settings.GtaSaFolder = f);
        }

        if (!string.Equals(game, "GtaV", StringComparison.OrdinalIgnoreCase)) return null;

        var editions = Enum.TryParse<GameEdition>(editionText, true, out var only) && only != GameEdition.Unknown
            ? new[] { only }
            : new[] { GameEdition.Legacy, GameEdition.Enhanced };

        string? legacySaved = app.Settings.GtaVFolder, enhancedSaved = app.Settings.GtaVEnhancedFolder;
        var found = await Task.Run(() =>
        {
            var result = new Dictionary<GameEdition, string>();
            foreach (var ed in editions)
            {
                string? saved = ed == GameEdition.Enhanced ? enhancedSaved : legacySaved;
                if (!string.IsNullOrWhiteSpace(saved) && GameDetector.IsGtaVFolder(saved!, ed)) { result[ed] = saved!; continue; }
                var det = GameDetector.DetectGtaV(ed);
                if (det.Count > 0) result[ed] = det[0].Folder;
            }
            return result;
        });

        GameEdition chosen;
        if (found.Count == 2)
        {
            int pick = DialogWindow.Choose(Loc.Instance["Choose_Edition"], "GTA V Legacy", "GTA V Enhanced");
            if (pick < 0) return null;
            chosen = pick == 0 ? GameEdition.Legacy : GameEdition.Enhanced;
        }
        else if (found.Count == 1)
        {
            chosen = found.Keys.First();
        }
        else
        {
            var target = editions.Length == 1 ? editions[0] : GameEdition.Unknown;
            string descKey = target == GameEdition.Enhanced ? "Folder_Select_GtaVE" : "Folder_Select_GtaV";
            return await BrowseForAsync(descKey,
                f => target == GameEdition.Unknown ? GameDetector.IsGtaVFolder(f) : GameDetector.IsGtaVFolder(f, target),
                f => app.Settings.SetGtaVFolder(GameDetector.EditionOf(f), f));
        }

        string folder = found[chosen];
        if (!string.Equals(app.Settings.GetGtaVFolder(chosen), folder, StringComparison.OrdinalIgnoreCase))
        {
            app.Settings.SetGtaVFolder(chosen, folder);
            app.Settings.Save();
        }
        return folder;
    }

    private static async Task<string?> BrowseForAsync(string descKey, Func<string, bool> validate, Action<string> save)
    {
        string path;
        using (var dlg = new WinForms.FolderBrowserDialog { Description = Loc.Instance[descKey], UseDescriptionForTitle = true })
        {
            if (dlg.ShowDialog() != WinForms.DialogResult.OK) return null;
            path = dlg.SelectedPath;
        }
        if (!await Task.Run(() => validate(path)))
        {
            DialogWindow.Show(Loc.Instance["Folder_Invalid"], DialogKind.Warning);
            return null;
        }
        save(path);
        ((App)Application.Current).Settings.Save();
        return path;
    }
}
