using System.Globalization;
using System.Windows;
using Modexa.App.Views;
using Modexa.Core.Diagnostics;
using Modexa.Core.Engine;
using Modexa.Core.I18n;
using Modexa.Core.Install;
using Modexa.Core.Licensing;

namespace Modexa.App.Services;

/// <summary>
/// At start-up, for the purchased mods that are installed: has the engine check their licenses with
/// the shop, tells the user about a license that ends soon or has ended (with time to enter a new
/// one), and removes a mod whose time to do so has passed. Each mod follows its own license.
/// </summary>
public static class LicenseNotices
{
    public static async Task RunAsync(Window owner)
    {
        try
        {
            if (!InstalledModsStore.All().Any(r => r.ProductId != null)) return;
            var engine = await EnsureEngineAsync();
            if (engine == null) return;

            // Anything a game left open (it crashed, the PC lost power…) is put away again.
            foreach (var folder in InstalledModsStore.All().Where(r => r.ProductId != null).Select(r => r.GameFolder)
                         .Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                if (!await Task.Run(() => GameProcess.IsRunning(folder)))
                    await Task.Run(() => engine.EndChanges(folder));

            var notices = await Task.Run(() => engine.CheckLicenses(LicenseManager.CreateCheckRequest()));
            foreach (var n in notices) await HandleAsync(owner, engine, n);
        }
        catch (Exception ex) { Log.Error("License notices", ex); }
    }

    private static async Task<IModEngine?> EnsureEngineAsync()
    {
        if (EngineModule.Load() is { } loaded) return loaded;
        try { return await Task.Run(() => EngineModule.EnsureAsync(LicenseStore.Best()?.Key ?? "", LicenseTier.Plus)); }
        catch (LicenseGateException ex)
        {
            Log.Error("Engine for licensed mods", ex);
            if (ex.Code == LicenseGateException.AppUpdate) DialogWindow.Show(LicenseWindow.GateMessage(ex), DialogKind.Warning);
            return null;
        }
    }

    private static async Task HandleAsync(Window owner, IModEngine engine, ProductNotice n)
    {
        string name = NameOf(n.ProductId);
        switch (n.State)
        {
            case ProductLicenseState.Active:
            case ProductLicenseState.EndsSoon:
                // A newer key covers it: the ended ones are no longer needed.
                foreach (var k in n.EndedKeys) LicenseStore.Remove(k);
                if (n.State == ProductLicenseState.EndsSoon)
                    DialogWindow.Show(Loc.Instance.Format("Lic_EndsSoon", name, Date(n.EndsUtc)), DialogKind.Info);
                break;

            case ProductLicenseState.Ended:
                EngineModule.ForgetContentKey(n.ProductId);
                if (DialogWindow.Prompt(Loc.Instance.Format("Lic_Ended", name, Date(n.RemoveAfterUtc)), Loc.Instance["Lic_EnterNew"],
                        Loc.Instance["Common_Later"], DialogKind.Warning))
                    await RenewAsync(owner, engine, n.ProductId, name);
                break;

            case ProductLicenseState.RemoveNow:
                EngineModule.ForgetContentKey(n.ProductId);
                await RemoveAsync(n.ProductId);
                DialogWindow.Show(Loc.Instance.Format("Lic_Removed", name), DialogKind.Info);
                break;
        }
    }

    /// <summary>The user enters a new license (bought again in the shop): the mod is on again.</summary>
    private static async Task RenewAsync(Window owner, IModEngine engine, string productId, string name)
    {
        var dlg = new LicenseWindow(LicensePurpose.Product, prompt: name, productId: productId) { Owner = owner };
        if (dlg.ShowDialog() != true) return;
        ThemeService.Raise(dlg.ActivatedTier);
        var notices = await Task.Run(() => engine.CheckLicenses(LicenseManager.CreateCheckRequest()));
        var mine = notices.FirstOrDefault(x => string.Equals(x.ProductId, productId, StringComparison.OrdinalIgnoreCase));
        if (mine?.State is ProductLicenseState.Active or ProductLicenseState.EndsSoon)
        {
            foreach (var k in mine.EndedKeys) LicenseStore.Remove(k);
            DialogWindow.Show(Loc.Instance.Format("Lic_Renewed", name), DialogKind.Success);
        }
    }

    /// <summary>Removes every install of the product from every game, and forgets its keys.</summary>
    private static async Task RemoveAsync(string productId)
    {
        bool Mine(string? p) => string.Equals(p, productId, StringComparison.OrdinalIgnoreCase);
        var records = InstalledModsStore.All().Where(r => Mine(r.ProductId)).ToList();
        foreach (var group in records.GroupBy(r => r.GameFolder, StringComparer.OrdinalIgnoreCase))
            if (!await Task.Run(() => GameProcess.IsRunning(group.Key)))
                await ModInstallService.UninstallAsync(group.ToList());
        foreach (var d in DisabledModsStore.All().Where(d => Mine(d.ProductId))) DisabledModsStore.Remove(d.Id);
        foreach (var l in LicenseStore.All().Where(l => Mine(l.ProductId) || LicenseFormat.Covers(l.Key, productId))) LicenseStore.Remove(l.Key);
    }

    private static string NameOf(string productId)
        => InstalledModsStore.All().Where(r => string.Equals(r.ProductId, productId, StringComparison.OrdinalIgnoreCase))
               .Select(ModInstallService.DisplayName).FirstOrDefault() ?? productId;

    private static string Date(DateTime? utc)
    {
        if (utc == null) return "";
        var local = utc.Value.ToLocalTime();
        return Loc.Instance.IsRtl
            ? local.ToString("yyyy/MM/dd", new CultureInfo("fa-IR")) // Persian calendar
            : local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
