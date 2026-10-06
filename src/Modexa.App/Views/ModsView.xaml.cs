using System.Windows;
using System.Windows.Controls;
using Modexa.App.Navigation;
using Modexa.App.Services;
using Modexa.Core.Format;
using Modexa.Core.I18n;
using Modexa.Core.Install;
using Modexa.Core.Licensing;
using WinForms = System.Windows.Forms;

namespace Modexa.App.Views;

public partial class ModsView : UserControl
{
    private readonly INavigator _nav;

    public ModsView(INavigator nav)
    {
        _nav = nav;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshList();
            RefreshProCard(ThemeService.CurrentTier);
            ThemeService.TierChanged += RefreshProCard;
        };
        Unloaded += (_, _) => ThemeService.TierChanged -= RefreshProCard;
    }

    private void RefreshProCard(LicenseTier tier)
    {
        bool pro = tier == LicenseTier.Pro;
        ProCardDesc.Bind(TextBlock.TextProperty, pro ? "Pro_Card_ActiveDesc" : "Pro_Card_Desc");
        BtnActivatePro.Visibility = pro ? Visibility.Collapsed : Visibility.Visible;
        ProActiveChip.Visibility = pro ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ActivatePro_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main) main.ActivatePro();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _nav.GoHome();

    // ---- Drag & drop / browse ----

    private static readonly string[] RawExts = { ".oiv", ".oivs", ".rpf", ".zip" };

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        bool ok = FirstMxa(e) != null || FirstRaw(e) != null;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropHighlight(ok);
        e.Handled = true;
    }

    private void Root_DragLeave(object sender, DragEventArgs e) => SetDropHighlight(false);

    private void SetDropHighlight(bool on)
    {
        DropOutline.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, on ? "Stroke.Accent" : "Stroke.Strong");
        DropBg.SetResourceReference(Border.BackgroundProperty, on ? "Bg.Elevated" : "Bg.Panel");
    }

    private void Root_Drop(object sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        // .mxa -> licensed install; raw mod files -> Pro "install any mod".
        var mxa = FirstMxa(e);
        if (mxa != null) { _ = InstallAsync(mxa); return; }
        var raw = FirstRaw(e);
        if (raw != null) _ = InstallRawAsync(raw);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new WinForms.OpenFileDialog
        {
            Filter = "Modexa package (*.mxa)|*.mxa",
            Title = Loc.Instance["Mods_SelectPackage"]
        };
        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            _ = InstallAsync(dlg.FileName);
    }

    private static string? FirstMxa(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        // Extension check only here: DragOver fires continuously, so no file I/O on this path.
        return files.FirstOrDefault(f => f.EndsWith(MxaFile.Extension, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FirstRaw(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        return files.FirstOrDefault(f => RawExts.Any(x => f.EndsWith(x, StringComparison.OrdinalIgnoreCase)));
    }

    private void InstallAny_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new WinForms.OpenFileDialog
        {
            Filter = "Mods (*.oiv;*.oivs;*.rpf;*.zip)|*.oiv;*.oivs;*.rpf;*.zip",
            Title = Loc.Instance["Mods_SelectAny"]
        };
        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            _ = InstallRawAsync(dlg.FileName);
    }

    /// <summary>Entry point for a file dropped anywhere in the app: route .mxa vs raw mod files.</summary>
    public Task HandleDrop(string path)
    {
        if (path.EndsWith(MxaFile.Extension, StringComparison.OrdinalIgnoreCase))
            return InstallAsync(path);
        return InstallRawAsync(path);
    }

    public Task InstallAsync(string mxaPath)
        => RunInstallAsync((owner, phase) => PaidModService.InstallAsync(mxaPath, owner, phase));

    private Task InstallRawAsync(string path)
        => RunInstallAsync((owner, phase) => PaidModService.InstallRawAsync(path, owner, phase));

    private async Task RunInstallAsync(Func<Window, IProgress<string>, Task<PaidModResult>> install)
    {
        // The overlay appears once real work starts (after any license prompt), with a live phase label.
        var phase = new Progress<string>(key =>
        {
            OverlayText.Bind(TextBlock.TextProperty, key);
            SetBusy(true);
        });
        try
        {
            var result = await install(Window.GetWindow(this)!, phase);
            SetBusy(false);
            RefreshList();
            DialogWindow.Show(result.Message, result.Success ? DialogKind.Success : DialogKind.Warning);
        }
        catch (Exception ex)
        {
            SetBusy(false);
            DialogWindow.Show(ex.Message, DialogKind.Error);
        }
    }

    private void SetBusy(bool busy)
    {
        Overlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        // The shimmer only runs while the overlay is actually shown.
        OverlayBar.IsIndeterminate = busy;
    }

    // ---- Installed list ----

    private void RefreshList()
    {
        InstalledList.Items.Clear();
        var mods = InstalledModsStore.All();
        CountText.Text = mods.Count.ToString();
        EmptyNote.Visibility = mods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var m in mods)
            InstalledList.Items.Add(BuildRow(m));
    }

    private FrameworkElement BuildRow(ModInstallRecord record)
    {
        var card = new Border
        {
            Style = (Style)FindResource("Card"),
            Padding = new Thickness(20, 16, 20, 16),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tile = new Border { Style = (Style)FindResource("IconTile"), Width = 40, Height = 40, CornerRadius = new CornerRadius(10) };
        var glyph = new TextBlock { Style = (Style)FindResource("Icon"), Text = "" };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        tile.Child = glyph;
        grid.Children.Add(tile);

        var info = new StackPanel { Margin = new Thickness(14, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(record.Name) ? "(mod)" : record.Name,
            Style = (Style)FindResource("Text.CardTitle"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        });
        var meta = new TextBlock
        {
            Text = $"{record.ModType}  ·  {record.Game}  ·  {Loc.Instance.Format("Mods_Files", record.Files.Count)}",
            Style = (Style)FindResource("Text.Desc"),
            Margin = new Thickness(0, 3, 0, 0)
        };
        info.Children.Add(meta);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var uninstall = new Button
        {
            Style = (Style)FindResource("DangerButton"),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = record
        };
        uninstall.Bind(ContentProperty, "Mods_Uninstall");
        uninstall.Click += Uninstall_Click;
        Grid.SetColumn(uninstall, 2);
        grid.Children.Add(uninstall);

        card.Child = grid;
        return card;
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not ModInstallRecord record) return;
        if (!DialogWindow.Confirm(Loc.Instance.Format("Mods_Uninstall_Confirm", record.Name), danger: true))
            return;

        SetBusy(true);
        try { await Task.Run(() => PaidModService.Uninstall(record)); }
        catch (Exception ex) { DialogWindow.Show(ex.Message, DialogKind.Error); }
        finally { SetBusy(false); }
        RefreshList();
    }
}
