using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Modexa.App.Services;
using Modexa.Core.Format;
using Modexa.Core.I18n;
using Modexa.Core.Install;
using Modexa.Core.Licensing;
using WinForms = System.Windows.Forms;

namespace Modexa.App.Views;

/// <summary>A page that can show its busy overlay for the install panel.</summary>
public interface IInstallHost
{
    void ShowBusy(string phaseKey, int? percent);
    void HideBusy();
}

/// <summary>
/// "Install mods" section of a game page: drag &amp; drop or pick a file (.oiv/.oivs on GTA V for
/// every tier, licensed .mxa packages, and for Pro also add-on .rpf / .zip), then the list of mods
/// installed into this game folder — each removable with one click.
/// </summary>
public partial class ModInstallPanel : UserControl
{
    private GameTarget? _target;
    private bool _busy;

    public IInstallHost? Host { get; set; }

    /// <summary>Raised after an install or uninstall finished.</summary>
    public event Action? Changed;

    public ModInstallPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Loc.Instance.LanguageChanged -= RefreshTexts;
            Loc.Instance.LanguageChanged += RefreshTexts;
            ThemeService.TierChanged -= OnTier;
            ThemeService.TierChanged += OnTier;
            RefreshTexts();
        };
        Unloaded += (_, _) =>
        {
            Loc.Instance.LanguageChanged -= RefreshTexts;
            ThemeService.TierChanged -= OnTier;
        };
    }

    private bool GtaV => _target?.IsGtaV ?? true;

    /// <summary>The game folder the panel installs into (null = not detected yet).</summary>
    public void SetTarget(GameTarget? target)
    {
        _target = target;
        RefreshTexts();
        Refresh();
    }

    private void OnTier(LicenseTier _) => RefreshTexts();

    private void RefreshTexts()
    {
        bool pro = ThemeService.CurrentTier == LicenseTier.Pro;
        SectionDesc.Text = Loc.Instance[GtaV ? "Mods_Section_Desc_GtaV" : "Mods_Section_Desc_Other"];
        DropHint.Text = Loc.Instance[GtaV ? (pro ? "Mods_Drop_Hint_GtaV_Pro" : "Mods_Drop_Hint_GtaV") : "Mods_Drop_Hint_Other"];
        Refresh(); // rows are built in code: rebuild them in the new language / direction
    }

    // ---- list ----------------------------------------------------------------------------------

    public void Refresh()
    {
        InstalledList.Items.Clear();
        var mods = _target == null ? new List<ModInstallRecord>() : InstalledModsStore.ForFolder(_target.Folder);
        mods.Reverse(); // newest first
        CountText.Text = mods.Count.ToString(CultureInfo.InvariantCulture);
        EmptyNote.Visibility = mods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var m in mods)
            InstalledList.Items.Add(ModRow.Build(this, m, onUninstall: Uninstall_Click));
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ((FrameworkElement)sender).Tag is not ModInstallRecord record) return;
        if (!DialogWindow.Confirm(Loc.Instance.Format("Mods_Uninstall_Confirm", record.Name), danger: true)) return;

        _busy = true;
        Host?.ShowBusy("Mods_Uninstalling", null);
        try
        {
            var result = await ModInstallService.UninstallAsync(record);
            Host?.HideBusy();
            DialogWindow.Show(result.Message, result.Success ? DialogKind.Success : DialogKind.Warning);
        }
        finally
        {
            _busy = false;
            Host?.HideBusy();
            Refresh();
            Changed?.Invoke();
        }
    }

    // ---- install -------------------------------------------------------------------------------

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        bool pro = ThemeService.CurrentTier == LicenseTier.Pro;
        string filter = !GtaV
            ? $"{Loc.Instance["Mods_Filter_Mxa"]} (*.mxa)|*.mxa"
            : pro
                ? $"{Loc.Instance["Mods_Filter_All"]} (*.oiv;*.oivs;*.mxa;*.rpf;*.zip)|*.oiv;*.oivs;*.mxa;*.rpf;*.zip|OpenIV (*.oiv;*.oivs)|*.oiv;*.oivs|Modexa (*.mxa)|*.mxa"
                : $"{Loc.Instance["Mods_Filter_All"]} (*.oiv;*.oivs;*.mxa)|*.oiv;*.oivs;*.mxa|OpenIV (*.oiv;*.oivs)|*.oiv;*.oivs|Modexa (*.mxa)|*.mxa";
        using var dlg = new WinForms.OpenFileDialog { Filter = filter, Title = Loc.Instance["Mods_ChooseFile"] };
        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            _ = InstallAsync(dlg.FileName);
    }

    /// <summary>Installs a file into this page's game (also used for files opened from Explorer).</summary>
    public async Task InstallAsync(string path)
    {
        if (_busy) return;
        if (_target == null)
        {
            DialogWindow.Show(Loc.Instance["Dl_NoGameFolder"], DialogKind.Warning);
            return;
        }
        if (!ModInstallService.IsInstallable(path, GtaV))
        {
            DialogWindow.Show(Loc.Instance[GtaV ? "Mods_Unsupported" : "Mods_UnsupportedHere"], DialogKind.Warning);
            return;
        }

        _busy = true;
        var phase = new Progress<InstallPhase>(p =>
        {
            if (!_busy) return; // a late report after completion must not re-open the overlay
            if (p.Key == InstallPhase.Hide) Host?.HideBusy();
            else Host?.ShowBusy(p.Key, p.Percent);
        });
        try
        {
            var result = await ModInstallService.InstallAsync(path, _target, Window.GetWindow(this)!, phase);
            _busy = false;
            Host?.HideBusy();
            if (!result.Cancelled)
                DialogWindow.Show(result.Message, result.Success ? DialogKind.Success : DialogKind.Warning);
        }
        catch (Exception ex)
        {
            Modexa.Core.Diagnostics.Log.Error("Install", ex);
            Host?.HideBusy();
            DialogWindow.Show(ex.Message, DialogKind.Error);
        }
        finally
        {
            _busy = false;
            Host?.HideBusy();
            Refresh();
            Changed?.Invoke();
        }
    }

    // ---- drag & drop ---------------------------------------------------------------------------

    private string? FirstFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        // Extension check only: DragOver fires continuously, so no file I/O on this path.
        return files.FirstOrDefault(f => f.EndsWith(MxaFile.Extension, StringComparison.OrdinalIgnoreCase)
                                         || (GtaV && ModInstallService.OivExtensions.Concat(ModInstallService.RawExtensions)
                                             .Any(x => f.EndsWith(x, StringComparison.OrdinalIgnoreCase))));
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        bool ok = FirstFile(e) != null;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropHighlight(ok);
        e.Handled = true;
    }

    private void Root_DragLeave(object sender, DragEventArgs e) => SetDropHighlight(false);

    private void Root_Drop(object sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        e.Handled = true;
        var file = FirstFile(e);
        if (file != null) _ = InstallAsync(file);
    }

    private void SetDropHighlight(bool on)
    {
        DropOutline.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, on ? "Stroke.Accent" : "Stroke.Strong");
        DropBg.SetResourceReference(Border.BackgroundProperty, on ? "Bg.Elevated" : "Bg.Panel");
    }
}

/// <summary>One installed-mod row (shared by the game pages and My Mods).</summary>
public static class ModRow
{
    public static FrameworkElement Build(FrameworkElement owner, ModInstallRecord record,
        RoutedEventHandler? onUninstall, RoutedEventHandler? onOpenFolder = null)
    {
        var card = new Border
        {
            Style = (Style)owner.FindResource("Card"),
            Padding = new Thickness(18, 14, 18, 14),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tile = new Border { Style = (Style)owner.FindResource("IconTile"), Width = 40, Height = 40, CornerRadius = new CornerRadius(10) };
        var glyph = new TextBlock
        {
            Style = (Style)owner.FindResource("Icon"),
            Text = record.KindLabel switch { "OIV" => "", "Modexa" => "", "Add-on" => "", _ => "" }
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        tile.Child = glyph;
        grid.Children.Add(tile);

        var info = new StackPanel { Margin = new Thickness(14, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(record.Name) ? "(mod)" : record.Name,
            Style = (Style)owner.FindResource("Text.CardTitle"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        var kind = new Border { Style = (Style)owner.FindResource("TierChip"), Margin = new Thickness(10, 0, 0, 0), MinHeight = 20, Padding = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        var kindText = new TextBlock { Text = record.KindLabel, FontSize = 11, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        kindText.SetResourceReference(TextBlock.FontFamilyProperty, "Font.En.Hud");
        kindText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        kind.Child = kindText;
        title.Children.Add(kind);
        info.Children.Add(title);

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(record.Version)) parts.Add("v" + record.Version);
        if (!string.IsNullOrWhiteSpace(record.Author)) parts.Add(record.Author!);
        parts.Add(record.InstalledUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        int changes = record.Files.Count + record.ArchiveEdits.Count;
        parts.Add(Loc.Instance.Format("Mods_Changes", changes));
        if (!string.IsNullOrWhiteSpace(record.Source)) parts.Add(record.Source!);
        var meta = new TextBlock
        {
            Text = string.Join("  ·  ", parts),
            Style = (Style)owner.FindResource("Text.Desc"),
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            // File names, versions and dates are Latin: lay the line out LTR (aligned to the page side)
            // so a Persian page doesn't scramble "206.oiv" into "oiv.206".
            FlowDirection = FlowDirection.LeftToRight,
            TextAlignment = Loc.Instance.IsRtl ? TextAlignment.Right : TextAlignment.Left
        };
        NumberSubstitution.SetSubstitution(meta, NumberSubstitutionMethod.European);
        info.Children.Add(meta);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (onOpenFolder != null)
        {
            var open = new Button { Style = (Style)owner.FindResource("IconButton"), Tag = record, Margin = new Thickness(0, 0, 8, 0) };
            open.Content = new TextBlock { Style = (Style)owner.FindResource("Icon"), FontSize = 15, Text = "" };
            open.SetBinding(FrameworkElement.ToolTipProperty, Tr.B("Common_OpenFolder"));
            open.Click += onOpenFolder;
            actions.Children.Add(open);
        }
        if (onUninstall != null)
        {
            var uninstall = new Button { Style = (Style)owner.FindResource("DangerButton"), Tag = record };
            uninstall.Bind(ContentControl.ContentProperty, "Mods_Uninstall");
            uninstall.Click += onUninstall;
            actions.Children.Add(uninstall);
        }
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        card.Child = grid;
        return card;
    }
}
