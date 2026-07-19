using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpuSuite.App.Services;

namespace GpuSuite.App.ViewModels;

public partial class BotDiagnosticsViewModel : ObservableObject
{
    private readonly BotDiagnosticsService _diagnostics;
    private bool _loadedOnce;

    public ObservableCollection<BotDiagnosticRow> Rows { get; } = new();

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string summary = "No diagnostic snapshot loaded.";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenEvidenceCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenLatestEvidenceCommand))]
    private BotDiagnosticRow? selectedRow;

    public BotDiagnosticsViewModel(BotDiagnosticsService diagnostics) => _diagnostics = diagnostics;

    public async Task EnsureLoadedAsync()
    {
        if (_loadedOnce) return;
        _loadedOnce = true;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var rows = await _diagnostics.LoadAsync();
            Rows.Clear();
            foreach (var row in rows) Rows.Add(row);
            SelectedRow = Rows.FirstOrDefault();
            int healthy = Rows.Count(row => row.IsHealthy);
            int incidents = Rows.Sum(row => row.IncidentCount);
            Summary = Rows.Count == 0
                ? "Run a benchmark sweep to create bot and runtime-health evidence."
                : $"Latest result per game: {healthy}/{Rows.Count} clean · {Rows.Sum(row => row.InvalidRuns)} invalid run(s) · {incidents} health incident(s)";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool HasEvidenceFolder() => SelectedRow is { EvidencePath.Length: > 0 } row && Directory.Exists(row.EvidencePath);
    private bool HasLatestEvidence() => SelectedRow is { LatestEvidence.Length: > 0 } row && File.Exists(row.LatestEvidence);

    [RelayCommand(CanExecute = nameof(HasEvidenceFolder))]
    private void OpenEvidence()
    {
        if (SelectedRow is { } row && Directory.Exists(row.EvidencePath))
            Process.Start(new ProcessStartInfo(row.EvidencePath) { UseShellExecute = true });
    }

    [RelayCommand(CanExecute = nameof(HasLatestEvidence))]
    private void OpenLatestEvidence()
    {
        if (SelectedRow is { } row && File.Exists(row.LatestEvidence))
            Process.Start(new ProcessStartInfo(row.LatestEvidence) { UseShellExecute = true });
    }
}
