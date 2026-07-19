using GpuSuite.Core.Models;
using GpuSuite.Measurement;

namespace GpuSuite.Load;

/// <summary>One noise-normalized fan setting: the target dBA and the fan duty (%) the operator calibrated to it.</summary>
public sealed record CoolerNoiseSetting(double TargetDba, double FanPercent);

/// <summary>Shared parser for the "25:30,30:38,…" (dBA:fan%) noise-level spec used by the CLI and the UI.</summary>
public static class CoolerLevels
{
    public static List<CoolerNoiseSetting> Parse(string? spec)
    {
        var list = new List<CoolerNoiseSetting>();
        if (string.IsNullOrWhiteSpace(spec)) return list;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (kv.Length == 2 &&
                double.TryParse(kv[0], System.Globalization.NumberStyles.Float, inv, out var dba) &&
                double.TryParse(kv[1], System.Globalization.NumberStyles.Float, inv, out var pct))
                list.Add(new CoolerNoiseSetting(dba, Math.Clamp(pct, 0, 100)));
        }
        return list;
    }
}

/// <summary>Parameters for a cooler sweep (the power axis, the noise levels, and the soak/settle policy).</summary>
public sealed class CoolerSweepOptions
{
    public double FromW { get; init; } = 80;
    public double ToW { get; init; } = 250;
    public double StepW { get; init; } = 25;
    /// <summary>Reference heat load for the single-point summary (typically the card's reference TDP).</summary>
    public double ReferencePowerW { get; init; } = 250;
    /// <summary>The noise-normalized fan settings to sweep (e.g. 25/30/35/40 dBA → calibrated fan %).</summary>
    public IReadOnlyList<CoolerNoiseSetting> NoiseLevels { get; init; } = Array.Empty<CoolerNoiseSetting>();

    /// <summary>Minimum soak before a step may be declared settled (lets power converge + thermal mass respond).</summary>
    public TimeSpan MinSoak { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Hard cap on soak per step (declared not-settled if the slope never falls below threshold).</summary>
    public TimeSpan MaxSoak { get; init; } = TimeSpan.FromSeconds(180);
    /// <summary>Window over which the GPU-temperature slope is measured for the settle test, and over which the
    /// steady values (power/temps) are averaged.</summary>
    public TimeSpan SettleWindow { get; init; } = TimeSpan.FromSeconds(45);
    /// <summary>Settle threshold — when |dGpuTemp/dt| falls below this (°C per minute) the step is settled.</summary>
    public double SettleSlopeCPerMin { get; init; } = 0.3;

    public string FanNote { get; init; } = "";
    public double AmbientC { get; init; }
    /// <summary>Independent unattended safety ceilings. The load is stopped and the automatic fan curve is
    /// restored as soon as any available sensor reaches its ceiling. Set a value to zero only for a supervised
    /// specialist run where the device firmware is intentionally the sole thermal guard.</summary>
    public double MaxGpuTempC { get; init; } = 88;
    public double MaxHotspotTempC { get; init; } = 105;
    public double MaxMemoryTempC { get; init; } = 100;
    /// <summary>Maximum time a sweep may continue without fresh temperature telemetry.</summary>
    public TimeSpan ThermalTelemetryGrace { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Settle is also gated on power being within this band of target (so an early thermal plateau while
    /// the controller is still converging power does not end the step prematurely). Watts.</summary>
    public double PowerSettleBandW { get; init; } = 15;
}

/// <summary>Pure thermal interlock used by the cooler runner and its offline regression tests.</summary>
public static class CoolerThermalSafety
{
    public static string? FindViolation(CoolerSweepOptions opt, TelemetrySample? telemetry)
    {
        if (telemetry is null) return null;
        if (opt.MaxGpuTempC > 0 && telemetry.GpuTempC is double gpu && gpu >= opt.MaxGpuTempC)
            return $"GPU core {gpu:0.0}°C reached the {opt.MaxGpuTempC:0.0}°C safety ceiling";
        if (opt.MaxHotspotTempC > 0 && telemetry.GpuHotspotC is double hot && hot >= opt.MaxHotspotTempC)
            return $"GPU hotspot {hot:0.0}°C reached the {opt.MaxHotspotTempC:0.0}°C safety ceiling";
        if (opt.MaxMemoryTempC > 0 && telemetry.GpuVramTempC is double memory && memory >= opt.MaxMemoryTempC)
            return $"GPU memory {memory:0.0}°C reached the {opt.MaxMemoryTempC:0.0}°C safety ceiling";
        return null;
    }
}

/// <summary>
/// Drives the full noise- and power-normalized cooler sweep: for each noise-normalized fan speed it sets the
/// fan, then steps the heat load across the power axis, converging each step with the closed-loop
/// <see cref="PowerController"/> and soaking until the on-die GPU temperature stops climbing — recording the
/// steady temperatures into a <see cref="CoolerSweepResult"/>. Fan setting is programmatic where the card
/// allows it (<see cref="GpuFanController"/>), otherwise the operator pre-sets the fan and this just records
/// the resulting RPM. The load + power feedback are supplied by the caller so the bench (Powenetics) and the
/// dev box (LHM) share one code path.
/// </summary>
public sealed class CoolerTestRunner
{
    private readonly IGpuLoad _load;
    private readonly PowerController _controller;
    private readonly Func<double?> _readWatts;
    private readonly Func<TelemetrySample?> _readTelemetry;
    private readonly GpuFanController? _fan;
    private readonly Action<string>? _log;

    public CoolerTestRunner(
        IGpuLoad load,
        Func<double?> readWatts,
        Func<TelemetrySample?> readTelemetry,
        GpuFanController? fan = null,
        Action<string>? log = null)
    {
        _load = load;
        _readWatts = readWatts;
        _readTelemetry = readTelemetry;
        _fan = fan;
        _log = log;
        _controller = new PowerController(load, readWatts);
    }

    /// <summary>One captured soak observation (the scalar fields we average over the steady window).</summary>
    private readonly record struct SoakSample(double T, double? W, double? Gpu, double? Hot, double? Mem, double? Rpm, double? Clk);

    /// <summary>
    /// Run the sweep. <paramref name="onTick"/> receives the live controller ticks (for a console readout);
    /// returns whatever was measured even if cancelled partway. Restores the automatic fan curve on exit.
    /// </summary>
    public async Task<CoolerSweepResult> RunAsync(CoolerSweepOptions opt, Action<PowerTick>? onTick, CancellationToken ct)
    {
        var result = new CoolerSweepResult
        {
            GpuName = _load.GpuName,
            Vendor = _fan?.Vendor ?? "Unknown",
            ReferencePowerW = opt.ReferencePowerW,
            ClockLockMhz = (_load as ComputeSharpGpuLoad)?.LockedClockMhz,
            FanAutoControlled = _fan?.CanControl == true,
            FanNote = opt.FanNote,
            AmbientC = opt.AmbientC,
        };

        var targets = CoolerMath.PowerAxis(opt.FromW, opt.ToW, opt.StepW);
        bool firstNoiseLevel = true;
        try
        {
            foreach (var noise in opt.NoiseLevels)
            {
                if (ct.IsCancellationRequested) break;

                var level = new CoolerNoiseLevel { TargetDba = noise.TargetDba, FanPercent = noise.FanPercent };

                if (_fan?.CanControl == true)
                {
                    _fan.SetPercent(noise.FanPercent);
                    _log?.Invoke($"\n== {noise.TargetDba:0} dBA → fan {noise.FanPercent:0}% (auto-set) ==");
                    // brief spin-up so RPM reflects the new duty before the first step
                    await SafeDelay(TimeSpan.FromSeconds(4), ct);
                }
                else
                {
                    _log?.Invoke($"\n== {noise.TargetDba:0} dBA → fan {noise.FanPercent:0}% (MANUAL — operator must hold this) ==");
                }

                // Every curve finishes at the hottest/highest-power point. Without an unrecorded
                // low-power conditioning soak, the next curve's first 80 W datum mostly measures the
                // previous curve cooling down and can hit MAX-SOAK despite a stable load. Bring the
                // heatsink back to equilibrium at the new fan speed, discard that transition, and only
                // then begin recording the new curve.
                if (!firstNoiseLevel && targets.Count > 0 && !ct.IsCancellationRequested)
                {
                    _log?.Invoke($"   Conditioning at {targets[0]:0}W before recording this fan curve...");
                    var conditioning = await SoakStepAsync(targets[0], opt, onTick, ct);
                    _log?.Invoke($"   Conditioning complete in {conditioning.SoakSeconds:0}s " +
                        $"({(conditioning.Settled ? "settled" : "max soak")}); starting recorded points.");
                }
                firstNoiseLevel = false;

                foreach (var targetW in targets)
                {
                    if (ct.IsCancellationRequested) break;
                    var step = await SoakStepAsync(targetW, opt, onTick, ct);
                    level.Steps.Add(step);
                    if (step.FanRpm is double rpm) level.FanRpm = rpm;
                    _log?.Invoke(
                        $"   {targetW,4:0}W → {(step.AchievedW?.ToString("0") ?? "—"),4}W  " +
                        $"gpu {(step.GpuTempC?.ToString("0.0") ?? "—")}C  mem {(step.MemTempC?.ToString("0") ?? "—")}C  " +
                        $"fan {(step.FanRpm?.ToString("0") ?? "—")}rpm  {(step.Settled ? "settled" : "MAX-SOAK")} in {step.SoakSeconds:0}s");
                }

                // single-point summary at the reference power
                level.RefGpuTempC = InterpolateStepField(level.Steps, opt.ReferencePowerW, s => s.GpuTempC);
                level.RefMemTempC = InterpolateStepField(level.Steps, opt.ReferencePowerW, s => s.MemTempC);
                result.Levels.Add(level);
            }
        }
        finally
        {
            try { _fan?.ResetAuto(); } catch { /* best-effort restore of the auto fan curve */ }
        }

        return result;
    }

    /// <summary>Drive to <paramref name="targetW"/> and soak until the GPU-temp slope settles (or max-soak).</summary>
    private async Task<CoolerStep> SoakStepAsync(double targetW, CoolerSweepOptions opt, Action<PowerTick>? onTick, CancellationToken ct)
    {
        var samples = new List<SoakSample>();
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bool settled = false;
        double? lastTelemetryTime = null;
        double lastControllerTime = 0;
        double missingThermalSeconds = 0;

        void OnControllerTick(PowerTick t)
        {
            var tel = _readTelemetry();
            double elapsed = Math.Max(0, t.ElapsedSec - lastControllerTime);
            lastControllerTime = t.ElapsedSec;
            bool hasTemperature = tel?.GpuTempC is not null || tel?.GpuHotspotC is not null || tel?.GpuVramTempC is not null;
            bool telemetryAdvanced = tel is not null &&
                (lastTelemetryTime is null || tel.TimeSec > lastTelemetryTime.Value + 0.0001);
            if (tel is not null) lastTelemetryTime = tel.TimeSec;
            missingThermalSeconds = hasTemperature && telemetryAdvanced ? 0 : missingThermalSeconds + elapsed;
            if (opt.ThermalTelemetryGrace > TimeSpan.Zero &&
                missingThermalSeconds >= opt.ThermalTelemetryGrace.TotalSeconds)
                throw new InvalidOperationException(
                    $"Cooler thermal safety abort: temperature telemetry was missing or stale for {missingThermalSeconds:0}s.");
            if (CoolerThermalSafety.FindViolation(opt, tel) is { } violation)
                throw new InvalidOperationException("Cooler thermal safety abort: " + violation + ".");
            samples.Add(new SoakSample(
                t.ElapsedSec, t.Watts,
                tel?.GpuTempC, tel?.GpuHotspotC, tel?.GpuVramTempC,
                tel?.MaxFanRpm ?? tel?.FanRpm1, tel?.GpuCoreClockMhz));

            onTick?.Invoke(t);

            // Settle gate: past the minimum soak, power near target, and the temp slope is flat.
            if (!settled && t.ElapsedSec >= opt.MinSoak.TotalSeconds)
            {
                var window = SamplesInWindow(samples, opt.SettleWindow.TotalSeconds);
                double? recentW = MeanOf(window, s => s.W);
                bool powerReady = recentW is null || Math.Abs(recentW.Value - targetW) <= opt.PowerSettleBandW;
                double? slope = CoolerMath.SlopePerMinute(
                    window.Where(s => s.Gpu is not null).Select(s => (s.T, s.Gpu!.Value)).ToList());
                if (powerReady && slope is double sl && Math.Abs(sl) <= opt.SettleSlopeCPerMin
                    && WindowSpanSeconds(window) >= opt.SettleWindow.TotalSeconds * 0.7)
                {
                    settled = true;
                    stepCts.Cancel();   // end this step early; the outer ct still aborts the whole run
                }
            }
        }

        await _controller.RunAsync(targetW, opt.MaxSoak, OnControllerTick, stepCts.Token);

        // Steady values = averages over the trailing settle window.
        var steady = SamplesInWindow(samples, opt.SettleWindow.TotalSeconds);
        double endT = samples.Count > 0 ? samples[^1].T : 0;
        return new CoolerStep
        {
            TargetW = targetW,
            AchievedW = MeanOf(steady, s => s.W),
            GpuTempC = MeanOf(steady, s => s.Gpu),
            GpuHotspotC = MeanOf(steady, s => s.Hot),
            MemTempC = MeanOf(steady, s => s.Mem),
            FanRpm = MeanOf(steady, s => s.Rpm),
            GpuClockMhz = MeanOf(steady, s => s.Clk),
            SoakSeconds = endT,
            // Settled only if WE ended it early; if the user aborted, ct is cancelled and it isn't a real settle.
            Settled = settled && !ct.IsCancellationRequested,
        };
    }

    private static List<SoakSample> SamplesInWindow(List<SoakSample> all, double windowSec)
    {
        if (all.Count == 0) return all;
        double end = all[^1].T;
        return all.Where(s => s.T >= end - windowSec).ToList();
    }

    private static double WindowSpanSeconds(List<SoakSample> w) => w.Count < 2 ? 0 : w[^1].T - w[0].T;

    private static double? MeanOf(List<SoakSample> w, Func<SoakSample, double?> sel)
    {
        double sum = 0; int n = 0;
        foreach (var s in w) if (sel(s) is double v) { sum += v; n++; }
        return n > 0 ? sum / n : null;
    }

    /// <summary>Interpolate a step field at <paramref name="x"/> watts along the power axis (X = achieved or
    /// target watts), skipping steps where the field is null. Delegates to <see cref="CoolerMath.InterpolateAt"/>.</summary>
    private static double? InterpolateStepField(List<CoolerStep> steps, double x, Func<CoolerStep, double?> sel)
    {
        var pts = steps
            .Where(s => sel(s) is not null)
            .Select(s => (s.AchievedW ?? s.TargetW, sel(s)!.Value))
            .ToList();
        return CoolerMath.InterpolateAt(pts, x);
    }

    private static async Task SafeDelay(TimeSpan d, CancellationToken ct)
    {
        try { await Task.Delay(d, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
    }
}
