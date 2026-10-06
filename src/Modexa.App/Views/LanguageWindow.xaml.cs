using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Modexa.App.Views;

public partial class LanguageWindow : Window
{
    public string? SelectedLanguage { get; private set; }

    public LanguageWindow()
    {
        InitializeComponent();
    }

    private void Lang_Click(object sender, RoutedEventArgs e)
    {
        var clicked = (ToggleButton)sender;
        // Enforce single-selection between the two cards.
        btnEn.IsChecked = ReferenceEquals(clicked, btnEn);
        btnFa.IsChecked = ReferenceEquals(clicked, btnFa);

        SelectedLanguage = clicked.Tag?.ToString();
        btnContinue.IsEnabled = SelectedLanguage != null;

        // Preview the action label in the chosen language and font.
        bool fa = SelectedLanguage == "fa";
        ContinueText.Text = fa ? "ادامه" : "Continue";
        ContinueText.SetResourceReference(TextBlock.FontFamilyProperty, fa ? "Font.Persian" : "Font.En.Hud");
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        SelectedLanguage ??= "en";
        Close();
    }

    private void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is Border or StackPanel or Grid)
            DragMove();
    }
}
