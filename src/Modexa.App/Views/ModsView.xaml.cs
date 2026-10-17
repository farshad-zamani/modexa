using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Modexa.App.Navigation;
using Modexa.App.Services;
using Modexa.Core.I18n;
using Modexa.Core.Install;
using Modexa.Core.Licensing;

namespace Modexa.App.Views;

/// <summary>
/// My Mods: everything installed on every game — mods and prepare steps — grouped by game, each
/// with its uninstall button (every tier). Installing happens on each game's own page.
/// </summary>
public partial class ModsView : UserControl, IInstallHost
{
    private readonly INavigator _nav;
    private bool _busy;
    /// <summary>Ticked rows, per game folder (each game section has its own selection bar).</summary>
    private readonly Dictionary<string, ModSelection> _selections = new(StringComparer.OrdinalIgnoreCase);

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
        var mods = ModItem.All();
        SummaryText.Text = Loc.Instance.Format("Mods_Summary", mods.Count(m => m.Record != null),
            mods.Select(m => m.GameFolder).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        EmptyNote.Visibility = mods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var g in mods.GroupBy(m => FolderKey(m.GameFolder), StringComparer.OrdinalIgnoreCase)
                              .OrderBy(g => Title(g.First())))
        {
            // Each game is a collapsible section: the header (arrow + title) opens and closes its list.
            string key = g.Key;
            bool open = !Collapsed.Contains(key);
            var header = new Grid { Margin = new Thickness(0, 24, 0, 12), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var chevron = new TextBlock
            {
                Style = (Style)FindResource("Icon"), FontSize = 12, Text = "\uE70D",
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(open ? 0 : -90)
            };
            chevron.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            header.Children.Add(chevron);
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(titles, 1);
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

            var openBtn = new Button { Style = (Style)FindResource("GhostButton.Small"), Tag = g.Key, VerticalAlignment = VerticalAlignment.Center };
            var openContent = new StackPanel { Orientation = Orientation.Horizontal };
            openContent.Children.Add(new TextBlock { Style = (Style)FindResource("Icon"), FontSize = 14, Text = "" });
            var openText = new TextBlock { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            openText.Bind(TextBlock.TextProperty, "Common_OpenFolder");
            openContent.Children.Add(openText);
            openBtn.Content = openContent;
            openBtn.Click += (s, _) => ModInstallService.OpenFolder(((FrameworkElement)s).Tag as string);
            Grid.SetColumn(openBtn, 2);
            header.Children.Add(openBtn);
            Groups.Children.Add(header);

            var list = new StackPanel { Visibility = open ? Visibility.Visible : Visibility.Collapsed };
            if (!_selections.TryGetValue(key, out var selection)) _selections[key] = selection = new ModSelection();
            ModList.Fill(list, this, g.OrderByDescending(m => m.InstalledUtc).ToList(), selection, new GroupHost(this));
            Groups.Children.Add(list);

            header.MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return; // the Open folder button
                bool nowOpen = list.Visibility != Visibility.Visible;
                list.Visibility = nowOpen ? Visibility.Visible : Visibility.Collapsed;
                if (nowOpen) Collapsed.Remove(key); else Collapsed.Add(key);
                ((RotateTransform)chevron.RenderTransform).BeginAnimation(RotateTransform.AngleProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(nowOpen ? 0 : -90, TimeSpan.FromMilliseconds(160)));
            };
        }
    }

    /// <summary>Game sections the user closed (kept while the app runs).</summary>
    private static readonly HashSet<string> Collapsed = new(StringComparer.OrdinalIgnoreCase);

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var cur = d; cur != null; cur = VisualTreeHelper.GetParent(cur))
            if (cur is T hit) return hit;
        return null;
    }

    private static string FolderKey(string folder)
    {
        try { return Path.GetFullPath(folder).TrimEnd('\\'); }
        catch { return folder; }
    }

    private static string Title(ModItem m)
    {
        string? title = m.Record?.GameTitle ?? m.Disabled?.GameTitle;
        string game = m.Record?.Game ?? m.Disabled!.Game;
        return !string.IsNullOrWhiteSpace(title) ? title!
            : game switch { "GtaV" => "Grand Theft Auto V", "GtaSa" => "Grand Theft Auto San Andreas", _ => game };
    }

    // ---- IInstallHost: the page's busy overlay ----

    public void ShowBusy(InstallPhase phase) => Busy.Update(phase);

    public void HideBusy() => Busy.Hide();

    /// <summary>Remove / disable / enable from a game section's list.</summary>
    private async void Run(ModAction action, IReadOnlyList<ModItem> items, bool single)
    {
        if (_busy || ModActions.IsRunning) return;
        _busy = true;
        try
        {
            if (await ModActions.RunAsync(action, items, single, this, Window.GetWindow(this)!))
                foreach (var s in _selections.Values) s.SetAll(items.Select(i => i.Id), false);
        }
        finally
        {
            _busy = false;
            Busy.Hide();
            Refresh();
        }
    }

    private sealed class GroupHost : IModListHost
    {
        private readonly ModsView _view;
        public GroupHost(ModsView view) => _view = view;
        public void Run(ModAction action, IReadOnlyList<ModItem> items, bool single) => _view.Run(action, items, single);
    }
}
