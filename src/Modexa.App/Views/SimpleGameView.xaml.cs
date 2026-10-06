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
/// Prepare page for the games that only need a single Mod Runner step (San Andreas, GTA IV, RDR1,
/// RDR2, Cyberpunk 2077). The right Mod Runner is picked by the detected game version. All disk work
/// runs off the UI thread.
/// </summary>
public partial class SimpleGameView : UserControl
{
    private readonly INavigator _nav;
    private readonly GameId _game;
    private readonly SimpleGame _def;
    private PrepareManifest _manifest = PrepareManifest.Empty();
    private CancellationTokenSource? _cts;
    private string? _folder;
    private string? _version;
    private string _statusKey = "Common_Detecting";

    public SimpleGameView(INavigator nav, GameId game)
    {
        _nav = nav;
        _game = game;
        _def = SimpleGames.Get(game);
        InitializeComponent();
        TitleText.Text = GameCatalog.Get(game).DisplayName;
        Loaded += OnLoaded;
        Unloaded += (_, _) => Loc.Instance.LanguageChanged -= RefreshFolderText;
        RefreshFolderText();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loc.Instance.LanguageChanged -= RefreshFolderText;
        Loc.Instance.LanguageChanged += RefreshFolderText;
        if (_folder != null) return;

        string? saved = ((App)Application.Current).Settings.GetSimpleFolder(_game);
        var def = _def;
        try
        {
            var result = await Task.Run(() =>
            {
                var manifest = PrepareManifest.LoadLocal();
                string? folder = !string.IsNullOrWhiteSpace(saved) && def.IsFolder(saved!)
                    ? saved
                    : GameDetector.DetectSimple(def.Id);
                string? version = folder != null ? def.DetectVersion(folder) : null;
                return (manifest, folder, version);
            });
            _manifest = result.manifest;
            SetFolder(result.folder, result.version);
        }
        catch
        {
            SetFolder(null, null);
        }

        try { _manifest = await Task.Run(PrepareManifest.RefreshAsync); } catch { }
        await RefreshStateAsync();
    }

    private void SetFolder(string? folder, string? version)
    {
        _folder = string.IsNullOrWhiteSpace(folder) ? null : folder;
        _version = version;
        _statusKey = "Common_NotDetected";
        if (_folder != null)
        {
            var app = (App)Application.Current;
            app.Settings.SetSimpleFolder(_game, _folder);
            app.Settings.Save();
        }
        VersionChip.Visibility = _folder != null && _version != null ? Visibility.Visible : Visibility.Collapsed;
        VersionText.Text = _version ?? "—";
        RefreshFolderText();
    }

    private void RefreshFolderText()
    {
        if (_folder != null)
        {
            FolderText.Text = _folder;
            FolderText.FlowDirection = FlowDirection.LeftToRight;
            FolderText.TextAlignment = Loc.Instance.IsRtl ? TextAlignment.Right : TextAlignment.Left;
            FolderText.ToolTip = _folder;
        }
        else
        {
            FolderText.Text = Loc.Instance[_statusKey];
            FolderText.ClearValue(FlowDirectionProperty);
            FolderText.TextAlignment = TextAlignment.Left;
            FolderText.ToolTip = null;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _nav.GoHome();

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new WinForms.FolderBrowserDialog
        {
            Description = Loc.Instance.Format("Folder_Select_Generic", GameCatalog.Get(_game).DisplayName),
            UseDescriptionForTitle = true
        };
        if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;
        string path = dlg.SelectedPath;
        var def = _def;

        var (ok, version) = await Task.Run(() => (def.IsFolder(path), def.IsFolder(path) ? def.DetectVersion(path) : null));
        if (!ok)
        {
            DialogWindow.Show(Loc.Instance.Format("Folder_Invalid_Generic", GameCatalog.Get(_game).DisplayName), DialogKind.Warning);
            return;
        }
        SetFolder(path, version);
        await RefreshStateAsync();
    }

    private void Runner_Click(object sender, RoutedEventArgs e) => _ = RunAsync();

    private async Task RunAsync()
    {
        if (_folder == null)
        {
            DialogWindow.Show(Loc.Instance["Dl_NoGameFolder"], DialogKind.Warning);
            return;
        }
        if (_cts != null) return;

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
            var result = await svc.RunAsync(_game, PrepareStep.ModRunner, _folder, GameEdition.Unknown, 0,
                _version, progress, _cts.Token);
            ShowOverlay(false);

            if (result.VersionWarning != null)
                DialogWindow.Show(Loc.Instance.Format("Simple_VersionWarn", result.VersionWarning, _version ?? "?"), DialogKind.Warning);
            else
                DialogWindow.Show(Loc.Instance["Simple_RunnerDone"], DialogKind.Success);
            await RefreshStateAsync();
        }
        catch (OperationCanceledException) { ShowOverlay(false); }
        catch (PrepareVersionException ex)
        {
            ShowOverlay(false);
            DialogWindow.Show(Loc.Instance.Format("Simple_VersionUnsupported", ex.Detected, string.Join("، ", ex.Available)), DialogKind.Warning);
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

    private async void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (_folder == null) { DialogWindow.Show(Loc.Instance["Dl_NoGameFolder"], DialogKind.Warning); return; }
        string folder = _folder;
        if (!await Task.Run(() => PrepareService.HasBackups(folder)))
        {
            DialogWindow.Show(Loc.Instance["GtaV_Revert_Nothing"], DialogKind.Info);
            return;
        }
        if (!DialogWindow.Confirm(Loc.Instance["GtaV_Revert_Confirm"], danger: true)) return;

        ShowOverlay(true, "Dl_Restoring");
        try
        {
            await Task.Run(() => PrepareService.Revert(folder));
            ShowOverlay(false);
            DialogWindow.Show(Loc.Instance["Common_Done"], DialogKind.Success);
        }
        catch (Exception ex)
        {
            ShowOverlay(false);
            DialogWindow.Show($"{Loc.Instance["Dl_Failed"]}\n\n{ex.Message}", DialogKind.Error);
        }
        await RefreshStateAsync();
    }

    private async Task RefreshStateAsync()
    {
        if (_folder == null) { StepRunner.SetDone(false); return; }
        string folder = _folder;
        var state = await Task.Run(() => PrepareState.Load(folder));
        StepRunner.SetDone(state.ContainsKey(PrepareStep.ModRunner));
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void ShowOverlay(bool show, string phaseKey = "Dl_Downloading", bool cancellable = false)
    {
        Overlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        OverlayBar.IsIndeterminate = false;
        if (!show) return;
        OverlayPhase.Bind(TextBlock.TextProperty, phaseKey);
        OverlayCancel.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        SetPercent(cancellable ? 0 : null);
    }

    private void SetPercent(int? pct)
    {
        if (pct is { } v) { OverlayBar.IsIndeterminate = false; OverlayBar.Value = v; OverlayPercent.Text = v + "%"; }
        else { OverlayBar.IsIndeterminate = true; OverlayPercent.Text = ""; }
    }
}
