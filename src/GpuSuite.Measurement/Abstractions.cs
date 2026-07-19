using GpuSuite.Core.Models;

namespace GpuSuite.Measurement;

/// <summary>Identifies the foreground game process PresentMon/Powenetics should target.</summary>
public sealed class FrameCaptureTarget
{
    public string ProcessName { get; init; } = "";   // e.g. "Cyberpunk2077.exe"
    public int? Pid { get; init; }
    /// <summary>Hint used by the synthetic provider to produce realistic data.</summary>
    public WorkloadHint Hint { get; init; } = new();
}

/// <summary>
/// Drives the synthetic providers so degraded-mode data is realistic AND deterministic.
/// Seed is derived from run identity so a given run reproduces the same trace.
/// </summary>
public sealed class WorkloadHint
{
    public double BaseFps { get; init; } = 120;
    public double BaseGpuPowerW { get; init; } = 250;
    public double BaseGpuTempC { get; init; } = 65;
    public int Seed { get; init; } = 1234;
    /// <summary>If &gt; 0, a simulated benchmark of this length: synthetic frames stop after it,
    /// so activity-based start/finish detection can be exercised in degraded mode.</summary>
    public double BenchDurationSec { get; init; }
}

/// <summary>A running capture/log that accumulates samples until stopped.</summary>
public interface ISampleSession<T> : IAsyncDisposable
{
    DataSourceMode Mode { get; }
    /// <summary>Live count of samples captured so far (for activity-based completion detection).</summary>
    int SampleCount { get; }
    /// <summary>
    /// Most recent sample captured so far — a non-destructive peek for the live benchmark OSD,
    /// or null if nothing has been captured yet. The default returns null; live sessions override
    /// it to expose their newest sample (cheap, lock-protected). Never consumes/clears anything.
    /// </summary>
    T? Latest => default;
    /// <summary>
    /// Optional human-readable diagnostics from the underlying capture tool — e.g. PresentMon's stderr
    /// (session-collision warnings, trace-start errors). Null when there's nothing to report. Callers
    /// surface it when a live session returns ZERO samples, so the *reason* lands in the log instead of
    /// a silent empty capture. Default returns null; live sessions override.
    /// </summary>
    string? Diagnostics => null;
    /// <summary>Stop logging and return all samples (TimeSec relative to start).</summary>
    Task<IReadOnlyList<T>> StopAsync();
}

public interface ITelemetryProvider
{
    string Name { get; }
    Task<ISampleSession<TelemetrySample>> StartAsync(int intervalMs, WorkloadHint hint, CancellationToken ct);
}

public interface IPowerProvider
{
    string Name { get; }
    bool IsLive { get; }
    /// <summary>Typed, user-visible provenance for this provider's measurements.</summary>
    PowerMeasurementMetadata Measurement { get; }
    Task<ISampleSession<PowerSample>> StartAsync(int intervalMs, WorkloadHint hint, CancellationToken ct);
}

public interface IFrameCaptureProvider
{
    string Name { get; }
    bool IsLive { get; }
    Task<ISampleSession<FrameSample>> StartAsync(FrameCaptureTarget target, CancellationToken ct);
}
