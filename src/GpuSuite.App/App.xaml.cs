using System.Threading;
using System.Windows;
using System.Windows.Threading;
using GpuSuite.App.Services;
using GpuSuite.App.ViewModels;
using GpuSuite.Authoring;
using Microsoft.Extensions.DependencyInjection;

namespace GpuSuite.App;

/// <summary>
/// Application entry point + composition root. The WPF management UI EMBEDS the engine libraries
/// directly (no HTTP): the App services below wrap the same calls the CLI's Program.cs makes
/// (ProfileManager, LauncherDiscovery/TestPlanResolver, MeasurementFactory, settings.json I/O).
///
/// SINGLE-INSTANCE: only one instance may run per user session. The app drives shared hardware
/// (the GPU load / fan control / clock-lock in the Cooler section) and shared files (profiles +
/// settings.json), so a second instance could fight the first for the card or silently clobber a
/// save. A named <see cref="Mutex"/> enforces it; a second launch signals the running instance to
/// the foreground (so a double-click feels like it focused the app) and then exits.
/// </summary>
public partial class App : Application
{
    /// <summary>Resolved service provider (composition root). Set once at startup.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    // Local\ (per-session) names — deliberately NOT Global\, so separate users / RDP sessions are unaffected.
    private const string SingleInstanceMutexName = "GpuSuite.App.SingleInstance.v1";
    private const string ShowWindowEventName = "GpuSuite.App.ShowWindow.v1";
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showWindowEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance already owns the mutex — ask it to surface, then bow out quietly.
            if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var existing))
            {
                existing.Set();
                existing.Dispose();
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Listen for later launches that ask us to come to the foreground (auto-reset = one pulse per launch).
        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        var listener = new Thread(() =>
        {
            while (_showWindowEvent.WaitOne())
                Dispatcher.BeginInvoke(new Action(BringToForeground));
        })
        { IsBackground = true, Name = "SingleInstanceListener" };
        listener.Start();

        // Surface unhandled UI-thread exceptions instead of a silent crash (this is a dev tool).
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var sc = new ServiceCollection();
        ConfigureServices(sc);
        Services = sc.BuildServiceProvider();

        // Keep the app alive while the modal disclaimer is the only window. Once it is accepted,
        // ownership moves to the main window; dismissing the disclaimer exits explicitly.
        var lifetime = new WpfApplicationLifetimeAdapter(this, () =>
        {
            var window = Services.GetRequiredService<MainWindow>();
            window.DataContext = Services.GetRequiredService<MainViewModel>();
            return window;
        });
        _ = new StartupLifecycleCoordinator(lifetime).Start(
            () => Services.GetRequiredService<DisclaimerService>().EnsureAcknowledged());
    }

    /// <summary>Restore + activate the main window when a second launch pings us (runs on the UI thread).</summary>
    private void BringToForeground()
    {
        if (MainWindow is not { } w) return;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
        w.Topmost = true;   // nudge above other windows…
        w.Topmost = false;  // …without actually pinning it there
        w.Focus();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* not owned / already gone */ }
        _singleInstanceMutex?.Dispose();
        _showWindowEvent?.Dispose();
        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection sc)
    {
        // App services — thin wrappers over the engine, shared app-wide (one Workspace + config).
        sc.AddSingleton<Workspace>();
        sc.AddSingleton<ConfigService>();
        sc.AddSingleton<ProfileService>();
        sc.AddSingleton<ProfilePackService>();
        sc.AddSingleton(sp => new DraftPackService(() => [sp.GetRequiredService<Workspace>().ProfilesDir]));
        sc.AddSingleton<DiscoveryService>();
        sc.AddSingleton<ProbeService>();
        sc.AddSingleton<Gx10ProbeService>();
        sc.AddSingleton<ResultsService>();
        sc.AddSingleton<RunService>();
        sc.AddSingleton<RosterSmokeService>();
        sc.AddSingleton<FullPreflightService>();
        sc.AddSingleton<FeedbackService>();
        sc.AddSingleton<DisclaimerService>();
        sc.AddSingleton<CoolerService>();
        sc.AddSingleton<BotDiagnosticsService>();

        // View models. The detail VM is created on demand by the list (it needs a selected game).
        sc.AddSingleton<MainViewModel>();
        sc.AddSingleton<DashboardViewModel>();
        sc.AddSingleton<GameListViewModel>();
        sc.AddSingleton<CatalogViewModel>();
        sc.AddSingleton<ResultsViewModel>();
        sc.AddSingleton<RunViewModel>();
        sc.AddSingleton<CoolerViewModel>();
        sc.AddSingleton<SettingsViewModel>();
        sc.AddSingleton<BotDiagnosticsViewModel>();
        sc.AddSingleton<ProfilePacksViewModel>();
        sc.AddSingleton<AuthorStudioViewModel>();
        sc.AddSingleton<HelpViewModel>();

        sc.AddSingleton<MainWindow>();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
