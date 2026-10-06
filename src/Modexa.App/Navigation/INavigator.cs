using System.Windows;

namespace Modexa.App.Navigation;

/// <summary>Lightweight view navigation contract implemented by the main window.</summary>
public interface INavigator
{
    void GoHome();
    void Navigate(FrameworkElement view);
}
