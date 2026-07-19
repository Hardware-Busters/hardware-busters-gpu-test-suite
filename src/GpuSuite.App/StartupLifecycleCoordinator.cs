using System.Windows;

namespace GpuSuite.App;

/// <summary>
/// Minimal application-lifetime surface used during startup. Keeping this separate from the
/// composition root lets the ordering around the first-run disclaimer be tested without creating
/// the hardware service graph.
/// </summary>
internal interface IApplicationLifetimeAdapter
{
    void AssignMainWindow();
    void SetShutdownModeOnMainWindowClose();
    void ShowMainWindow();
    void Shutdown();
}

/// <summary>Coordinates the one irreversible startup transition from disclaimer to main window.</summary>
internal sealed class StartupLifecycleCoordinator
{
    private readonly IApplicationLifetimeAdapter _lifetime;

    public StartupLifecycleCoordinator(IApplicationLifetimeAdapter lifetime)
    {
        _lifetime = lifetime;
    }

    /// <returns><see langword="true"/> when the main window was shown.</returns>
    public bool Start(Func<bool> ensureDisclaimerAcknowledged)
    {
        if (!ensureDisclaimerAcknowledged())
        {
            _lifetime.Shutdown();
            return false;
        }

        _lifetime.AssignMainWindow();
        _lifetime.SetShutdownModeOnMainWindowClose();
        _lifetime.ShowMainWindow();
        return true;
    }
}

/// <summary>WPF implementation of <see cref="IApplicationLifetimeAdapter"/> for the real app.</summary>
internal sealed class WpfApplicationLifetimeAdapter : IApplicationLifetimeAdapter
{
    private readonly Application _application;
    private readonly Func<Window> _mainWindowFactory;
    private Window? _mainWindow;

    public WpfApplicationLifetimeAdapter(Application application, Func<Window> mainWindowFactory)
    {
        _application = application;
        _mainWindowFactory = mainWindowFactory;
    }

    public void AssignMainWindow() => _application.MainWindow = GetMainWindow();

    public void SetShutdownModeOnMainWindowClose() =>
        _application.ShutdownMode = ShutdownMode.OnMainWindowClose;

    public void ShowMainWindow() => GetMainWindow().Show();

    public void Shutdown() => _application.Shutdown();

    private Window GetMainWindow() => _mainWindow ??= _mainWindowFactory();
}
