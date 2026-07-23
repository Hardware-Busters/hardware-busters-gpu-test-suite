using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpuSuite.App.Services;
using System.Windows;

namespace GpuSuite.App.ViewModels;

/// <summary>Shell view model: navigation between sections + the active workspace folder + version.</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly Workspace _ws;
    private readonly FeedbackService _feedback;
    private readonly DisclaimerService _disclaimer;

    public DashboardViewModel Dashboard { get; }
    public GameListViewModel Games { get; }
    public CatalogViewModel Catalog { get; }
    public RunViewModel Run { get; }
    public CoolerViewModel Cooler { get; }
    public ResultsViewModel Results { get; }
    public SettingsViewModel Settings { get; }
    public BotDiagnosticsViewModel Diagnostics { get; }
    public ProfilePacksViewModel ProfilePacks { get; }
    public AuthorStudioViewModel AuthorStudio { get; }
    public HelpViewModel Help { get; }

    /// <summary>Suite build version (e.g. "v0.1.0.16"), from the generated BuildInfo.</summary>
    public string Version { get; }

    [ObservableProperty] private object? currentView;
    [ObservableProperty] private string workingFolder = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FeedbackButtonText))]
    [NotifyCanExecuteChangedFor(nameof(ReportFeedbackCommand))]
    private bool isPreparingFeedback;

    public string FeedbackButtonText => IsPreparingFeedback ? "Opening issue form…" : "Open public issue";

    public MainViewModel(Workspace ws, DashboardViewModel dashboard, GameListViewModel games, CatalogViewModel catalog,
        RunViewModel run, CoolerViewModel cooler, ResultsViewModel results, SettingsViewModel settings,
        BotDiagnosticsViewModel diagnostics, ProfilePacksViewModel profilePacks, AuthorStudioViewModel authorStudio, HelpViewModel help, FeedbackService feedback,
        DisclaimerService disclaimer)
    {
        _ws = ws;
        _feedback = feedback;
        _disclaimer = disclaimer;
        Dashboard = dashboard;
        Games = games;
        Catalog = catalog;
        Run = run;
        Cooler = cooler;
        Results = results;
        Settings = settings;
        Diagnostics = diagnostics;
        ProfilePacks = profilePacks;
        AuthorStudio = authorStudio;
        Help = help;

        Version = "v" + GpuSuite.BuildInfo.Version;
        WorkingFolder = _ws.Root;
        CurrentView = dashboard;

        _ws.Changed += () => WorkingFolder = _ws.Root;
    }
    private bool CanReportFeedback() => !IsPreparingFeedback;

    [RelayCommand]
    private void ShowDisclaimer() => _disclaimer.ShowForReview(Application.Current.MainWindow);

    [RelayCommand(CanExecute = nameof(CanReportFeedback))]
    private void ReportFeedback()
    {
        IsPreparingFeedback = true;
        try { _feedback.OpenPublicIssue(); }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the public issue form.\n\n{ex.Message}",
                "Open public issue", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { IsPreparingFeedback = false; }
    }

    [RelayCommand] private void ShowDashboard() => CurrentView = Dashboard;
    [RelayCommand] private void ShowGames() => CurrentView = Games;
    [RelayCommand] private void ShowSettings() => CurrentView = Settings;

    [RelayCommand]
    private void ShowCatalog()
    {
        CurrentView = Catalog;
        _ = Catalog.EnsureLoadedAsync();   // lazy first-load (avoids a second startup scan)
    }

    [RelayCommand]
    private void ShowResults()
    {
        CurrentView = Results;
        _ = Results.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void ShowRun()
    {
        CurrentView = Run;
        _ = Run.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void ShowCooler()
    {
        CurrentView = Cooler;
        _ = Cooler.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void ShowDiagnostics()
    {
        CurrentView = Diagnostics;
        _ = Diagnostics.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void ShowProfilePacks()
    {
        CurrentView = ProfilePacks;
        _ = ProfilePacks.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void ShowAuthorStudio()
    {
        CurrentView = AuthorStudio;
    }

    [RelayCommand]
    private void ShowHelp()
    {
        Help.OpenSection("");
        CurrentView = Help;
    }

    [RelayCommand]
    private void ShowContextHelp()
    {
        string section = CurrentView switch
        {
            GameListViewModel => "Benchmark List",
            CatalogViewModel => "Installed Games",
            RunViewModel => "Run",
            CoolerViewModel => "Cooler",
            ResultsViewModel => "Results",
            SettingsViewModel => "Settings",
            BotDiagnosticsViewModel => "Bot diagnostics",
            ProfilePacksViewModel => "Profile packs",
            AuthorStudioViewModel => "Author Studio",
            _ => "Dashboard"
        };
        Help.OpenSection(section);
        CurrentView = Help;
    }

    [RelayCommand]
    private void ChangeWorkspace()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the suite working folder (contains settings.json + profiles)",
            InitialDirectory = _ws.Root
        };
        if (dlg.ShowDialog() == true)
            _ws.SetRoot(dlg.FolderName);
    }
}
