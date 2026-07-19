using LibreHardwareMonitor.Hardware;

namespace GpuSuite.Measurement;

/// <summary>
/// Sets the GPU fan to a fixed software-controlled percentage via LibreHardwareMonitor's IControl —
/// which drives NVAPI (NVIDIA) or ADL Overdrive5 (AMD) under the hood. This is what lets the cooler
/// evaluation hold the fan at each noise-normalized speed (25/30/35/40 dBA points). Cross-vendor caveats:
/// NVIDIA works broadly; AMD uses LHM's legacy Overdrive5 fan API (may not cover newer RDNA cards);
/// Intel GPUs expose no fan control through LHM. Always <see cref="ResetAuto"/> / Dispose to restore the
/// card's automatic fan curve.
/// </summary>
public sealed class GpuFanController : IDisposable
{
    private readonly Computer _computer;
    private readonly IHardware? _gpu;
    private readonly List<IControl> _fanControls = new();

    public string GpuName { get; } = "Unknown GPU";
    public string Vendor { get; } = "Unknown";
    public bool CanControl => _fanControls.Count > 0;
    public string Diagnostics { get; private set; } = "";

    public GpuFanController(string? preferGpuNameContains = null)
    {
        _computer = new Computer { IsGpuEnabled = true };
        _computer.Open();
        _gpu = SelectGpu(preferGpuNameContains, out var vendor);
        Vendor = vendor;

        if (_gpu is null) { Diagnostics = "no GPU found by LibreHardwareMonitor"; return; }
        GpuName = _gpu.Name;
        _gpu.Update();
        foreach (var s in _gpu.Sensors)
            if (s.Control is not null)
                _fanControls.Add(s.Control);

        if (_fanControls.Count == 0)
            Diagnostics = Vendor switch
            {
                "Intel" => "Intel GPUs expose no fan control through LibreHardwareMonitor (would need Intel IGCL).",
                "AMD" => "no controllable fan exposed — LHM uses legacy ADL Overdrive5, which may not cover this (RDNA?) card.",
                _ => "no controllable fan sensor exposed by this GPU."
            };
    }

    /// <summary>Force the GPU fan(s) to a fixed percentage (0..100). Returns false if uncontrollable.</summary>
    public bool SetPercent(double percent)
    {
        if (_fanControls.Count == 0) return false;
        float v = (float)Math.Clamp(percent, 0, 100);
        foreach (var c in _fanControls) c.SetSoftware(v);
        return true;
    }

    /// <summary>
    /// Applies a fixed duty and requires sensor evidence that the command took effect. Merely exposing an
    /// LHM control object is insufficient: some driver/card combinations accept SetSoftware but ignore it.
    /// The caller remains responsible for <see cref="ResetAuto"/>.
    /// </summary>
    public bool TrySetPercentVerified(double percent, TimeSpan timeout, out string evidence)
    {
        double target = Math.Clamp(percent, 0, 100);
        double? baselineDuty = FanPercent();
        double? baselineRpm = MaxFanRpm();
        if (!SetPercent(target))
        {
            evidence = "no writable fan control was exposed";
            return false;
        }

        var deadline = DateTime.UtcNow + timeout;
        double? lastDuty = baselineDuty;
        double? lastRpm = baselineRpm;
        do
        {
            Thread.Sleep(500);
            lastDuty = FanPercent();
            lastRpm = MaxFanRpm();
            if (ResponseObserved(target, baselineDuty, baselineRpm, lastDuty, lastRpm))
            {
                evidence = $"target {target:0}%, duty {Format(lastDuty)}%, RPM {Format(lastRpm)} " +
                           $"(baseline {Format(baselineDuty)}%/{Format(baselineRpm)} RPM)";
                return true;
            }
        } while (DateTime.UtcNow < deadline);

        evidence = $"target {target:0}% produced duty {Format(lastDuty)}% and {Format(lastRpm)} RPM " +
                   $"(baseline {Format(baselineDuty)}%/{Format(baselineRpm)} RPM)";
        return false;
    }

    public static bool ResponseObserved(
        double targetPercent, double? baselineDuty, double? baselineRpm,
        double? observedDuty, double? observedRpm)
    {
        bool dutyReached = observedDuty is double duty && Math.Abs(duty - targetPercent) <= 8;
        bool rpmMoved = observedRpm is double rpm && rpm >= 300 &&
                        (baselineRpm is null || rpm - baselineRpm.Value >= 150);
        bool dutyMoved = observedDuty is double changedDuty && baselineDuty is double originalDuty &&
                         Math.Abs(changedDuty - originalDuty) >= 8;
        return dutyReached || rpmMoved || dutyMoved;
    }

    private static string Format(double? value) => value?.ToString("0") ?? "—";

    /// <summary>Restore the card's automatic (BIOS/driver) fan curve.</summary>
    public void ResetAuto()
    {
        foreach (var c in _fanControls)
            try { c.SetDefault(); } catch { /* best-effort */ }
    }

    /// <summary>Highest current fan RPM across the GPU's fans (null if none reported).</summary>
    public double? MaxFanRpm()
    {
        _gpu?.Update();
        double? best = null;
        if (_gpu is not null)
            foreach (var s in _gpu.Sensors)
                if (s.SensorType == SensorType.Fan && s.Value is float v && (best is null || v > best)) best = v;
        return best;
    }

    /// <summary>Current commanded fan duty percent (the Control sensor), if reported.</summary>
    public double? FanPercent()
    {
        _gpu?.Update();
        double? best = null;
        if (_gpu is not null)
            foreach (var s in _gpu.Sensors)
                if (s.SensorType == SensorType.Control && s.Value is float v && (best is null || v > best)) best = v;
        return best;
    }

    private IHardware? SelectGpu(string? nameContains, out string vendor)
    {
        vendor = "Unknown";
        IHardware? match = null;
        foreach (var hw in _computer.Hardware)
        {
            if (hw.HardwareType is not (HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel))
                continue;
            if (!string.IsNullOrWhiteSpace(nameContains) &&
                hw.Name.Contains(nameContains!, StringComparison.OrdinalIgnoreCase))
            {
                match = hw;
                break;
            }
            match ??= hw;   // first GPU as the fallback
        }
        if (match is not null)
            vendor = match.HardwareType switch
            {
                HardwareType.GpuNvidia => "NVIDIA",
                HardwareType.GpuAmd => "AMD",
                HardwareType.GpuIntel => "Intel",
                _ => "Unknown"
            };
        return match;
    }

    public void Dispose()
    {
        try { ResetAuto(); } catch { /* ignore */ }
        try { _computer.Close(); } catch { /* ignore */ }
    }
}
