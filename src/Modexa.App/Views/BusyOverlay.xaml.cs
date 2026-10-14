using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Modexa.App.Services;
using Modexa.Core.I18n;

namespace Modexa.App.Views;

/// <summary>
/// The progress card shown over a game page while something installs, uninstalls or downloads.
/// Besides the percentage it estimates the time left (from the progress rate) and shows which step
/// of how many is running and which file or archive is being worked on.
/// </summary>
public partial class BusyOverlay : UserControl
{
    private DateTime _etaStart;
    private int _etaStartPct = -1;
    private int _lastPct = -1;

    public event RoutedEventHandler? CancelRequested;

    public BusyOverlay() => InitializeComponent();

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Opens the card fresh for a new operation.</summary>
    public void Show(string phaseKey, bool cancellable = false)
    {
        Reset();
        Visibility = Visibility.Visible;
        CancelButton.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        PhaseText.Bind(TextBlock.TextProperty, phaseKey);
        SetPercent(cancellable ? 0 : null);
    }

    /// <summary>Updates (opening it if needed). Title / icon stay until the card is hidden.</summary>
    public void Update(InstallPhase p)
    {
        if (!IsOpen)
        {
            Reset();
            Visibility = Visibility.Visible;
            CancelButton.Visibility = Visibility.Collapsed;
        }
        if (p.Title != null)
        {
            TitleText.Text = p.Title;
            TitleRow.Visibility = Visibility.Visible;
        }
        if (p.Icon != null && IconImage.Source == null && LoadImage(p.Icon) is { } img)
        {
            IconImage.Source = img;
            IconFrame.Visibility = Visibility.Visible;
            TitleRow.Visibility = Visibility.Visible;
        }
        PhaseText.Bind(TextBlock.TextProperty, p.Key);
        StepText.Text = p.Step ?? "";
        StepText.Visibility = string.IsNullOrEmpty(p.Step) ? Visibility.Collapsed : Visibility.Visible;
        DetailText.Text = p.Detail ?? "";
        DetailText.ToolTip = string.IsNullOrEmpty(p.Detail) ? null : p.Detail;
        SetPercent(p.Percent);
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        Bar.IsIndeterminate = false; // never leave an indeterminate animation ticking behind a hidden card
        Reset();
    }

    public void SetPercent(int? pct)
    {
        if (pct is { } v)
        {
            v = Math.Clamp(v, 0, 100);
            Bar.IsIndeterminate = false;
            Bar.Value = v;
            PercentText.Text = v.ToString(CultureInfo.InvariantCulture) + "%";
            // A falling percentage means a new operation started: measure the rate afresh.
            if (v < _lastPct) _etaStartPct = -1;
            _lastPct = v;
            UpdateEta(v);
        }
        else
        {
            Bar.IsIndeterminate = true;
            PercentText.Text = "";
            EtaText.Text = "";
            _etaStartPct = -1;
            _lastPct = -1;
        }
    }

    private void UpdateEta(int pct)
    {
        var now = DateTime.UtcNow;
        if (_etaStartPct < 0 || pct < _etaStartPct)
        {
            _etaStart = now;
            _etaStartPct = pct;
            EtaText.Text = "";
            return;
        }
        double elapsed = (now - _etaStart).TotalSeconds;
        int gained = pct - _etaStartPct;
        if (elapsed < 4 || gained < 1 || pct >= 100) { if (pct >= 100) EtaText.Text = ""; return; }
        double left = elapsed * (100 - pct) / gained;
        EtaText.Text = left < 60
            ? Loc.Instance["Busy_EtaSec"]
            : Loc.Instance.Format("Busy_EtaMin", ((int)Math.Ceiling(left / 60)).ToString(CultureInfo.InvariantCulture));
    }

    private void Reset()
    {
        TitleRow.Visibility = Visibility.Collapsed;
        IconFrame.Visibility = Visibility.Collapsed;
        IconImage.Source = null;
        TitleText.Text = "";
        StepText.Text = "";
        StepText.Visibility = Visibility.Collapsed;
        DetailText.Text = "";
        EtaText.Text = "";
        _etaStartPct = -1;
        _lastPct = -1;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, e);

    /// <summary>A frozen bitmap from PNG/JPG bytes (null when they are not an image).</summary>
    public static ImageSource? LoadImage(byte[] bytes, int decodeWidth = 96)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = decodeWidth;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    public static ImageSource? LoadImage(string? file, int decodeWidth = 96)
    {
        try { return file != null && File.Exists(file) ? LoadImage(File.ReadAllBytes(file), decodeWidth) : null; }
        catch { return null; }
    }
}
