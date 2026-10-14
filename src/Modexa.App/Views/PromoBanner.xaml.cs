using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Modexa.App.Services;

namespace Modexa.App.Views;

/// <summary>A clickable product banner (artwork + name + pitch) that opens the product page.</summary>
public partial class PromoBanner : UserControl
{
    private string? _url;

    public PromoBanner() => InitializeComponent();

    /// <param name="image">Artwork inside the Modexa assembly, e.g. "assets/promo/menyoofa.jpg".</param>
    public void Set(string image, string titleKey, string textKey, string url)
    {
        try
        {
            var img = new BitmapImage(new Uri($"pack://application:,,,/Modexa;component/{image}"));
            img.Freeze();
            Art.Source = img;
        }
        catch { /* banner still works without its artwork */ }
        TitleText.Bind(TextBlock.TextProperty, titleKey);
        BodyText.Bind(TextBlock.TextProperty, textKey);
        _url = url;
    }

    /// <summary>Changes the pitch (e.g. a variant for another edition of the game).</summary>
    public void SetText(string textKey) => BodyText.Bind(TextBlock.TextProperty, textKey);

    private void Open()
    {
        if (_url != null) MainWindow.OpenUrl(_url);
    }

    private void Root_Click(object sender, MouseButtonEventArgs e) => Open();

    private void Root_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space) { Open(); e.Handled = true; }
    }

    private void Root_MouseEnter(object sender, MouseEventArgs e) => Hover(true);

    private void Root_MouseLeave(object sender, MouseEventArgs e) => Hover(false);

    private void Hover(bool on)
    {
        Frame.SetResourceReference(Border.BorderBrushProperty, on ? "Stroke.Accent" : "Stroke.Subtle");
        Frame.SetResourceReference(Border.BackgroundProperty, on ? "Bg.Elevated" : "Bg.Panel");
        if (!SystemParameters.ClientAreaAnimation) return;
        Lift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(on ? -2 : 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase() });
    }

    /// <summary>Clips the artwork to the card's rounded corners (on whichever side it sits).</summary>
    private void Frame_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var r = new Rect(0, 0, Math.Max(0, Frame.ActualWidth - 2), Math.Max(0, Frame.ActualHeight - 2));
        ((Grid)Frame.Child).Clip = new RectangleGeometry(r, 13, 13);
    }
}
