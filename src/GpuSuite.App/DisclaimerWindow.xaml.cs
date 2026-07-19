using System.Windows;

namespace GpuSuite.App;

public partial class DisclaimerWindow : Window
{
    private readonly bool _requireAcknowledgement;

    /// <summary>Whether a successful acknowledgement should be remembered for this disclaimer version.</summary>
    public bool DontShowAgain => DontShowAgainCheck.IsChecked == true;

    public DisclaimerWindow(bool requireAcknowledgement)
    {
        InitializeComponent();
        _requireAcknowledgement = requireAcknowledgement;

        if (requireAcknowledgement)
        {
            ContinueButton.IsEnabled = false;
        }
        else
        {
            AcknowledgementCheck.Visibility = Visibility.Collapsed;
            DontShowAgainCheck.Visibility = Visibility.Collapsed;
            ExitButton.Visibility = Visibility.Collapsed;
            ContinueButton.Content = "Close";
            ContinueButton.IsEnabled = true;
        }
    }

    private void OnAcknowledgementChanged(object sender, RoutedEventArgs e)
    {
        if (_requireAcknowledgement)
            ContinueButton.IsEnabled = AcknowledgementCheck.IsChecked == true;
    }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        if (!_requireAcknowledgement || AcknowledgementCheck.IsChecked == true)
            DialogResult = true;
    }

    private void OnExit(object sender, RoutedEventArgs e) => DialogResult = false;
}
