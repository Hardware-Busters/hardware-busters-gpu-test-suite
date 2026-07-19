using GpuSuite.Core.Config;
using GpuSuite.Core.Models;

namespace GpuSuite.Engine.Validation;

/// <summary>
/// (8) Run Validator. Decides whether a run is Valid / Outlier / Invalid. Invalid runs
/// are never averaged into results. Two phases:
///   * ValidateSingle: hard rejects from raw data (no launch, empty capture, missing
///     power/telemetry, too short, broken frame times).
///   * FlagOutliers: across the repeat set, flags runs whose avg FPS deviates too far
///     from the median (kept, but marked; orchestrator may auto-repeat).
/// </summary>
public sealed class RunValidator
{
    private readonly ValidationThresholds _t;
    public RunValidator(ValidationThresholds thresholds) => _t = thresholds;

    public void ValidateSingle(RunResult run,
        IReadOnlyList<FrameSample> frames,
        IReadOnlyList<PowerSample> power,
        IReadOnlyList<TelemetrySample> telemetry,
        bool gameLaunchedOrSimulated,
        bool allowSyntheticFrames = false)
    {
        var issues = run.ValidationIssues;

        if (!gameLaunchedOrSimulated)
            issues.Add("Game did not launch and was not simulated.");

        if (run.FrameSource == DataSourceMode.Synthetic && !allowSyntheticFrames)
            issues.Add("Synthetic FPS data was selected for a real run. Synthetic frames are permitted only " +
                "with an explicit simulation/--force-synth request and are never an implicit launch fallback.");

        // ---- frame capture ----
        if (frames.Count == 0)
            issues.Add("FPS capture produced no frames.");
        else
        {
            if (frames.Count < _t.MinFrameCount)
                issues.Add($"Too few frames captured ({frames.Count} < {_t.MinFrameCount}).");
            if (run.Frames.DurationSec < _t.MinCaptureSeconds)
                issues.Add($"Capture too short ({run.Frames.DurationSec:0.0}s < {_t.MinCaptureSeconds}s).");
            if (run.Frames.AvgFps < _t.MinAvgFps)
                issues.Add($"Average FPS implausibly low ({run.Frames.AvgFps:0.0} < {_t.MinAvgFps}).");

            // A game-written result is an independent, authoritative check on the captured window. Catch a
            // contaminated/short PresentMon window here, while the orchestrator can still auto-repeat it;
            // post-set outlier flagging happens only after the attempt loop and is too late to recover a cell.
            if (_t.MaxGameReportedFpsDeviationPct > 0 && run.GameReportedFps is double gameFps && gameFps > 0)
            {
                double deviationPct = Math.Abs(run.Frames.AvgFps - gameFps) / gameFps * 100.0;
                if (deviationPct > _t.MaxGameReportedFpsDeviationPct)
                    issues.Add($"Measured average FPS {run.Frames.AvgFps:0.0} differs {deviationPct:0.0}% from the game's " +
                        $"authoritative result {gameFps:0.0} FPS (> {_t.MaxGameReportedFpsDeviationPct:0.#}%) — capture window mismatch; auto-repeat required.");
            }

            // broken frame-time behavior
            double maxFt = 0; foreach (var f in frames) if (f.FrameTimeMs > maxFt) maxFt = f.FrameTimeMs;
            if (maxFt > _t.MaxSingleFrameTimeMs)
                issues.Add($"A single frame time of {maxFt:0}ms exceeds {_t.MaxSingleFrameTimeMs}ms (hitch/capture gap).");

            double maxGap = LargestTimeGap(frames);
            if (maxGap > _t.MaxCaptureGapSeconds)
                issues.Add($"Capture gap of {maxGap:0.0}s exceeds {_t.MaxCaptureGapSeconds}s.");

            // Stutter gate. Frame generation presents an alternating rendered/generated cadence that reads as
            // ~25-50% "stutter" against the global mean even on a perfectly smooth capture (CP FG-2x: 138 fps,
            // 0.2% game cross-check, yet 25% global stutter — the run was wrongly rejected). So an FG-flagged
            // run is gated on the cadence-robust FrameGenStutterPct (de-interleaved phase-local stutter; a
            // genuine hitch still trips it, and a non-cadence erratic capture falls back to the global metric).
            // The hard hitch / capture-gap / GPU-idle / frozen gates stay FG-agnostic, so a truly broken FG
            // capture still fails on those.
            bool fgAware = run.FrameGenActive == true;
            double stutterPct = fgAware ? run.Frames.FrameGenStutterPct : run.Frames.StutterPct;
            if (stutterPct > _t.MaxStutterPct)
                issues.Add($"Stutter {stutterPct:0.0}%{(fgAware ? " (frame-gen cadence-corrected)" : "")} exceeds {_t.MaxStutterPct}% (frame-time behavior broken).");

            if (AllNearlyIdentical(frames))
                issues.Add("Frame times are all nearly identical (capture likely frozen/synthetic-stuck).");
        }

        // ---- power ----
        if (power.Count < _t.MinPowerSamples)
            issues.Add($"Powenetics data missing/insufficient ({power.Count} < {_t.MinPowerSamples} samples).");

        // ---- telemetry ----
        if (telemetry.Count < _t.MinTelemetrySamples)
            issues.Add($"Telemetry missing/insufficient ({telemetry.Count} < {_t.MinTelemetrySamples} samples).");

        // ---- GPU idle / frozen-frame capture ----
        // A measured window where the GPU sat essentially idle is normally not real rendering — it's a paused/
        // frozen or non-gameplay screen. A borderless game that loses focus pauses, and the desktop compositor
        // keeps re-presenting its frozen frame at the refresh rate, so the capture reads a fake ~60fps at ~13%
        // load (caught live on AW2). At low resolutions, however, a real VSync-limited scene can legitimately sit
        // below the load floor (caught live on TLOU at 1080p). Strong off-GPU capture-card motion corroborates
        // that rendering, so accept it; absent that independent evidence, retain the hard rejection.
        if (_t.MinGpuLoadPct > 0 && frames.Count > 0)
        {
            double sumLoad = 0; int nLoad = 0;
            foreach (var t in telemetry) if (t.GpuLoadPct is double gl && gl >= 0) { sumLoad += gl; nLoad++; }
            if (nLoad >= 3)
            {
                double avgLoad = sumLoad / nLoad;
                if (avgLoad < _t.MinGpuLoadPct && !HasCorroboratingMotion(run.MeasuredMotion))
                    issues.Add($"GPU idle during the measured window (avg load {avgLoad:0}% < {_t.MinGpuLoadPct:0}%) — the capture is a paused/frozen or non-gameplay screen (e.g. a borderless game that lost focus → its frozen frame re-presented at the refresh rate), not real rendering. Run rejected to avoid a fake number.");
            }
        }

        run.Verdict = issues.Count == 0 ? RunVerdict.Valid : RunVerdict.Invalid;
    }

    /// <summary>Flag outliers across the repeat set by avg-FPS deviation from the median.</summary>
    public void FlagOutliers(IReadOnlyList<RunResult> repeatSet)
    {
        var valid = repeatSet.Where(r => r.Verdict == RunVerdict.Valid).ToList();
        if (valid.Count < 3) return; // need a few points for a meaningful median

        var fps = valid.Select(r => r.Frames.AvgFps).OrderBy(x => x).ToList();
        double median = fps[fps.Count / 2];
        if (median <= 0) return;

        foreach (var r in valid)
        {
            double devAbs = Math.Abs(r.Frames.AvgFps - median);
            double devPct = devAbs / median * 100.0;
            // Scale-aware: a run is an outlier only if it deviates by BOTH more than the percentage AND more
            // than the absolute-fps floor. Percentage-only over-rejected low-fps benchmarks (e.g. rt-native at
            // ~19 fps, where a normal 1.4 fps spread is ~7%) — see OutlierFpsMinAbsDeviation.
            if (devPct > _t.OutlierFpsDeviationPct && devAbs > _t.OutlierFpsMinAbsDeviation)
            {
                r.Verdict = RunVerdict.Outlier;
                r.ValidationIssues.Add($"Avg FPS {r.Frames.AvgFps:0.0} deviates {devPct:0.0}% / {devAbs:0.0} fps from set median {median:0.0} (> {_t.OutlierFpsDeviationPct}% AND > {_t.OutlierFpsMinAbsDeviation} fps).");
            }
        }
    }

    /// <summary>Reject a cell whose post-launch settings fingerprint contradicts its requested resolution.</summary>
    internal static void ValidateResolutionFingerprint(RunResult run, GameProfile game, Resolution requested)
    {
        var apply = game.ResolutionApply;
        if (string.IsNullOrWhiteSpace(apply.FingerprintWidthLabel) ||
            string.IsNullOrWhiteSpace(apply.FingerprintHeightLabel))
            return;

        if (run.SettingsFingerprint is null ||
            !run.SettingsFingerprint.TryGetValue(apply.FingerprintWidthLabel, out var wRaw) ||
            !run.SettingsFingerprint.TryGetValue(apply.FingerprintHeightLabel, out var hRaw) ||
            !int.TryParse(wRaw, out int width) || !int.TryParse(hRaw, out int height))
        {
            run.ValidationIssues.Add($"Resolution fingerprint is required but unreadable " +
                $"({apply.FingerprintWidthLabel}/{apply.FingerprintHeightLabel}).");
            return;
        }

        if (width != requested.Width || height != requested.Height)
            run.ValidationIssues.Add($"Resolution fingerprint reports {width}x{height}, but {requested.Name} " +
                $"({requested.Width}x{requested.Height}) was requested — run rejected as mislabeled.");
    }

    /// <summary>
    /// Reject an authoritative frame-generation fingerprint that contradicts the selected model. This closes
    /// the dangerous gap where menu application could verify one requested knob while a prior model's FG state
    /// remained active, yielding valid-looking but mislabeled FPS.
    /// </summary>
    internal static void ValidateFrameGenerationFingerprint(RunResult run, GameVariant? variant)
    {
        if (variant is null || run.FrameGenActive is not bool actual)
            return;

        string? requested = null;
        if (variant.Settings.TryGetValue("frameGen", out var frameGen))
            requested = frameGen;
        else if (variant.Settings.TryGetValue("upscaler", out var upscaler) &&
                 upscaler.Equals("Off", StringComparison.OrdinalIgnoreCase))
            requested = "Off"; // Native/TAA makes DLSS/FSR frame generation unavailable.

        if (string.IsNullOrWhiteSpace(requested))
            return;

        bool expected = requested.Trim() is not ("0" or "Off" or "OFF" or "False" or "false" or "Disabled" or "disabled" or "None" or "none");
        if (actual != expected)
            run.ValidationIssues.Add($"Frame-generation fingerprint reports {(actual ? "ON" : "OFF")}, but variant " +
                $"'{variant.Id}' requested '{requested}' — run rejected as mislabeled and must auto-repeat.");
    }

    private static double LargestTimeGap(IReadOnlyList<FrameSample> frames)
    {
        double max = 0;
        for (int i = 1; i < frames.Count; i++)
        {
            double gap = frames[i].TimeSec - frames[i - 1].TimeSec;
            if (gap > max) max = gap;
        }
        return max;
    }

    private static bool AllNearlyIdentical(IReadOnlyList<FrameSample> frames)
    {
        if (frames.Count < 50) return false;
        double first = frames[0].FrameTimeMs;
        foreach (var f in frames)
            if (Math.Abs(f.FrameTimeMs - first) > 1e-4) return false;
        return true;
    }

    /// <summary>
    /// Independent capture-card evidence strong enough to exonerate a below-floor GPU-load reading.
    /// Three probes prevent a single transition/fade from passing; 3x the calibrated static floor (with an
    /// absolute 0.03 minimum) keeps animated-but-nearly-static screens from masquerading as traversal; and
    /// limiting low probes prevents one burst of motion from carrying an otherwise frozen window.
    /// </summary>
    private static bool HasCorroboratingMotion(MotionStats? motion)
    {
        if (motion is null || motion.Probes < 3 || motion.FloorScore <= 0)
            return false;

        double requiredMean = Math.Max(0.03, motion.FloorScore * 3.0);
        return motion.MeanScore >= requiredMean && motion.BelowFloor * 3 <= motion.Probes;
    }
}
