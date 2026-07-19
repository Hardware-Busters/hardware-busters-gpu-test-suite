using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpuSuite.App.Services;

namespace GpuSuite.App.ViewModels;

public sealed class ResultComparisonRow
{
    public string Label { get; init; } = "";
    public double BaselineFps { get; init; }
    public double TargetFps { get; init; }
    public double FpsDeltaPct { get; init; }
    public double LowDeltaPct { get; init; }
    public double? PowerDeltaPct { get; init; }
    public double? EfficiencyDeltaPct { get; init; }
    public string? PowerComparisonReason { get; init; }
}

/// <summary>
/// Results browser: lists saved suite result sets (one per GPU under Results/) and shows the selected
/// one's system info + per-scene aggregates, with shortcuts to open its HTML report or folder.
/// Read-only. Loaded lazily on first navigation.
/// </summary>
public partial class ResultsViewModel : ObservableObject
{
    private readonly ResultsService _results;
    private bool _loadedOnce;

    public ObservableCollection<SuiteResultEntry> Results { get; } = new();
    public ObservableCollection<ResultComparisonRow> ComparisonRows { get; } = new();

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private string summary = "";
    [ObservableProperty] private string comparisonSummary = "Select two result sets to compare matching cells.";
    [ObservableProperty] private SuiteResultEntry? comparisonBaseline;
    [ObservableProperty] private SuiteResultEntry? comparisonTarget;

    partial void OnComparisonBaselineChanged(SuiteResultEntry? value) => BuildComparison();
    partial void OnComparisonTargetChanged(SuiteResultEntry? value) => BuildComparison();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    private SuiteResultEntry? selectedResult;

    public ResultsViewModel(ResultsService results) => _results = results;

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
            var items = await _results.LoadAllAsync();
            Results.Clear();
            foreach (var e in items) Results.Add(e);
            SelectedResult = Results.FirstOrDefault();
            ComparisonTarget = Results.FirstOrDefault();
            ComparisonBaseline = Results.Skip(1).FirstOrDefault();
            IsEmpty = Results.Count == 0;
            Summary = Results.Count == 1 ? "1 result set" : $"{Results.Count} result sets";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BuildComparison()
    {
        ComparisonRows.Clear();
        if (ComparisonBaseline is null || ComparisonTarget is null)
        {
            ComparisonSummary = Results.Count < 2
                ? "At least two saved result sets are required."
                : "Select a baseline and target result.";
            return;
        }

        static string Key(GpuSuite.Core.Models.SceneResolutionAggregate a) =>
            string.Join("|", a.GameId, a.SceneId, a.VariantId ?? "", a.ResolutionName);
        var baseline = ComparisonBaseline.Result.Aggregates.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
        int suppressedPower = 0;
        foreach (var target in ComparisonTarget.Result.Aggregates)
        {
            if (!baseline.TryGetValue(Key(target), out var source)) continue;
            bool compatiblePower = GpuSuite.Core.Models.PowerProvenance.AreCompatible(source.PowerMeasurement, target.PowerMeasurement);
            bool directEfficiency = compatiblePower
                && GpuSuite.Core.Models.PowerProvenance.IsDirectEfficiencyEligible(source.PowerMeasurement)
                && GpuSuite.Core.Models.PowerProvenance.IsDirectEfficiencyEligible(target.PowerMeasurement);
            string? powerReason = compatiblePower ? null : "Power comparison suppressed: incompatible measurement provenance.";
            if (powerReason is not null) suppressedPower++;
            ComparisonRows.Add(new ResultComparisonRow
            {
                Label = $"{target.GameId} · {target.SceneId} · {target.ResolutionName} · {(string.IsNullOrWhiteSpace(target.VariantName) ? "default" : target.VariantName)}",
                BaselineFps = source.AvgFps,
                TargetFps = target.AvgFps,
                FpsDeltaPct = Delta(source.AvgFps, target.AvgFps),
                LowDeltaPct = Delta(source.P1LowFps, target.P1LowFps),
                PowerDeltaPct = compatiblePower ? Delta(source.AvgGpuPowerW, target.AvgGpuPowerW) : null,
                EfficiencyDeltaPct = directEfficiency ? Delta(source.FpsPerWatt, target.FpsPerWatt) : null,
                PowerComparisonReason = powerReason
            });
        }
        ComparisonSummary = ComparisonRows.Count == 0
            ? "The selected result sets have no matching game/scene/resolution/model cells."
            : $"{ComparisonRows.Count} matching cell(s) · percentages show target versus baseline." +
              (suppressedPower > 0 ? $" Power comparison suppressed for {suppressedPower} cell(s): incompatible measurement provenance." : "");
    }

    private static double Delta(double baseline, double target) => baseline == 0 ? 0 : (target / baseline - 1) * 100;
    private static double? Delta(double? baseline, double? target) =>
        baseline is > 0 && target.HasValue ? (target.Value / baseline.Value - 1) * 100 : null;

    private bool HasReport() => SelectedResult?.HasReport == true;
    private bool HasSelection() => SelectedResult is not null;

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void OpenReport()
    {
        var path = SelectedResult?.ReportPath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenFolder()
    {
        var dir = SelectedResult?.GpuDir;
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }
}
