using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Modexa.App.Services;
using Modexa.Core.I18n;

namespace Modexa.App.Views;

/// <summary>
/// A warning at the top of a game page while the game is open (even in the background), with a button that closes it.
/// A user who is really playing can ignore it; mods simply can't be installed or removed until the game is closed.
/// </summary>
public partial class GameRunningBar : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private string? _folder;
    private bool _checking;

    public GameRunningBar()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await CheckAsync();
        Loaded += (_, _) => { _timer.Start(); _ = CheckAsync(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>The game folder to watch (null = none yet: the bar stays hidden).</summary>
    public string? Folder
    {
        get => _folder;
        set { _folder = value; _ = CheckAsync(); }
    }

    private async Task CheckAsync()
    {
        string? folder = _folder;
        if (folder == null) { Bar.Visibility = Visibility.Collapsed; return; }
        if (_checking) return;
        _checking = true;
        try
        {
            bool running = await Task.Run(() => GameProcess.IsRunning(folder));
            if (folder == _folder) Bar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _checking = false; }
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        string? folder = _folder;
        if (folder == null) return;
        CloseButton.IsEnabled = false;
        try
        {
            if (!await Task.Run(() => GameProcess.Close(folder)))
                DialogWindow.Show(Loc.Instance["Game_Close_Failed"], DialogKind.Warning);
        }
        finally
        {
            CloseButton.IsEnabled = true;
            await CheckAsync();
        }
    }
}
