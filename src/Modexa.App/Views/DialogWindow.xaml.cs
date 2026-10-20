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

    /// <summary>
    /// One line of <see cref="ChooseMany"/>: a label, a detail line, where it sits (a short folder path),
    /// and whether it can be ticked. <paramref name="Checked"/> null = the list decides (ticked only when
    /// it is the only choice).
    /// </summary>
    public sealed record CheckItem(string Label, string? Detail = null, bool Enabled = true, bool? Checked = null, string? Badge = null,
        string? Location = null);

    /// <summary>
    /// A list of tick boxes (e.g. the mods found in a file): returns the indexes the user ticked, or
    /// null when cancelled. With several choices nothing is ticked at first, and "Select all" ticks or
    /// clears them all. Items that can't be ticked are shown with their reason only.
    /// </summary>
    public static List<int>? ChooseMany(string title, string message, IReadOnlyList<CheckItem> items, string primaryText, byte[]? image = null)
    {
        var dlg = new DialogWindow(message, DialogKind.Question, title, confirm: true, danger: false) { Width = 640 };
        dlg.BtnPrimary.Content = primaryText;
        dlg.BtnSecondary.Content = Loc.Instance["Common_Cancel"];
        dlg.ShowImage(image);
        int selectable = items.Count(i => i.Enabled);
        var boxes = new List<System.Windows.Controls.CheckBox?>();

        System.Windows.Controls.CheckBox? all = null;
        if (selectable > 1)
        {
            all = new System.Windows.Controls.CheckBox
            {
                Style = (Style)dlg.FindResource("SelectBox"), IsChecked = false, Margin = new Thickness(0, 0, 0, 12),
                Content = Loc.Instance["Mods_SelectAll"]
            };
            dlg.ChecksHeader.Child = all;
            dlg.ChecksHeader.Visibility = Visibility.Visible;
        }

        foreach (var item in items)
        {
            var row = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var head = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            System.Windows.Controls.CheckBox? box = null;
            var label = new System.Windows.Controls.TextBlock
            {
                Text = item.Label, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center, MaxWidth = 440
            };
            label.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, item.Enabled ? "Text.Primary" : "Text.Muted");
            if (item.Enabled)
            {
                box = new System.Windows.Controls.CheckBox
                {
                    Style = (Style)dlg.FindResource("SelectBox"), IsChecked = item.Checked ?? selectable == 1, Content = label,
                    VerticalAlignment = VerticalAlignment.Center
                };
                head.Children.Add(box);
            }
            else
            {
                var no = new System.Windows.Controls.TextBlock { Style = (Style)dlg.FindResource("Icon"), FontSize = 14, Text = "\uE711", Margin = new Thickness(3, 0, 13, 0) };
                no.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Warning");
                head.Children.Add(no);
                head.Children.Add(label);
            }
            if (item.Badge != null)
            {
                var chip = new System.Windows.Controls.Border { Style = (Style)dlg.FindResource("TierChip"), Margin = new Thickness(10, 0, 0, 0), MinHeight = 20, Padding = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
                var t = new System.Windows.Controls.TextBlock { Text = item.Badge, FontSize = 11, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
                t.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Accent");
                chip.Child = t;
                head.Children.Add(chip);
            }
            row.Children.Add(head);
            // Where it sits (a few last folders): tells same-named files in different folders apart.
            if (!string.IsNullOrWhiteSpace(item.Location))
            {
                var where = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(30, 4, 0, 0) };
                var icon = new System.Windows.Controls.TextBlock { Style = (Style)dlg.FindResource("Icon"), FontSize = 11, Text = "\uE8B7", Margin = new Thickness(0, 0, 6, 0) };
                icon.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Text.Muted");
                var path = new System.Windows.Controls.TextBlock
                {
                    Text = item.Location, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 470,
                    FlowDirection = FlowDirection.LeftToRight, VerticalAlignment = VerticalAlignment.Center
                };
                path.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Text.Muted");
                where.Children.Add(icon);
                where.Children.Add(path);
                row.Children.Add(where);
            }
            if (!string.IsNullOrWhiteSpace(item.Detail))
            {
                var detail = new System.Windows.Controls.TextBlock
                {
                    Text = item.Detail, Style = (Style)dlg.FindResource("Text.Desc"), TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(30, 3, 0, 0)
                };
                if (item.Enabled)
                {
                    // File names and paths: laid out left-to-right (aligned to the page side) so a Persian
                    // page doesn't scramble them.
                    detail.FlowDirection = FlowDirection.LeftToRight;
                    detail.TextAlignment = Loc.Instance.IsRtl ? TextAlignment.Right : TextAlignment.Left;
                }
                row.Children.Add(detail);
            }
            dlg.ChecksPanel.Children.Add(row);
            boxes.Add(box);
        }
        dlg.ChecksScroll.Visibility = Visibility.Visible;

        bool syncing = false;
        void Sync()
        {
            var live = boxes.Where(b => b != null).ToList();
            int on = live.Count(b => b!.IsChecked == true);
            dlg.BtnPrimary.IsEnabled = on > 0;
            if (all == null) return;
            syncing = true;
            all.IsChecked = on == 0 ? false : on == live.Count ? true : null;
            syncing = false;
        }
        foreach (var b in boxes.Where(b => b != null)) { b!.Checked += (_, _) => Sync(); b.Unchecked += (_, _) => Sync(); }
        if (all != null)
            all.Click += (_, _) =>
            {
                if (syncing) return;
                // A click on a partly ticked box ticks everything; on a full one it clears everything.
                bool tick = boxes.Any(b => b != null && b.IsChecked != true);
                foreach (var b in boxes.Where(b => b != null)) b!.IsChecked = tick;
                Sync();
            };
        Sync();
        var owner = ActiveOwner();
        if (owner != null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (dlg.ShowDialog() != true) return null;
        return boxes.Select((b, i) => (b, i)).Where(x => x.b?.IsChecked == true).Select(x => x.i).ToList();
    }

    /// <summary>Asks for a line of text (e.g. the place a mod's author gives). Returns null when cancelled.</summary>
    public static string? AskText(string title, string message, string? example = null, string? initial = null)
    {
        var dlg = new DialogWindow(message, DialogKind.Question, title, confirm: true, danger: false) { Width = 600 };
        dlg.BtnPrimary.Content = Loc.Instance["Common_Install"];
        dlg.BtnSecondary.Content = Loc.Instance["Common_Cancel"];
        dlg.InputRow.Visibility = Visibility.Visible;
        dlg.InputText.Text = initial ?? "";
        if (example != null) dlg.InputHint.Text = example;
        void Sync() => dlg.BtnPrimary.IsEnabled = !string.IsNullOrWhiteSpace(dlg.InputText.Text);
        dlg.InputText.TextChanged += (_, _) => Sync();
        Sync();
        dlg.Loaded += (_, _) => { dlg.InputText.Focus(); dlg.InputText.SelectAll(); };
        var owner = ActiveOwner();
        if (owner != null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true ? dlg.InputText.Text.Trim() : null;
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
