using GpuSuite.Core.Models;
using GpuSuite.Core.Stats;

namespace GpuSuite.Engine.Aggregation;

/// <summary>
/// (9) Result Aggregator. Combines the repeats of a (game, scene, resolution) into a
/// single aggregate using ONLY valid runs, and rolls all aggregates into a SuiteResult
/// with an overall performance index (geometric mean of avg FPS) and perf-per-watt.
/// </summary>
public sealed class ResultAggregator
{
    public SceneResolutionAggregate Aggregate(string game, string scene, string variantId, string variantName, string resolution, IReadOnlyList<RunResult> runs)
    {
        var agg = new SceneResolutionAggregate
        {
            GameId = game,
            SceneId = scene,
            VariantId = variantId,
            VariantName = variantName,
            ResolutionName = resolution,
            TotalRuns = runs.Count,
            Runs = runs.ToList()
        };

        var valid = runs.Where(r => r.Verdict == RunVerdict.Valid).ToList();
        agg.ValidRuns = valid.Count;
        if (valid.Count == 0) return agg; // nothing to average — surfaced as 0 valid runs

        agg.AvgFps = valid.Average(r => r.Frames.AvgFps);
        agg.P1LowFps = valid.Average(r => r.Frames.P1LowFps);
        agg.P01LowFps = valid.Average(r => r.Frames.P01LowFps);
        agg.AvgFrameTimeMs = valid.Average(r => r.Frames.AvgFrameTimeMs);
        agg.P99FrameTimeMs = valid.Average(r => r.Frames.P99FrameTimeMs);
        // Report the same cadence-aware metric used by validation. A smooth frame-generation capture has
        // alternating rendered/generated present times, which makes the naive global metric read as 25-50%
        // stutter even when the phase-local cadence is stable.
        agg.StutterPct = valid.Average(r => r.FrameGenActive == true
            ? r.Frames.FrameGenStutterPct
            : r.Frames.StutterPct);

        var measurements = valid.Select(r => r.Power.Measurement).ToList();
        if (PowerProvenance.AreCompatible(measurements))
        {
            agg.PowerMeasurement = CopyMeasurement(valid[0].Power.Measurement);
            agg.AvgGpuPowerW = Statistics.Avg(valid.Select(r => r.Power.AvgGpuPowerW));
            agg.PeakGpuPowerW = Statistics.Max(valid.Select(r => r.Power.PeakGpuPowerW));
            if (PowerProvenance.IsDirectEfficiencyEligible(agg.PowerMeasurement))
            {
                agg.EnergyPerFrameJ = Statistics.Avg(valid.Select(r => r.Power.EnergyPerFrameJ));
                agg.FpsPerWatt = Statistics.Avg(valid.Select(r => r.FpsPerWatt));
            }
        }
        else
        {
            agg.PowerComparisonNote = "Power and efficiency withheld: valid repeats use incompatible power provenance.";
            agg.PowerMeasurement = PowerMeasurementMetadata.Unknown(agg.PowerComparisonNote);
        }

        agg.GpuTempAvgC = Statistics.Avg(valid.Select(r => r.Telemetry.GpuTempAvgC));
        agg.GpuHotspotMaxC = Statistics.Max(valid.Select(r => r.Telemetry.GpuHotspotMaxC));
        agg.GpuVramTempMaxC = Statistics.Max(valid.Select(r => r.Telemetry.GpuVramTempMaxC));
        agg.GpuCoreClockAvgMhz = Statistics.Avg(valid.Select(r => r.Telemetry.GpuCoreClockAvgMhz));
        agg.FanRpmAvg = Statistics.Avg(valid.Select(r => r.Telemetry.FanRpmAvg));
        agg.CpuTempAvgC = Statistics.Avg(valid.Select(r => r.Telemetry.CpuTempAvgC));   // null unless elevated (LHM SMU)
        agg.CpuLoadAvgPct = Statistics.Avg(valid.Select(r => r.Telemetry.CpuLoadAvgPct));

        agg.FpsVariancePct = Statistics.CoefficientOfVariationPct(valid.Select(r => r.Frames.AvgFps));
        return agg;
    }

    public SuiteResult BuildSuite(SystemInfo system, IReadOnlyList<SceneResolutionAggregate> aggregates)
    {
        var result = new SuiteResult
        {
            GpuName = system.GpuName,
            System = system,
            Aggregates = aggregates.ToList()
        };

        // Overall index = geometric mean of avg FPS across aggregates that have valid runs.
        var fpsValues = aggregates.Where(a => a.ValidRuns > 0 && a.AvgFps > 0).Select(a => a.AvgFps).ToList();
        result.OverallPerformanceIndex = GeoMean(fpsValues);

        var ppwValues = aggregates.Where(a => a.FpsPerWatt is > 0).Select(a => a.FpsPerWatt!.Value).ToList();
        result.OverallPerfPerWatt = ppwValues.Count > 0 ? GeoMean(ppwValues) : null;
        return result;
    }

    private static double GeoMean(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        double sumLog = 0;
        foreach (var v in values) sumLog += Math.Log(v);
        return Math.Exp(sumLog / values.Count);
    }

    private static PowerMeasurementMetadata CopyMeasurement(PowerMeasurementMetadata source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        Kind = source.Kind,
        Scope = source.Scope,
        EffectiveSampleHz = source.EffectiveSampleHz,
        HasPerRailData = source.HasPerRailData,
        HardwareBustersVerifiedPowerEligible = source.HardwareBustersVerifiedPowerEligible,
        LegacyInferred = source.LegacyInferred,
        QualificationNote = source.QualificationNote
    };
}
