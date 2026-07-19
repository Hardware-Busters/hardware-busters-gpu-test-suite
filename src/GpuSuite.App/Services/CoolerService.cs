using System.IO;
using GpuSuite.Core.Io;
using GpuSuite.Core.Models;
using GpuSuite.Load;
using GpuSuite.Measurement;
using GpuSuite.Reporting;

namespace GpuSuite.App.Services;

/// <summary>Outcome of a cooler sweep, including integrity warnings that keep partial data from looking complete.</summary>
public sealed record CoolerOutcome(
    bool Cancelled,
    CoolerSweepResult? Result,
    string JsonPath,
    string ReportPath,
    string? Error = null,
    IReadOnlyList<string>? Warnings = null,
    bool IsComplete = false);

/// <summary>
/// Drives a cooler sweep from the UI, mirroring the CLI `cooler` flow: build the measurement factory, start the
/// live power + telemetry providers, create the controllable GPU load (+ optional fan control / clock-lock),
/// run the <see cref="CoolerTestRunner"/> over the noise levels × power axis, then save the CoolerSweepResult
/// JSON and the HTML report. Precise heat-load control + programmatic fan need the elevated bench (Powenetics).
/// </summary>
public sealed class CoolerService
{
    private readonly Workspace _ws;
    public CoolerService(Workspace ws) => _ws = ws;

    public async Task<CoolerOutcome> RunAsync(
        CoolerSweepOptions opt, string? preferGpu, int? lockClock, bool manualFan,
        Action<string> onLog, CancellationToken ct)
    {
        var cfg = _ws.Config;
        Directory.CreateDirectory(cfg.ResultsRoot);

        using var factory = new MeasurementFactory(cfg);
        foreach (var line in factory.ProbeAll()) onLog(line);

        var power = factory.SelectPowerProvider();
        var telemetry = factory.SelectTelemetryProvider();
        onLog($"GPU under test: {factory.DetectedGpuName}  ·  power: {power.Name} (live={power.IsLive})");
        if (!power.IsLive)
            return new CoolerOutcome(false, null, "", "",
                "No live GPU power source is available. Cooler load targeting cannot use synthetic data.");

        string? pref = string.IsNullOrWhiteSpace(preferGpu)
            ? (string.IsNullOrWhiteSpace(cfg.PreferredGpu) ? null : cfg.PreferredGpu)
            : preferGpu;

        // Provider teardown is best-effort. A serial/device disposal failure must not turn an already
        // completed sweep with saved reports into a UI failure.
        var powerSession = await power.StartAsync(cfg.PowerLogIntervalMs, new WorkloadHint(), ct);
        var telSession = await telemetry.StartAsync(Math.Max(100, cfg.TelemetryIntervalMs), new WorkloadHint(), ct);
        try
        {

        ComputeSharpGpuLoad load;
        try { load = new ComputeSharpGpuLoad(pref, lockClock); }
        catch (Exception ex) { return new CoolerOutcome(false, null, "", "", "No D3D12 device: " + ex.Message); }
        string gpuName = load.GpuName;
        onLog($"Load device: {gpuName}");

        GpuFanController? fan = null;
        if (!manualFan)
        {
            fan = new GpuFanController(pref);
            if (!fan.CanControl)
            {
                string reason = fan.Diagnostics;
                fan.Dispose();
                return new CoolerOutcome(false, null, "", "",
                    "Automatic fan control is unavailable: " + reason + " Enable Manual fan only with an operator holding calibrated speeds.");
            }
            onLog($"Fan control interface found ({fan.Vendor}); verifying a real response at 60%...");
            bool fanVerified = fan.TrySetPercentVerified(60, TimeSpan.FromSeconds(8), out string evidence);
            fan.ResetAuto();
            if (!fanVerified)
            {
                fan.Dispose();
                return new CoolerOutcome(false, null, "", "",
                    "Automatic fan command was ignored: " + evidence + ". Use Administrator/vendor fan control or a supervised manual-fan run.");
            }
            onLog("Fan control verified — " + evidence);
        }
        else onLog("Fan control: MANUAL — hold each calibrated fan speed yourself.");

        load.Start();
        if (lockClock is int lc)
            onLog(load.ClockLocked ? $"Clock locked at {lc} MHz." : $"Clock lock FAILED ({lc} MHz) — needs Administrator + NVIDIA.");

        Func<double?> readWatts = () => powerSession.Latest?.GpuTotalW ?? telSession.Latest?.GpuBoardPowerW;
        var runner = new CoolerTestRunner(load, readWatts, () => telSession.Latest, fan, onLog);

        double lastPrint = -100;
        void OnTick(PowerTick t)
        {
            if (t.ElapsedSec < lastPrint) lastPrint = -100;
            if (t.ElapsedSec - lastPrint < 8) return;
            lastPrint = t.ElapsedSec;
            var tel = telSession.Latest;
            onLog($"   t={t.ElapsedSec,4:0}s  target={t.TargetW:0}W  power={(t.Watts?.ToString("0") ?? "—")}W  " +
                  $"gpu={(tel?.GpuTempC?.ToString("0") ?? "—")}C");
        }

        CoolerSweepResult result;
        try { result = await runner.RunAsync(opt, OnTick, ct); }
        finally { load.Stop(); load.Dispose(); fan?.Dispose(); }

        result.PowerSource = power.Name;
        if (string.IsNullOrWhiteSpace(result.Vendor) || result.Vendor == "Unknown")
            result.Vendor = InferVendor(gpuName);

        string jsonPath = Path.Combine(cfg.ResultsRoot, $"cooler_{Sanitize(gpuName)}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        Json.Save(jsonPath, result);
        string htmlPath = Path.ChangeExtension(jsonPath, ".html");
        new CoolerReportGenerator().Save(htmlPath, result);
        onLog($"Saved → {jsonPath}");
        onLog($"Report → {htmlPath}");

        var quality = CoolerResultQualityEvaluator.Evaluate(opt, result, ct.IsCancellationRequested, manualFan);
        foreach (string warning in quality.Warnings) onLog("! " + warning);

            return new CoolerOutcome(
                ct.IsCancellationRequested, result, jsonPath, htmlPath,
                Warnings: quality.Warnings, IsComplete: quality.IsComplete);
        }
        finally
        {
            await DisposeSessionQuietly(telSession, "telemetry", onLog);
            await DisposeSessionQuietly(powerSession, "power", onLog);
        }
    }

    private static async Task DisposeSessionQuietly<T>(ISampleSession<T> session, string name, Action<string> onLog)
    {
        try { await session.DisposeAsync(); }
        catch (Exception ex) { onLog($"! {name} provider cleanup warning (result is safe): {ex.Message}"); }
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Replace(' ', '_');
    }

    private static string InferVendor(string gpuName)
    {
        string n = gpuName.ToLowerInvariant();
        if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("rtx") || n.Contains("gtx")) return "NVIDIA";
        if (n.Contains("radeon") || n.Contains("amd") || n.Contains("rx ")) return "AMD";
        if (n.Contains("intel") || n.Contains("arc")) return "Intel";
        return "Unknown";
    }
}
