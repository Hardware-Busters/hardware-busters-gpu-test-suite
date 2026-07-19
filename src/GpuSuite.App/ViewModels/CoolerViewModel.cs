using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpuSuite.App.Services;
using GpuSuite.Load;

namespace GpuSuite.App.ViewModels;

/// <summary>
/// Cooler evaluation console: configure the power axis + noise-normalized fan levels + soak policy, then Start.
/// Streams the runner log live, supports Cancel, and offers the produced HTML report. Precise heat-load control
/// and programmatic fan-setting need the elevated bench (Powenetics) — on the dev PC this builds/launches and
/// drives the load loosely against the LHM board-power sensor.
/// </summary>
public partial class CoolerViewModel : ObservableObject
{
    private readonly Workspace _ws;
    private readonly CoolerService _cooler;
    private CancellationTokenSource? _cts;
    private bool _loadedOnce;

    public ObservableCollection<string> Log { get; } = new();

    // Power axis
    [ObservableProperty] private double fromW = 80;
    [ObservableProperty] private double toW = 250;
    [ObservableProperty] private double stepW = 25;
    [ObservableProperty] private double refPowerW = 250;

    // Noise levels (dBA:fan% pairs from the operator's one-time calibration)
    [ObservableProperty] private string levelsText = "25:30, 30:40, 35:50, 40:60";

    // Soak / settle policy
    [ObservableProperty] private int soakMin = 30;
    [ObservableProperty] private int soakMax = 180;
    [ObservableProperty] private int settleWindow = 45;
    [ObservableProperty] private double settleSlope = 0.3;

    // Options
    [ObservableProperty] private bool manualFan = true;
    [ObservableProperty] private string lockClockText = "";
    [ObservableProperty] private string preferGpu = "";
    [ObservableProperty] private string fanNote = "";
    [ObservableProperty] private double ambient = 0;

    [ObservableProperty] private string statusText = "Idle";
    [ObservableProperty] private string summary = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenReportCommand))]
    private string reportPath = "";

    public CoolerViewModel(Workspace ws, CoolerService cooler)
    {
        _ws = ws;
        _cooler = cooler;
    }

    public Task EnsureLoadedAsync()
    {
        if (_loadedOnce) return Task.CompletedTask;
        _loadedOnce = true;
        // Prefill the GPU pin from settings so the load/fan target the device under test, not an iGPU.
        if (string.IsNullOrWhiteSpace(PreferGpu) && !string.IsNullOrWhiteSpace(_ws.Config.PreferredGpu))
            PreferGpu = _ws.Config.PreferredGpu!;
        return Task.CompletedTask;
    }

    private bool CanStart() => !IsRunning && CoolerLevels.Parse(LevelsText).Count > 0;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var levels = CoolerLevels.Parse(LevelsText);
        if (levels.Count == 0) { StatusText = "Enter at least one dBA:fan% level."; return; }

        int? lockClock = int.TryParse(LockClockText, out var lc) ? lc : null;
        var opt = new CoolerSweepOptions
        {
            FromW = FromW,
            ToW = ToW,
            StepW = StepW,
            ReferencePowerW = RefPowerW,
            NoiseLevels = levels,
            MinSoak = TimeSpan.FromSeconds(Math.Max(1, SoakMin)),
            MaxSoak = TimeSpan.FromSeconds(Math.Max(SoakMin + 1, SoakMax)),
            SettleWindow = TimeSpan.FromSeconds(Math.Max(5, SettleWindow)),
            SettleSlopeCPerMin = SettleSlope,
            FanNote = FanNote,
            AmbientC = Ambient,
        };

        Log.Clear();
        ReportPath = "";
        Summary = "";
        IsRunning = true;
        StatusText = "Running…";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var dispatcher = Application.Current.Dispatcher;

        void OnLog(string s) => dispatcher.BeginInvoke(() =>
        {
            Log.Add(s);
            if (Log.Count > 5000) Log.RemoveAt(0);
        });

        try
        {
            var outcome = await Task.Run(() => _cooler.RunAsync(
                opt, string.IsNullOrWhiteSpace(PreferGpu) ? null : PreferGpu, lockClock, ManualFan, OnLog, ct), ct);

            if (outcome.Error is { } err)
            {
                StatusText = "Failed: " + err;
            }
            else
            {
                ReportPath = outcome.ReportPath;
                if (outcome.Result is { } r)
                {
                    int warningCount = outcome.Warnings?.Count ?? 0;
                    Summary = $"{r.Levels.Count} level(s) · {r.Levels.Sum(l => l.Steps.Count)} step(s)" +
                              (warningCount > 0 ? $" · {warningCount} warning(s)" : "");
                }
                StatusText = outcome.Cancelled
                    ? "Cancelled (partial result saved)"
                    : outcome.IsComplete && (outcome.Warnings?.Count ?? 0) == 0
                        ? "Completed — quality checks passed"
                        : "Completed with warnings — review the sweep log";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled";
        }
        catch (Exception ex)
        {
            StatusText = "Failed: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Cancelling…";
    }

    private bool HasReport() => !string.IsNullOrEmpty(ReportPath);

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void OpenReport()
    {
        if (!string.IsNullOrEmpty(ReportPath) && File.Exists(ReportPath))
            Process.Start(new ProcessStartInfo(ReportPath) { UseShellExecute = true });
    }

    partial void OnLevelsTextChanged(string value) => StartCommand.NotifyCanExecuteChanged();
}
