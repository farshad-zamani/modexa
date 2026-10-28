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
    /// what goes where (<paramref name="Targets"/>), and whether it can be ticked. <paramref name="Checked"/>
    /// null = the list decides. <paramref name="PlaceRequired"/>: ticking it asks where it goes (under it);
    /// <paramref name="CanPlace"/>: a small edit button lets the user give another place.
    /// </summary>
    public sealed record CheckItem(string Label, string? Detail = null, bool Enabled = true, bool? Checked = null, string? Badge = null,
        string? Location = null, IReadOnlyList<(string Name, string Destination)>? Targets = null,
        bool PlaceRequired = false, bool CanPlace = false);

    /// <summary>What the user chose in <see cref="ChooseList"/>: the ticked items and, per item, a place given.</summary>
    public sealed record ChooseResult(List<int> Picked, Dictionary<int, string> Places);

    /// <summary>Tick list (see <see cref="ChooseList"/>): only the ticked indexes.</summary>
    public static List<int>? ChooseMany(string title, string message, IReadOnlyList<CheckItem> items, string primaryText, byte[]? image = null)
        => ChooseList(title, message, items, primaryText, image)?.Picked;

    /// <summary>
    /// A list of mods to choose from (e.g. what a file holds). With several choices nothing is ticked
    /// at first, a "Select all" box and a count are on top; a single choice is shown on its own, without
    /// a tick box. Items that can't be ticked show their reason. Returns null when cancelled.
    /// </summary>
    public static ChooseResult? ChooseList(string title, string message, IReadOnlyList<CheckItem> items, string primaryText, byte[]? image = null)
    {
        var dlg = new DialogWindow(message, DialogKind.Question, title, confirm: true, danger: false) { Width = 660 };
        dlg.BtnPrimary.Content = primaryText;
        dlg.BtnSecondary.Content = Loc.Instance["Common_Cancel"];
        dlg.ShowImage(image);
        int selectable = items.Count(i => i.Enabled);
        bool single = selectable == 1;
        var boxes = new List<System.Windows.Controls.CheckBox?>();
        var places = new Dictionary<int, PlacePicker>();

        // Top: "Select all" and how many there are / are ticked.
        System.Windows.Controls.CheckBox? all = null;
        System.Windows.Controls.TextBlock? count = null;
        if (selectable > 1)
        {
            var head = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = false };
            all = new System.Windows.Controls.CheckBox
            {
                Style = (Style)dlg.FindResource("SelectBox"), IsChecked = false, Content = Loc.Instance["Mods_SelectAll"]
            };
            count = new System.Windows.Controls.TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            count.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Text.Muted");
            System.Windows.Controls.DockPanel.SetDock(count, System.Windows.Controls.Dock.Right);
            head.Children.Add(all);
            head.Children.Add(count);
            dlg.ChecksHeader.Child = head;
            dlg.ChecksHeader.Visibility = Visibility.Visible;
        }

        for (int index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var row = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            var head = new System.Windows.Controls.DockPanel { LastChildFill = true };
            System.Windows.Controls.CheckBox? box = null;
            var label = new System.Windows.Controls.TextBlock
            {
                Text = item.Label, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420, FlowDirection = FlowDirection.LeftToRight
            };
            label.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, item.Enabled ? "Text.Primary" : "Text.Muted");
            var titleLine = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            if (item.Enabled && !single)
            {
                box = new System.Windows.Controls.CheckBox
                {
                    Style = (Style)dlg.FindResource("SelectBox"), IsChecked = item.Checked ?? false, Content = label,
                    VerticalAlignment = VerticalAlignment.Center
                };
                titleLine.Children.Add(box);
            }
            else if (!item.Enabled)
            {
                var no = new System.Windows.Controls.TextBlock { Style = (Style)dlg.FindResource("Icon"), FontSize = 14, Text = "\uE711", Margin = new Thickness(3, 0, 13, 0) };
                no.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Warning");
                titleLine.Children.Add(no);
                titleLine.Children.Add(label);
            }
            else titleLine.Children.Add(label);
            if (item.Badge != null)
            {
                var chip = new System.Windows.Controls.Border { Style = (Style)dlg.FindResource("TierChip"), Margin = new Thickness(10, 0, 0, 0), MinHeight = 20, Padding = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
                var t = new System.Windows.Controls.TextBlock { Text = item.Badge, FontSize = 11, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
                t.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Accent");
                chip.Child = t;
                titleLine.Children.Add(chip);
            }
            PlacePicker? picker = item.Enabled && (item.PlaceRequired || item.CanPlace) ? new PlacePicker(dlg) : null;
            if (picker != null && item.CanPlace && !item.PlaceRequired)
            {
                // Quiet edit button: only for when the user sees a mod going to the wrong place.
                var edit = new System.Windows.Controls.Button
                {
                    Style = (Style)dlg.FindResource("TitleButton"), Height = 26, Padding = new Thickness(6, 0, 6, 0),
                    Content = new System.Windows.Controls.TextBlock { Style = (Style)dlg.FindResource("Icon"), FontSize = 12, Text = "\uE70F" },
                    ToolTip = Loc.Instance["Raw_EditPlace"], VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7
                };
                edit.Click += (_, _) => picker.Toggle();
                System.Windows.Controls.DockPanel.SetDock(edit, System.Windows.Controls.Dock.Right);
                head.Children.Add(edit);
            }
            head.Children.Add(titleLine);
            row.Children.Add(head);

            double indent = box != null || !item.Enabled ? 30 : 0;
            if (!string.IsNullOrWhiteSpace(item.Location))
                row.Children.Add(LocationLine(dlg, item.Location!, indent));
            if (item.Targets is { Count: > 0 } targets)
                row.Children.Add(TargetsGrid(dlg, targets, indent));
            if (!string.IsNullOrWhiteSpace(item.Detail))
            {
                var detail = new System.Windows.Controls.TextBlock
                {
                    Text = item.Detail, Style = (Style)dlg.FindResource("Text.Desc"), TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(indent, 4, 0, 0)
                };
                row.Children.Add(detail);
            }
            if (picker != null)
            {
                picker.Root.Margin = new Thickness(indent, 8, 0, 0);
                row.Children.Add(picker.Root);
                places[index] = picker;
                if (item.PlaceRequired)
                {
                    // Asked as soon as the item is ticked (or right away when it is the only one).
                    picker.Show(single || box?.IsChecked == true);
                    if (box != null)
                    {
                        box.Checked += (_, _) => picker.Show(true);
                        box.Unchecked += (_, _) => picker.Show(false);
                    }
                }
                picker.Changed += () => Sync();
            }
            dlg.ChecksPanel.Children.Add(row);
            boxes.Add(box);
        }
        dlg.ChecksScroll.Visibility = Visibility.Visible;

        bool Ticked(int i) => items[i].Enabled && (single || boxes[i]?.IsChecked == true);
        bool syncing = false;
        void Sync()
        {
            var live = boxes.Where(b => b != null).ToList();
            int on = Enumerable.Range(0, items.Count).Count(Ticked);
            // A ticked item that needs a place needs one before installing.
            bool placesOk = Enumerable.Range(0, items.Count).Where(Ticked)
                .All(i => !items[i].PlaceRequired || places.TryGetValue(i, out var p) && p.Value != null);
            dlg.BtnPrimary.IsEnabled = on > 0 && placesOk;
            if (count != null) count.Text = Loc.Instance.Format("Raw_Count", selectable, on);
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
                bool tick = boxes.Any(b => b != null && b.IsChecked != true);
                foreach (var b in boxes.Where(b => b != null)) b!.IsChecked = tick;
                Sync();
            };
        Sync();
        var owner = ActiveOwner();
        if (owner != null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (dlg.ShowDialog() != true) return null;
        var picked = Enumerable.Range(0, items.Count).Where(Ticked).ToList();
        var given = picked.Where(i => places.TryGetValue(i, out var p) && p.IsShown && p.Value != null)
            .ToDictionary(i => i, i => places[i].Value!);
        return new ChooseResult(picked, given);
    }

    /// <summary>
    /// "Where it sits": the end of the path always visible; a path too long fades out at its start
    /// (never at the folder that tells two same-named files apart).
    /// </summary>
    private static FrameworkElement LocationLine(DialogWindow dlg, string location, double indent)
    {
        var line = new System.Windows.Controls.DockPanel { Margin = new Thickness(indent, 4, 0, 0), FlowDirection = FlowDirection.LeftToRight };
        if (Loc.Instance.IsRtl) line.HorizontalAlignment = HorizontalAlignment.Right;
        var icon = new System.Windows.Controls.TextBlock { Style = (Style)dlg.FindResource("Icon"), FontSize = 11, Text = "\uE8B7", Margin = new Thickness(0, 0, 6, 0) };
        icon.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Text.Muted");
        System.Windows.Controls.DockPanel.SetDock(icon, System.Windows.Controls.Dock.Left);
        var text = new System.Windows.Controls.TextBlock { Text = location, FontSize = 12, TextWrapping = TextWrapping.NoWrap, HorizontalAlignment = HorizontalAlignment.Right };
        text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Text.Muted");
        var clip = new System.Windows.Controls.Border { ClipToBounds = true, MaxWidth = 500, Child = text };
        clip.SizeChanged += (_, _) =>
        {
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            bool cut = text.DesiredSize.Width > clip.ActualWidth + 0.5;
            if (!cut) { clip.OpacityMask = null; return; }
            var fade = new System.Windows.Media.LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            fade.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Colors.Transparent, 0));
            fade.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Colors.Black, 0.12));
            clip.OpacityMask = fade;
        };
        line.Children.Add(icon);
        line.Children.Add(clip);
        return line;
    }

    /// <summary>What goes where, as a small two-column table: name | → place.</summary>
    private static FrameworkElement TargetsGrid(DialogWindow dlg, IReadOnlyList<(string Name, string Destination)> targets, double indent)
    {
        var grid = new System.Windows.Controls.Grid { Margin = new Thickness(indent, 6, 0, 0), FlowDirection = FlowDirection.LeftToRight };
        if (Loc.Instance.IsRtl) grid.HorizontalAlignment = HorizontalAlignment.Right;
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto, MaxWidth = 260 });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < targets.Count; r++)
        {
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            var name = new System.Windows.Controls.TextBlock
            {
                Text = targets[r].Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 14, 1), ToolTip = targets[r].Name
            };
            name.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Text.Secondary");
            var dest = new System.Windows.Controls.TextBlock { Text = "→  " + targets[r].Destination, FontSize = 12.5, Margin = new Thickness(0, 1, 0, 1) };
            dest.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Text.Muted");
            System.Windows.Controls.Grid.SetRow(name, r);
            System.Windows.Controls.Grid.SetRow(dest, r);
            System.Windows.Controls.Grid.SetColumn(dest, 1);
            grid.Children.Add(name);
            grid.Children.Add(dest);
        }
        return grid;
    }

    /// <summary>
    /// Where an item goes, given by the user: the game folder, its scripts folder, or a path typed in
    /// (a folder of the game, or a place inside a game archive as OpenIV shows it).
    /// </summary>
    private sealed class PlacePicker
    {
        public readonly System.Windows.Controls.StackPanel Root = new() { Visibility = Visibility.Collapsed };
        private readonly System.Windows.Controls.ComboBox _choice;
        private readonly System.Windows.Controls.TextBox _text;
        public event Action? Changed;
        public bool IsShown => Root.Visibility == Visibility.Visible;

        public PlacePicker(DialogWindow dlg)
        {
            var label = new System.Windows.Controls.TextBlock { Text = Loc.Instance["Raw_Place_Label"], FontSize = 12.5, Margin = new Thickness(0, 0, 0, 6) };
            label.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Text.Secondary");
            _choice = new System.Windows.Controls.ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
            _choice.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Instance["Raw_Place_Game"], Tag = "\\" });
            _choice.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Instance["Raw_Place_Scripts"], Tag = "scripts" });
            _choice.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Instance["Raw_Place_Custom"], Tag = null });
            _text = new System.Windows.Controls.TextBox
            {
                Style = (Style)dlg.FindResource("InputBox"), FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed, ToolTip = Loc.Instance["Raw_Place_Hint"]
            };
            var hint = new System.Windows.Controls.TextBlock
            {
                Text = Loc.Instance["Raw_Place_Hint"], FontSize = 11.5, Margin = new Thickness(2, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
                FlowDirection = FlowDirection.LeftToRight, Visibility = Visibility.Collapsed
            };
            hint.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Text.Muted");
            _choice.SelectionChanged += (_, _) =>
            {
                bool custom = _choice.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: null };
                _text.Visibility = hint.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
                if (custom) _text.Focus();
                Changed?.Invoke();
            };
            _text.TextChanged += (_, _) => Changed?.Invoke();
            Root.Children.Add(label);
            Root.Children.Add(_choice);
            Root.Children.Add(_text);
            Root.Children.Add(hint);
        }

        /// <summary>The place given, or null when none yet.</summary>
        public string? Value => _choice.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string tag }
            ? tag
            : _choice.SelectedItem != null && !string.IsNullOrWhiteSpace(_text.Text) ? _text.Text.Trim() : null;

        public void Show(bool on) { Root.Visibility = on ? Visibility.Visible : Visibility.Collapsed; Changed?.Invoke(); }

        public void Toggle() => Show(!IsShown);
    }

    /// <summary>Asks for a line of text (e.g. the place a mod's author gives). Returns null when cancelled.</summary>
    /// <param name="secret">A password: typed into a password box.</param>
    public static string? AskText(string title, string message, string? example = null, string? initial = null, bool secret = false,
        string? primaryText = null)
    {
        var dlg = new DialogWindow(message, DialogKind.Question, title, confirm: true, danger: false) { Width = 600 };
        dlg.BtnPrimary.Content = primaryText ?? Loc.Instance["Common_Install"];
        dlg.BtnSecondary.Content = Loc.Instance["Common_Cancel"];
        dlg.InputRow.Visibility = Visibility.Visible;
        dlg.InputText.Text = initial ?? "";
        if (example != null) dlg.InputHint.Text = example;
        else dlg.InputHint.Visibility = Visibility.Collapsed;
        if (secret)
        {
            dlg.InputText.Visibility = Visibility.Collapsed;
            dlg.InputSecret.Visibility = Visibility.Visible;
        }
        string Value() => secret ? dlg.InputSecret.Password : dlg.InputText.Text;
        void Sync() => dlg.BtnPrimary.IsEnabled = !string.IsNullOrWhiteSpace(Value());
        dlg.InputText.TextChanged += (_, _) => Sync();
        dlg.InputSecret.PasswordChanged += (_, _) => Sync();
        Sync();
        dlg.Loaded += (_, _) =>
        {
            if (secret) dlg.InputSecret.Focus();
            else { dlg.InputText.Focus(); dlg.InputText.SelectAll(); }
        };
        var owner = ActiveOwner();
        if (owner != null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true ? (secret ? Value() : Value().Trim()) : null;
    }

    private void ShowImage(byte[]? image)
    {
        if (image == null || BusyOverlay.LoadImage(image, 128) is not { } src) return;
        ImageBox.Source = src;
        ImageFrame.Visibility = Visibility.Visible;
        IconBg.Visibility = Visibility.Collapsed;
        TitleText.Margin = new Thickness(20, 2, 0, 0);
        ((FrameworkElement)MessageScroll.Parent).Margin = new Thickness(20, 8, 0, 0);
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
