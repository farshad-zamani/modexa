using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Modexa.App.Navigation;
using Modexa.App.Services;
using Modexa.Core.I18n;
using Modexa.Core.Install;
using Modexa.Core.Licensing;

namespace Modexa.App.Views;

/// <summary>
/// My Mods: everything installed on every game, grouped by game. Free and Plus see the list; Pro
/// adds management here (uninstall, open the game folder). Installing — and uninstalling for every
/// tier — happens on each game's own page.
/// </summary>
public partial class ModsView : UserControl
{
    private readonly INavigator _nav;
    private bool _busy;

    public ModsView(INavigator nav)
    {
        _nav = nav;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ThemeService.TierChanged -= OnTier;
            ThemeService.TierChanged += OnTier;
            Loc.Instance.LanguageChanged -= Refresh;
            Loc.Instance.LanguageChanged += Refresh;
            OnTier(ThemeService.CurrentTier);
        };
        Unloaded += (_, _) =>
        {
            ThemeService.TierChanged -= OnTier;
            Loc.Instance.LanguageChanged -= Refresh;
        };
    }

    private static bool IsPro => ThemeService.CurrentTier == LicenseTier.Pro;

    private void OnTier(LicenseTier tier)
    {
        bool pro = tier == LicenseTier.Pro;
        ProCardDesc.Bind(TextBlock.TextProperty, pro ? "Pro_Card_ActiveDesc" : "Pro_Card_Desc");
        BtnActivatePro.Visibility = pro ? Visibility.Collapsed : Visibility.Visible;
        ProActiveChip.Visibility = pro ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    private void ActivatePro_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main) main.ActivatePro();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _nav.GoHome();

    private void Refresh()
    {
        Groups.Children.Clear();
        var mods = InstalledModsStore.All();
        SummaryText.Text = Loc.Instance.Format("Mods_Summary", mods.Count,
            mods.Select(m => m.GameFolder).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        EmptyNote.Visibility = mods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var g in mods.GroupBy(m => FolderKey(m.GameFolder), StringComparer.OrdinalIgnoreCase)
                              .OrderBy(g => Title(g.First())))
        {
            var header = new Grid { Margin = new Thickness(0, 24, 0, 12) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            var title = new TextBlock { Text = Title(g.First()), Style = (Style)FindResource("Display.H2"), VerticalAlignment = VerticalAlignment.Center };
            title.SetResourceReference(TextBlock.FontFamilyProperty, "Font.En.Display");
            titleRow.Children.Add(title);
            var chip = new Border { Style = (Style)FindResource("Chip"), Margin = new Thickness(10, 0, 0, 0), MinHeight = 22, Padding = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var count = new TextBlock { Text = g.Count().ToString(CultureInfo.InvariantCulture), Style = (Style)FindResource("Label.Caps"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            count.SetResourceReference(TextBlock.FontFamilyProperty, "Font.En.Hud");
            chip.Child = count;
            titleRow.Children.Add(chip);
            titles.Children.Add(titleRow);
            titles.Children.Add(new TextBlock
            {
                Text = g.Key,
                Style = (Style)FindResource("Text.Desc"),
                FlowDirection = FlowDirection.LeftToRight,
                TextAlignment = Loc.Instance.IsRtl ? TextAlignment.Right : TextAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            });
            header.Children.Add(titles);

            var open = new Button { Style = (Style)FindResource("GhostButton.Small"), Tag = g.Key, VerticalAlignment = VerticalAlignment.Center };
            var openContent = new StackPanel { Orientation = Orientation.Horizontal };
            openContent.Children.Add(new TextBlock { Style = (Style)FindResource("Icon"), FontSize = 14, Text = "" });
            var openText = new TextBlock { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            openText.Bind(TextBlock.TextProperty, "Common_OpenFolder");
            openContent.Children.Add(openText);
            open.Content = openContent;
            open.Click += (s, _) => ModInstallService.OpenFolder(((FrameworkElement)s).Tag as string);
            Grid.SetColumn(open, 1);
            header.Children.Add(open);
            Groups.Children.Add(header);

            // Pro: manage right here. Free / Plus: view only (uninstall on the game's page).
            foreach (var m in g.OrderByDescending(m => m.InstalledUtc))
                Groups.Children.Add(ModRow.Build(this, m, onUninstall: IsPro ? Uninstall_Click : null));
        }
    }

    private static string FolderKey(string folder)
    {
        try { return Path.GetFullPath(folder).TrimEnd('\\'); }
        catch { return folder; }
    }

    private static string Title(ModInstallRecord m)
        => !string.IsNullOrWhiteSpace(m.GameTitle) ? m.GameTitle!
            : m.Game switch { "GtaV" => "Grand Theft Auto V", "GtaSa" => "Grand Theft Auto San Andreas", _ => m.Game };

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ((FrameworkElement)sender).Tag is not ModInstallRecord record) return;
        if (!DialogWindow.Confirm(Loc.Instance.Format("Mods_Uninstall_Confirm", record.Name), danger: true)) return;

        _busy = true;
        Overlay.Visibility = Visibility.Visible;
        OverlayBar.IsIndeterminate = true;
        try
        {
            var result = await ModInstallService.UninstallAsync(record);
            Overlay.Visibility = Visibility.Collapsed;
            OverlayBar.IsIndeterminate = false;
            DialogWindow.Show(result.Message, result.Success ? DialogKind.Success : DialogKind.Warning);
        }
        finally
        {
            _busy = false;
            Overlay.Visibility = Visibility.Collapsed;
            OverlayBar.IsIndeterminate = false;
            Refresh();
        }
    }
}
