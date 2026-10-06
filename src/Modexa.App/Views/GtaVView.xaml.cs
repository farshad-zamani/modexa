using System.Windows;
using System.Windows.Controls;
using Modexa.App.Navigation;
using Modexa.App.Services;
using Modexa.Core.Games;
using Modexa.Core.I18n;
using Modexa.Core.Prepare;
using WinForms = System.Windows.Forms;

namespace Modexa.App.Views;

/// <summary>
/// GTA V page. Every filesystem / registry touch (detection, clean-launch state, backups, revert)
/// runs on a worker thread: game folders often live on slow or sleeping HDDs and backups copy
/// multi-GB archives, which previously froze the window ("Not Responding").
/// </summary>
public partial class GtaVView : UserControl
{
    private readonly INavigator _nav;
    private readonly GameEdition _edition;
    private GameInstall? _install;
    private PrepareManifest _manifest = PrepareManifest.Empty();
    private CancellationTokenSource? _cts;
    private string? _folderKey = "Common_Detecting";

    /// <param name="edition">Legacy or Enhanced — each is its own page, folder and bundle set.</param>
    public GtaVView(INavigator nav, GameEdition edition)
    {
        _nav = nav;
        _edition = edition == GameEdition.Enhanced ? GameEdition.Enhanced : GameEdition.Legacy;
        InitializeComponent();
        TitleText.Text = "Grand Theft Auto V";
        EditionBadgeText.Text = _edition == GameEdition.Enhanced ? "ENHANCED" : "LEGACY";
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ThemeService.TierChanged += ApplyTier;
        ApplyTier(ThemeService.CurrentTier);
        RefreshFolderText();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Loc.Instance.LanguageChanged -= RefreshFolderText;
        ThemeService.TierChanged -= ApplyTier;
    }

    /// <summary>Clean launch + revert are Plus/Pro features; Free sees a locked card with a shop link.</summary>
    private void ApplyTier(Modexa.Core.Licensing.LicenseTier tier)
    {
        bool paid = tier != Modexa.Core.Licensing.LicenseTier.Free;
        MaintLock.Visibility = paid ? Visibility.Collapsed : Visibility.Visible;
        MaintCard.IsEnabled = paid;
    }

    private void Unlock_Click(object sender, RoutedEventArgs e)
        => MainWindow.OpenUrl(Modexa.Core.Remote.Endpoints.Shop);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loc.Instance.LanguageChanged -= RefreshFolderText;
        Loc.Instance.LanguageChanged += RefreshFolderText;
        if (_install != null) return; // re-loaded (e.g. tab back) — keep state

        var app = (App)Application.Current;
        string? saved = app.Settings.GetGtaVFolder(_edition);
        string? other = app.Settings.GetGtaVFolder(_edition == GameEdition.Enhanced ? GameEdition.Legacy : GameEdition.Enhanced);
        var edition = _edition;

        try
        {
            var result = await Task.Run(() =>
            {
                var manifest = PrepareManifest.LoadLocal();
                GameInstall? install = null;
                // Prefer a remembered, still-valid folder of THIS edition. Older versions stored one
                // GTA V folder for both editions, so also adopt the other slot if it matches us.
                if (!string.IsNullOrWhiteSpace(saved) && GameDetector.IsGtaVFolder(saved!, edition))
                    install = ToInstall(saved!);
                else if (!string.IsNullOrWhiteSpace(other) && GameDetector.IsGtaVFolder(other!, edition))
                    install = ToInstall(other!);
                else
                {
                    var found = GameDetector.DetectGtaV(edition);
                    if (found.Count > 0) install = found[0];
                }
                return (manifest, install);
            });

            _manifest = result.manifest;
            if (result.install != null) await SetInstallAsync(result.install);
            else { _folderKey = "Common_NotDetected"; RefreshFolderText(); }
        }
        catch
        {
            _folderKey = "Common_NotDetected";
            RefreshFolderText();
        }

        // Refresh the prepare manifest from the client's remote (new builds) without blocking.
        try { _manifest = await Task.Run(PrepareManifest.RefreshAsync); } catch { }
    }

    private static GameInstall ToInstall(string folder)
    {
        var v = GameVersion.Detect(folder);
        var edition = v?.Edition ?? GameDetector.EditionOf(folder);
        return new GameInstall(GameCatalog.ForEdition(edition), folder, edition, v, "Saved");
    }

    private async Task SetInstallAsync(GameInstall install)
    {
        _install = install;
        var app = (App)Application.Current;
        // Un-mix the legacy single-slot setting if it held this edition's folder.
        var otherEdition = _edition == GameEdition.Enhanced ? GameEdition.Legacy : GameEdition.Enhanced;
        if (string.Equals(app.Settings.GetGtaVFolder(otherEdition), install.Folder, StringComparison.OrdinalIgnoreCase))
            app.Settings.SetGtaVFolder(otherEdition, null);
        app.Settings.SetGtaVFolder(_edition, install.Folder);
        app.Settings.Save();

        _folderKey = null;
        RefreshFolderText();
        EditionText.Text = install.Edition == GameEdition.Unknown ? "—" : install.Edition.ToString();
        VersionText.Text = install.Version is { } v && v.Build > 0 ? v.FileVersion : "—";
        await RefreshPrepareStateAsync();
        await UpdateCleanLaunchStateAsync();
    }

    /// <summary>Path (always LTR, hugging the icon) or a localized status line.</summary>
    private void RefreshFolderText()
    {
        if (_folderKey == null && _install != null)
        {
            FolderText.Text = _install.Folder;
            FolderText.FlowDirection = FlowDirection.LeftToRight;
            FolderText.TextAlignment = Loc.Instance.IsRtl ? TextAlignment.Right : TextAlignment.Left;
            FolderText.ToolTip = _install.Folder;
        }
        else
        {
            FolderText.Text = Loc.Instance[_folderKey ?? "Common_NotDetected"];
            FolderText.ClearValue(FlowDirectionProperty);
            FolderText.TextAlignment = TextAlignment.Left;
            FolderText.ToolTip = null;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _nav.GoHome();

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        bool enhanced = _edition == GameEdition.Enhanced;
        using var dlg = new WinForms.FolderBrowserDialog
        {
            Description = Loc.Instance[enhanced ? "Folder_Select_GtaVE" : "Folder_Select_GtaV"],
            UseDescriptionForTitle = true
        };
        if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;
        string path = dlg.SelectedPath;
        var edition = _edition;

        var (install, actual) = await Task.Run(() =>
            (GameDetector.IsGtaVFolder(path, edition) ? ToInstall(path) : null,
             GameDetector.IsGtaVFolder(path) ? GameDetector.EditionOf(path) : GameEdition.Unknown));
        if (install == null)
        {
            // Point the user at the right page when they picked the other edition.
            string key = actual == GameEdition.Unknown
                ? (enhanced ? "Folder_Invalid_GtaVE" : "Folder_Invalid_GtaV")
                : (enhanced ? "Folder_WrongEdition_ToLegacy" : "Folder_WrongEdition_ToEnhanced");
            DialogWindow.Show(Loc.Instance[key], DialogKind.Warning);
            return;
        }
        await SetInstallAsync(install);
    }

    // ---- Prepare steps -------------------------------------------------------------------------

    private void ModRunner_Click(object sender, RoutedEventArgs e) => _ = RunStepAsync(PrepareStep.ModRunner);
    private void GameConfig_Click(object sender, RoutedEventArgs e) => _ = RunStepAsync(PrepareStep.GameConfig);
    private void Menu_Click(object sender, RoutedEventArgs e) => _ = RunStepAsync(PrepareStep.Menu);

    private async Task RunStepAsync(PrepareStep step)
    {
        if (_install == null)
        {
            DialogWindow.Show(Loc.Instance["Dl_NoGameFolder"], DialogKind.Warning);
            return;
        }
        if (_cts != null) return; // a step is already running

        _cts = new CancellationTokenSource();
        ShowOverlay(true, "Dl_Downloading", cancellable: true);

        var progress = new Progress<PrepareProgress>(p =>
        {
            OverlayPhase.Bind(TextBlock.TextProperty, p.Phase switch
            {
                PreparePhase.Downloading => "Dl_Downloading",
                PreparePhase.Verifying => "Dl_Verifying",
                PreparePhase.Extracting => "Dl_Extracting",
                PreparePhase.Installing => "Dl_Installing",
                _ => "Common_Done"
            });
            SetPercent(p.Percent);
        });

        try
        {
            var svc = new PrepareService(_manifest);
            int build = _install.Version?.Build ?? 0;
            var result = await svc.RunAsync(GameCatalog.ForEdition(_edition), step, _install.Folder, _install.Edition, build,
                versionToken: null, progress, _cts.Token,
                (variants, def) => Task.FromResult(ChooseGameConfig(variants, def)));

            ShowOverlay(false);
            ShowResult(step, result, build);
            await RefreshPrepareStateAsync();
            await UpdateCleanLaunchStateAsync();
        }
        catch (OperationCanceledException)
        {
            ShowOverlay(false);
        }
        catch (PrepareNotConfiguredException)
        {
            ShowOverlay(false);
            DialogWindow.Show(Loc.Instance["Prepare_NotConfigured"], DialogKind.Info);
        }
        catch (PrepareLayoutException ex)
        {
            ShowOverlay(false);
            Modexa.Core.Diagnostics.Log.Error("Prepare layout", ex);
            DialogWindow.Show(Loc.Instance["Prepare_LayoutChanged"], DialogKind.Error);
        }
        catch (Exception ex)
        {
            ShowOverlay(false);
            Modexa.Core.Diagnostics.Log.Error("Prepare", ex);
            DialogWindow.Show($"{Loc.Instance["Dl_Failed"]}\n\n{ex.Message}", DialogKind.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Lets the user pick the gameconfig variant (traffic density / mod amount).</summary>
    private string? ChooseGameConfig(IReadOnlyList<string> variants, string? def)
    {
        var items = GameConfigLabels.Order(variants).Select(v => (v, GameConfigLabels.Label(v))).ToList();
        return DialogWindow.ChooseFromList(Loc.Instance["Prep_Choose_Title"], Loc.Instance["Prep_Choose_Message"], items, def);
    }

    private void ShowResult(PrepareStep step, PrepareResult r, int build)
    {
        var lines = new List<string>();
        var kind = DialogKind.Success;
        switch (r.GameConfig)
        {
            case GameConfigOutcome.Applied:
                lines.Add(Loc.Instance.Format("Prep_GameConfigApplied", GameConfigLabels.Label(r.GameConfigVariant ?? "")));
                break;
            case GameConfigOutcome.NeedsModsUpdateRpf:
            case GameConfigOutcome.UpdateRpfEncrypted:
            case GameConfigOutcome.NoGameConfigEntry:
                kind = DialogKind.Warning;
                lines.Add(Loc.Instance[_edition == GameEdition.Legacy ? "Prep_AdjustersInstalled" : "Prep_NothingElse"]);
                lines.Add(Loc.Instance["Prep_NeedsModsRpf"]);
                break;
            case GameConfigOutcome.Skipped:
                kind = DialogKind.Info;
                lines.Add(Loc.Instance["Prep_GameConfigSkipped"]);
                break;
            default:
                lines.Add(Loc.Instance[step == PrepareStep.ModRunner ? "Prep_RunnerDone" : "Prep_MenuDone"]);
                break;
        }
        if (r.BuildMismatch && r.ConfigBuild is { } cfg)
            lines.Add(Loc.Instance.Format("Prep_BuildMismatch", cfg, build));
        DialogWindow.Show(string.Join("\n\n", lines), kind);
    }

    /// <summary>"Installed" chips on the steps + the editable update.rpf status.</summary>
    private async Task RefreshPrepareStateAsync()
    {
        if (_install == null) return;
        string folder = _install.Folder;
        var (state, rpf) = await Task.Run(() => (PrepareState.Load(folder), PrepareService.ModsUpdateRpfState(folder)));

        StepModRunner.SetDone(state.ContainsKey(PrepareStep.ModRunner));
        StepGameConfig.SetDone(state.ContainsKey(PrepareStep.GameConfig),
            state.TryGetValue(PrepareStep.GameConfig, out var gc) && gc.Variant != null ? GameConfigLabels.Label(gc.Variant) : null);
        StepMenu.SetDone(state.ContainsKey(PrepareStep.Menu));

        bool ready = rpf == GameConfigOutcome.Applied;
        ModsRpfChip.Visibility = Visibility.Visible;
        ModsRpfIcon.Text = ready ? "" : "";
        ModsRpfIcon.SetResourceReference(TextBlock.ForegroundProperty, ready ? "Success" : "Warning");
        ModsRpfText.Bind(TextBlock.TextProperty, ready ? "Prep_ModsRpf_Ready"
            : rpf == GameConfigOutcome.UpdateRpfEncrypted ? "Prep_ModsRpf_Encrypted" : "Prep_ModsRpf_Missing");
        ModsRpfChip.ToolTip = Loc.Instance["Prep_ModsRpf_Tip"];
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void ShowOverlay(bool show, string phaseKey = "Dl_Downloading", bool cancellable = false)
    {
        Overlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // Never leave an indeterminate animation ticking behind a hidden overlay.
        OverlayBar.IsIndeterminate = false;
        if (!show) return;

        OverlayPhase.Bind(TextBlock.TextProperty, phaseKey);
        OverlayCancel.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        SetPercent(cancellable ? 0 : null);
    }

    private void SetPercent(int? pct)
    {
        if (pct is { } v)
        {
            OverlayBar.IsIndeterminate = false;
            OverlayBar.Value = v;
            OverlayPercent.Text = v + "%";
        }
        else
        {
            OverlayBar.IsIndeterminate = true;
            OverlayPercent.Text = "";
        }
    }

    // ---- Revert / clean launch -----------------------------------------------------------------

    private async void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (_install == null)
        {
            DialogWindow.Show(Loc.Instance["Dl_NoGameFolder"], DialogKind.Warning);
            return;
        }
        string folder = _install.Folder;

        if (!await Task.Run(() => PrepareService.HasBackups(folder)))
        {
            DialogWindow.Show(Loc.Instance["GtaV_Revert_Nothing"], DialogKind.Info);
            return;
        }
        if (!DialogWindow.Confirm(Loc.Instance["GtaV_Revert_Confirm"], danger: true))
            return;

        ShowOverlay(true, "Dl_Restoring");
        try
        {
            await Task.Run(() => PrepareService.Revert(folder));
            ShowOverlay(false);
            DialogWindow.Show(Loc.Instance["Common_Done"], DialogKind.Success);
            await RefreshPrepareStateAsync();
        }
        catch (Exception ex)
        {
            ShowOverlay(false);
            DialogWindow.Show($"{Loc.Instance["Dl_Failed"]}\n\n{ex.Message}", DialogKind.Error);
        }
        await UpdateCleanLaunchStateAsync();
    }

    private async void CleanLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (_install == null) { await UpdateCleanLaunchStateAsync(); return; }
        string folder = _install.Folder;
        bool enable = CleanLaunchToggle.IsChecked == true;

        CleanLaunchToggle.IsEnabled = false;
        try
        {
            await Task.Run(() =>
            {
                if (enable) CleanLaunchService.EnableCleanLaunch(folder);
                else CleanLaunchService.DisableCleanLaunch(folder);
            });
        }
        catch (Exception ex)
        {
            DialogWindow.Show(ex.Message, DialogKind.Error);
        }
        await UpdateCleanLaunchStateAsync();
    }

    private async Task UpdateCleanLaunchStateAsync()
    {
        if (_install == null) { CleanLaunchToggle.IsEnabled = false; return; }
        string folder = _install.Folder;
        bool clean = await Task.Run(() => CleanLaunchService.IsCleanLaunchEnabled(folder));
        CleanLaunchToggle.IsChecked = clean;
        CleanLaunchToggle.IsEnabled = true;
    }
}
