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
    /// <param name="advancedLabel">
    /// When set, a switch with this label guards the list: off, <paramref name="selected"/> is used and
    /// the list is locked; on, any item can be picked.
    /// </param>
    public static string? ChooseFromList(string title, string message, IReadOnlyList<(string Value, string Label)> items, string? selected,
        string? advancedLabel = null)
    {
        var dlg = new DialogWindow(message, DialogKind.Question, title, confirm: true, danger: false);
        dlg.IconGlyph.Text = "";
        dlg.BtnPrimary.Content = Loc.Instance["Common_Install"];
        dlg.BtnSecondary.Content = Loc.Instance["Common_Cancel"];
        foreach (var (value, label) in items)
            dlg.ChoiceBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = label, Tag = value });
        int def = Math.Max(0, items.ToList().FindIndex(i => i.Value == selected));
        dlg.ChoiceBox.SelectedIndex = def;
        dlg.ChoiceBox.Visibility = Visibility.Visible;
        if (advancedLabel != null)
        {
            dlg.AdvancedRow.Visibility = Visibility.Visible;
            dlg.AdvancedText.Text = advancedLabel;
            dlg.ChoiceBox.IsEnabled = false;
            void Sync()
            {
                bool on = dlg.AdvancedToggle.IsChecked == true;
                dlg.ChoiceBox.IsEnabled = on;
                if (!on) dlg.ChoiceBox.SelectedIndex = def;
            }
            dlg.AdvancedToggle.Checked += (_, _) => Sync();
            dlg.AdvancedToggle.Unchecked += (_, _) => Sync();
            dlg.AdvancedText.MouseLeftButtonUp += (_, _) => dlg.AdvancedToggle.IsChecked = dlg.AdvancedToggle.IsChecked != true;
        }
        var owner = ActiveOwner();
        if (owner != null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true ? (dlg.ChoiceBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string : null;
    }

    /// <summary>
    /// A two-button prompt with custom labels (e.g. Download / Later). True = primary chosen.
    /// <paramref name="image"/> (PNG/JPG bytes, e.g. a package icon) replaces the round glyph.
    /// </summary>
    public static bool Prompt(string message, string primaryText, string secondaryText,
        DialogKind kind = DialogKind.Info, string? title = null, byte[]? image = null)
    {
        var dlg = new DialogWindow(message, kind, title, confirm: true, danger: false);
        dlg.BtnPrimary.Content = primaryText;
        dlg.BtnSecondary.Content = secondaryText;
        dlg.BtnSecondary.Visibility = Visibility.Visible;
        dlg.ShowImage(image);
        var owner = ActiveOwner();
        if (owner != null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true;
    }

    /// <summary>
    /// Up to three answers: returns the index of the chosen button (0 = primary, 1 = second,
    /// 2 = third), or -1 when cancelled (Esc). The last label acts as Cancel.
    /// </summary>
    public static int Ask(string message, IReadOnlyList<string> buttons, bool dangerPrimary = false, string? title = null, byte[]? image = null)
    {
        var dlg = new DialogWindow(message, DialogKind.Question, title, confirm: true, danger: dangerPrimary);
        dlg._choice = -1;
        dlg.BtnPrimary.Content = buttons[0];
        dlg.BtnPrimary.Click += (_, _) => dlg._choice = 0;
        if (buttons.Count > 1)
        {
            dlg.BtnSecondary.Content = buttons[1];
            dlg.BtnSecondary.Visibility = Visibility.Visible;
            dlg.BtnSecondary.Click += (_, _) => dlg._choice = 1;
        }
        if (buttons.Count > 2)
        {
            // Order on screen: [third] [second] [primary]; the third (last) one is the "Cancel".
            dlg.BtnTertiary.Content = buttons[2];
            dlg.BtnTertiary.Visibility = Visibility.Visible;
            dlg.BtnSecondary.IsCancel = false;
            dlg.BtnTertiary.IsCancel = true;
            dlg.BtnTertiary.Click += (_, _) => { dlg._choice = 2; dlg.DialogResult = false; };
        }
        dlg.ShowImage(image);
        var owner = ActiveOwner();
        if (owner != null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.ShowDialog();
        return dlg._choice;
    }

    private void ShowImage(byte[]? image)
    {
        if (image == null || BusyOverlay.LoadImage(image, 128) is not { } src) return;
        ImageBox.Source = src;
        ImageFrame.Visibility = Visibility.Visible;
        IconBg.Visibility = Visibility.Collapsed;
        TitleText.Margin = new Thickness(20, 2, 0, 0);
        ((FrameworkElement)MessageText.Parent).Margin = new Thickness(20, 8, 0, 0);
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
