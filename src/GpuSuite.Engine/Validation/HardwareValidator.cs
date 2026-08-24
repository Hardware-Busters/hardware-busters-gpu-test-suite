using System.Diagnostics;
using System.Runtime.InteropServices;
using GpuSuite.Core.Config;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Diagnostics;
using GpuSuite.Measurement;

namespace GpuSuite.Engine.Validation;

/// <summary>
/// Pre-game hardware-validation gate (Milestone 2). Before each game it verifies the bench is in a known-good
/// state: the GPU exists and matches the expected model + VRAM, the PCIe link is healthy, a monitor is
/// attached, the frame + telemetry (+ Powenetics, if required) sources are live, there's enough disk, and the
/// GPU isn't already over temperature. A failed FATAL check aborts ONLY the affected benchmark (the game is
/// classified <see cref="FailureClass.HardwarePrecheck"/> and skipped); the roster continues. Every failure
/// carries a clear reason.
/// </summary>
public sealed class HardwareValidator
{
    private readonly SuiteConfig _cfg;
    private readonly RunLogger _log;

    public HardwareValidator(SuiteConfig cfg, RunLogger log) { _cfg = cfg; _log = log; }

    public async Task<HardwareValidationResult> ValidateAsync(
        MeasurementFactory factory,
        FrameCapturePreflightPlan? framePlan = null,
        bool? rtssReadyForLazyStart = null)
    {
        var hv = _cfg.HardwareValidation;
        var r = new HardwareValidationResult();
        var smi = QueryNvidiaSmi();   // null when nvidia-smi is unavailable

        // ---- GPU exists ----
        bool gpuDetected = !string.IsNullOrWhiteSpace(factory.DetectedGpuName)
                           && !factory.DetectedGpuName.Equals("Unknown GPU", StringComparison.OrdinalIgnoreCase);
        string gpuName = smi?.Name ?? factory.DetectedGpuName;
        r.Checks.Add(gpuDetected || smi is not null
            ? HardwareCheck.Pass("GPU present", gpuName)
            : HardwareCheck.Fail("GPU present", "no GPU detected (LHM and nvidia-smi both failed)"));

        // ---- expected GPU model ----
        var expectedModel = string.IsNullOrWhiteSpace(hv.ExpectedGpuModel) ? _cfg.PreferredGpu : hv.ExpectedGpuModel;
        if (!string.IsNullOrWhiteSpace(expectedModel))
        {
            bool ok = ModelMatches(gpuName, expectedModel!);
            r.Checks.Add(ok
                ? HardwareCheck.Pass("Expected GPU model", $"'{gpuName}' matches '{expectedModel}'")
                : HardwareCheck.Fail("Expected GPU model", $"detected '{gpuName}' does not match expected '{expectedModel}'"));
        }

        // ---- VRAM ----
        if (hv.ExpectedVramGb > 0)
        {
            if (smi is { VramGb: > 0 })
                r.Checks.Add(smi.VramGb >= hv.ExpectedVramGb - 1
                    ? HardwareCheck.Pass("VRAM", $"{smi.VramGb} GB (expected ≥ {hv.ExpectedVramGb})")
                    : HardwareCheck.Fail("VRAM", $"{smi.VramGb} GB present, expected ≥ {hv.ExpectedVramGb} GB"));
            else
                r.Checks.Add(HardwareCheck.Fail("VRAM", "could not query VRAM (nvidia-smi unavailable)", fatal: false));
        }

        // ---- PCIe link width / gen (non-fatal: a downshift is informational) ----
        if (hv.MinPcieWidth > 0)
        {
            if (smi is { PcieWidth: > 0 })
                r.Checks.Add(smi.PcieWidth >= hv.MinPcieWidth
                    ? HardwareCheck.Pass("PCIe link width", $"x{smi.PcieWidth} (expected ≥ x{hv.MinPcieWidth})", fatal: false)
                    : HardwareCheck.Fail("PCIe link width", $"x{smi.PcieWidth} (expected ≥ x{hv.MinPcieWidth}) — link may be downshifted", fatal: false));
            else
                r.Checks.Add(HardwareCheck.Fail("PCIe link width", "could not query (nvidia-smi unavailable)", fatal: false));
        }
        if (hv.MinPcieGen > 0)
        {
            if (smi is { PcieGen: > 0 })
                r.Checks.Add(smi.PcieGen >= hv.MinPcieGen
                    ? HardwareCheck.Pass("PCIe link gen", $"Gen{smi.PcieGen} (expected ≥ Gen{hv.MinPcieGen})", fatal: false)
                    : HardwareCheck.Fail("PCIe link gen", $"Gen{smi.PcieGen} (expected ≥ Gen{hv.MinPcieGen})", fatal: false));
            else
                r.Checks.Add(HardwareCheck.Fail("PCIe link gen", "could not query (nvidia-smi unavailable)", fatal: false));
        }

        // ---- monitor attached ----
        if (hv.RequireMonitor)
        {
            int monitors = MonitorCount();
            r.Checks.Add(monitors > 0
                ? HardwareCheck.Pass("Monitor attached", $"{monitors} display(s)")
                : HardwareCheck.Fail("Monitor attached", "no display detected (capture/refresh control would fail)"));
        }

        // ---- FPS / frametime backend (PresentMon and/or RTSS, never the vision capture card) ----
        if (hv.RequirePresentMon)
        {
            bool rtssAvailable = rtssReadyForLazyStart ?? factory.RtssLive;
            var verdict = framePlan?.Evaluate(factory.PresentMonLive, rtssAvailable, _cfg.ForceSyntheticFrames);
            bool framesOk = verdict?.IsReady ?? (factory.PresentMonLive || rtssAvailable || _cfg.ForceSyntheticFrames);
            string detail = verdict?.Detail ?? (_cfg.ForceSyntheticFrames ? "synthetic frames forced (capture not required)"
                : factory.PresentMonLive ? "PresentMon available"
                : factory.RtssLive ? "RTSS frame backend available"
                : "no FPS / frametime backend available");
            r.Checks.Add(framesOk
                ? HardwareCheck.Pass("FPS / frametime capture", detail)
                : HardwareCheck.Fail("FPS / frametime capture", detail));
        }

        // ---- telemetry alive ----
        if (hv.RequireTelemetry)
        {
            if (_cfg.ForceSyntheticTelemetry)
                r.Checks.Add(HardwareCheck.Pass("Telemetry", "synthetic telemetry forced", fatal: false));
            else
                r.Checks.Add(factory.LhmLive
                    ? HardwareCheck.Pass("Telemetry", "LibreHardwareMonitor live")
                    : HardwareCheck.Fail("Telemetry", "LibreHardwareMonitor not live (no GPU/CPU sensors)"));
        }

        // ---- Powenetics (only when required) ----
        bool poweneticsRequired = hv.RequirePowenetics
            || string.Equals(_cfg.PowerSource, "powenetics", StringComparison.OrdinalIgnoreCase);
        if (poweneticsRequired && !_cfg.ForceSyntheticPower)
        {
            r.Checks.Add(factory.PoweneticsLive
                ? HardwareCheck.Pass("Powenetics PMD", "PMD streaming")
                : HardwareCheck.Fail("Powenetics PMD", $"PMD not streaming (port {_cfg.PoweneticsComPort ?? "unset"})"));
        }
        else if (!string.IsNullOrWhiteSpace(_cfg.PoweneticsComPort) && !_cfg.ForceSyntheticPower)
        {
            // PMD configured but optional (powerSource=auto): a drop is a warning — LHM board power covers it.
            r.Checks.Add(factory.PoweneticsLive
                ? HardwareCheck.Pass("Powenetics PMD", "PMD streaming", fatal: false)
                : HardwareCheck.Fail("Powenetics PMD", "PMD not streaming — falling back to LHM board power (if its COM port is still present, the MCU is WEDGED: physically replug its USB — a host reboot does not reset it)", fatal: false));
        }

        // ---- disk space ----
        double freeGb = FreeDiskGb(_cfg.ResultsRoot);
        r.Checks.Add(freeGb >= hv.MinFreeDiskGb
            ? HardwareCheck.Pass("Disk space", $"{freeGb:0.0} GB free (need ≥ {hv.MinFreeDiskGb:0.0})")
            : HardwareCheck.Fail("Disk space", $"{freeGb:0.0} GB free, need ≥ {hv.MinFreeDiskGb:0.0} GB"));

        // ---- GPU temperature pre-check ----
        if (smi is { TempC: > 0 })
            r.Checks.Add(smi.TempC <= hv.MaxGpuTempC
                ? HardwareCheck.Pass("GPU temperature", $"{smi.TempC:0}°C (limit {hv.MaxGpuTempC:0})")
                : HardwareCheck.Fail("GPU temperature", $"{smi.TempC:0}°C exceeds the {hv.MaxGpuTempC:0}°C pre-run limit — GPU not cooled down"));

        // ---- vision-nav endpoint alive (settings.navSupervisor = "vision") ----
        // #201 (root incident 2026-07-09): Ollama dies across sessions. With the endpoint dead, every
        // vision-supervised nav wedges into gate timeouts and the game burns its whole run budget failing
        // nav (the AW2 as-set 4K contamination). The nav CANNOT succeed without the endpoint, so this is a
        // FATAL per-game check — classify + skip up-front instead of an hour of doomed retries.
        if (string.Equals(_cfg.NavSupervisor?.Trim(), "vision", StringComparison.OrdinalIgnoreCase))
        {
            bool up = false;
            string detail;
            try
            {
                var backend = GpuSuite.Engine.Automation.VisionNavSupervisor.ResolveBackend(_cfg, _log);
                if (backend.Provider != GpuSuite.Engine.Automation.VisionNavSupervisor.VisionApiProvider.Ollama)
                {
                    // Do not make a billable cloud inference merely to preflight. A configured key is the
                    // available local proof; each real nav request is still fail-closed on HTTP/auth failure.
                    up = !string.IsNullOrWhiteSpace(backend.ApiKey);
                    detail = up ? $"{backend.Source} configured for '{backend.Model}' (key available; no cloud request sent during pre-flight)"
                                : $"{backend.Source} selected but its API key is unavailable";
                }
                else
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    using var resp = await http.GetAsync(backend.Endpoint.TrimEnd('/') + "/api/version").ConfigureAwait(false);
                    up = resp.IsSuccessStatusCode;
                    detail = up
                        ? $"alive on {backend.Source} ({backend.Endpoint})"
                        : $"{backend.Endpoint} answered HTTP {(int)resp.StatusCode}";
                }
            }
            catch (Exception ex) { detail = "unreachable: " + ex.Message; }
            r.Checks.Add(up
                ? HardwareCheck.Pass("Vision-nav endpoint", detail)
                : HardwareCheck.Fail("Vision-nav endpoint", detail + " — vision-supervised nav cannot succeed; configure its API key or start Ollama (`ollama serve`)."));
        }

        return r;
    }

    /// <summary>Log the result line-by-line at the appropriate level.</summary>
    public void LogResult(string gameId, HardwareValidationResult r)
    {
        _log.Info("HwCheck", $"{gameId}: hardware validation — {r.Summary}{(r.Ok ? "" : " — ABORTING this benchmark")}.");
        foreach (var c in r.Checks)
        {
            if (c.Passed) _log.Trace("HwCheck", $"  ✓ {c.Name}: {c.Detail}");
            else if (c.Fatal) _log.Error("HwCheck", $"  ✗ {c.Name}: {c.Detail}");
            else _log.Warn("HwCheck", $"  ! {c.Name}: {c.Detail}");
        }
    }

    private static bool ModelMatches(string detected, string expected)
    {
        if (detected.Contains(expected, StringComparison.OrdinalIgnoreCase)) return true;
        // token-overlap fallback: every significant token of the expected model appears in the detected name
        var tokens = expected.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1 && !t.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase) && !t.Equals("GeForce", StringComparison.OrdinalIgnoreCase));
        return tokens.Any() && tokens.All(t => detected.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record SmiInfo(string Name, int VramGb, int PcieWidth, int PcieGen, double TempC);

    private SmiInfo? QueryNvidiaSmi()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi",
                "--query-gpu=name,memory.total,pcie.link.width.current,pcie.link.gen.current,temperature.gpu --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) return null;
            string outp = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return null; }
            var line = outp.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (line is null) return null;
            var f = line.Split(',').Select(s => s.Trim()).ToArray();
            if (f.Length < 5) return null;
            int vramMb = int.TryParse(f[1], out var mb) ? mb : 0;
            return new SmiInfo(
                Name: f[0],
                VramGb: vramMb > 0 ? (int)Math.Round(vramMb / 1024.0) : 0,
                PcieWidth: int.TryParse(f[2], out var w) ? w : 0,
                PcieGen: int.TryParse(f[3], out var g) ? g : 0,
                TempC: double.TryParse(f[4], out var t) ? t : 0);
        }
        catch (Exception ex) { _log.Trace("HwCheck", $"nvidia-smi query failed: {ex.Message}"); return null; }
    }

    private static double FreeDiskGb(string resultsRoot)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(resultsRoot));
            if (string.IsNullOrEmpty(root)) return double.MaxValue;
            return new DriveInfo(root).AvailableFreeSpace / 1_000_000_000.0;
        }
        catch { return double.MaxValue; }   // can't measure → don't block the run on it
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    private const int SM_CMONITORS = 80;

    private static int MonitorCount()
    {
        try { return GetSystemMetrics(SM_CMONITORS); } catch { return 1; }   // assume present if the API is unavailable
    }
}
