using GpuSuite.Core.Models;
using GpuSuite.Reporting;
using Xunit;

namespace GpuSuite.Tests;

/// <summary>
/// The comparison join/delta math behind `gpusuite compare`. Pins the honesty rules: only identical
/// (game, scene, model, resolution) cells are matched, unmatched sides are kept visible, watt deltas are
/// suppressed across incompatible power provenance, and the report escapes injected labels.
/// </summary>
public class SuiteComparisonTests
{
    private static SceneResolutionAggregate Agg(string game, string scene, string variant, string res,
        double avgFps, PowerMeasurementMetadata? power = null, double? watts = null)
    {
        var agg = new SceneResolutionAggregate
        {
            GameId = game, SceneId = scene, VariantId = variant, ResolutionName = res,
            TotalRuns = 3, ValidRuns = 3, AvgFps = avgFps,
        };
        if (power is not null) agg.PowerMeasurement = power;
        foreach (var r in Enumerable.Range(0, 3)) agg.Runs.Add(new RunResult { Verdict = RunVerdict.Valid });
        if (watts is { } w) foreach (var r in agg.Runs) r.Power.AvgGpuPowerW = w;
        return agg;
    }

    private static SuiteResult Suite(string gpuName, params SceneResolutionAggregate[] aggs)
    {
        var s = new SuiteResult { GpuName = gpuName, GeneratedUtc = new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc) };
        s.Aggregates.AddRange(aggs);
        s.OverallPerformanceIndex = aggs.Length > 0 ? aggs.Average(a => a.AvgFps) : 0;
        return s;
    }

    private static PowerMeasurementMetadata Powenetics() => new()
    {
        Kind = PowerMeasurementKind.PoweneticsDirect,
        Scope = "GPU rails",
        HasPerRailData = true,
        HardwareBustersVerifiedPowerEligible = true,
    };

    private static PowerMeasurementMetadata Lhm() => new()
    {
        Kind = PowerMeasurementKind.GpuReportedTelemetry,
        Scope = "board",
    };

    [Fact]
    public void Build_MatchesIdenticalCells_AndKeepsUnmatchedSides()
    {
        var baseline = Suite("RTX A", Agg("doom", "riparium", "default", "4K", 100));
        var target = Suite("RTX B",
            Agg("doom", "riparium", "default", "4K", 120),
            Agg("f1", "melbourne", "default", "4K", 40));

        var cmp = SuiteComparison.Build(baseline, target);

        Assert.Equal(2, cmp.Rows.Count);
        Assert.Single(cmp.Matched);
        Assert.Empty(cmp.OnlyInBaseline);
        Assert.Single(cmp.OnlyInTarget);

        var row = cmp.Matched.First();
        Assert.Equal(20.0, row.DeltaPct!.Value, 6);   // +20%
    }

    [Fact]
    public void Build_VariantOrResolutionMismatch_IsNotMatched()
    {
        // Same game/scene but a different model and resolution must NOT be differenced — that's how
        // reviews accidentally compare DLSS-Q against native.
        var baseline = Suite("A", Agg("cp77", "city", "rt-dlss-q", "4K", 50), Agg("aw2", "cauldron", "default", "1440p", 200));
        var target = Suite("B", Agg("cp77", "city", "rt-native", "4K", 25), Agg("aw2", "cauldron", "default", "2160p", 90));

        var cmp = SuiteComparison.Build(baseline, target);

        Assert.Empty(cmp.Matched);
        Assert.Equal(2, cmp.OnlyInBaseline.Count());
        Assert.Equal(2, cmp.OnlyInTarget.Count());
    }

    [Fact]
    public void DeltaPct_WithAnEmptySide_IsNull_NeverPlusOrMinus100()
    {
        var cmp = SuiteComparison.Build(Suite("A"), Suite("B", Agg("g", "s", "default", "4K", 100)));
        var row = cmp.OnlyInTarget.Single();
        Assert.False(row.Matched);
        Assert.Null(row.DeltaPct);
    }

    [Fact]
    public void MatchedIndexDelta_GeometricMeanOverMatchedOnly()
    {
        var baseline = Suite("A", Agg("g1", "s", "default", "4K", 100), Agg("g2", "s", "default", "4K", 100), Agg("g3", "s", "default", "4K", 100));
        var target = Suite("B", Agg("g1", "s", "default", "4K", 110), Agg("g2", "s", "default", "4K", 90), Agg("g9", "s", "default", "4K", 999));

        var cmp = SuiteComparison.Build(baseline, target);

        // matched: +10% and −10% → geo-mean delta ≈ sqrt(1.1 × 0.9) − 1 ≈ −0.5%; g9 excluded.
        Assert.Equal(2, cmp.Matched.Count());
        var d = cmp.MatchedIndexDeltaPct!.Value;
        Assert.InRange(d, -0.51, -0.49);
    }

    [Fact]
    public void PowerComparable_RequiresSameProvenanceContract()
    {
        var powBase = Agg("g", "s", "d", "4K", 100, Powenetics(), 200);
        var lhmTarget = Agg("g", "s", "d", "4K", 120, Lhm(), 180);
        var powTarget = Agg("g", "s", "d", "4K", 120, Powenetics(), 180);

        var mixed = SuiteComparison.Build(Suite("A", powBase), Suite("B", lhmTarget)).Matched.Single();
        Assert.False(mixed.PowerComparable);   // PMD vs LHM must never difference as equivalent

        var same = SuiteComparison.Build(Suite("A", powBase), Suite("B", powTarget)).Matched.Single();
        Assert.True(same.PowerComparable);
    }

    [Fact]
    public void Generate_EscapesScriptTagsInLabels()
    {
        var evil = "<script>alert(1)</script>";
        var baseline = Suite(evil, Agg(evil, "<b>s</b>", "v<i>", "4K", 100));
        var target = Suite("B", Agg(evil, "<b>s</b>", "v<i>", "4K", 110));

        var html = new SuiteComparisonReportGenerator().Generate(SuiteComparison.Build(baseline, target));

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&lt;b&gt;s&lt;/b&gt;", html);
    }

    [Theory]
    [InlineData("game\" onload=\"alert(1)")]
    [InlineData("gpu<img src=x onerror=alert(2)>")]
    public void Generate_EscapesAttributeBreakoutsInLabels(string evil)
    {
        var baseline = Suite(evil, Agg("g", "s", "default", "4K", 100));
        var target = Suite("B", Agg("g", "s", "default", "4K", 110));

        var html = new SuiteComparisonReportGenerator().Generate(SuiteComparison.Build(baseline, target));

        // The RAW label must never reach the document (attribute/tag breakouts are impossible once
        // < > & and quotes are entity-escaped); only its escaped form may appear.
        Assert.DoesNotContain(evil, html);
        Assert.Contains(SvgCharts.Esc(evil), html);
    }
}
