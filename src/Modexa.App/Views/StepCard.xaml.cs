using System.Windows;
using System.Windows.Controls;

namespace Modexa.App.Views;

public partial class StepCard : UserControl
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(StepCard), new PropertyMetadata(""));
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(StepCard), new PropertyMetadata(""));
    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(StepCard), new PropertyMetadata(""));
    public static readonly DependencyProperty ActionTextProperty =
        DependencyProperty.Register(nameof(ActionText), typeof(string), typeof(StepCard), new PropertyMetadata(""));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string ActionText { get => (string)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }

    public event RoutedEventHandler? Click;

    public StepCard() => InitializeComponent();

    /// <summary>The step is installed: the action button removes it (see <see cref="SetDone"/>).</summary>
    public bool IsDone { get; private set; }

    /// <summary>
    /// Shows the "Installed" chip (with an optional detail such as the gameconfig variant) and turns the
    /// action into "Remove" while the step is installed.
    /// </summary>
    public void SetDone(bool done, string? detail = null)
    {
        IsDone = done;
        DoneChip.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        Services.Tr.Bind(DoneText, System.Windows.Controls.TextBlock.TextProperty, "Common_Installed");
        DoneChip.ToolTip = detail;
        IsUpdate = false;
        StateChip.Visibility = Visibility.Collapsed;
        ActionButton.Style = (Style)FindResource(done ? "DangerButton" : "PrimaryButton");
        if (done) Services.Tr.Bind(ActionButton, ContentProperty, "Mods_Uninstall");
        else ActionButton.SetBinding(ContentProperty, new System.Windows.Data.Binding(nameof(ActionText)) { Source = this });
    }

    /// <summary>The step is installed and a newer version is due: the action button updates it.</summary>
    public bool IsUpdate { get; private set; }

    /// <summary>A newer version of an installed step: the button becomes "Update".</summary>
    public void SetUpdate(bool available)
    {
        if (!IsDone) return;
        IsUpdate = available;
        if (available)
        {
            ShowState("", "Accent", "Prep_Update_Chip", "Prep_Update_Tip");
            ActionButton.Style = (Style)FindResource("PrimaryButton");
            Services.Tr.Bind(ActionButton, ContentProperty, "Common_Update");
        }
        else SetDone(true, DoneChip.ToolTip as string);
    }

    /// <summary>The game was updated but this step's pack has no newer version yet.</summary>
    public void SetGameUpdated() => ShowState("", "Warning", "Prep_GameUpdated_Chip", "Prep_GameUpdated_Tip");

    /// <summary>Its files are in the game folder, but not installed by Modexa (can't be told right or wrong).</summary>
    public void SetForeign(bool found)
    {
        if (IsDone) return;
        if (found) ShowState("", "Warning", "Prep_Foreign_Chip", "Prep_Foreign_Tip");
        else StateChip.Visibility = Visibility.Collapsed;
    }

    private void ShowState(string glyph, string brush, string textKey, string tipKey)
    {
        StateIcon.Text = glyph;
        StateIcon.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, brush);
        StateText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, brush);
        Services.Tr.Bind(StateText, System.Windows.Controls.TextBlock.TextProperty, textKey);
        StateChip.SetResourceReference(Border.BorderBrushProperty, brush);
        StateChip.ToolTip = Modexa.Core.I18n.Loc.Instance[tipKey];
        StateChip.Visibility = Visibility.Visible;
    }

    private void Action_Click(object sender, RoutedEventArgs e) => Click?.Invoke(this, e);
}
