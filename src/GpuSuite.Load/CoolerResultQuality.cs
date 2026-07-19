using GpuSuite.Core.Models;

namespace GpuSuite.Load;

/// <summary>A post-run integrity assessment. Warnings never discard the raw sweep or report.</summary>
public sealed record CoolerResultQuality(bool IsComplete, IReadOnlyList<string> Warnings);

/// <summary>
/// Separates "the runner returned" from "the requested cooler experiment completed credibly".
/// This prevents a cancelled or badly under-powered partial sweep from being presented as a success.
/// </summary>
public static class CoolerResultQualityEvaluator
{
    public static CoolerResultQuality Evaluate(
        CoolerSweepOptions options,
        CoolerSweepResult result,
        bool cancelled,
        bool manualFan)
    {
        var warnings = new List<string>();
        var targets = CoolerMath.PowerAxis(options.FromW, options.ToW, options.StepW);

        if (cancelled)
            warnings.Add("The sweep was cancelled; the saved result is partial.");

        if (result.Levels.Count != options.NoiseLevels.Count)
            warnings.Add($"Captured {result.Levels.Count}/{options.NoiseLevels.Count} requested noise level(s).");

        int expectedSteps = options.NoiseLevels.Count * targets.Count;
        int actualSteps = result.Levels.Sum(level => level.Steps.Count);
        if (actualSteps != expectedSteps)
            warnings.Add($"Captured {actualSteps}/{expectedSteps} requested power step(s).");

        var completedSteps = result.Levels.SelectMany(level => level.Steps).ToList();
        int missingPower = completedSteps.Count(step => step.AchievedW is null || !double.IsFinite(step.AchievedW.Value));
        if (missingPower > 0)
            warnings.Add($"{missingPower} step(s) have no valid live power reading.");

        int missedTargets = completedSteps.Count(step =>
            step.AchievedW is double watts &&
            double.IsFinite(watts) &&
            Math.Abs(watts - step.TargetW) > options.PowerSettleBandW);
        if (missedTargets > 0)
            warnings.Add($"{missedTargets} step(s) missed the requested power by more than ±{options.PowerSettleBandW:0.#} W.");

        int unsettled = completedSteps.Count(step => !step.Settled);
        if (unsettled > 0)
            warnings.Add($"{unsettled} step(s) reached max soak without thermal equilibrium.");

        if (options.AmbientC <= 0)
            warnings.Add("Ambient temperature was not recorded; delta-over-ambient comparisons are unavailable.");

        if (manualFan)
            warnings.Add("Manual fan mode requires the operator to hold every listed calibrated fan speed; this cannot be verified automatically.");

        bool complete = !cancelled && result.Levels.Count == options.NoiseLevels.Count &&
                        actualSteps == expectedSteps && missingPower == 0 && missedTargets == 0;
        return new CoolerResultQuality(complete, warnings);
    }
}
