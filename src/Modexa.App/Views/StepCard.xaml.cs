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
        ActionButton.Style = (Style)FindResource(done ? "DangerButton" : "PrimaryButton");
        if (done) Services.Tr.Bind(ActionButton, ContentProperty, "Mods_Uninstall");
        else ActionButton.SetBinding(ContentProperty, new System.Windows.Data.Binding(nameof(ActionText)) { Source = this });
    }

    private void Action_Click(object sender, RoutedEventArgs e) => Click?.Invoke(this, e);
}
