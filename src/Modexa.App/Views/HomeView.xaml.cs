using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Modexa.App.Navigation;
using Modexa.App.Services;
using Modexa.Core.Games;

namespace Modexa.App.Views;

public partial class HomeView : UserControl
{
    private const double ReadyCardMinWidth = 300;
    private const double SoonCardMinWidth = 200;

    private readonly INavigator _nav;

    public HomeView(INavigator nav)
    {
        _nav = nav;
        InitializeComponent();
        BuildCards();
        Page.SizeChanged += (_, _) => UpdateColumns();
    }

    private void BuildCards()
    {
        foreach (var game in GameCatalog.All)
        {
            if (game.Supported) ReadyGrid.Children.Add(BuildCard(game, large: true));
            else SoonGrid.Children.Add(BuildCard(game, large: false));
        }
        bool anySoon = SoonGrid.Children.Count > 0;
        SoonHeader.Visibility = anySoon ? Visibility.Visible : Visibility.Collapsed;
        SoonGrid.Visibility = anySoon ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Responsive columns: as many cards as fit at their minimum width, stretched to fill.</summary>
    private void UpdateColumns()
    {
        double w = Page.ActualWidth + 16; // compensate for the -8 panel margins
        // The few playable titles are heroes: they share the full row instead of leaving gaps.
        SetColumns(ReadyGrid, w, ReadyCardMinWidth, fillRow: true);
        SetColumns(SoonGrid, w, SoonCardMinWidth, fillRow: false);
    }

    private static void SetColumns(UniformGrid grid, double width, double minCard, bool fillRow)
    {
        int cols = Math.Max(1, (int)(width / (minCard + 16)));
        if (fillRow) cols = Math.Max(1, Math.Min(cols, grid.Children.Count));
        if (grid.Columns != cols) grid.Columns = cols;
    }

    private FrameworkElement BuildCard(GameInfo game, bool large)
    {
        var card = new Button
        {
            Height = large ? 148 : 128,
            Margin = new Thickness(8),
            Tag = game,
            Style = (Style)FindResource("GameCard"),
            IsEnabled = game.Supported,
            Content = BuildCardContent(game, large)
        };
        AutomationProperties_SetName(card, game.DisplayName);
        if (game.Supported)
            card.Click += Card_Click;
        return card;
    }

    private static void AutomationProperties_SetName(DependencyObject d, string name)
        => System.Windows.Automation.AutomationProperties.SetName(d, name);

    private static FrameworkElement BuildCardContent(GameInfo game, bool large)
    {
        var root = new Grid { Margin = new Thickness(large ? 20 : 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Monogram tile (placeholder art until logos arrive). Resource references keep it live
        // across theme and language swaps.
        double tileSize = large ? 52 : 40;
        var tile = new Border
        {
            Width = tileSize,
            Height = tileSize,
            CornerRadius = new CornerRadius(large ? 12 : 10),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        tile.SetResourceReference(Border.BackgroundProperty, "Bg.Inset");
        tile.SetResourceReference(Border.BorderBrushProperty, game.Supported ? "Stroke.Strong" : "Stroke.Subtle");
        var mono = new TextBlock
        {
            Text = Monogram(game.Id),
            FontWeight = FontWeights.Bold,
            FontSize = large ? 19 : 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FlowDirection = FlowDirection.LeftToRight
        };
        mono.SetResourceReference(TextBlock.FontFamilyProperty, "Font.En.Display");
        mono.SetResourceReference(TextBlock.ForegroundProperty, game.Supported ? "Accent" : "Text.Muted");
        tile.Child = mono;
        root.Children.Add(tile);

        // Status chip, top-trailing corner.
        var chip = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            BorderThickness = new Thickness(1)
        };
        chip.SetResourceReference(Border.BackgroundProperty, game.Supported ? "Accent.Soft" : "Bg.Inset");
        chip.SetResourceReference(Border.BorderBrushProperty, game.Supported ? "Stroke.Accent" : "Stroke.Subtle");
        var chipText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold };
        chipText.Bind(TextBlock.TextProperty, game.Supported ? "Home_Supported" : "Home_ComingSoon");
        chipText.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Hud");
        chipText.SetResourceReference(TextBlock.ForegroundProperty, game.Supported ? "Accent" : "Text.Muted");
        chip.Child = chipText;
        Grid.SetColumn(chip, 1);
        root.Children.Add(chip);

        // Game name (product names stay in Latin script; alignment follows the page direction).
        var name = new TextBlock
        {
            Text = game.DisplayName,
            FontSize = large ? 17 : 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = large ? 48 : 40,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 12, 0, 0)
        };
        name.SetResourceReference(TextBlock.FontFamilyProperty, "Font.En.Display");
        name.SetResourceReference(TextBlock.ForegroundProperty, game.Supported ? "Text.Primary" : "Text.Secondary");
        Grid.SetRow(name, 1);
        Grid.SetColumnSpan(name, 2);
        root.Children.Add(name);

        return root;
    }

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not GameInfo game) return;
        switch (game.Id)
        {
            case GameId.GtaV:
                _nav.Navigate(new GtaVView(_nav, GameEdition.Legacy));
                break;
            case GameId.GtaVEnhanced:
                _nav.Navigate(new GtaVView(_nav, GameEdition.Enhanced));
                break;
            default:
                if (SimpleGames.Has(game.Id)) _nav.Navigate(new SimpleGameView(_nav, game.Id));
                break;
        }
    }

    /// <summary>Short monogram for the placeholder art until real logos are provided.</summary>
    public static string Monogram(GameId id) => id switch
    {
        GameId.GtaV => "VL",
        GameId.GtaVEnhanced => "VE",
        GameId.GtaSanAndreas => "SA",
        GameId.GtaTrilogy => "TR",
        GameId.GtaIV => "IV",
        GameId.RedDeadRedemption1 => "RDR",
        GameId.RedDeadRedemption2 => "RDR2",
        GameId.AssettoCorsa => "AC",
        GameId.EuroTruckSimulator2 => "ETS",
        GameId.Cyberpunk2077 => "CP",
        GameId.ForzaHorizon5 => "FH5",
        GameId.BeamNgDrive => "BNG",
        _ => "?"
    };
}
