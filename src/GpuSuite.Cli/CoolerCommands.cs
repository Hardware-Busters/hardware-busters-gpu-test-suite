// Extracted from Program.cs (CLI god-file split): same partial Program class, grouped by command area.
// Behavior-preserving move only — see HANDOVER.md before editing bench-path logic.
using GpuSuite.Core.Config;
using GpuSuite.Core;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Io;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Aggregation;
using GpuSuite.Engine.Discovery;
using GpuSuite.Engine.Display;
using GpuSuite.Engine.Orchestration;
using GpuSuite.Engine.Profiles;
using GpuSuite.Engine.Scenes;
using GpuSuite.Engine.Validation;
using GpuSuite.Load;
using GpuSuite.Measurement;
using GpuSuite.Reporting;

namespace GpuSuite.Cli;

internal static partial class Program
{
    /// <summary>
    /// Cooler-eval load selftest (the linchpin of the GPU heatsink evaluation): start the controllable
    /// GPU compute load and CLOSED-LOOP it to a target wattage using the live power provider — Powenetics
    /// on the bench, LHM GPU board power on a dev box — printing achieved W + GPU temp/util/clock. `--max`
    /// ramps to full load to find the card's sustainable power ceiling. Proves we can dial an exact heat load.
    /// </summary>
    /// <summary>
    /// Input lock for the long unattended cooler paths — same semantics as `run` (physical kb+mouse
    /// swallowed, physical ESC = graceful cancel, ESC×3 force-unlocks, --no-input-lock opts out). No
    /// heartbeat hang-release here: a cooler soak presents no frames, so a stale-beacon check would
    /// false-trip; process death still frees the hooks. Null when opted out or hooks fail (never fatal).
    /// </summary>
    private static PhysicalInputGuard? ArmCoolerInputLock(ArgMap a, CancellationTokenSource cts)
    {
        if (a.Has("--no-input-lock")) return null;
        var guard = PhysicalInputGuard.Arm(
            onAbort: () => { cts.Cancel(); Console.WriteLine("  [InputLock] ESC — cancelling."); },
            log: m => Console.WriteLine("  [InputLock] " + m),
            isHung: null);
        if (!guard.IsArmed) { guard.Dispose(); return null; }
        Console.WriteLine("  Input lock     : ARMED — physical kb+mouse blocked; ESC aborts, ESC×3 force-unlocks (--no-input-lock to disable)");
        return guard;
    }

    private static async Task<int> CoolerLoad(SuiteConfig cfg, ArgMap a)
    {
        using var factory = new MeasurementFactory(cfg);
        Console.WriteLine("Probing power / telemetry...");
        foreach (var line in factory.ProbeAll()) Console.WriteLine("  " + line);

        var power = factory.SelectPowerProvider();
        var telemetry = factory.SelectTelemetryProvider();
        Console.WriteLine($"\n  GPU under test : {factory.DetectedGpuName}");
        Console.WriteLine($"  Power source   : {power.Name} (live={power.IsLive})");
        if (!power.IsLive)
            Console.WriteLine("  ! Power is SYNTHETIC — closed-loop targeting needs a LIVE source (Powenetics or LHM board power).");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        using var inputGuard = ArmCoolerInputLock(a, cts);

        await using var powerSession = await power.StartAsync(cfg.PowerLogIntervalMs, new WorkloadHint(), cts.Token);
        await using var telSession = await telemetry.StartAsync(Math.Max(100, cfg.TelemetryIntervalMs), new WorkloadHint(), cts.Token);

        int? lockClock = a.GetInt("--lock-clock");
        ComputeSharpGpuLoad load;
        try
        {
            load = new ComputeSharpGpuLoad(string.IsNullOrWhiteSpace(cfg.PreferredGpu) ? null : cfg.PreferredGpu, lockClock);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\nFailed to create the GPU load (no D3D12 device?): {ex.Message}");
            return 1;
        }

        Console.WriteLine($"  Load device    : {load.GpuName}");
        Console.WriteLine($"  Load kernel    : {load.LoadDescription}");
        load.Start();
        if (lockClock is int lc)
            Console.WriteLine(load.ClockLocked
                ? $"  Clock lock     : LOCKED at {lc} MHz (boost hysteresis removed)"
                : $"  Clock lock     : FAILED ({lc} MHz) — needs Administrator + NVIDIA. {load.ClockLockDetail}");

        Func<double?> readWatts = () => powerSession.Latest?.GpuTotalW ?? telSession.Latest?.GpuBoardPowerW;
        var controller = new PowerController(load, readWatts);

        void PrintTick(PowerTick t)
        {
            var tel = telSession.Latest;
            Console.WriteLine($"  t={t.ElapsedSec,5:0.0}s  target={t.TargetW,4:0}W  power={(t.Watts?.ToString("0") ?? "—"),4}W  " +
                $"intensity={t.Intensity,4:0.00}  gpu={(tel?.GpuTempC?.ToString("0") ?? "—")}C  " +
                $"util={(tel?.GpuLoadPct?.ToString("0") ?? "—")}%  clk={(tel?.GpuCoreClockMhz?.ToString("0") ?? "—")}MHz");
        }

        int seconds = a.GetInt("--seconds") ?? 30;
        int rc = 0;
        try
        {
            if (a.Has("--max"))
            {
                Console.WriteLine($"\nRamping to MAX for {seconds}s (intensity = 100%)...\n");
                double peak = await controller.FindMaxAsync(TimeSpan.FromSeconds(seconds), PrintTick, cts.Token);
                Console.WriteLine($"\nRESULT: max sustained GPU power ≈ {peak:0} W.");
            }
            else
            {
                double watts = a.GetInt("--watts") ?? 150;
                Console.WriteLine($"\nDriving to {watts:0} W for {seconds}s (Ctrl+C to stop)...\n");
                var ticks = await controller.RunAsync(watts, TimeSpan.FromSeconds(seconds), PrintTick, cts.Token);
                var tail = ticks.Where(t => t.ElapsedSec >= seconds - 5 && t.Watts is > 0).Select(t => t.Watts!.Value).ToList();
                if (tail.Count > 0)
                {
                    double mean = tail.Average();
                    Console.WriteLine($"\nRESULT: target {watts:0} W → held {mean:0} W (last-5s mean, error {mean - watts:+0;-0} W).");
                }
                else
                {
                    Console.WriteLine("\nRESULT: no live power samples captured — is the power source live?");
                    rc = 1;
                }
            }
        }
        finally
        {
            load.Stop();
            load.Dispose();
        }
        return rc;
    }

    /// <summary>
    /// Phase B of the cooler evaluation: the full noise- and power-normalized sweep. For each calibrated
    /// fan speed (the operator's 25/30/35/40 dBA @ 1 m points) it holds the fan, then steps the heat load
    /// across the power axis, converging each step with the closed-loop controller and soaking until the
    /// on-die GPU temperature settles — producing a family of temp-vs-power curves + a reference-power
    /// summary, saved as a CoolerSweepResult JSON. Fan is set programmatically (GpuFanController) unless
    /// --manual-fan, in which case the operator pre-locks each fan speed and this records the RPM.
    /// </summary>
    private static async Task<int> Cooler(SuiteConfig cfg, ArgMap a)
    {
        // --- noise levels (required): "25:30,30:38,35:46,40:55" = dBA:fan% pairs (from the operator's calibration)
        var levels = ParseNoiseLevels(a.Get("--levels"));
        if (levels.Count == 0)
        {
            Console.Error.WriteLine(
                "cooler: supply --levels \"dBA:fan%,...\" (your calibrated fan settings), e.g.\n" +
                "  --levels \"25:30,30:38,35:46,40:55\"   (find each fan% with your meter once, per card)\n" +
                "Add --manual-fan if you will hold each fan speed yourself instead of letting the suite set it.");
            return 2;
        }

        double fromW = a.GetDouble("--from") ?? 80;
        double toW = a.GetDouble("--to") ?? 250;
        double stepW = a.GetDouble("--step") ?? 25;
        double refPower = a.GetDouble("--ref-power") ?? toW;
        int? lockClock = a.GetInt("--lock-clock");
        bool manualFan = a.Has("--manual-fan");
        string? pref = a.Get("--gpu") ?? (string.IsNullOrWhiteSpace(cfg.PreferredGpu) ? null : cfg.PreferredGpu);

        using var factory = new MeasurementFactory(cfg);
        Console.WriteLine("Probing power / telemetry...");
        foreach (var line in factory.ProbeAll()) Console.WriteLine("  " + line);

        var power = factory.SelectPowerProvider();
        var telemetry = factory.SelectTelemetryProvider();
        Console.WriteLine($"\n  GPU under test : {factory.DetectedGpuName}");
        Console.WriteLine($"  Power source   : {power.Name} (live={power.IsLive})");
        if (!power.IsLive)
            Console.WriteLine("  ! Power is SYNTHETIC — the heat-load targeting is only meaningful with a LIVE source (Powenetics/LHM).");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        using var inputGuard = ArmCoolerInputLock(a, cts);

        await using var powerSession = await power.StartAsync(cfg.PowerLogIntervalMs, new WorkloadHint(), cts.Token);
        await using var telSession = await telemetry.StartAsync(Math.Max(100, cfg.TelemetryIntervalMs), new WorkloadHint(), cts.Token);

        ComputeSharpGpuLoad load;
        try { load = new ComputeSharpGpuLoad(pref, lockClock); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\nFailed to create the GPU load (no D3D12 device?): {ex.Message}");
            return 1;
        }
        Console.WriteLine($"  Load device    : {load.GpuName}");

        GpuFanController? fan = null;
        if (!manualFan)
        {
            fan = new GpuFanController(pref);
            Console.WriteLine($"  Fan control    : {(fan.CanControl ? $"AVAILABLE ({fan.Vendor})" : "NOT available — " + fan.Diagnostics)}");
            if (!fan.CanControl)
            {
                Console.Error.WriteLine("  → automatic sweep refused. Re-run with --manual-fan only when an operator will hold every calibrated fan speed.");
                fan.Dispose();
                return 1;
            }
            Console.WriteLine("  Verifying fan response at 60%...");
            bool fanVerified = fan.TrySetPercentVerified(60, TimeSpan.FromSeconds(8), out string fanEvidence);
            fan.ResetAuto();
            if (!fanVerified)
            {
                Console.Error.WriteLine("  → automatic sweep refused: command was ignored — " + fanEvidence);
                fan.Dispose();
                return 1;
            }
            Console.WriteLine("  Fan verified   : " + fanEvidence);
        }
        else Console.WriteLine("  Fan control    : MANUAL (operator holds each calibrated fan speed).");

        load.Start();
        if (lockClock is int lc)
            Console.WriteLine(load.ClockLocked
                ? $"  Clock lock     : LOCKED at {lc} MHz"
                : $"  Clock lock     : FAILED ({lc} MHz) — needs Administrator + NVIDIA. {load.ClockLockDetail}");

        var opt = new CoolerSweepOptions
        {
            FromW = fromW,
            ToW = toW,
            StepW = stepW,
            ReferencePowerW = refPower,
            NoiseLevels = levels,
            MinSoak = TimeSpan.FromSeconds(a.GetDouble("--soak-min") ?? 30),
            MaxSoak = TimeSpan.FromSeconds(a.GetDouble("--soak-max") ?? 180),
            SettleWindow = TimeSpan.FromSeconds(a.GetDouble("--settle-window") ?? 45),
            SettleSlopeCPerMin = a.GetDouble("--settle-slope") ?? 0.3,
            FanNote = a.Get("--fan-note") ?? "",
            AmbientC = a.GetDouble("--ambient") ?? 0,
        };

        Console.WriteLine(
            $"\nSweep: {opt.FromW:0}→{opt.ToW:0} W step {opt.StepW:0}, ref {opt.ReferencePowerW:0} W, " +
            $"{levels.Count} noise level(s), soak {opt.MinSoak.TotalSeconds:0}-{opt.MaxSoak.TotalSeconds:0}s/step. Ctrl+C to stop.\n");

        Func<double?> readWatts = () => powerSession.Latest?.GpuTotalW ?? telSession.Latest?.GpuBoardPowerW;
        var runner = new CoolerTestRunner(load, readWatts, () => telSession.Latest, fan, Console.WriteLine);

        // throttled live readout (~ every 8 s) so a long soak shows progress without spamming
        double lastPrint = -100;
        void OnTick(PowerTick t)
        {
            if (t.ElapsedSec < lastPrint) lastPrint = -100;   // step elapsed reset → print its first tick
            if (t.ElapsedSec - lastPrint < 8) return;
            lastPrint = t.ElapsedSec;
            var tel = telSession.Latest;
            Console.WriteLine(
                $"      t={t.ElapsedSec,5:0}s  target={t.TargetW,4:0}W  power={(t.Watts?.ToString("0") ?? "—"),4}W  " +
                $"gpu={(tel?.GpuTempC?.ToString("0") ?? "—")}C  fan={(tel?.MaxFanRpm?.ToString("0") ?? "—")}rpm");
        }

        CoolerSweepResult result;
        try
        {
            result = await runner.RunAsync(opt, OnTick, cts.Token);
        }
        finally
        {
            load.Stop();
            load.Dispose();
            fan?.Dispose();
        }

        result.PowerSource = power.Name;
        if (string.IsNullOrWhiteSpace(result.Vendor) || result.Vendor == "Unknown")
            result.Vendor = InferVendor(load.GpuName);

        string outPath = a.Get("--out") ?? Path.Combine(cfg.ResultsRoot,
            $"cooler_{Sanitize(load.GpuName)}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        Json.Save(outPath, result);
        string htmlPath = Path.ChangeExtension(outPath, ".html");
        new CoolerReportGenerator().Save(htmlPath, result);

        Console.WriteLine("\n— Cooler sweep summary —");
        Console.WriteLine($"  {"dBA",4}  {"fan%",5}  {"rpm",6}  {"steps",6}   GPU@{refPower:0}W   Mem@{refPower:0}W");
        foreach (var lvl in result.Levels)
            Console.WriteLine(
                $"  {lvl.TargetDba,4:0}  {lvl.FanPercent,5:0}  {(lvl.FanRpm?.ToString("0") ?? "—"),6}  {lvl.Steps.Count,6}   " +
                $"{(lvl.RefGpuTempC?.ToString("0.0") ?? "—"),7}C   {(lvl.RefMemTempC?.ToString("0") ?? "—"),7}C");
        Console.WriteLine($"\nSaved cooler result → {outPath}");
        Console.WriteLine($"Saved cooler report → {htmlPath}");
        if (a.Has("--open")) TryOpen(htmlPath);
        if (cts.IsCancellationRequested) Console.WriteLine("(cancelled — result holds the steps completed before stop.)");
        return 0;
    }

    /// <summary>Render a saved CoolerSweepResult JSON to a self-contained HTML cooler report (offline).</summary>
    private static int CoolerReport(SuiteConfig cfg, ArgMap a)
    {
        string? inPath = a.Get("--in");
        if (string.IsNullOrWhiteSpace(inPath))
        {
            // default to the newest cooler_*.json in the results root
            var dir = cfg.ResultsRoot;
            inPath = Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "cooler_*.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
        }
        if (string.IsNullOrWhiteSpace(inPath) || !File.Exists(inPath))
        {
            Console.Error.WriteLine("cooler-report: pass --in <CoolerSweepResult.json> (or run `cooler` first to produce one).");
            return 2;
        }

        var result = Json.Load<CoolerSweepResult>(inPath);
        if (result is null) { Console.Error.WriteLine($"cooler-report: could not parse {inPath}."); return 1; }

        string outPath = a.Get("--out") ?? Path.ChangeExtension(inPath, ".html");
        new CoolerReportGenerator().Save(outPath, result);
        Console.WriteLine($"Rendered cooler report → {outPath}  ({result.Levels.Count} noise level(s), {result.Levels.Sum(l => l.Steps.Count)} steps)");
        if (a.Has("--open")) TryOpen(outPath);
        return 0;
    }

    /// <summary>Parse "25:30,30:38,..." (dBA:fan%) into noise settings; skips malformed/empty entries.</summary>
    private static List<CoolerNoiseSetting> ParseNoiseLevels(string? spec) => CoolerLevels.Parse(spec);

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Replace(' ', '_');
    }

    /// <summary>Best-effort GPU vendor from the device name (used when no fan controller reported it).</summary>
    private static string InferVendor(string gpuName)
    {
        string n = gpuName.ToLowerInvariant();
        if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("rtx") || n.Contains("gtx")) return "NVIDIA";
        if (n.Contains("radeon") || n.Contains("amd") || n.Contains("rx ")) return "AMD";
        if (n.Contains("intel") || n.Contains("arc")) return "Intel";
        return "Unknown";
    }

    /// <summary>
    /// GPU fan-control selftest (for the cooler eval's noise normalization): set the GPU fan to a fixed
    /// percentage via LibreHardwareMonitor (NVAPI on NVIDIA, ADL on AMD), watch the RPM change, then
    /// restore the automatic curve. Confirms we can hold the fan at each 25/30/35/40 dBA point.
    /// </summary>
    private static int FanTest(SuiteConfig cfg, ArgMap a)
    {
        var pref = a.Get("--gpu") ?? (string.IsNullOrWhiteSpace(cfg.PreferredGpu) ? null : cfg.PreferredGpu);
        using var fan = new GpuFanController(pref);
        Console.WriteLine($"GPU            : {fan.GpuName} ({fan.Vendor})");
        Console.WriteLine($"Fan control    : {(fan.CanControl ? "AVAILABLE" : "NOT available — " + fan.Diagnostics)}");
        if (!fan.CanControl) return 1;

        int pct = a.GetInt("--pct") ?? 60;
        Console.WriteLine($"\nBaseline       : {fan.FanPercent()?.ToString("0") ?? "—"}%  /  {fan.MaxFanRpm()?.ToString("0") ?? "—"} RPM");
        Console.WriteLine($"Setting fan to {pct}% (forcing software control)...\n");
        bool verified = fan.TrySetPercentVerified(pct, TimeSpan.FromSeconds(9), out string evidence);
        Console.WriteLine("  " + evidence);

        Console.WriteLine("\nRestoring automatic fan curve...");
        fan.ResetAuto();
        System.Threading.Thread.Sleep(2000);
        Console.WriteLine($"After reset    : {fan.FanPercent()?.ToString("0") ?? "—"}%  /  {fan.MaxFanRpm()?.ToString("0") ?? "—"} RPM");
        Console.WriteLine(verified
            ? "\nRESULT: PASS — a real duty/RPM response was observed and the automatic curve was restored."
            : "\nRESULT: FAIL — the control object accepted the command but no duty/RPM response was observed; auto-fan cooler sweeps are blocked.");
        return verified ? 0 : 1;
    }
}
