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
/// every tier, licensed .mxa packages, and for Pro any mod: .zip / .rar / .7z, folders, add-on .rpf), then everything
/// installed into this game folder — mods and prepare steps alike — each removable with one click.
/// </summary>
public partial class ModInstallPanel : UserControl, IModListHost
{
    private GameTarget? _target;
    private bool _busy;
    private List<UnusedArchive> _unused = new();
    private readonly ModSelection _selection = new();

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
        BtnChooseFolder.Visibility = GtaV ? Visibility.Visible : Visibility.Collapsed;
        Refresh(); // rows are built in code: rebuild them in the new language / direction
    }

    // ---- list ----------------------------------------------------------------------------------

    public async void Refresh()
    {
        var target = _target;
        var (mods, unused) = target == null
            ? (new List<ModItem>(), new List<UnusedArchive>())
            : await Task.Run(() => (ModItem.ForFolder(target.Folder), SafeUnused(target.Folder)));
        if (!ReferenceEquals(target, _target)) return; // the folder changed meanwhile

        // Newest first; disabled mods stay listed (with Enable) so they are easy to turn back on.
        CountText.Text = mods.Count.ToString(CultureInfo.InvariantCulture);
        EmptyNote.Visibility = mods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ModList.Fill(InstalledList, this, mods, _selection, this);

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

    /// <summary>Remove / disable / enable from the list (one row, or the ticked rows).</summary>
    public async void Run(ModAction action, IReadOnlyList<ModItem> items, bool single)
    {
        if (_busy || ModActions.IsRunning) return;
        _busy = true;
        UnusedCard.Visibility = Visibility.Collapsed;
        bool changed = false;
        try
        {
            changed = await ModActions.RunAsync(action, items, single, Host, Window.GetWindow(this)!);
            if (changed) _selection.SetAll(items.Select(i => i.Id), false);
        }
        finally
        {
            _busy = false;
            Host?.HideBusy();
            Refresh();
            if (changed) Changed?.Invoke();
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
        string loose = string.Join(";", ModInstallService.LooseExtensions.Select(x => "*" + x));
        string filter = !GtaV
            ? $"{Loc.Instance["Mods_Filter_Mxa"]} (*.mxa)|*.mxa"
            : pro
                ? $"{Loc.Instance["Mods_Filter_All"]}|*.oiv;*.oivs;*.mxa;*.zip;*.rar;*.7z;*.rpf;{loose}|OpenIV (*.oiv;*.oivs)|*.oiv;*.oivs|Modexa (*.mxa)|*.mxa|{Loc.Instance["Mods_Filter_GameFiles"]}|{loose}"
                :$"{Loc.Instance["Mods_Filter_All"]} (*.oiv;*.oivs;*.mxa)|*.oiv;*.oivs;*.mxa|OpenIV (*.oiv;*.oivs)|*.oiv;*.oivs|Modexa (*.mxa)|*.mxa";
        // Several files at once: a mod split into parts is installed part 1, part 2, … in one go.
        using var dlg = new WinForms.OpenFileDialog { Filter = filter, Title = Loc.Instance["Mods_ChooseFile"], Multiselect = true };
        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            _ = InstallAsync(dlg.FileNames);
    }

    /// <summary>A folder of mods (GTA V): read like an archive, the user ticks what to install.</summary>
    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new WinForms.FolderBrowserDialog { Description = Loc.Instance["Mods_ChooseFolder_Tip"], UseDescriptionForTitle = true };
        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            _ = InstallAsync(new[] { dlg.SelectedPath });
    }

    /// <summary>Installs a file into this page's game (also used for files opened from Explorer).</summary>
    public Task InstallAsync(string path) => InstallAsync(new[] { path });

    public async Task InstallAsync(IReadOnlyList<string> paths)
    {
        if (_busy || ModActions.IsRunning || paths.Count == 0) return;
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
            // One short summary, however many mods were installed.
            var summary = ModInstallService.Summarize(results);
            if (!summary.Cancelled && summary.Message.Length > 0)
                DialogWindow.Show(summary.Message, summary.Success ? DialogKind.Success : DialogKind.Warning);
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
        // Extension check (plus a cheap "is it a folder" for GTA V: a folder of mods is read like an archive).
        return files.Where(f => ModInstallService.IsInstallable(f, GtaV)).ToList();
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
