using System.Windows;
using System.Windows.Input;
using System.Windows.Shell;
using GpuSuite.App.ViewModels;

namespace GpuSuite.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        StateChanged += (_, _) => UpdateMaximizeRestoreGlyph();
        Loaded += (_, _) => UpdateMaximizeRestoreGlyph();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F1 || DataContext is not MainViewModel vm) return;
        vm.ShowContextHelpCommand.Execute(null);
        e.Handled = true;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void UpdateMaximizeRestoreGlyph()
    {
        MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    private void OnClose(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
}
