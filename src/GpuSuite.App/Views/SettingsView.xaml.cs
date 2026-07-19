using System.Windows.Controls;
using System.Windows;
using GpuSuite.App.ViewModels;

namespace GpuSuite.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void SaveOpenAiKey_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && vm.SaveApiKey("openai", OpenAiKeyBox.Password))
            OpenAiKeyBox.Clear();
    }

    private void SaveAnthropicKey_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && vm.SaveApiKey("anthropic", AnthropicKeyBox.Password))
            AnthropicKeyBox.Clear();
    }
}
