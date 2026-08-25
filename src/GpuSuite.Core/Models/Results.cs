namespace GpuSuite.Core.Models;

/// <summary>How the GPU power value was obtained. These kinds are deliberately not interchangeable.</summary>
public enum PowerMeasurementKind
{
    Unknown,
    PoweneticsDirect,
    GpuReportedTelemetry,
    Synthetic,
    Replay
}

/// <summary>
/// Auditable provenance for a power series. Only direct Powenetics GPU-rail measurements are
/// eligible for Hardware Busters power-efficiency publication metrics.
/// </summary>
public sealed class PowerMeasurementMetadata
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public PowerMeasurementKind Kind { get; set; } = PowerMeasurementKind.Unknown;
    public string Scope { get; set; } = "Unknown power scope";
    public double? EffectiveSampleHz { get; set; }
    public bool HasPerRailData { get; set; }
    public bool HardwareBustersVerifiedPowerEligible { get; set; }
    public bool LegacyInferred { get; set; }
    public string QualificationNote { get; set; } = "Power provenance was not recorded.";
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayLabel => PowerProvenance.Label(this);

    public static PowerMeasurementMetadata Unknown(string note = "Power provenance was not recorded.") => new() { QualificationNote = note };
}

public static class PowerProvenance
{
    /// <summary>
    /// The sole eligibility gate for Hardware Busters efficiency metrics. A mutable eligibility flag is
    /// intentionally insufficient: the value must be a current direct Powenetics rail measurement.
    /// </summary>
    public static bool IsDirectEfficiencyEligible(PowerMeasurementMetadata? measurement) =>
        measurement is not null
        && measurement.Kind == PowerMeasurementKind.PoweneticsDirect
        && !measurement.LegacyInferred
        && measurement.HasPerRailData
        && measurement.HardwareBustersVerifiedPowerEligible;

    /// <summary>Power values can be compared only when their full provenance contract matches.</summary>
    public static bool AreCompatible(PowerMeasurementMetadata? left, PowerMeasurementMetadata? right) =>
        left is not null && right is not null
        && left.Kind != PowerMeasurementKind.Unknown
        && right.Kind != PowerMeasurementKind.Unknown
        && left.Kind == right.Kind
        && left.LegacyInferred == right.LegacyInferred
        && left.HasPerRailData == right.HasPerRailData
        && left.HardwareBustersVerifiedPowerEligible == right.HardwareBustersVerifiedPowerEligible
        && string.Equals(left.Scope?.Trim(), right.Scope?.Trim(), StringComparison.OrdinalIgnoreCase);

    public static bool AreCompatible(IEnumerable<PowerMeasurementMetadata> measurements)
    {
        using var iterator = measurements.GetEnumerator();
        if (!iterator.MoveNext()) return false;
        var first = iterator.Current;
        while (iterator.MoveNext())
            if (!AreCompatible(first, iterator.Current)) return false;
        return first.Kind != PowerMeasurementKind.Unknown;
    }

    public static string Label(PowerMeasurementMetadata? measurement)
    {
        if (measurement is null)
            return "NOT A HARDWARE RESULT";
        if (measurement.LegacyInferred)
            return "LEGACY INFERRED · NOT A HARDWARE RESULT";
        if (measurement.Kind == PowerMeasurementKind.PoweneticsDirect && !IsDirectEfficiencyEligible(measurement))
            return "UNVERIFIED · NOT A HARDWARE RESULT";
        return measurement.Kind switch
        {
        PowerMeasurementKind.PoweneticsDirect => "DIRECT · POWENETICS",
        PowerMeasurementKind.GpuReportedTelemetry => "APPROXIMATE · GPU TELEMETRY",
        PowerMeasurementKind.Synthetic => "SYNTHETIC · NOT A HARDWARE RESULT",
        PowerMeasurementKind.Replay => "REPLAY · NOT A HARDWARE RESULT",
        _ => "NOT A HARDWARE RESULT"
        };
    }
}

public sealed class FrameStats
{
    public int FrameCount { get; set; }
    public double DurationSec { get; set; }
    public double AvgFps { get; set; }
    public double MinFps { get; set; }
    public double MaxFps { get; set; }

    /// <summary>Headline 1% low (time-weighted, CapFrameX-style).</summary>
    public double P1LowFps { get; set; }
    /// <summary>Headline 0.1% low (time-weighted).</summary>
    public double P01LowFps { get; set; }

    /// <summary>1% low computed as FPS at the 99th-percentile frame time.</summary>
    public double P1LowFpsPercentile { get; set; }
    public double P01LowFpsPercentile { get; set; }

    public double AvgFrameTimeMs { get; set; }
    public double MedianFrameTimeMs { get; set; }
    public double P95FrameTimeMs { get; set; }
    public double P99FrameTimeMs { get; set; }
    public double P999FrameTimeMs { get; set; }
    public double FrameTimeStdDevMs { get; set; }

    /// <summary>% of frames exceeding 2x mean frame time.</summary>
    public double StutterPct { get; set; }

    /// <summary>Cadence-robust stutter %: de-interleaves the frame-time series into phase sub-streams at the
    /// detected frame-generation cadence, so FG's alternating rendered/generated present cadence is not
    /// miscounted as stutter (see <see cref="Stats.Statistics.FrameGenAwareStutterFraction"/>). The validator
    /// gates on this INSTEAD of <see cref="StutterPct"/> only when the run is flagged frame-gen-on; for a
    /// non-FG run it is computed but unused. Prevents a smooth FG capture (e.g. CP FG-2x: 138 fps, 0.2%
    /// cross-check) from failing the stutter gate on structural cadence alone.</summary>
    public double FrameGenStutterPct { get; set; }

    /// <summary>
    /// Number of frame times rejected as capture seams (PresentMon trace gaps) — a few extreme values
    /// excluded from the distribution so a single dropout doesn't dominate the time-weighted lows. The
    /// run's <see cref="FrameCount"/> still counts every captured frame; this is transparency only.
    /// </summary>
    public int CaptureArtifactCount { get; set; }
    /// <summary>Total wall time (seconds) attributed to the excluded capture-seam frames.</summary>
    public double CaptureArtifactSeconds { get; set; }
}

public sealed class PowerStats
{
    /// <summary>Schema-versioned measurement provenance; retained alongside legacy source fields.</summary>
    public PowerMeasurementMetadata Measurement { get; set; } = new();
    public int SampleCount { get; set; }
    public double DurationSec { get; set; }
    public double? AvgGpuPowerW { get; set; }
    public double? PeakGpuPowerW { get; set; }
    public double? MinGpuPowerW { get; set; }
    /// <summary>Total GPU energy over the capture (J = avg W * duration).</summary>
    public double? GpuEnergyJoules { get; set; }
    /// <summary>Energy per rendered frame (J/frame) — efficiency at fixed work.</summary>
    public double? EnergyPerFrameJ { get; set; }
    public double? AvgSystemPowerW { get; set; }
    public double? PeakSystemPowerW { get; set; }
    /// <summary>Source of the GPU power numbers (Powenetics Live vs Synthetic vs LHM fallback).</summary>
    public DataSourceMode Source { get; set; } = DataSourceMode.Synthetic;
}

public sealed class TelemetryStats
{
    public int SampleCount { get; set; }
    public double? GpuTempAvgC { get; set; }
    public double? GpuTempMaxC { get; set; }
    public double? GpuHotspotAvgC { get; set; }
    public double? GpuHotspotMaxC { get; set; }
    public double? GpuVramTempMaxC { get; set; }
    public double? GpuCoreClockAvgMhz { get; set; }
    public double? GpuMemClockAvgMhz { get; set; }
    public double? GpuLoadAvgPct { get; set; }
    public double? GpuBoardPowerAvgW { get; set; }   // LHM cross-check
    public double? FanRpmAvg { get; set; }
    public double? FanRpmMax { get; set; }
    public double? CpuTempAvgC { get; set; }
    public double? CpuLoadAvgPct { get; set; }
}

/// <summary>Auditable health telemetry emitted by an adaptive in-world bot action. Unlike the general
/// scene-static monitor, this sensor runs inside SmartTraverse/Gx10Traverse and records whether the route
/// kept producing camera translation or spent the window repeatedly recovering from obstacles.</summary>
public sealed class BotTraversalStats
{
    public string Type { get; set; } = "";
    public double DurationSec { get; set; }
    public bool MotionSensorActive { get; set; }
    public int MotionProbes { get; set; }
    public int HealthyMotionProbes { get; set; }
    public double? HealthyMotionRatio { get; set; }
    public double? MeanMotionScore { get; set; }
    public double? MinMotionScore { get; set; }
    public double? MaxMotionScore { get; set; }
    public int RecoveryTurns { get; set; }
    public int ForcedRecoveryTurns { get; set; }
    public int Decisions { get; set; }
    public int BlindHolds { get; set; }
}

/// <summary>The result of one scene run (one repeat).</summary>
public sealed class RunResult
{
    public string GpuName { get; set; } = "";
    public string GameId { get; set; } = "";
    public string SceneId { get; set; } = "";

    /// <summary>The graphics variant ("extra model") this run was measured in, e.g. "rt-dlss-fg".
    /// Empty/"default" = the game's profile-default settings (no variant selected).</summary>
    public string VariantId { get; set; } = "";
    /// <summary>Display name of the variant (for reports), e.g. "RT + DLSS Quality + Frame Gen".</summary>
    public string VariantName { get; set; } = "";

    public string ResolutionName { get; set; } = "";
    public int RepeatIndex { get; set; }

    public DateTime StartedUtc { get; set; }
    public DateTime EndedUtc { get; set; }

    public FrameStats Frames { get; set; } = new();
    public PowerStats Power { get; set; } = new();
    public TelemetryStats Telemetry { get; set; } = new();

    /// <summary>FPS per watt is published only for direct, eligible Powenetics GPU-rail power.</summary>
    public double? FpsPerWatt =>
        PowerProvenance.IsDirectEfficiencyEligible(Power.Measurement) && Power.AvgGpuPowerW is double w && w > 0
            ? Frames.AvgFps / w : null;

    public RunVerdict Verdict { get; set; } = RunVerdict.Valid;
    public List<string> ValidationIssues { get; set; } = new();

    /// <summary>Measured-window capture-card motion stats from the scene-static sensor (see
    /// <see cref="MotionStats"/>) — the trust signal that the measured scene was genuinely moving.
    /// Null when the sensor didn't arm for this run.</summary>
    public MotionStats? MeasuredMotion { get; set; }

    /// <summary>Per-action adaptive traversal health. Persisted even though adaptive bots own the capture-card
    /// motion sensor and therefore disable the redundant scene-static monitor.</summary>
    public List<BotTraversalStats> BotTraversals { get; set; } = new();

    /// <summary>Render-settings fingerprint (label → value) read off the game's OWN config file at run time
    /// (see <see cref="GameProfile.SettingsFingerprintKeys"/>): the actual as-set upscaler / render
    /// resolution / frame-gen state behind this number. Null = profile declares no fingerprint (settings
    /// store unreadable externally).</summary>
    public Dictionary<string, string>? SettingsFingerprint { get; set; }

    /// <summary>Frame-generation state at run time (from the fingerprint's IsFrameGen key). True means
    /// <see cref="FrameStats.AvgFps"/> counts GENERATED presents — 2-4× the rendered rate — and must never
    /// be read as a render-fps number. Null = unknown (no FG key declared).</summary>
    public bool? FrameGenActive { get; set; }

    /// <summary>How the measured window was detected: "log-file" | "activity" | "duration".</summary>
    public string? CompletionMode { get; set; }
    /// <summary>Total frames captured before trimming to the measured window (transparency).</summary>
    public int CapturedFrameCount { get; set; }
    /// <summary>The game's own reported avg FPS (from its result file), for cross-checking.</summary>
    public double? GameReportedFps { get; set; }

    public DataSourceMode FrameSource { get; set; } = DataSourceMode.Synthetic;
    public DataSourceMode PowerSource { get; set; } = DataSourceMode.Synthetic;
    public DataSourceMode TelemetrySource { get; set; } = DataSourceMode.Synthetic;
    /// <summary>Exact provider identities used for this run (not merely Live/Synthetic).</summary>
    public string FrameProviderName { get; set; } = "";
    public string PowerProviderName { get; set; } = "";
    public string TelemetryProviderName { get; set; } = "";

    /// <summary>
    /// True when this run showed signs of a frame-capture discontinuity (a large trace gap or seam
    /// frame). The orchestrator uses it to switch the next auto-repeat to the RTSS backend, which —
    /// unlike PresentMon's ETW session — stays reliable over short/awkward capture windows. Transient
    /// run state, not persisted.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CaptureDiscontinuity { get; set; }

    // raw file paths (relative to run folder)
    public string RawFramesFile { get; set; } = "presentmon_raw.csv";
    public string RawPowerFile { get; set; } = "power_raw.csv";
    public string PowerMetadataFile { get; set; } = "power_metadata.json";
    public string RawTelemetryFile { get; set; } = "telemetry_raw.csv";

    public bool IsValid => Verdict == RunVerdict.Valid;
}

/// <summary>Aggregate of all repeats for one (game, scene, resolution).</summary>
public sealed class SceneResolutionAggregate
{
    public string GameId { get; set; } = "";
    public string SceneId { get; set; } = "";

    /// <summary>Graphics variant ("extra model") these repeats were measured in. Empty/"default" = profile defaults.</summary>
    public string VariantId { get; set; } = "";
    public string VariantName { get; set; } = "";

    public string ResolutionName { get; set; } = "";

    public int TotalRuns { get; set; }
    public int ValidRuns { get; set; }

    // Means across valid runs
    public double AvgFps { get; set; }
    public double P1LowFps { get; set; }
    public double P01LowFps { get; set; }
    public double AvgFrameTimeMs { get; set; }
    public double P99FrameTimeMs { get; set; }
    public double StutterPct { get; set; }

    public double? AvgGpuPowerW { get; set; }
    public double? PeakGpuPowerW { get; set; }
    public double? EnergyPerFrameJ { get; set; }
    public double? FpsPerWatt { get; set; }
    /// <summary>Shared power provenance when all valid repeats use the same kind; otherwise Unknown.</summary>
    public PowerMeasurementMetadata PowerMeasurement { get; set; } = new();
    /// <summary>Explains why power/efficiency values were withheld from a mixed-provenance aggregate.</summary>
    public string? PowerComparisonNote { get; set; }

    public double? GpuTempAvgC { get; set; }
    public double? GpuHotspotMaxC { get; set; }
    public double? GpuVramTempMaxC { get; set; }
    public double? GpuCoreClockAvgMhz { get; set; }
    public double? FanRpmAvg { get; set; }

    // CPU is measured per-run (TelemetryStats); surface it at the aggregate level too so the combined
    // report shows it. CPU temp comes from LHM's SMU and is non-null only when the suite runs elevated.
    public double? CpuTempAvgC { get; set; }
    public double? CpuLoadAvgPct { get; set; }

    /// <summary>Run-to-run variance of avg FPS (coefficient of variation %).</summary>
    public double? FpsVariancePct { get; set; }

    public List<RunResult> Runs { get; set; } = new();
}

public sealed class SuiteResult
{
    public string GpuName { get; set; } = "";
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
    public SystemInfo System { get; set; } = new();
    public List<SceneResolutionAggregate> Aggregates { get; set; } = new();

    /// <summary>Geometric-mean performance index across all valid scene/resolution aggregates (avg FPS).</summary>
    public double OverallPerformanceIndex { get; set; }
    /// <summary>Overall performance-per-watt index.</summary>
    public double? OverallPerfPerWatt { get; set; }

    /// <summary>
    /// Per-game roster outcomes (passed / failed / skipped + failure class + reason). Populated by the
    /// orchestrator so the final summary and report can show, at a glance, what survived and what didn't —
    /// a failed game is recorded here, never silently dropped.
    /// </summary>
    public List<GameOutcome> GameOutcomes { get; set; } = new();

    /// <summary>True when the orchestrator reached the END of the game list (every game attempted, however it
    /// turned out). False only if the campaign was aborted (physical ESC) before the list finished.</summary>
    public bool RosterCompleted { get; set; }

    /// <summary>True when this run was launched in autonomous/unattended mode (never-fake launch handling on).</summary>
    public bool Unattended { get; set; }

    /// <summary>
    /// Where the menu-nav VISION model ran for this run, auto-selected before the benchmarks: "GX10 (off-bench)",
    /// "Local GPU", "Local CPU", or "n/a" when no vision nav was used. Recorded for traceability in the report
    /// + roster summary (the GX10 is unloaded before each measured window regardless, so it never affects fps).
    /// </summary>
    public string VisionCompute { get; set; } = "";
}

public sealed class SystemInfo
{
    public string GpuName { get; set; } = "";
    public string CpuName { get; set; } = "";
    public string? OsVersion { get; set; }
    public string? DriverVersion { get; set; }
    public double? TotalRamGb { get; set; }
    public string SuiteVersion { get; set; } = "0.1.0-mvp";
    public bool PoweneticsConnected { get; set; }

    /// <summary>
    /// Set when a Powenetics PMD was CONFIGURED (port/auto-detect) but was not streaming at probe time.
    /// The report surfaces this loudly so a fallback-power result can never be read as a verified PMD
    /// measurement — the recurring bench failure is the wedged MCU that only a physical USB replug clears.
    /// </summary>
    public string? PoweneticsNote { get; set; }

    public bool PresentMonAvailable { get; set; }
    public bool LhmAvailable { get; set; }
}
