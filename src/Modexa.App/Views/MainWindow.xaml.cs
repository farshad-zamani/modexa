using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Modexa.App.Navigation;
using Modexa.App.Services;
using Modexa.App.Views;
using Modexa.Core.Engine;
using Modexa.Core.I18n;
using Modexa.Core.Licensing;
using Modexa.Core.Remote;
using LicenseManager = Modexa.Core.Licensing.LicenseManager;

namespace Modexa.App;

public partial class MainWindow : Window, INavigator
{
    private string? _updateUrl;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        StateChanged += (_, _) => OnWindowStateChanged();
        SizeChanged += (_, _) => { if (WindowState == WindowState.Maximized) FitMaximizedFrame(); };
        ThemeService.TierChanged += OnTierChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;

        var app = (App)Application.Current;
        Width = Math.Max(MinWidth, app.Settings.WindowWidth);
        Height = Math.Max(MinHeight, app.Settings.WindowHeight);
        if (app.Settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        OnLanguageChanged();
        OnWindowStateChanged();

        GoHome();

        var app = (App)Application.Current;
        if (!string.IsNullOrWhiteSpace(app.PendingOpenPath))
        {
            BringToFrontAndOpen(app.PendingOpenPath);
            app.PendingOpenPath = null;
        }

        // Resolve license tier (theme) and check for updates without blocking the UI.
        _ = LoadTierAsync();
        _ = CheckUpdatesAsync();
    }

    // ---- Navigation ----------------------------------------------------------------------------

    public void GoHome() => Navigate(new HomeView(this));

    public void GoToMods() => Navigate(new ModsView(this));

    public void Navigate(FrameworkElement view)
    {
        PART_Content.Content = view;
        // Subtle fade+rise on view change.
        view.Opacity = 0;
        var tt = new TranslateTransform(0, 10);
        view.RenderTransform = tt;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase() };
        var rise = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase() };
        // Release the animated values when done so the view isn't left holding animation clocks.
        fade.Completed += (_, _) => { view.BeginAnimation(OpacityProperty, null); view.Opacity = 1; };
        rise.Completed += (_, _) => { tt.BeginAnimation(TranslateTransform.YProperty, null); tt.Y = 0; };
        view.BeginAnimation(OpacityProperty, fade);
        tt.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    // ---- Tier / theme --------------------------------------------------------------------------

    private async Task LoadTierAsync()
    {
        // Dev-only theme preview: set MODEXA_FORCE_TIER=Plus|Pro to force the look (ignored in normal use).
        var forced = Environment.GetEnvironmentVariable("MODEXA_FORCE_TIER");
        if (!string.IsNullOrWhiteSpace(forced) && Enum.TryParse<LicenseTier>(forced, true, out var ft))
        {
            ThemeService.Apply(ft);
            return;
        }

        try
        {
            // Everything here (WMI hardware id, DPAPI, file I/O, network) runs off the UI thread.
            var (tier, hadKeys) = await Task.Run(async () =>
                (await LicenseManager.GetEffectiveTierAsync(), LicenseStore.Any()));

            if (tier == LicenseTier.Free)
            {
                ThemeService.Apply(LicenseTier.Free);
                if (!hadKeys) return;

                // Keys exist but no longer validate (refunded / revoked / offline too long): drop the
                // engine + cached content keys and revoke the on-disk entitlement so the ASI
                // deactivates protected content on the next game launch.
                var app = (App)Application.Current;
                var folders = new[] { app.Settings.GtaVFolder, app.Settings.GtaVEnhancedFolder, app.Settings.GtaSaFolder };
                await Task.Run(() =>
                {
                    EngineModule.Remove();
                    foreach (var folder in folders)
                        if (!string.IsNullOrWhiteSpace(folder))
                            try { Modexa.Core.Drm.EntitlementService.RevokeEntitlement(folder!); } catch { }
                });
                return;
            }

            // Pro is shown only when the verified Pro engine is actually installed.
            var flavor = await Task.Run(() => EngineModule.InstalledFlavor);
            ThemeService.Apply(tier == LicenseTier.Pro && flavor == LicenseTier.Pro ? LicenseTier.Pro : LicenseTier.Plus);

            if (tier == LicenseTier.Pro && flavor != LicenseTier.Pro)
            {
                // Valid PRO key but the Pro engine is missing (new PC, cleared data): fetch it quietly.
                string? key = LicenseStore.Best()?.Key;
                if (key != null)
                {
                    var engine = await Task.Run(() => EngineModule.EnsureAsync(key, LicenseTier.Pro));
                    if (engine.Flavor == LicenseTier.Pro) ThemeService.Apply(LicenseTier.Pro);
                }
            }
        }
        catch (Exception ex)
        {
            // Keep whatever tier was applied; never crash over licensing.
            Modexa.Core.Diagnostics.Log.Error("Tier", ex);
        }
    }

    private void OnTierChanged(LicenseTier tier)
    {
        TierText.Text = tier switch
        {
            LicenseTier.Pro => "PRO",
            LicenseTier.Plus => "PLUS",
            _ => "FREE"
        };
        // The gold upgrade shortcut disappears once the user is on Pro.
        GoProBtn.Visibility = tier == LicenseTier.Pro ? Visibility.Collapsed : Visibility.Visible;

        // Fade the ambient gold edge in/out to match the tier; fully collapse it on Free.
        double target = tier switch { LicenseTier.Pro => 1.0, LicenseTier.Plus => 0.55, _ => 0.0 };
        if (target > 0) GlowOverlay.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(400)) { EasingFunction = new CubicEase() };
        if (target <= 0) anim.Completed += (_, _) => GlowOverlay.Visibility = Visibility.Collapsed;
        GlowOverlay.BeginAnimation(OpacityProperty, anim);

        Ambience.SetTier(tier);
        SetProFlourish(tier == LicenseTier.Pro);
    }

    /// <summary>Pro-only motion: a light sweep around the window frame and a shimmer on the badge.</summary>
    private void SetProFlourish(bool on)
    {
        FrameSweepTx.BeginAnimation(TranslateTransform.XProperty, null);
        FrameSweepTx.BeginAnimation(TranslateTransform.YProperty, null);
        TierShineTx.BeginAnimation(TranslateTransform.XProperty, null);
        FrameSweep.Opacity = on ? 0.9 : 0;
        TierShine.Opacity = on ? 0.85 : 0;
        if (!on || !SystemParameters.ClientAreaAnimation) return;

        // Sweep, then rest, forever.
        AnimateLoop(FrameSweepTx, TranslateTransform.XProperty, -1, 1, 3.2, 4.5);
        AnimateLoop(FrameSweepTx, TranslateTransform.YProperty, -1, 1, 3.2, 4.5);
        AnimateLoop(TierShineTx, TranslateTransform.XProperty, -1, 1, 1.4, 3.6);
    }

    private static void AnimateLoop(Animatable target, DependencyProperty prop, double from, double to, double seconds, double pause)
    {
        var a = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds)),
            new SineEase { EasingMode = EasingMode.EaseInOut }));
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds + pause))));
        Timeline.SetDesiredFrameRate(a, 30);
        target.BeginAnimation(prop, a);
    }

    // ---- Updates -------------------------------------------------------------------------------

    private async Task CheckUpdatesAsync()
    {
        try
        {
            var info = await Task.Run(UpdateChecker.CheckAsync);
            if (info.Available)
            {
                _updateUrl = info.Url ?? Modexa.Core.Remote.Endpoints.Shop;
                _updateVersion = info.LatestVersion;
                _updateNotes = info.Notes;
                UpdateChipText.Text = $"{Loc.Instance["Update_Available"]}  {info.LatestVersion}";
                UpdateChip.Visibility = Visibility.Visible;
            }
        }
        catch { }
    }

    private string? _updateVersion;
    private string? _updateNotes;

    private void UpdateChip_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_updateUrl)) return;

        string message = Loc.Instance.Format("Update_Message", _updateVersion ?? "");
        if (!string.IsNullOrWhiteSpace(_updateNotes))
            message += "\n\n" + _updateNotes!.Trim();

        if (DialogWindow.Prompt(message, Loc.Instance["Update_Download"], Loc.Instance["Update_Later"],
                DialogKind.Info, Loc.Instance["Update_Title"]))
            OpenUrl(_updateUrl!);
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { }
    }

    // ---- Nav / Pro activation / drag-drop ------------------------------------------------------

    private void Home_Click(object sender, RoutedEventArgs e) => GoHome();

    private void MyMods_Click(object sender, RoutedEventArgs e) => GoToMods();

    private void TierBadge_Click(object sender, RoutedEventArgs e)
    {
        // Already Pro -> just open My Mods. Otherwise offer Pro activation.
        if (ThemeService.CurrentTier == LicenseTier.Pro) { GoToMods(); return; }
        ActivatePro();
    }

    private void GoPro_Click(object sender, RoutedEventArgs e) => ActivatePro();

    /// <summary>
    /// Pro activation: the dialog activates the PRO key, downloads the Pro engine from the
    /// license-gated endpoint and verifies it; only then does the app switch to Pro.
    /// </summary>
    public bool ActivatePro()
    {
        var dlg = new LicenseWindow(LicensePurpose.Pro) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.ActivatedTier != LicenseTier.Pro) return false;
        ThemeService.Apply(LicenseTier.Pro);
        return true;
    }

    /// <summary>Called when Explorer hands us a file (startup argument or a second launch).</summary>
    public void BringToFrontAndOpen(string? path)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false; // reliably bring to front
        if (string.IsNullOrWhiteSpace(path)) return;
        Dispatcher.BeginInvoke(new Action(async () => await OpenModFileAsync(path)),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private static readonly string[] DropExts = { ".mxa", ".oiv", ".oivs", ".rpf", ".zip" };

    /// <summary>
    /// A mod file opened from Explorer or dropped on the window: it is installed on its game's page
    /// (where the game folder is known). The current page is used when it is the right game.
    /// </summary>
    private async Task OpenModFileAsync(string path)
    {
        bool mxa = path.EndsWith(Modexa.Core.Format.MxaFile.Extension, StringComparison.OrdinalIgnoreCase);
        try
        {
            if (!mxa)
            {
                // OIV and Pro raw mods are GTA V only.
                if (PART_Content.Content is GtaVView here) { await here.InstallFileAsync(path); return; }
                var edition = ChooseGtaVEdition();
                if (edition == null) return;
                Navigate(new GtaVView(this, edition.Value) { PendingFile = path });
                return;
            }

            var info = await Task.Run(() => Modexa.Core.Format.MxaFile.ReadInfo(path));
            var game = GameOfPackage(info.Game);
            if (game == null)
            {
                DialogWindow.Show(Loc.Instance["Mxa_Corrupt"], DialogKind.Warning);
                return;
            }
            if (game == Modexa.Core.Games.GameId.GtaV)
            {
                Modexa.Core.Games.GameEdition? edition =
                    Enum.TryParse<Modexa.Core.Games.GameEdition>(info.Edition, true, out var ed) && ed != Modexa.Core.Games.GameEdition.Unknown
                        ? ed : null;
                if (edition == null && PART_Content.Content is GtaVView any) { await any.InstallFileAsync(path); return; }
                edition ??= ChooseGtaVEdition();
                if (edition == null) return;
                Navigate(new GtaVView(this, edition.Value) { PendingFile = path });
                return;
            }
            if (PART_Content.Content is SimpleGameView sv && sv.Game == game) { await sv.InstallFileAsync(path); return; }
            Navigate(new SimpleGameView(this, game.Value) { PendingFile = path });
        }
        catch (Exception ex)
        {
            Modexa.Core.Diagnostics.Log.Error("Open mod file", ex);
            DialogWindow.Show($"{Loc.Instance["Mxa_Corrupt"]}\n{ex.Message}", DialogKind.Warning);
        }
    }

    private static Modexa.Core.Games.GameId? GameOfPackage(string key) => key.ToLowerInvariant() switch
    {
        "gtav" => Modexa.Core.Games.GameId.GtaV,
        "gtasa" or "gtasanandreas" => Modexa.Core.Games.GameId.GtaSanAndreas,
        "gtaiv" => Modexa.Core.Games.GameId.GtaIV,
        "rdr1" or "reddeadredemption1" => Modexa.Core.Games.GameId.RedDeadRedemption1,
        "rdr2" or "reddeadredemption2" => Modexa.Core.Games.GameId.RedDeadRedemption2,
        "cp2077" or "cyberpunk2077" => Modexa.Core.Games.GameId.Cyberpunk2077,
        _ => null
    };

    /// <summary>The GTA V edition to install into: the one the user has, or ask when both are set up.</summary>
    private Modexa.Core.Games.GameEdition? ChooseGtaVEdition()
    {
        if (PART_Content.Content is GtaVView current) return current.Edition;
        var s = ((App)Application.Current).Settings;
        bool legacy = !string.IsNullOrWhiteSpace(s.GtaVFolder), enhanced = !string.IsNullOrWhiteSpace(s.GtaVEnhancedFolder);
        if (legacy && enhanced)
        {
            int pick = DialogWindow.Choose(Loc.Instance["Choose_Edition_Install"], "GTA V Legacy", "GTA V Enhanced");
            if (pick < 0) return null;
            return pick == 0 ? Modexa.Core.Games.GameEdition.Legacy : Modexa.Core.Games.GameEdition.Enhanced;
        }
        return enhanced ? Modexa.Core.Games.GameEdition.Enhanced : Modexa.Core.Games.GameEdition.Legacy;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = FromDrop(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        string? path = FromDrop(e);
        if (path == null) return;
        // Installs happen on the game's page, so the mod lands in the right game folder.
        Dispatcher.BeginInvoke(new Action(async () => await OpenModFileAsync(path)),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private static string? FromDrop(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        return files.FirstOrDefault(f => DropExts.Any(x => f.EndsWith(x, StringComparison.OrdinalIgnoreCase)));
    }

    // ---- Language ------------------------------------------------------------------------------

    private void LangToggle_Click(object sender, RoutedEventArgs e)
        => LanguageService.Apply(Loc.Instance.IsRtl ? "en" : "fa");

    private void OnLanguageChanged()
    {
        ContentRoot.FlowDirection = LanguageService.Flow;

        // The label names the language you can switch TO, rendered in that language's font.
        LangLabel.Text = Loc.Instance["Lang_Other"];
        LangLabel.SetResourceReference(TextBlock.FontFamilyProperty, Loc.Instance.IsRtl ? "Font.En.Hud" : "Font.Persian");

        UpdateMaxButton();
    }

    // ---- Caption buttons -----------------------------------------------------------------------

    private void Min_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Max_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void OnWindowStateChanged()
    {
        UpdateMaxButton();
        if (WindowState == WindowState.Maximized)
        {
            FitMaximizedFrame();
        }
        else
        {
            OuterFrame.Margin = new Thickness(0);
            OuterFrame.BorderThickness = new Thickness(1);
        }
    }

    private void UpdateMaxButton()
    {
        bool max = WindowState == WindowState.Maximized;
        BtnMax.Tag = FindResource(max ? "Glyph.Restore" : "Glyph.Max");
        BtnMax.ToolTip = Loc.Instance[max ? "Win_Restore" : "Win_Maximize"];
    }

    /// <summary>
    /// A maximized chromeless window overhangs the monitor by its (invisible) resize frame, which
    /// pushed the caption buttons and the page edges off-screen. Inset the content by exactly the
    /// measured overhang so the visible area matches the work area.
    /// </summary>
    private void FitMaximizedFrame()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var px = NativeMethods.GetMaximizedOverhang(hwnd);
        var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        OuterFrame.Margin = new Thickness(px.Left * m.M11, px.Top * m.M22, px.Right * m.M11, px.Bottom * m.M22);
        OuterFrame.BorderThickness = new Thickness(0);
    }

    // ---- Persist window state ------------------------------------------------------------------

    protected override void OnClosing(CancelEventArgs e)
    {
        try
        {
            var app = (App)Application.Current;
            app.Settings.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                app.Settings.WindowWidth = Width;
                app.Settings.WindowHeight = Height;
            }
            app.Settings.Save();
        }
        catch { }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeService.TierChanged -= OnTierChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        base.OnClosed(e);
    }
}
