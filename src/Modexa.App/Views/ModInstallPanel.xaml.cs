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
using Modexa.Core.Rpf;
using WinForms = System.Windows.Forms;

namespace Modexa.App.Views;

/// <summary>A page that can show its busy overlay for the install panel.</summary>
public interface IInstallHost
{
    void ShowBusy(InstallPhase phase);
    void HideBusy();
}

/// <summary>
/// "Install mods" section of a game page: drag &amp; drop or pick files (.oiv/.oivs on GTA V for
/// every tier, licensed .mxa packages, and for Pro also add-on .rpf / .zip), then everything
/// installed into this game folder — mods and prepare steps alike — each removable with one click.
/// </summary>
public partial class ModInstallPanel : UserControl
{
    private GameTarget? _target;
    private bool _busy;
    private List<UnusedArchive> _unused = new();

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

    public async void Refresh()
    {
        var target = _target;
        var (mods, unused) = target == null
            ? (new List<ModInstallRecord>(), new List<UnusedArchive>())
            : await Task.Run(() => (InstalledModsStore.ForFolder(target.Folder), SafeUnused(target.Folder)));
        if (!ReferenceEquals(target, _target)) return; // the folder changed meanwhile

        InstalledList.Items.Clear();
        mods.Reverse(); // newest first
        CountText.Text = mods.Count.ToString(CultureInfo.InvariantCulture);
        EmptyNote.Visibility = mods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var m in mods)
            InstalledList.Items.Add(ModRow.Build(this, m, onUninstall: Uninstall_Click));

        _unused = unused;
        long bytes = unused.Sum(u => u.Bytes);
        UnusedCard.Visibility = unused.Count > 0 && !_busy ? Visibility.Visible : Visibility.Collapsed;
        UnusedText.Text = Loc.Instance.Format("Mods_Unused_Desc", unused.Count, ModInstallService.Size(bytes));
        BtnFreeUnused.Content = Loc.Instance.Format("Mods_Unused_Remove", ModInstallService.Size(bytes));
    }

    private static List<UnusedArchive> SafeUnused(string folder)
    {
        try { return ModsArchives.Unused(folder); }
        catch { return new List<UnusedArchive>(); }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ((FrameworkElement)sender).Tag is not ModInstallRecord record) return;
        var records = await ConfirmUninstallAsync(record);
        if (records == null) return;

        _busy = true;
        try
        {
            var result = await ModInstallService.UninstallAsync(records, BusyProgress());
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

    /// <summary>
    /// Asks before removing. A split mod (several installed parts, e.g. Package.1 / Package.2) can be
    /// removed part by part or all together. Returns what to remove, or null when cancelled.
    /// </summary>
    public static async Task<List<ModInstallRecord>?> ConfirmUninstallAsync(ModInstallRecord record)
    {
        var parts = await Task.Run(() => ModInstallService.PartsOf(record));
        string name = ModInstallService.DisplayName(record);
        byte[]? icon = null;
        try { if (record.IconFile != null && File.Exists(record.IconFile)) icon = File.ReadAllBytes(record.IconFile); } catch { }

        if (parts.Count > 1)
        {
            string list = string.Join("\n", parts.OrderBy(p => p.Source, NaturalComparer.Instance).Select(p => "• " + (p.Source ?? p.Name)));
            int pick = DialogWindow.Ask(Loc.Instance.Format("Mods_Uninstall_Parts", name, parts.Count, list),
                new[] { Loc.Instance["Mods_Uninstall_ThisPart"], Loc.Instance.Format("Mods_Uninstall_AllParts", parts.Count), Loc.Instance["Common_Cancel"] },
                dangerPrimary: true, image: icon);
            return pick switch
            {
                0 => new List<ModInstallRecord> { record },
                1 => parts,
                _ => null
            };
        }
        return DialogWindow.Prompt(Loc.Instance.Format("Mods_Uninstall_Confirm", name), Loc.Instance["Mods_Uninstall"],
                   Loc.Instance["Common_Cancel"], DialogKind.Question, image: icon)
            ? new List<ModInstallRecord> { record }
            : null;
    }

    private async void FreeUnused_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _target == null || _unused.Count == 0) return;
        var unused = _unused.ToList();
        if (!DialogWindow.Confirm(Loc.Instance.Format("Mods_Unused_Confirm", unused.Count, ModInstallService.Size(unused.Sum(u => u.Bytes))), danger: true))
            return;
        _busy = true;
        try
        {
            var result = await ModInstallService.FreeUnusedAsync(_target.Folder, unused, BusyProgress());
            Host?.HideBusy();
            DialogWindow.Show(result.Message, result.Success ? DialogKind.Success : DialogKind.Warning);
        }
        finally
        {
            _busy = false;
            Host?.HideBusy();
            Refresh();
        }
    }

    private IProgress<InstallPhase> BusyProgress() => new Progress<InstallPhase>(p =>
    {
        if (!_busy) return; // a late report after completion must not re-open the overlay
        if (p.Key == InstallPhase.Hide) Host?.HideBusy();
        else Host?.ShowBusy(p);
    });

    // ---- install -------------------------------------------------------------------------------

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        bool pro = ThemeService.CurrentTier == LicenseTier.Pro;
        string filter = !GtaV
            ? $"{Loc.Instance["Mods_Filter_Mxa"]} (*.mxa)|*.mxa"
            : pro
                ? $"{Loc.Instance["Mods_Filter_All"]} (*.oiv;*.oivs;*.mxa;*.rpf;*.zip)|*.oiv;*.oivs;*.mxa;*.rpf;*.zip|OpenIV (*.oiv;*.oivs)|*.oiv;*.oivs|Modexa (*.mxa)|*.mxa"
                : $"{Loc.Instance["Mods_Filter_All"]} (*.oiv;*.oivs;*.mxa)|*.oiv;*.oivs;*.mxa|OpenIV (*.oiv;*.oivs)|*.oiv;*.oivs|Modexa (*.mxa)|*.mxa";
        // Several files at once: a mod split into parts is installed part 1, part 2, … in one go.
        using var dlg = new WinForms.OpenFileDialog { Filter = filter, Title = Loc.Instance["Mods_ChooseFile"], Multiselect = true };
        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            _ = InstallAsync(dlg.FileNames);
    }

    /// <summary>Installs a file into this page's game (also used for files opened from Explorer).</summary>
    public Task InstallAsync(string path) => InstallAsync(new[] { path });

    public async Task InstallAsync(IReadOnlyList<string> paths)
    {
        if (_busy || paths.Count == 0) return;
        if (_target == null)
        {
            DialogWindow.Show(Loc.Instance["Dl_NoGameFolder"], DialogKind.Warning);
            return;
        }
        var files = paths.Where(p => ModInstallService.IsInstallable(p, GtaV)).ToList();
        if (files.Count == 0)
        {
            DialogWindow.Show(Loc.Instance[GtaV ? "Mods_Unsupported" : "Mods_UnsupportedHere"], DialogKind.Warning);
            return;
        }

        _busy = true;
        UnusedCard.Visibility = Visibility.Collapsed;
        try
        {
            var results = await ModInstallService.InstallManyAsync(files, _target, Window.GetWindow(this)!, BusyProgress());
            _busy = false;
            Host?.HideBusy();
            var shown = results.Where(r => !r.Cancelled).ToList();
            if (shown.Count > 0)
                DialogWindow.Show(string.Join("\n\n", shown.Select(r => r.Message)),
                    shown.All(r => r.Success) ? DialogKind.Success : DialogKind.Warning);
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

    private List<string> DroppedFiles(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return new List<string>();
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        // Extension check only: DragOver fires continuously, so no file I/O on this path.
        return files.Where(f => f.EndsWith(MxaFile.Extension, StringComparison.OrdinalIgnoreCase)
                                || (GtaV && ModInstallService.OivExtensions.Concat(ModInstallService.RawExtensions)
                                    .Any(x => f.EndsWith(x, StringComparison.OrdinalIgnoreCase))))
            .ToList();
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        bool ok = DroppedFiles(e).Count > 0;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropHighlight(ok);
        e.Handled = true;
    }

    private void Root_DragLeave(object sender, DragEventArgs e) => SetDropHighlight(false);

    private void Root_Drop(object sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        e.Handled = true;
        var files = DroppedFiles(e);
        if (files.Count > 0) _ = InstallAsync(files);
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

        // The package's own icon when it has one, else a glyph for the kind.
        var tile = new Border { Style = (Style)owner.FindResource("IconTile"), Width = 40, Height = 40, CornerRadius = new CornerRadius(10) };
        if (BusyOverlay.LoadImage(record.IconFile, 80) is { } icon)
        {
            tile.Child = new Image { Source = icon, Stretch = Stretch.UniformToFill };
            tile.Clip = new RectangleGeometry(new Rect(0, 0, 40, 40), 10, 10);
        }
        else
        {
            var glyph = new TextBlock
            {
                Style = (Style)owner.FindResource("Icon"),
                Text = record.KindLabel switch { "OIV" => "\uE7B8", "Modexa" => "\uE8D7", "Add-on" => "\uE710", "Setup" => "\uE90F", _ => "\uE8B7" }
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            tile.Child = glyph;
        }
        grid.Children.Add(tile);

        var info = new StackPanel { Margin = new Thickness(14, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock
        {
            Text = ModInstallService.DisplayName(record),
            Style = (Style)owner.FindResource("Text.CardTitle"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        var kind = new Border { Style = (Style)owner.FindResource("TierChip"), Margin = new Thickness(10, 0, 0, 0), MinHeight = 20, Padding = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        var kindText = new TextBlock { FontSize = 11, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        if (record.Setup != null) kindText.Bind(TextBlock.TextProperty, "Mods_Kind_Setup");
        else kindText.Text = record.KindLabel;
        kindText.SetResourceReference(TextBlock.FontFamilyProperty, record.Setup != null ? "Font.Hud" : "Font.En.Hud");
        kindText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        kind.Child = kindText;
        title.Children.Add(kind);
        info.Children.Add(title);

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(record.Version)) parts.Add(record.Setup == null ? "v" + record.Version : record.Version!);
        if (!string.IsNullOrWhiteSpace(record.Author)) parts.Add(record.Author!);
        parts.Add(record.InstalledUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        int changes = record.Files.Count + record.ArchiveEdits.Count;
        parts.Add(Loc.Instance.Format("Mods_Changes", changes));
        if (!string.IsNullOrWhiteSpace(record.Source)) parts.Add(record.Source!);
        var meta = new TextBlock
        {
            // Each part ends with a left-to-right mark, so a Persian part ("2 تغییر") never pulls the
            // numbers of the next one ("206.oiv") into right-to-left order.
            Text = string.Join("\u200E  ·  ", parts) + "\u200E",
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
            open.Content = new TextBlock { Style = (Style)owner.FindResource("Icon"), FontSize = 15, Text = "\uE838" };
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
