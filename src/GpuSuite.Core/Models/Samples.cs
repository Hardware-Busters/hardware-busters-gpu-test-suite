namespace GpuSuite.Core.Models;

/// <summary>
/// One presented frame, mirroring the meaningful columns of a PresentMon CSV row.
/// All times are in milliseconds; <see cref="TimeSec"/> is relative to capture start.
/// </summary>
public sealed class FrameSample
{
    /// <summary>Seconds since capture start (PresentMon "TimeInSeconds").</summary>
    public double TimeSec { get; set; }
    /// <summary>Wall time between this present and the previous (PresentMon "msBetweenPresents"/"FrameTime").</summary>
    public double FrameTimeMs { get; set; }
    /// <summary>Time until the frame was displayed, if available (PresentMon "msBetweenDisplayChange"/"msUntilDisplayed").</summary>
    public double? DisplayedTimeMs { get; set; }
    /// <summary>GPU busy time for the frame, if available (PresentMon "msGPUActive"/"GPUBusy").</summary>
    public double? GpuBusyMs { get; set; }
    /// <summary>Instantaneous FPS = 1000 / FrameTimeMs.</summary>
    public double Fps => FrameTimeMs > 0 ? 1000.0 / FrameTimeMs : 0;

    public FrameSample() { }
    public FrameSample(double timeSec, double frameTimeMs)
    {
        TimeSec = timeSec;
        FrameTimeMs = frameTimeMs;
    }
}

/// <summary>
/// One Powenetics V2 power sample. Rails mirror the proven logger's rail set.
/// Values are watts unless the field name says otherwise (V = volts, A = amps).
/// </summary>
public sealed class PowerSample
{
    public double TimeSec { get; set; }

    // GPU rails
    public double? PcieSlot12vW { get; set; }
    public double? PcieSlot3v3W { get; set; }
    public double? Pcie8pin1W { get; set; }
    public double? Pcie8pin2W { get; set; }
    public double? Pcie8pin3W { get; set; }
    /// <summary>Sum of all GPU-supplying rails (slot + connectors) — the real board power.</summary>
    public double? GpuTotalW { get; set; }

    // CPU / system rails
    public double? Eps1W { get; set; }
    public double? Eps2W { get; set; }
    public double? CpuTotalW { get; set; }
    public double? Atx12vW { get; set; }
    public double? SystemTotalW { get; set; }
}

/// <summary>
/// One LibreHardwareMonitor telemetry sample. Mirrors HardwareTelemetrySnapshot
/// from the proven Powenetics app so the embedded service maps 1:1.
/// </summary>
public sealed class TelemetrySample
{
    public double TimeSec { get; set; }

    // GPU
    public double? GpuTempC { get; set; }
    public double? GpuHotspotC { get; set; }
    public double? GpuVramTempC { get; set; }
    public double? GpuCoreClockMhz { get; set; }
    public double? GpuMemClockMhz { get; set; }
    public double? GpuLoadPct { get; set; }
    public double? GpuVoltageV { get; set; }
    public double? GpuBoardPowerW { get; set; }   // LHM's own board-power reading (cross-check vs Powenetics)
    public double? VramUsedMb { get; set; }

    // Fans (up to 4)
    public double? FanRpm1 { get; set; }
    public double? FanRpm2 { get; set; }
    public double? FanRpm3 { get; set; }
    public double? FanRpm4 { get; set; }
    public double? FanPct1 { get; set; }

    // CPU / system context
    public double? CpuTempC { get; set; }
    public double? CpuLoadPct { get; set; }
    public double? CpuPowerW { get; set; }
    public double? CpuClockMhz { get; set; }

    /// <summary>Highest non-null fan RPM across all fans.</summary>
    public double? MaxFanRpm
    {
        get
        {
            double? best = null;
            foreach (var v in new[] { FanRpm1, FanRpm2, FanRpm3, FanRpm4 })
                if (v is not null && (best is null || v > best)) best = v;
            return best;
        }
    }
}
