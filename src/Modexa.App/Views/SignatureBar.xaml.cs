using System.Windows.Controls;
using System.Windows.Navigation;

namespace Modexa.App.Views;

public partial class SignatureBar : UserControl
{
    public SignatureBar() => InitializeComponent();

    private void Link_Navigate(object sender, RequestNavigateEventArgs e)
    {
        MainWindow.OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }
}
