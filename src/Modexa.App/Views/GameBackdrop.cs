using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Modexa.App.Services;
using Modexa.Core.Games;

namespace Modexa.App.Views;

/// <summary>
/// The game's own look behind its page: its artwork, very faint, across the top of the page and
/// fading out downwards, so the page feels like that game without competing with the content.
/// </summary>
public sealed class GameBackdrop : Grid
{
    private readonly Border _art = new()
    {
        Height = 460,
        VerticalAlignment = VerticalAlignment.Top,
        Opacity = 0.2
    };

    public GameBackdrop()
    {
        IsHitTestVisible = false;
        FlowDirection = FlowDirection.LeftToRight; // the artwork is never mirrored on a right-to-left page
        ClipToBounds = true;
        var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        fade.GradientStops.Add(new GradientStop(Colors.Black, 0));
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(0x99, 0, 0, 0), 0.45));
        fade.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        fade.Freeze();
        _art.OpacityMask = fade;
        Children.Add(_art);
    }

    /// <summary>Shows <paramref name="game"/>'s artwork (nothing when it has none).</summary>
    public void SetGame(GameId game)
    {
        var art = GameArt.Artwork(game);
        _art.Background = art == null ? null
            : new ImageBrush(art) { Stretch = Stretch.UniformToFill, AlignmentX = GameArt.ArtAlignment(game), AlignmentY = AlignmentY.Top };
    }
}
