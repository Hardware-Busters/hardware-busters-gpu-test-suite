using GpuSuite.Core.Models;
using LibreHardwareMonitor.Hardware;

namespace GpuSuite.Measurement.Real;

/// <summary>
/// Real telemetry via LibreHardwareMonitor. The sensor-matching heuristics are ported
/// from the proven Powenetics V2 'LibreHardwareTelemetryService', condensed and mapped
/// to <see cref="TelemetrySample"/>. Works on this machine today (real GPU/CPU sensors).
/// </summary>
public sealed class LhmTelemetryProvider : ITelemetryProvider, IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true
    };
    private bool _open;
    private readonly object _lock = new();
    private IHardware? _selectedGpu;   // locked once so all samples come from ONE GPU

    public string Name => "LibreHardwareMonitor";

    /// <summary>When set, GPU selection pins to the first GPU whose name contains this substring
    /// (case-insensitive) — the device under test on a hybrid dGPU+iGPU system. Empty/null =>
    /// fall back to the highest-load heuristic. Must be set before the first GPU access.</summary>
    public string? PreferredGpuNameContains { get; set; }

    private void EnsureOpen()
    {
        if (_open) return;
        _computer.Open();
        _open = true;
    }

    /// <summary>Select the active GPU once (highest load, tie-broken by board power) and cache it.
    /// On a hybrid system (discrete + iGPU) this locks onto the card actually rendering, and keeps
    /// every telemetry sample consistent instead of flipping between GPUs.</summary>
    private IHardware? GetGpu()
    {
        if (_selectedGpu is not null) return _selectedGpu;
        _selectedGpu = SelectBestGpu();
        return _selectedGpu;
    }

    /// <summary>True if LHM opens and at least one GPU exposes a usable sensor.</summary>
    public bool Probe(out string detail, out string gpuName)
    {
        gpuName = "";
        try
        {
            lock (_lock)
            {
                EnsureOpen();
                var gpu = GetGpu();
                if (gpu is not null)
                {
                    gpu.Update();
                    gpuName = gpu.Name;
                    bool hasSensor = gpu.Sensors.Any(s =>
                        s.Value is not null &&
                        (s.SensorType == SensorType.Temperature || s.SensorType == SensorType.Load));
                    if (hasSensor) { detail = $"GPU '{gpu.Name}' sensors OK"; return true; }
                }
            }
            detail = "LHM opened but no usable GPU sensors (may require admin).";
            return false;
        }
        catch (Exception ex)
        {
            detail = "LHM probe failed: " + ex.Message;
            return false;
        }
    }

    public string DetectGpuName()
    {
        lock (_lock)
        {
            EnsureOpen();
            return GetGpu()?.Name ?? "Unknown GPU";
        }
    }

    public string DetectCpuName()
    {
        lock (_lock)
        {
            EnsureOpen();
            foreach (var hw in _computer.Hardware)
                if (hw.HardwareType == HardwareType.Cpu) return hw.Name;
        }
        return "Unknown CPU";
    }

    public Task<ISampleSession<TelemetrySample>> StartAsync(int intervalMs, WorkloadHint hint, CancellationToken ct)
    {
        lock (_lock) EnsureOpen();
        return Task.FromResult<ISampleSession<TelemetrySample>>(new Session(this, intervalMs));
    }

    /// <summary>
    /// Read the device-under-test GPU's board power (watts) right now — backing the LHM-based power
    /// provider (a no-extra-hardware alternative to the Powenetics PMD). Watts come ONLY from a
    /// Power-typed sensor named Package/Board/Total/TGP/TBP (NVIDIA Blackwell names total board power
    /// "GPU Package"; older NV/AMD use Board/Total/TGP/TBP). The Load-typed "GPU Board Power"/"GPU Power"
    /// sensors are PERCENTAGES of the power limit, NOT watts — reading one as watts was the bug that
    /// logged ~100 "W" at full 4K load (really ~100% of the limit), so we never fall back to a Load
    /// sensor here. Mirrors <see cref="ReadGpu"/>. Reuses the same locked Computer + pinned GPU as
    /// telemetry. Returns false when no positive Power-typed board reading is available.
    /// </summary>
    public bool TryReadGpuBoardPowerW(out double watts)
    {
        watts = 0;
        lock (_lock)
        {
            try
            {
                EnsureOpen();
                var gpu = GetGpu();
                if (gpu is null) return false;
                gpu.Update();
                double? wPower = null;
                foreach (var sensor in gpu.Sensors)
                {
                    if (sensor.Value is null || sensor.SensorType != SensorType.Power) continue;
                    var n = sensor.Name ?? ""; float v = sensor.Value.Value;
                    if (v <= 0) continue;
                    if (n.Contains("Board", StringComparison.OrdinalIgnoreCase) || n.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("Package", StringComparison.OrdinalIgnoreCase) || n.Contains("Pkg", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("TGP", StringComparison.OrdinalIgnoreCase) || n.Contains("TBP", StringComparison.OrdinalIgnoreCase))
                        wPower ??= v;
                }
                if (wPower is > 0) { watts = wPower.Value; return true; }
                return false;
            }
            catch { return false; }
        }
    }

    private TelemetrySample ReadSample(double timeSec)
    {
        var s = new TelemetrySample { TimeSec = timeSec };
        lock (_lock)
        {
            foreach (var hw in _computer.Hardware)
            {
                if (hw.HardwareType == HardwareType.Cpu) { hw.Update(); ReadCpu(hw, s); }
            }
            var gpu = GetGpu();
            if (gpu is not null) { gpu.Update(); ReadGpu(gpu, s); }
        }
        return s;
    }

    private static bool IsGpu(IHardware h) =>
        h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel;

    private IHardware? SelectBestGpu()
    {
        var gpus = _computer.Hardware.Where(IsGpu).ToList();

        // 1) Explicit device-under-test pin. On a hybrid system the load heuristic below can lock
        //    onto the idle iGPU at pre-flight, so an exact name match wins when configured.
        if (!string.IsNullOrWhiteSpace(PreferredGpuNameContains))
        {
            var pinned = gpus.FirstOrDefault(h =>
                h.Name.Contains(PreferredGpuNameContains, StringComparison.OrdinalIgnoreCase));
            if (pinned is not null) return pinned;
        }

        // 2) Fallback: highest current load (correct on single-GPU boxes, or once one card is busy).
        IHardware? best = null;
        float bestLoad = float.MinValue;
        foreach (var hw in gpus)
        {
            hw.Update();
            float load = hw.Sensors.Where(s => s.SensorType == SensorType.Load && s.Value is not null)
                                   .Select(s => s.Value!.Value).DefaultIfEmpty(0).Max();
            if (best is null || load > bestLoad) { best = hw; bestLoad = load; }
        }
        return best;
    }

    private static void ReadCpu(IHardware hw, TelemetrySample s)
    {
        // Tctl is the canonical CPU package temperature; Tdie (per-CCD) and Package are fallbacks.
        // On AMD Ryzen the SMU-derived sensors (temperature, package power, real per-core clocks) read
        // exactly 0 unless the process is ELEVATED — LHM's Ring0 driver needs admin to reach the SMU.
        // A running CPU is never at 0 C / 0 W / 0 MHz, so reject non-positive readings as "unavailable"
        // (leave null) instead of reporting a misleading 0; the OSD/report then show "--". CPU Load and
        // VID come from the OS / CPUID (no driver) and read fine without elevation.
        float? tctl = null, tdie = null, pkgTemp = null, load = null, power = null, coreAvgClk = null;
        float clkSum = 0; int clkN = 0;
        foreach (var sensor in hw.Sensors)
        {
            if (sensor.Value is null) continue;
            var n = sensor.Name ?? ""; float v = sensor.Value.Value;
            switch (sensor.SensorType)
            {
                case SensorType.Temperature:
                    if (v <= 0) break;
                    if (n.Contains("Tctl", StringComparison.OrdinalIgnoreCase)) tctl = v;          // canonical package temp
                    else if (n.Contains("Tdie", StringComparison.OrdinalIgnoreCase)) tdie ??= v;   // per-CCD fallback
                    else if (n.Contains("Package", StringComparison.OrdinalIgnoreCase)) pkgTemp ??= v;
                    break;
                case SensorType.Load:
                    if (n.Contains("Total", StringComparison.OrdinalIgnoreCase)) load = v;
                    break;
                case SensorType.Power:
                    if (v <= 0) break;
                    if (n.Contains("Package", StringComparison.OrdinalIgnoreCase) || n.Contains("PPT", StringComparison.OrdinalIgnoreCase)) power ??= v;
                    break;
                case SensorType.Clock:
                    if (v <= 0) break;
                    // Prefer the package-level "Cores (Average)" sensor; otherwise average the real
                    // per-core clocks, excluding the "Effective" variants (which read differently and
                    // would double-count alongside the nominal "Core #N" sensors).
                    if (n.Equals("Cores (Average)", StringComparison.OrdinalIgnoreCase)) coreAvgClk = v;
                    else if (n.Contains("Core", StringComparison.OrdinalIgnoreCase)
                             && !n.Contains("Bus", StringComparison.OrdinalIgnoreCase)
                             && !n.Contains("Effective", StringComparison.OrdinalIgnoreCase)
                             && !n.Contains("Average", StringComparison.OrdinalIgnoreCase)) { clkSum += v; clkN++; }
                    break;
            }
        }
        s.CpuTempC = tctl ?? tdie ?? pkgTemp;
        s.CpuLoadPct = load;
        s.CpuPowerW = power;
        s.CpuClockMhz = coreAvgClk ?? (clkN > 0 ? clkSum / clkN : null);
    }

    /// <summary>Quick health check on the CPU's SMU-derived sensors. On AMD Ryzen, CPU temperature /
    /// package power / real clocks read 0 unless the process is elevated (LHM's Ring0 driver needs
    /// admin). Returns false plus an operator hint when they look blocked, so callers can warn that a
    /// run should be elevated for CPU telemetry. CPU Load reads fine either way and is not gated here.</summary>
    public bool CpuSmuReadable(out string detail)
    {
        detail = "";
        try
        {
            lock (_lock)
            {
                EnsureOpen();
                foreach (var hw in _computer.Hardware)
                {
                    if (hw.HardwareType != HardwareType.Cpu) continue;
                    hw.Update();
                    float? tctl = null, load = null;
                    foreach (var sensor in hw.Sensors)
                    {
                        if (sensor.Value is null) continue;
                        var n = sensor.Name ?? "";
                        if (sensor.SensorType == SensorType.Temperature && n.Contains("Tctl", StringComparison.OrdinalIgnoreCase))
                            tctl = sensor.Value;
                        else if (sensor.SensorType == SensorType.Load && n.Contains("Total", StringComparison.OrdinalIgnoreCase))
                            load = sensor.Value;
                    }
                    if (tctl is > 0) { detail = $"CPU temp {tctl:0.0} C OK"; return true; }
                    detail = "CPU SMU sensors read 0 (temp/power/clock) — run ELEVATED (Administrator) for CPU temperature & package power; "
                           + $"CPU load {load ?? 0:0}% reads fine without admin.";
                    return false;
                }
            }
            detail = "no CPU hardware found";
            return false;
        }
        catch (Exception ex) { detail = "CPU sensor check failed: " + ex.Message; return false; }
    }

    private static void ReadGpu(IHardware hw, TelemetrySample s)
    {
        foreach (var sensor in hw.Sensors)
        {
            if (sensor.Value is null) continue;
            var n = sensor.Name ?? ""; float v = sensor.Value.Value;
            switch (sensor.SensorType)
            {
                case SensorType.Temperature:
                    if (n.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) || n.Contains("Hotspot", StringComparison.OrdinalIgnoreCase) || n.Contains("Junction", StringComparison.OrdinalIgnoreCase))
                        s.GpuHotspotC ??= v;
                    else if (n.Contains("Memory", StringComparison.OrdinalIgnoreCase) || n.Contains("VRAM", StringComparison.OrdinalIgnoreCase))
                        s.GpuVramTempC ??= v;
                    else if (n.Contains("Core", StringComparison.OrdinalIgnoreCase) || n.Equals("GPU", StringComparison.OrdinalIgnoreCase) || s.GpuTempC is null)
                        s.GpuTempC ??= v;
                    break;
                case SensorType.Load:
                    // NOTE: Load-typed "GPU Board Power"/"GPU Power" are PERCENTAGES of the power limit,
                    // NOT watts — board power in watts is read from the Power case below. Only Core load here.
                    if (n.Contains("Core", StringComparison.OrdinalIgnoreCase) || n.Equals("GPU Core", StringComparison.OrdinalIgnoreCase)) s.GpuLoadPct ??= v;
                    break;
                case SensorType.Clock:
                    if (n.Contains("Memory", StringComparison.OrdinalIgnoreCase)) s.GpuMemClockMhz ??= v;
                    else if (n.Contains("Core", StringComparison.OrdinalIgnoreCase)) s.GpuCoreClockMhz ??= v;
                    break;
                case SensorType.Power:
                    if (n.Contains("Board", StringComparison.OrdinalIgnoreCase) || n.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("Package", StringComparison.OrdinalIgnoreCase) || n.Contains("Pkg", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("TGP", StringComparison.OrdinalIgnoreCase) || n.Contains("TBP", StringComparison.OrdinalIgnoreCase))
                        s.GpuBoardPowerW ??= v;
                    break;
                case SensorType.Voltage:
                    if (n.Contains("Core", StringComparison.OrdinalIgnoreCase) || n.Contains("VDDC", StringComparison.OrdinalIgnoreCase) || n.Contains("GFX", StringComparison.OrdinalIgnoreCase))
                        if (v is >= 0.2f and <= 2.0f) s.GpuVoltageV ??= v;
                    break;
                case SensorType.Fan:
                    if (s.FanRpm1 is null) s.FanRpm1 = v;
                    else if (s.FanRpm2 is null) s.FanRpm2 = v;
                    else if (s.FanRpm3 is null) s.FanRpm3 = v;
                    else s.FanRpm4 ??= v;
                    break;
                case SensorType.Control:
                    s.FanPct1 ??= v;
                    break;
                case SensorType.SmallData:
                case SensorType.Data:
                    if (n.Contains("Memory Used", StringComparison.OrdinalIgnoreCase) || n.Contains("VRAM Used", StringComparison.OrdinalIgnoreCase))
                        s.VramUsedMb ??= v; // LHM reports GB for some; left as-is, documented
                    break;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock) { if (_open) { try { _computer.Close(); } catch { } _open = false; } }
    }

    private sealed class Session : ISampleSession<TelemetrySample>
    {
        private readonly LhmTelemetryProvider _p;
        private readonly List<TelemetrySample> _samples = new();
        private readonly object _lock = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _task;
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

        public Session(LhmTelemetryProvider p, int intervalMs)
        {
            _p = p;
            _task = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var sample = _p.ReadSample(_sw.Elapsed.TotalSeconds);
                        lock (_lock) _samples.Add(sample);
                        await Task.Delay(Math.Max(50, intervalMs), _cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        public DataSourceMode Mode => DataSourceMode.Live;
        public int SampleCount { get { lock (_lock) return _samples.Count; } }
        public TelemetrySample? Latest { get { lock (_lock) return _samples.Count > 0 ? _samples[^1] : null; } }

        public async Task<IReadOnlyList<TelemetrySample>> StopAsync()
        {
            _cts.Cancel();
            try { await _task.ConfigureAwait(false); } catch (OperationCanceledException) { }
            lock (_lock) return _samples.ToList();
        }

        public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); _cts.Dispose(); }
    }
}
