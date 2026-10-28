using System.Windows;
using System.Windows.Controls;
using Modexa.App.Navigation;
using Modexa.App.Services;
using Modexa.Core.Games;
using Modexa.Core.I18n;
using Modexa.Core.Install;
using Modexa.Core.Prepare;
using WinForms = System.Windows.Forms;

namespace Modexa.App.Views;

/// <summary>
/// GTA V page. Every filesystem / registry touch (detection, clean-launch state, backups, revert)
/// runs on a worker thread: game folders often live on slow or sleeping HDDs and backups copy
/// multi-GB archives, which previously froze the window ("Not Responding").
/// </summary>
public partial class GtaVView : UserControl, IInstallHost
{
    private readonly INavigator _nav;
    private readonly GameEdition _edition;
    private GameInstall? _install;
    private PrepareManifest _manifest = PrepareManifest.Empty();
    private CancellationTokenSource? _cts;
    private string? _folderKey = "Common_Detecting";

    /// <summary>Mod files to install once the game folder is known (opened from Explorer / dropped on the window).</summary>
    public IReadOnlyList<string>? PendingFiles { get; set; }

    public GameEdition Edition => _edition;

    /// <param name="edition">Legacy or Enhanced — each is its own page, folder and bundle set.</param>
    public GtaVView(INavigator nav, GameEdition edition)
    {
        _nav = nav;
        _edition = edition == GameEdition.Enhanced ? GameEdition.Enhanced : GameEdition.Legacy;
        InitializeComponent();
        TitleText.Text = "Grand Theft Auto V";
        Backdrop.SetGame(GameCatalog.ForEdition(_edition));
        EditionBadgeText.Text = _edition == GameEdition.Enhanced ? "ENHANCED" : "LEGACY";
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ThemeService.TierChanged += ApplyTier;
        ApplyTier(ThemeService.CurrentTier);
        InstallPanel.Host = this;
        InstallPanel.Changed += () => _ = RefreshPrepareStateAsync();
        // Each edition describes only what it installs (OpenRPF is Enhanced only; the limit adjusters Legacy only).
        if (_edition == GameEdition.Enhanced)
        {
            StepModRunner.Bind(StepCard.DescriptionProperty, "GtaV_Step_ModRunner_Desc_Enhanced");
            StepGameConfig.Bind(StepCard.DescriptionProperty, "GtaV_Step_GameConfig_Desc_Enhanced");
        }
        // Products from the client's shop. Backup Manager is a GTA V Legacy tool: on the Enhanced page
        // its pitch speaks to players who also have Legacy.
        PromoBackup.Set("assets/promo/backup-manager.jpg", "Promo_Bm_Title",
            _edition == GameEdition.Enhanced ? "Promo_Bm_Text_Enhanced" : "Promo_Bm_Text", Modexa.Core.Remote.Endpoints.BackupManagerPro);
        PromoMenyoo.Set("assets/promo/menyoofa.jpg", "Promo_Menyoo_Title", "Promo_Menyoo_Text", Modexa.Core.Remote.Endpoints.MenyooFa);
        RefreshFolderText();
    }

    /// <summary>Installs mod files into this page's game (used when files are dropped on the window).</summary>
    public Task InstallFilesAsync(IReadOnlyList<string> paths) => InstallPanel.InstallAsync(paths);

    // ---- IInstallHost (the page overlay shows install / uninstall progress) ----

    public void ShowBusy(InstallPhase phase) => Busy.Update(phase);

    public void HideBusy() => Busy.Hide();

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => ModInstallService.OpenFolder(_install?.Folder);

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

        if (PendingFiles is { Count: > 0 } pending)
        {
            PendingFiles = null;
            await InstallPanel.InstallAsync(pending);
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
        BtnOpenFolder.IsEnabled = true;
        InstallPanel.SetTarget(new GameTarget(GameCatalog.ForEdition(_edition), install.Folder,
            GameCatalog.Get(GameCatalog.ForEdition(_edition)).DisplayName, _edition));
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

    // An installed step's button removes it; otherwise it installs.
    private void ModRunner_Click(object sender, RoutedEventArgs e) => _ = StepClickAsync(StepModRunner, PrepareStep.ModRunner);
    private void GameConfig_Click(object sender, RoutedEventArgs e) => _ = StepClickAsync(StepGameConfig, PrepareStep.GameConfig);
    private void Menu_Click(object sender, RoutedEventArgs e) => _ = StepClickAsync(StepMenu, PrepareStep.Menu);
    private void Spawner_Click(object sender, RoutedEventArgs e) => _ = StepClickAsync(StepSpawner, PrepareStep.AddonSpawner);

    /// <summary>
    /// One click: Mod Runner, then GameConfig with the game's normal traffic — no questions. Steps
    /// already installed are skipped; one result at the end.
    /// </summary>
    private async void PrepareAll_Click(object sender, RoutedEventArgs e)
    {
        if (_install == null) { DialogWindow.Show(Loc.Instance["Dl_NoGameFolder"], DialogKind.Warning); return; }
        if (_cts != null) return;
        string folder = _install.Folder;
        var done = await Task.Run(() => PrepareService.InstalledSteps(folder));
        var todo = new[] { PrepareStep.ModRunner, PrepareStep.GameConfig }.Where(s => !done.ContainsKey(s)).ToList();
        if (todo.Count == 0) { DialogWindow.Show(Loc.Instance["GtaV_PrepareAll_Ready"], DialogKind.Success); return; }

        _cts = new CancellationTokenSource();
        Busy.Show("Dl_Downloading", cancellable: true);
        var lines = new List<string>();
        var kind = DialogKind.Success;
        int build = _install.Version?.Build ?? 0;
        try
        {
            for (int i = 0; i < todo.Count; i++)
            {
                var step = todo[i];
                string stepText = Loc.Instance.Format("Busy_Step", i + 1, todo.Count);
                bool copyStep = step == PrepareStep.GameConfig && !await Task.Run(() => Modexa.Core.Rpf.ModsArchives.IsReady(folder));
                var progress = new Progress<PrepareProgress>(p => Busy.Update(PrepareBusy.Phase(p, copyStep) with { Step = stepText }));
                var result = await new PrepareService(_manifest).RunAsync(GameCatalog.ForEdition(_edition), step, folder, _install.Edition, build,
                    versionToken: null, progress, _cts.Token,
                    // The game's own (normal) traffic, never asked.
                    (variants, def) => Task.FromResult<string?>(variants.FirstOrDefault(GameConfigLabels.IsStock) ?? def));
                if (result.GameConfig == GameConfigOutcome.GameTooOld)
                {
                    Busy.Hide();
                    ShowResult(step, result, build);
                    return;
                }
                if (result.GameConfig is GameConfigOutcome.NeedsModsUpdateRpf or GameConfigOutcome.UpdateRpfEncrypted or GameConfigOutcome.NoGameConfigEntry)
                {
                    kind = DialogKind.Warning;
                    lines.Add(Loc.Instance.Format("Prep_NeedsModsRpf", result.ModsRpfError != null
                        ? ModsArchiveMessages.For(result.ModsRpfError) : Loc.Instance.Format("ModsRpf_Failed", result.GameConfig.ToString())));
                }
                else lines.Add("✓ " + ModInstallService.DisplayName(new ModInstallRecord { Setup = step, Game = "GtaV" }));
                if (step == PrepareStep.GameConfig && !string.IsNullOrWhiteSpace(result.ConfigName))
                    lines.Add(Loc.Instance.Format("Prep_ConfigPicked", "‎" + result.ConfigName + "‎",
                        build > 0 ? build.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?"));
                if (result.BuildMismatch && result.ConfigFor is { } cfg) { kind = DialogKind.Warning; lines.Add(Loc.Instance.Format("Prep_BuildMismatch", cfg, build)); }
            }
            Busy.Hide();
            DialogWindow.Show(Loc.Instance["GtaV_PrepareAll_Done"] + "\n\n" + string.Join("\n", lines), kind);
        }
        catch (OperationCanceledException) { Busy.Hide(); }
        catch (PrepareNotConfiguredException) { Busy.Hide(); DialogWindow.Show(Loc.Instance["Prepare_NotConfigured"], DialogKind.Info); }
        catch (PrepareLayoutException ex)
        {
            Busy.Hide();
            Modexa.Core.Diagnostics.Log.Error("Prepare layout", ex);
            DialogWindow.Show(Loc.Instance["Prepare_LayoutChanged"], DialogKind.Error);
        }
        catch (Exception ex)
        {
            Busy.Hide();
            Modexa.Core.Diagnostics.Log.Error("Prepare all", ex);
            DialogWindow.Show($"{Loc.Instance["Dl_Failed"]}\n\n{ex.Message}", DialogKind.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            await RefreshPrepareStateAsync();
            InstallPanel.Refresh();
            await UpdateCleanLaunchStateAsync();
        }
    }

    // Update when a newer version is due; else remove an installed step, or install it.
    private Task StepClickAsync(StepCard card, PrepareStep step)
        => card.IsUpdate ? RunStepAsync(step, update: true) : card.IsDone ? RemoveStepAsync(step) : RunStepAsync(step);

    /// <summary>Removes an installed prepare step exactly like any installed mod.</summary>
    private async Task RemoveStepAsync(PrepareStep step)
    {
        if (_install == null || _cts != null) return;
        string folder = _install.Folder;
        var steps = await Task.Run(() => PrepareService.InstalledSteps(folder));
        if (!steps.TryGetValue(step, out var record)) { await RefreshPrepareStateAsync(); return; }
        var records = await ModInstallPanel.ConfirmUninstallAsync(record);
        if (records == null) return;

        _cts = new CancellationTokenSource(); // also keeps other steps from starting meanwhile
        bool busy = true;
        Busy.Show("Mods_Uninstalling");
        try
        {
            var result = await ModInstallService.UninstallAsync(records, new Progress<InstallPhase>(p => { if (busy) Busy.Update(p); }));
            busy = false;
            Busy.Hide();
            DialogWindow.Show(result.Message, result.Success ? DialogKind.Success : DialogKind.Warning);
        }
        finally
        {
            busy = false;
            Busy.Hide();
            _cts?.Dispose();
            _cts = null;
            await RefreshPrepareStateAsync();
            InstallPanel.Refresh();
            await UpdateCleanLaunchStateAsync();
        }
    }

    private async Task RunStepAsync(PrepareStep step, bool update = false)
    {
        if (_install == null)
        {
            DialogWindow.Show(Loc.Instance["Dl_NoGameFolder"], DialogKind.Warning);
            return;
        }
        if (_cts != null) return; // a step is already running

        _cts = new CancellationTokenSource();
        Busy.Show("Dl_Downloading", cancellable: true);

        // Download -> (editable update.rpf) -> install: say which step of how many is running.
        string folder = _install.Folder;
        bool copyStep = step == PrepareStep.GameConfig && !await Task.Run(() => Modexa.Core.Rpf.ModsArchives.IsReady(folder));
        var progress = new Progress<PrepareProgress>(p => Busy.Update(PrepareBusy.Phase(p, copyStep)));

        try
        {
            var svc = new PrepareService(_manifest);
            int build = _install.Version?.Build ?? 0;
            // An update keeps what was chosen before (e.g. the traffic): no questions.
            string? installedVariant = update ? PrepareService.InstalledSteps(folder).GetValueOrDefault(step)?.Version : null;
            var result = await svc.RunAsync(GameCatalog.ForEdition(_edition), step, _install.Folder, _install.Edition, build,
                versionToken: null, progress, _cts.Token,
                update
                    ? (variants, def) => Task.FromResult<string?>(installedVariant != null && variants.Contains(installedVariant) ? installedVariant
                        : variants.FirstOrDefault(GameConfigLabels.IsStock) ?? def)
                    : (variants, def) => Task.FromResult(ChooseGameConfig(variants, def)),
                update: update);

            Busy.Hide();
            ShowResult(step, result, build);
            await UpdateCleanLaunchStateAsync();
        }
        catch (OperationCanceledException)
        {
            Busy.Hide();
        }
        catch (PrepareNotConfiguredException)
        {
            Busy.Hide();
            DialogWindow.Show(Loc.Instance["Prepare_NotConfigured"], DialogKind.Info);
        }
        catch (PrepareLayoutException ex)
        {
            Busy.Hide();
            Modexa.Core.Diagnostics.Log.Error("Prepare layout", ex);
            DialogWindow.Show(Loc.Instance["Prepare_LayoutChanged"], DialogKind.Error);
        }
        catch (Exception ex)
        {
            Busy.Hide();
            Modexa.Core.Diagnostics.Log.Error("Prepare", ex);
            DialogWindow.Show($"{Loc.Instance["Dl_Failed"]}\n\n{ex.Message}", DialogKind.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            // The step is now in the installed list (with its own uninstall button).
            await RefreshPrepareStateAsync();
            InstallPanel.Refresh();
        }
    }

    /// <summary>Lets the user pick the gameconfig variant (traffic density / mod amount).</summary>
    private string? ChooseGameConfig(IReadOnlyList<string> variants, string? def)
    {
        // Default: the game's normal traffic; other densities only behind the "advanced" switch.
        var items = GameConfigLabels.Order(variants).Select(v => (v, GameConfigLabels.Label(v))).ToList();
        string? normal = variants.FirstOrDefault(GameConfigLabels.IsStock) ?? def;
        return DialogWindow.ChooseFromList(Loc.Instance["Prep_Choose_Title"], Loc.Instance["Prep_Choose_Message"], items, normal,
            advancedLabel: Loc.Instance["Prep_Advanced"]);
    }

    private void ShowResult(PrepareStep step, PrepareResult r, int build)
    {
        var lines = new List<string>();
        var kind = DialogKind.Success;
        if (r.GameConfig == GameConfigOutcome.GameTooOld)
        {
            // The game is too old for every config in the pack: it has to be updated first.
            string b = build > 0 ? build.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
            int pick = DialogWindow.Ask(Loc.Instance.Format("Prep_GameTooOld", b, r.ConfigFor ?? "?"),
                new[] { Loc.Instance["Prep_GameTooOld_Open"], Loc.Instance["Common_Close"] });
            if (pick == 0) MainWindow.OpenUrl(Modexa.Core.Remote.Endpoints.GameUpdates);
            return;
        }
        switch (r.GameConfig)
        {
            case GameConfigOutcome.Applied:
                lines.Add(Loc.Instance.Format("Prep_GameConfigApplied", GameConfigLabels.Label(r.GameConfigVariant ?? "")));
                // Which config of the pack was chosen for this build (e.g. "Gta Config v34 for v 1.0.3407").
                if (!string.IsNullOrWhiteSpace(r.ConfigName))
                    lines.Add(Loc.Instance.Format("Prep_ConfigPicked", "‎" + r.ConfigName + "‎",
                        build > 0 ? build.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?"));
                if (_edition == GameEdition.Legacy) lines.Add(Loc.Instance["Prep_AdjustersInstalled"]);
                if (r.ModsRpfCreated) lines.Add(Loc.Instance["Prep_ModsRpfCreated"]);
                break;
            case GameConfigOutcome.NeedsModsUpdateRpf:
            case GameConfigOutcome.UpdateRpfEncrypted:
            case GameConfigOutcome.NoGameConfigEntry:
                kind = DialogKind.Warning;
                lines.Add(Loc.Instance[_edition == GameEdition.Legacy ? "Prep_AdjustersInstalled" : "Prep_NothingElse"]);
                string reason = r.ModsRpfError != null
                    ? ModsArchiveMessages.For(r.ModsRpfError)
                    : Loc.Instance.Format("ModsRpf_Failed", r.GameConfig.ToString());
                lines.Add(Loc.Instance.Format("Prep_NeedsModsRpf", reason));
                break;
            case GameConfigOutcome.Skipped:
                kind = DialogKind.Info;
                lines.Add(Loc.Instance["Prep_GameConfigSkipped"]);
                break;
            default:
                lines.Add(Loc.Instance[step switch
                {
                    PrepareStep.ModRunner => "Prep_RunnerDone",
                    PrepareStep.AddonSpawner => "Prep_SpawnerDone",
                    _ => "Prep_MenuDone"
                }]);
                break;
        }
        if (r.BuildMismatch && r.ConfigFor is { } cfg)
            lines.Add(Loc.Instance.Format("Prep_BuildMismatch", cfg, build));
        DialogWindow.Show(string.Join("\n\n", lines), kind);
    }

    /// <summary>"Installed" chips on the steps + the editable update.rpf status.</summary>
    private async Task RefreshPrepareStateAsync()
    {
        if (_install == null) return;
        string folder = _install.Folder;
        var (state, rpf) = await Task.Run(() => (PrepareService.InstalledSteps(folder), PrepareService.ModsUpdateRpfState(folder)));

        StepModRunner.SetDone(state.ContainsKey(PrepareStep.ModRunner));
        StepGameConfig.SetDone(state.ContainsKey(PrepareStep.GameConfig),
            state.TryGetValue(PrepareStep.GameConfig, out var gc) && gc.Version != null ? GameConfigLabels.Label(gc.Version) : null);
        StepMenu.SetDone(state.ContainsKey(PrepareStep.Menu));
        StepSpawner.SetDone(state.ContainsKey(PrepareStep.AddonSpawner));
        // The one-click card says so once both base steps are in.
        bool allReady = state.ContainsKey(PrepareStep.ModRunner) && state.ContainsKey(PrepareStep.GameConfig);
        PrepareAllDesc.Bind(TextBlock.TextProperty, allReady ? "GtaV_PrepareAll_Ready" : "GtaV_PrepareAll_Desc");

        bool ready = rpf == GameConfigOutcome.Applied;
        ModsRpfChip.Visibility = Visibility.Visible;
        ModsRpfIcon.Text = ready ? "" : "";
        ModsRpfIcon.SetResourceReference(TextBlock.ForegroundProperty, ready ? "Success" : "Warning");
        ModsRpfText.Bind(TextBlock.TextProperty, ready ? "Prep_ModsRpf_Ready"
            : rpf == GameConfigOutcome.UpdateRpfEncrypted ? "Prep_ModsRpf_Encrypted" : "Prep_ModsRpf_Missing");
        ModsRpfChip.ToolTip = Loc.Instance["Prep_ModsRpf_Tip"];

        // Prepared before Modexa (e.g. with OpenIV): shown, never counted as installed — can't tell right from wrong.
        var edition = _install.Edition;
        var foreign = await Task.Run(() => PrepareService.ForeignSteps(folder, edition));
        StepModRunner.SetForeign(foreign.Contains(PrepareStep.ModRunner));
        StepGameConfig.SetForeign(foreign.Contains(PrepareStep.GameConfig));
        StepMenu.SetForeign(foreign.Contains(PrepareStep.Menu));
        StepSpawner.SetForeign(foreign.Contains(PrepareStep.AddonSpawner));
        ForeignNote.Visibility = foreign.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _ = CheckUpdatesAsync(folder, state);
    }

    private bool _checkingUpdates;

    /// <summary>
    /// In the background: does an installed step need an update (the game was updated, or its pack has
    /// a newer version)? The step's button then becomes "Update".
    /// </summary>
    private async Task CheckUpdatesAsync(string folder, Dictionary<PrepareStep, ModInstallRecord> state)
    {
        if (_checkingUpdates || _install == null) return;
        _checkingUpdates = true;
        try
        {
            var game = GameCatalog.ForEdition(_edition);
            var edition = _install.Edition;
            int build = _install.Version?.Build ?? 0;
            var cards = new Dictionary<PrepareStep, StepCard>
            {
                [PrepareStep.ModRunner] = StepModRunner, [PrepareStep.GameConfig] = StepGameConfig,
                [PrepareStep.Menu] = StepMenu, [PrepareStep.AddonSpawner] = StepSpawner
            };
            foreach (var (step, record) in state)
            {
                if (!cards.TryGetValue(step, out var card)) continue;
                StepUpdateState s;
                try { s = await Task.Run(() => PrepareUpdates.CheckAsync(_manifest, game, edition, build, record)); }
                catch (Exception ex) { Modexa.Core.Diagnostics.Log.Info($"Update check {step}: {ex.Message}"); continue; }
                if (!ReferenceEquals(folder, _install?.Folder) && folder != _install?.Folder) return;
                if (s == StepUpdateState.Available) card.SetUpdate(true);
                else if (s == StepUpdateState.GameUpdatedNoPack) card.SetGameUpdated();
            }
        }
        finally { _checkingUpdates = false; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ---- Revert / clean launch -----------------------------------------------------------------

    private async void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (_install == null)
        {
            DialogWindow.Show(Loc.Instance["Dl_NoGameFolder"], DialogKind.Warning);
            return;
        }
        string folder = _install.Folder;

        if (!await Task.Run(() => PrepareService.HasBackups(folder) || InstalledModsStore.ForFolder(folder).Count > 0))
        {
            DialogWindow.Show(Loc.Instance["GtaV_Revert_Nothing"], DialogKind.Info);
            return;
        }
        if (!DialogWindow.Confirm(Loc.Instance["GtaV_Revert_Confirm"], danger: true))
            return;

        if (await Task.Run(() => GameProcess.IsRunning(folder)))
        {
            DialogWindow.Show(Loc.Instance["Mods_GameRunning"], DialogKind.Warning);
            return;
        }

        Busy.Show("Dl_Restoring");
        try
        {
            // Every installed item (mods and prepare steps, newest first, each exactly as installed),
            // then whatever is left of Modexa in the folder.
            var step = new Progress<string>(s => Busy.Update(new InstallPhase("Dl_Restoring", null, s.Replace('/', '\\'))));
            var errors = await Task.Run(() => ModUninstaller.UninstallAll(folder, step));
            await Task.Run(() => PrepareService.Revert(folder));
            Busy.Hide();
            DialogWindow.Show(errors.Count == 0 ? Loc.Instance["Common_Done"]
                : Loc.Instance.Format("Mods_UninstallIncomplete", string.Join("\n", errors.Take(6).Select(x => "• " + x))),
                errors.Count == 0 ? DialogKind.Success : DialogKind.Warning);
            await RefreshPrepareStateAsync();
            InstallPanel.Refresh();
        }
        catch (Exception ex)
        {
            Busy.Hide();
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
