namespace GpuSuite.Core.Models;

/// <summary>
/// Warm-up engine configuration (Milestone 4). Before the FIRST measured run of a cell, render for a
/// configurable duration (optionally waiting for shader compilation / cache warm), discard those frames,
/// and start the official measurement ONLY after frame times have stabilized — so the recorded numbers are
/// steady-state, not contaminated by cold-cache / shader-compile spikes. Opt-in (disabled by default so the
/// proven roster's timing is unchanged until the operator turns it on).
/// </summary>
public sealed class WarmupConfig
{
    public bool Enabled { get; set; }

    /// <summary>Seconds to render (and discard) before measuring. 0 = skip the fixed warm-up render.</summary>
    public double DurationSeconds { get; set; } = 8.0;

    /// <summary>Optional extra fixed wait (s) specifically to let shader compilation finish. 0 = none.</summary>
    public double ShaderCompileWaitSeconds { get; set; }

    /// <summary>Log-tagged cache warm-up (the duration render also warms asset/shader caches).</summary>
    public bool CacheWarmup { get; set; }

    /// <summary>Gate the official measurement on frame-time stabilization (shader-compile / streaming settled).</summary>
    public bool WaitForStabilization { get; set; } = true;
    /// <summary>Frame-time coefficient-of-variation (%) at/below which the render is considered stable.</summary>
    public double StabilizationVariancePct { get; set; } = 8.0;
    /// <summary>The CoV must stay at/below the threshold for this long (s) to count as stabilized.</summary>
    public double StabilizationWindowSeconds { get; set; } = 3.0;
    /// <summary>Cap (s) on how long to wait for stabilization before measuring anyway.</summary>
    public double MaxStabilizationWaitSeconds { get; set; } = 30.0;
}
