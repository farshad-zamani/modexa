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
using Modexa.Core.Install;
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
        ThemeService.LookChanged += OnLookChanged;
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

                // Keys exist but none validates any more (ended / refunded / offline too long). Purchased
                // mods still installed follow their own licenses (notices, then removal), which needs
                // the engine; with none left, the engine goes.
                if (await Task.Run(() => InstalledModsStore.All().Any(r => r.ProductId != null)))
                    await LicenseNotices.RunAsync(this);
                else
                    await Task.Run(EngineModule.Remove);
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
                    try
                    {
                        var engine = await Task.Run(() => EngineModule.EnsureAsync(key, LicenseTier.Pro));
                        if (engine.Flavor == LicenseTier.Pro) ThemeService.Apply(LicenseTier.Pro);
                    }
                    catch (LicenseGateException ex) when (ex.Code == LicenseGateException.AppUpdate)
                    {
                        DialogWindow.Show(LicenseWindow.GateMessage(ex), DialogKind.Warning); // this version is retired
                    }
                }
            }

            // Purchased mods: each follows its own license.
            await LicenseNotices.RunAsync(this);
        }
        catch (Exception ex)
        {
            // Keep whatever tier was applied; never crash over licensing.
            Modexa.Core.Diagnostics.Log.Error("Tier", ex);
        }
    }

    /// <summary>The license changed: badge, upgrade shortcut, theme picker.</summary>
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
        // Licensed users can pick the look they like.
        BtnTheme.Visibility = tier == LicenseTier.Free ? Visibility.Collapsed : Visibility.Visible;
        ThemePopup.IsOpen = false;
    }

    /// <summary>The look changed (license tier, or the user's pick from the theme menu).</summary>
    private void OnLookChanged(LicenseTier look, bool licensed)
    {
        // Fade the ambient gold edge in/out to match the look; fully collapse it on Free.
        double target = look switch { LicenseTier.Pro => 1.0, LicenseTier.Plus => 0.55, _ => 0.0 };
        if (target > 0) GlowOverlay.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(400)) { EasingFunction = new CubicEase() };
        if (target <= 0) anim.Completed += (_, _) => GlowOverlay.Visibility = Visibility.Collapsed;
        GlowOverlay.BeginAnimation(OpacityProperty, anim);

        // A licensed user on the Free look keeps the animated threads and lights — in cyan.
        Ambience.SetTier(look, licensed);
        SetProFlourish(look == LicenseTier.Pro);
    }

    // ---- Theme picker --------------------------------------------------------------------------

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (ThemePopup.IsOpen) { ThemePopup.IsOpen = false; return; }
        BuildThemeOptions();
        ThemePopup.IsOpen = true;
    }

    private void BuildThemeOptions()
    {
        ThemeOptions.Children.Clear();
        ThemeOptions.FlowDirection = LanguageService.Flow;
        var looks = new (LicenseTier tier, string key, Color from, Color to)[]
        {
            (LicenseTier.Free, "Theme_Free", Color.FromRgb(0x48, 0xE0, 0xF0), Color.FromRgb(0x1C, 0x8F, 0xA3)),
            (LicenseTier.Plus, "Theme_Plus", Color.FromRgb(0xF0, 0xCB, 0x6E), Color.FromRgb(0xB0, 0x7F, 0x2E)),
            (LicenseTier.Pro, "Theme_Pro", Color.FromRgb(0xFF, 0xE3, 0x9A), Color.FromRgb(0x14, 0x10, 0x08)),
        };
        foreach (var (tier, key, from, to) in looks)
        {
            bool allowed = ThemeService.CanUseLook(tier);
            bool current = ThemeService.LookTier == tier;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var swatch = new System.Windows.Shapes.Ellipse
            {
                Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center,
                Fill = new LinearGradientBrush(from, to, 45),
                StrokeThickness = 1
            };
            swatch.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Stroke.Strong");
            row.Children.Add(swatch);

            var label = new TextBlock { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            label.Bind(TextBlock.TextProperty, key);
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            var end = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            if (current)
            {
                end.Style = (Style)FindResource("Icon");
                end.FontSize = 13;
                end.Text = "\uE73E";
                end.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            }
            else if (!allowed)
            {
                end.FontSize = 11.5;
                end.Bind(TextBlock.TextProperty, "Theme_ProOnly");
                end.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
            }
            Grid.SetColumn(end, 2);
            row.Children.Add(end);

            var button = new Button { Style = (Style)FindResource("MenuRowButton"), Content = row, IsEnabled = allowed };
            button.Click += (_, _) =>
            {
                ThemePopup.IsOpen = false;
                // Picking the license's own look means "follow the license" (an upgrade then shows).
                ThemeService.ChooseLook(tier == ThemeService.CurrentTier ? null : tier);
            };
            ThemeOptions.Children.Add(button);
        }
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
        Dispatcher.BeginInvoke(new Action(async () => await OpenModFilesAsync(new[] { path })),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private static readonly string[] DropExts = { ".mxa", ".oiv", ".oivs", ".rpf", ".zip", ".rar", ".7z" };

    /// <summary>
    /// Mod files opened from Explorer or dropped on the window: they are installed on their game's
    /// page (where the game folder is known). OpenIV / Pro files go together to the GTA V page (so a
    /// split mod installs part 1, part 2, … in one go); each .mxa goes to the game it is made for.
    /// </summary>
    private async Task OpenModFilesAsync(IReadOnlyList<string> paths)
    {
        var gtaFiles = paths.Where(p => !p.EndsWith(Modexa.Core.Format.MxaFile.Extension, StringComparison.OrdinalIgnoreCase)).ToList();
        if (gtaFiles.Count > 0)
        {
            if (PART_Content.Content is GtaVView here) await here.InstallFilesAsync(gtaFiles);
            else if (ChooseGtaVEdition() is { } edition) Navigate(new GtaVView(this, edition) { PendingFiles = gtaFiles });
        }
        foreach (var mxa in paths.Except(gtaFiles))
            await OpenModFileAsync(mxa);
    }

    /// <summary>One .mxa: installed on the page of the game (and GTA V edition) it is made for.</summary>
    private async Task OpenModFileAsync(string path)
    {
        try
        {
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
                if (edition == null && PART_Content.Content is GtaVView any) { await any.InstallFilesAsync(new[] { path }); return; }
                if (PART_Content.Content is GtaVView same && same.Edition == edition) { await same.InstallFilesAsync(new[] { path }); return; }
                edition ??= ChooseGtaVEdition();
                if (edition == null) return;
                Navigate(new GtaVView(this, edition.Value) { PendingFiles = new[] { path } });
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
        e.Effects = FromDrop(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        var paths = FromDrop(e);
        if (paths.Count == 0) return;
        // Installs happen on the game's page, so the mods land in the right game folder.
        Dispatcher.BeginInvoke(new Action(async () => await OpenModFilesAsync(paths)),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private static List<string> FromDrop(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return new List<string>();
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        // Folders too: a folder of mods is read like an archive (on the GTA V page).
        return files.Where(f => DropExts.Any(x => f.EndsWith(x, StringComparison.OrdinalIgnoreCase)) || System.IO.Directory.Exists(f)).ToList();
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
        ThemeService.LookChanged -= OnLookChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        base.OnClosed(e);
    }
}
