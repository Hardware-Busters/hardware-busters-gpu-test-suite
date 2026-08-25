using GpuSuite.Core.Models;
using GpuSuite.Reporting;
using Xunit;

namespace GpuSuite.Tests;

/// <summary>
/// The comparison join/delta math behind `gpusuite compare`. Pins the honesty rules: identity-matched
/// cells contribute deltas only with stable, identical settings fingerprints; unmatched/settings-excluded
/// sides stay visible; watt deltas require compatible known provenance; labels are escaped.
/// </summary>
public class SuiteComparisonTests
{
    private static SceneResolutionAggregate Agg(string game, string scene, string variant, string res,
        double avgFps, PowerMeasurementMetadata? power = null, double? watts = null,
        string? fingerprint = "preset=verified")
    {
        var agg = new SceneResolutionAggregate
        {
            GameId = game, SceneId = scene, VariantId = variant, ResolutionName = res,
            TotalRuns = 3, ValidRuns = 3, AvgFps = avgFps,
        };
        if (power is not null) agg.PowerMeasurement = power;
        foreach (var r in Enumerable.Range(0, 3))
        {
            var run = new RunResult { Verdict = RunVerdict.Valid };
            if (fingerprint is not null)
            {
                var parts = fingerprint.Split('=', 2);
                run.SettingsFingerprint = new Dictionary<string, string> { [parts[0]] = parts.Length == 2 ? parts[1] : "" };
            }
            agg.Runs.Add(run);
        }
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
        Assert.True(row.SettingsComparable);
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
    public void PowerComparable_RejectsUnknownProvenanceEvenWhenBothSidesAreUnknown()
    {
        var baseline = Agg("g", "s", "d", "4K", 100, new PowerMeasurementMetadata(), 200);
        var target = Agg("g", "s", "d", "4K", 120, new PowerMeasurementMetadata(), 180);

        var row = SuiteComparison.Build(Suite("A", baseline), Suite("B", target)).Matched.Single();

        Assert.False(row.PowerComparable);
        Assert.False(PowerProvenance.AreCompatible(baseline.PowerMeasurement, target.PowerMeasurement));
    }

    [Fact]
    public void SettingsFingerprintMismatch_RemainsVisibleButIsExcludedFromEveryDelta()
    {
        var baseline = Agg("g", "s", "default", "4K", 100, Powenetics(), 200, "preset=Ultra");
        var target = Agg("g", "s", "default", "4K", 150, Powenetics(), 180, "preset=Low");

        var cmp = SuiteComparison.Build(Suite("A", baseline), Suite("B", target));
        var row = cmp.Matched.Single();

        Assert.Equal(SettingsComparisonStatus.Mismatch, row.SettingsComparison.Status);
        Assert.False(row.SettingsComparable);
        Assert.Null(row.DeltaPct);
        Assert.False(row.PowerComparable);
        Assert.Null(cmp.MatchedIndexDeltaPct);
        Assert.Single(cmp.SettingsExcluded);

        var html = new SuiteComparisonReportGenerator().Generate(cmp);
        Assert.Contains("actual settings fingerprints differ", html);
        Assert.Contains("preset=Ultra", html);
        Assert.Contains("preset=Low", html);
        Assert.DoesNotContain("+50.0%", html);
    }

    [Fact]
    public void MissingOrInconsistentSettingsFingerprints_AreExcludedFailClosed()
    {
        var missing = Agg("g1", "s", "default", "4K", 100, fingerprint: null);
        var missingTarget = Agg("g1", "s", "default", "4K", 110);
        var inconsistent = Agg("g2", "s", "default", "4K", 100, fingerprint: "preset=Ultra");
        inconsistent.Runs[2].SettingsFingerprint!["preset"] = "Low";
        var stableTarget = Agg("g2", "s", "default", "4K", 110, fingerprint: "preset=Ultra");

        var cmp = SuiteComparison.Build(
            Suite("A", missing, inconsistent),
            Suite("B", missingTarget, stableTarget));

        Assert.Equal(2, cmp.Matched.Count());
        Assert.Empty(cmp.Comparable);
        Assert.All(cmp.Matched, row => Assert.Equal(SettingsComparisonStatus.Unverified, row.SettingsComparison.Status));
        Assert.Null(cmp.MatchedIndexDeltaPct);
    }

    [Fact]
    public void SettingsFingerprintComparison_IsOrderCaseAndWhitespaceInsensitive()
    {
        var baseline = Agg("g", "s", "default", "4K", 100);
        var target = Agg("g", "s", "default", "4K", 110);
        foreach (var run in baseline.Runs)
            run.SettingsFingerprint = new Dictionary<string, string> { ["Preset"] = " Ultra ", ["Upscaler"] = "DLSS" };
        foreach (var run in target.Runs)
            run.SettingsFingerprint = new Dictionary<string, string> { ["upscaler"] = "dlss", ["preset"] = "ultra" };

        var row = SuiteComparison.Build(Suite("A", baseline), Suite("B", target)).Matched.Single();

        Assert.True(row.SettingsComparable);
        Assert.Equal(10.0, row.DeltaPct!.Value, 6);
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
