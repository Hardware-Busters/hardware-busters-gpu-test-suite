using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpuSuite.App.Services;

namespace GpuSuite.App.ViewModels;

/// <summary>
/// Dashboard: the pre-flight hardware probe (same data as `gpusuite probe`). Detects the GPU/CPU
/// under test and lists the live frames/power/telemetry sources, plus a read-only summary of the
/// power-related settings. Runs the probe off the UI thread (it touches LHM/PresentMon).
/// </summary>
public partial class DashboardViewModel : ObservableObject
{
    private readonly ProbeService _probe;

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool hasResult;
    [ObservableProperty] private string gpuName = "";
    [ObservableProperty] private string cpuName = "";
    [ObservableProperty] private string preferredGpu = "";
    [ObservableProperty] private string powerSource = "";
    [ObservableProperty] private string frameProvider = "";
    [ObservableProperty] private string poweneticsPort = "";
    [ObservableProperty] private string powerProvenanceStatus = "NOT A HARDWARE RESULT";
    [ObservableProperty] private string powerProvenanceExplanation = "Power provenance is not available until the probe completes.";
    [ObservableProperty] private bool osdEnabled;
    [ObservableProperty] private string? error;

    public ObservableCollection<string> ProbeLines { get; } = new();

    public DashboardViewModel(ProbeService probe)
    {
        _probe = probe;
        _ = RefreshAsync();   // probe at startup
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Error = null;
        try
        {
            var r = await _probe.ProbeAsync();
            GpuName = r.GpuName;
            CpuName = r.CpuName;
            PreferredGpu = string.IsNullOrWhiteSpace(r.PreferredGpu) ? "(auto — highest load)" : r.PreferredGpu;
            PowerSource = r.PowerSource;
            var powerProvenance = DescribePowerProvenance(r.EffectivePowerMeasurement);
            PowerProvenanceStatus = powerProvenance.Status;
            PowerProvenanceExplanation = powerProvenance.Explanation;
            FrameProvider = r.FrameProvider;
            PoweneticsPort = string.IsNullOrWhiteSpace(r.PoweneticsComPort) ? "(not set)" : r.PoweneticsComPort;
            OsdEnabled = r.Osd;

            ProbeLines.Clear();
            foreach (var line in r.Lines) ProbeLines.Add(line);
            HasResult = true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Maps the factory-selected measurement contract to the exact dashboard status text.</summary>
    public static (string Status, string Explanation) DescribePowerProvenance(GpuSuite.Core.Models.PowerMeasurementMetadata measurement) =>
        (GpuSuite.Core.Models.PowerProvenance.Label(measurement), measurement.QualificationNote);
}
