using System.Windows;
using System.Windows.Input;
using Modexa.App.Services;
using Modexa.Core.I18n;

namespace Modexa.App.Views;

public enum DialogKind { Info, Success, Warning, Error, Question }

/// <summary>Themed replacement for MessageBox: matches the tier theme, language and direction.</summary>
public partial class DialogWindow : Window
{
    private DialogWindow(string message, DialogKind kind, string? title, bool confirm, bool danger)
    {
        InitializeComponent();
        FlowDirection = LanguageService.Flow;

        TitleText.Text = title ?? kind switch
        {
            DialogKind.Error => Loc.Instance["Common_Error"],
            DialogKind.Warning => Loc.Instance["Common_Notice"],
            DialogKind.Success => Loc.Instance["Common_Done"],
            DialogKind.Question => Loc.Instance["Common_Confirm"],
            _ => Loc.Instance["Common_Notice"]
        };
        MessageText.Text = message;

        (string glyph, string brush) = kind switch
        {
            DialogKind.Error => ("", "Danger"),
            DialogKind.Warning => ("", "Warning"),
            DialogKind.Success => ("", "Success"),
            DialogKind.Question => ("", danger ? "Danger" : "Accent"),
            _ => ("", "Accent")
        };
        IconGlyph.Text = glyph;
        IconGlyph.SetResourceReference(ForegroundProperty, brush);
        if (brush != "Accent")
        {
            IconBg.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty,
                brush == "Danger" ? "Danger.Soft" : "Bg.Elevated");
        }

        if (confirm)
        {
            BtnPrimary.Content = Loc.Instance["Common_Yes"];
            BtnSecondary.Content = Loc.Instance["Common_No"];
            BtnSecondary.Visibility = Visibility.Visible;
            if (danger) BtnPrimary.Style = (Style)FindResource("DangerButton");
        }
        else
        {
            BtnPrimary.Content = Loc.Instance["Common_OK"];
            BtnPrimary.IsCancel = true;
        }
    }

    private void Primary_Click(object sender, RoutedEventArgs e) { DialogResult = true; }
    private void Secondary_Click(object sender, RoutedEventArgs e) { DialogResult = false; }

    private void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private static Window? ActiveOwner()
    {
        var app = Application.Current;
        return app?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app?.MainWindow;
    }

    public static void Show(string message, DialogKind kind = DialogKind.Info, string? title = null)
    {
        var dlg = new DialogWindow(message, kind, title, confirm: false, danger: false);
        var owner = ActiveOwner();
        if (owner != null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.ShowDialog();
    }

    /// <summary>
    /// Two-option choice (e.g. GTA V Legacy vs Enhanced). Returns 0 for <paramref name="first"/>,
    /// 1 for <paramref name="second"/>, -1 when dismissed.
    /// </summary>
    public static int Choose(string message, string first, string second, string? title = null)
    {
        var dlg = new DialogWindow(message, DialogKind.Question, title, confirm: true, danger: false);
        dlg.BtnSecondary.Content = second;
        dlg.BtnSecondary.IsCancel = false;
        dlg.BtnPrimary.Content = first;
        dlg._choice = -1;
        dlg.BtnSecondary.Click += (_, _) => dlg._choice = 1;
        dlg.BtnPrimary.Click += (_, _) => dlg._choice = 0;
        var owner = ActiveOwner();
        if (owner != null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.ShowDialog();
        return dlg._choice;
    }

    private int _choice = -1;

    /// <summary>
    /// Pick one item from a list (e.g. a gameconfig variant). Returns the chosen item's value, or null
    /// when cancelled. <paramref name="items"/> are (value, label) pairs.
    /// </summary>
    public static string? ChooseFromList(string title, string message, IReadOnlyList<(string Value, string Label)> items, string? selected)
    {
        var dlg = new DialogWindow(message, DialogKind.Question, title, confirm: true, danger: false);
        dlg.IconGlyph.Text = "";
        dlg.BtnPrimary.Content = Loc.Instance["Common_Install"];
        dlg.BtnSecondary.Content = Loc.Instance["Common_Cancel"];
        foreach (var (value, label) in items)
            dlg.ChoiceBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = label, Tag = value });
        dlg.ChoiceBox.SelectedIndex = Math.Max(0, items.ToList().FindIndex(i => i.Value == selected));
        dlg.ChoiceBox.Visibility = Visibility.Visible;
        var owner = ActiveOwner();
        if (owner != null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true ? (dlg.ChoiceBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string : null;
    }

    /// <summary>A two-button prompt with custom labels (e.g. Download / Later). True = primary chosen.</summary>
    public static bool Prompt(string message, string primaryText, string secondaryText,
        DialogKind kind = DialogKind.Info, string? title = null)
    {
        var dlg = new DialogWindow(message, kind, title, confirm: true, danger: false);
        dlg.BtnPrimary.Content = primaryText;
        dlg.BtnSecondary.Content = secondaryText;
        dlg.BtnSecondary.Visibility = Visibility.Visible;
        var owner = ActiveOwner();
        if (owner != null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true;
    }

    public static bool Confirm(string message, bool danger = false, string? title = null)
    {
        var dlg = new DialogWindow(message, DialogKind.Question, title, confirm: true, danger: danger);
        var owner = ActiveOwner();
        if (owner != null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true;
    }
}
