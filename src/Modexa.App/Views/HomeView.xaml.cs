using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
        // Layers: the game's artwork filling the card -> a dark scrim (light at the top, deep where the
        // name sits) so text stays readable on any picture -> icon, status chip and name.
        var frame = new Grid();
        if (GameArt.Artwork(game.Id, decodeWidth: large ? 760 : 520, gray: !game.Supported) is { } art)
        {
            frame.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(13),
                Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill, AlignmentX = GameArt.ArtAlignment(game.Id), AlignmentY = AlignmentY.Center },
                Opacity = game.Supported ? 1 : 0.55,
                FlowDirection = FlowDirection.LeftToRight // pictures are never mirrored on a right-to-left page
            });
            frame.Children.Add(new Border { CornerRadius = new CornerRadius(13), Background = Scrim(game.Supported) });
        }

        var root = new Grid { Margin = new Thickness(large ? 20 : 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        frame.Children.Add(root);

        // The game's icon (its monogram when the icon is missing).
        double tileSize = large ? 52 : 40;
        var tile = new Border
        {
            Width = tileSize,
            Height = tileSize,
            CornerRadius = new CornerRadius(large ? 12 : 10),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 0, Opacity = 0.55, Color = Colors.Black },
            FlowDirection = FlowDirection.LeftToRight
        };
        tile.SetResourceReference(Border.BorderBrushProperty, game.Supported ? "Stroke.Strong" : "Stroke.Subtle");
        if (GameArt.Icon(game.Id) is { } icon)
        {
            tile.Background = new ImageBrush(icon) { Stretch = Stretch.UniformToFill };
            if (!game.Supported) tile.Opacity = 0.75;
        }
        else
        {
            tile.SetResourceReference(Border.BackgroundProperty, "Bg.Inset");
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
        }
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
        chip.SetResourceReference(Border.BackgroundProperty, "Bg.Inset"); // solid: readable on any artwork
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
        name.Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9, Color = Colors.Black };
        Grid.SetRow(name, 1);
        Grid.SetColumnSpan(name, 2);
        root.Children.Add(name);

        return frame;
    }

    /// <summary>Dark veil over the artwork: see-through at the top, nearly solid behind the name.</summary>
    private static Brush Scrim(bool supported)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(supported ? (byte)0x40 : (byte)0x70, 6, 8, 11), 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(supported ? (byte)0x80 : (byte)0xA0, 6, 8, 11), 0.5));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0xEC, 6, 8, 11), 1));
        b.Freeze();
        return b;
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
