using System.IO;
using System.Windows;
using System.Windows.Threading;
using Modexa.App.Services;
using Modexa.App.Views;
using Modexa.Core;
using Modexa.Core.Diagnostics;
using Modexa.Core.I18n;
using Modexa.Core.Security;
using Modexa.Core.Settings;

namespace Modexa.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    private bool _showingCrash;

    public AppSettings Settings { get; private set; } = new();

    public static new App Current => (App)Application.Current;

    /// <summary>A package path passed on the command line (Explorer "Open with" / double-click).</summary>
    public string? PendingOpenPath { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        SecurityHelper.CheckDebugger();
        if (!SecurityHelper.ValidateIntegrity())
        {
            Shutdown(-1);
            return;
        }

        string? argPath = e.Args.FirstOrDefault(a => File.Exists(a) || Directory.Exists(a));

        _instance = new SingleInstance();
        if (!_instance.IsFirst)
        {
            // Hand the file to the running window instead of showing a second one.
            SingleInstance.Forward(argPath);
            Shutdown();
            return;
        }

        HookCrashHandlers();
        Log.Prune();
        Log.Info($"Start v{typeof(App).Assembly.GetName().Version} data={AppPaths.DataDir}");

        base.OnStartup(e);

        // Track the tier theme dictionary so ThemeService can swap it live.
        foreach (var d in Resources.MergedDictionaries)
        {
            if (d.Source != null && d.Source.OriginalString.Contains("Theme.Free", StringComparison.OrdinalIgnoreCase))
            {
                ThemeService.Initialize(d);
                break;
            }
        }

        Settings = AppSettings.Load();

        // First run (or no saved language): ask for a language before anything else.
        if (string.IsNullOrWhiteSpace(Settings.Language))
        {
            var langWin = new LanguageWindow();
            langWin.ShowDialog();
            Settings.Language = langWin.SelectedLanguage ?? "en";
            Settings.Save();
        }

        LanguageService.Apply(Settings.Language!, persist: false);

        PendingOpenPath = argPath;
        var main = new MainWindow();
        MainWindow = main;
        _instance.Received += path => Dispatcher.BeginInvoke(() => main.BringToFrontAndOpen(path));
        main.Show();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
    }

    private void HookCrashHandlers()
    {
        // UI-thread exceptions: log, tell the user, keep the app alive.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("UI", args.Exception);
            args.Handled = true;
            if (_showingCrash) return;
            _showingCrash = true;
            try
            {
                DialogWindow.Show(
                    $"{Loc.Instance["Crash_Message"]}\n\n{args.Exception.Message}\n\n{Log.CurrentFile}",
                    DialogKind.Error);
            }
            catch { }
            finally { _showingCrash = false; }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Log.Error("Fatal", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Task", args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info($"Exit code={e.ApplicationExitCode}");
        _instance?.Dispose();
        base.OnExit(e);
    }
}
