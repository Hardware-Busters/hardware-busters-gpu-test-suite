using System.Windows.Controls;
using System.Windows;
using GpuSuite.App.Services;

namespace GpuSuite.App.Views;

public partial class HelpView : UserControl
{
    public HelpView() => InitializeComponent();

    private void OpenPatreon(object sender, RoutedEventArgs e) => _ = new ExternalLinkService().Open(ExternalLinkService.PatreonUrl);
}
