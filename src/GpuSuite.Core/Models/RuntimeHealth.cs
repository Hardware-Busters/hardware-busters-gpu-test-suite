namespace GpuSuite.Core.Models;

/// <summary>
/// A runtime-health incident detected DURING a measured run (Milestone 3): the GPU went idle, frames froze,
/// telemetry/power stopped, the process died, or the benchmark timed out. Recorded with a screenshot + log so
/// the operator can see what the bench looked like at the moment it went wrong. The run is then invalidated
/// (→ deterministic auto-repeat recovery → if persistent, the game is classified RuntimeHealth and skipped).
/// </summary>
public sealed class HealthIncident
{
    /// <summary>"gpu-idle" | "frames-frozen" | "scene-static" | "telemetry-stopped" | "power-flatline" | "process-exited" | "benchmark-timeout".</summary>
    public string Kind { get; set; } = "";
    public string Detail { get; set; } = "";
    /// <summary>Seconds into the measured window when the incident was confirmed.</summary>
    public double AtSeconds { get; set; }
    /// <summary>Path (relative to the run dir) of the screenshot grabbed at the incident, if any.</summary>
    public string? ScreenshotFile { get; set; }
    public string DetectedUtc { get; set; } = "";
}

/// <summary>
/// Capture-card MOTION stats over one measured window, from the runtime-health scene-static sensor
/// (ffmpeg scene-change, CPU-only/off-bench). The "was the scene actually moving" trust signal that sits
/// next to GPU load in the report: high mean = real traversal/flythrough; a min near 0 with a sustained
/// streak would have fired a scene-static incident. Null on runs where the sensor didn't arm (disabled,
/// no capture device, or a self-sensing SmartTraverse/Gx10Traverse bot).
/// </summary>
public sealed class MotionStats
{
    /// <summary>Number of successful motion probes inside the measured window.</summary>
    public int Probes { get; set; }
    /// <summary>Mean scene-change score across probes (Ratchet-style traversal ≈ 0.05–0.3; flythroughs ≈ 1+).</summary>
    public double MeanScore { get; set; }
    /// <summary>Lowest single probe (a brief fade-to-black can legitimately read ~0).</summary>
    public double MinScore { get; set; }
    /// <summary>The static floor this run was judged against.</summary>
    public double FloorScore { get; set; }
    /// <summary>Probes that read below the floor (tolerated unless sustained past the grace).</summary>
    public int BelowFloor { get; set; }
}

/// <summary>
/// Configuration for the runtime health watchdog. Each condition has a grace period — a transient blip
/// (e.g. a shader-compile hitch) won't trip it; a sustained problem will. Set a grace to a large value to
/// effectively disable that condition, or <see cref="Enabled"/> = false to disable the whole watchdog.
/// </summary>
public sealed class RuntimeHealthConfig
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>GPU load at/below this counts as "near 0%" (idle / not rendering).</summary>
    public double IdleGpuLoadPct { get; set; } = 5.0;
    public double IdleGraceSeconds { get; set; } = 6.0;

    /// <summary>Seconds the captured frame count may stay frozen before flagging frozen render / 0 fps.</summary>
    public double FrameStallGraceSeconds { get; set; } = 6.0;
    /// <summary>Seconds the telemetry sample count may stay frozen before flagging telemetry stopped.</summary>
    public double TelemetryStallGraceSeconds { get; set; } = 10.0;
    /// <summary>Seconds the power reading may stay identical before flagging a flatline (frozen PMD/sensor).</summary>
    public double PowerFlatlineGraceSeconds { get; set; } = 12.0;

    /// <summary>Hard cap (s) on a single measured window. 0 = derive from the scene (window + generous grace).</summary>
    public double BenchmarkTimeoutSeconds { get; set; }

    /// <summary>Sense capture-card MOTION during the measured window (ffmpeg scene-change — the SmartTraverse
    /// Tier-0 sensor: CPU-only, off-bench, zero benchmark-GPU cost) and flag a run whose scene goes STATIC.
    /// Catches what gpu-idle and frames-frozen cannot: a wedged/dead character, a paused game, or a static
    /// screen that still RENDERS at high GPU with advancing frames (the AW2 #107 failure mode — two static
    /// screens measured as a "valid" run). Scenes whose bot already senses motion in-world (SmartTraverse /
    /// Gx10Traverse) skip the redundant monitor probe automatically; per-scene opt-out via
    /// <see cref="SceneProfile.MeasuredMotionFloor"/> = 0.</summary>
    public bool MotionEnabled { get; set; } = true;
    /// <summary>Mean scene-change score below this counts as "static". Calibration: Ratchet reads ~0.05–0.22
    /// while genuinely moving (the scale tops out around 0.1–0.3, NOT 1.0); a frozen/paused frame reads ~0.00x.
    /// 0.01 sits well under any real in-world motion observed so far.</summary>
    public double MotionFloorScore { get; set; } = 0.01;
    /// <summary>Seconds the motion must stay below the floor before flagging scene-static — long enough to ride
    /// out legit still moments (turn-arounds, benchmark fade-to-black between loops).</summary>
    public double MotionStaticGraceSeconds { get; set; } = 15.0;
    /// <summary>Seconds between motion probes (each probe samples ~8 frames off the Elgato, ~0.7s).</summary>
    public double MotionProbeIntervalSeconds { get; set; } = 3.0;

    /// <summary>Grab a capture-card screenshot when an incident is confirmed (best-effort).</summary>
    public bool CaptureScreenshotOnIncident { get; set; } = true;
}
