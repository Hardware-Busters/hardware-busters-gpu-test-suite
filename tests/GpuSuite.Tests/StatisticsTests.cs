using GpuSuite.Core.Models;
using GpuSuite.Core.Stats;
using Xunit;

namespace GpuSuite.Tests;

/// <summary>
/// Edge-case coverage for the frame-time statistics — the math every headline number (avg FPS, 1% / 0.1%
/// low) is derived from. The selftest CLI verbs cover the artifact-rejection story end-to-end; these tests
/// pin the individual definitions: time-weighted low budgets, percentile interpolation, capture-seam
/// exclusion limits, and FG cadence de-interleaving.
/// </summary>
public class StatisticsTests
{
    private static List<FrameSample> Frames(int n, double ftMs)
    {
        var list = new List<FrameSample>(n);
        double t = 0;
        for (int i = 0; i < n; i++) { t += ftMs / 1000.0; list.Add(new FrameSample(t, ftMs)); }
        return list;
    }

    // ---- TimeWeightedLowFps (the headline "1% / 0.1% low" definition) ----

    [Fact]
    public void TimeWeightedLow_EmptySeries_ReturnsZero()
    {
        Assert.Equal(0, Statistics.TimeWeightedLowFps(Array.Empty<double>(), 0.01));
    }

    [Fact]
    public void TimeWeightedLow_SingleFrame_IsItsOwnFps()
    {
        // one 10 ms frame → 100 fps, and it is trivially also the slowest 1% of capture time
        Assert.Equal(100.0, Statistics.TimeWeightedLowFps(new[] { 10.0 }, 0.01), 6);
    }

    [Fact]
    public void TimeWeightedLow_UniformSeries_EqualsAverageFps()
    {
        var sorted = new double[1_000];
        Array.Fill(sorted, 8.0);   // 125 fps everywhere
        Assert.Equal(125.0, Statistics.TimeWeightedLowFps(sorted, 0.01), 6);
        Assert.Equal(125.0, Statistics.TimeWeightedLowFps(sorted, 0.001), 6);
    }

    [Fact]
    public void TimeWeightedLow_BudgetBoundary_IncludesTheFrameThatCrossesIt()
    {
        // 990 frames @ 5 ms + 10 frames @ 500 ms. Total = 9950 ms; the slowest-1% budget = 99.5 ms,
        // which ONE 500 ms frame already crosses → the window is exactly that single frame.
        var sorted = new double[1_000];
        Array.Fill(sorted, 5.0);
        for (int i = 990; i < 1_000; i++) sorted[i] = 500.0;
        Assert.Equal(1000.0 / 500.0, Statistics.TimeWeightedLowFps(sorted, 0.01), 6);
    }

    [Fact]
    public void TimeWeightedLow_MixedWindow_MeanIsTimeWeightedNotPercentile()
    {
        // Slowest 10% budget over 90 frames @ 10 ms + 10 frames @ 90 ms:
        // total = 900 + 900 = 1800 ms; budget(10%) = 180 ms → crossed by two 90 ms frames (180 ms),
        // so the window mean is exactly 90 ms → 11.11… fps (a pure frame-count percentile would differ).
        var sorted = new double[100];
        Array.Fill(sorted, 10.0);
        for (int i = 90; i < 100; i++) sorted[i] = 90.0;
        Assert.Equal(1000.0 / 90.0, Statistics.TimeWeightedLowFps(sorted, 0.10), 4);
    }

    // ---- Percentile ----

    [Fact]
    public void Percentile_InterpolatesBetweenOrderStatistics()
    {
        var sorted = new double[] { 10, 20, 30, 40 };
        Assert.Equal(10, Statistics.Percentile(sorted, 0));
        Assert.Equal(40, Statistics.Percentile(sorted, 100));
        Assert.Equal(25, Statistics.Percentile(sorted, 50));       // midpoint of 20/30
        Assert.Equal(32.5, Statistics.Percentile(sorted, 75));     // 30 + 0.25 × (40−30)
    }

    [Fact]
    public void Percentile_SingleElement_AndEmpty()
    {
        Assert.Equal(7, Statistics.Percentile(new[] { 7d }, 99));
        Assert.Equal(0, Statistics.Percentile(Array.Empty<double>(), 50));
    }

    // ---- ComputeFrameStats: capture-artifact (trace-seam) rejection ----

    [Fact]
    public void ComputeFrameStats_DurationCoversAllFrames_EvenWhenSeamsExcluded()
    {
        // The window length must be computed over ALL frames so MinCaptureSeconds validation can never be
        // softened by the artifact rejection.
        var frames = Frames(1_000, 8.0);
        frames[500].FrameTimeMs = 400.0;
        var s = Statistics.ComputeFrameStats(frames);
        Assert.Equal(1, s.CaptureArtifactCount);
        Assert.Equal(999 * 8.0 / 1000.0 + 400.0 / 1000.0, s.DurationSec, 6);   // 999×8ms + the one 400ms seam
    }

    [Fact]
    public void ComputeFrameStats_TooManySpikes_AreGenuineStutterAndNeverRemoved()
    {
        // More than maxArtifacts (n * 0.005, floor 2) extreme values → NOT excluded; the run must keep
        // them and honestly fail the stutter/single-frame gates downstream instead of being cleaned up.
        var frames = Frames(1_000, 8.0);
        for (int i = 0; i < 10; i++) frames[i * 100].FrameTimeMs = 400.0;
        var s = Statistics.ComputeFrameStats(frames);
        Assert.Equal(0, s.CaptureArtifactCount);
        Assert.True(s.StutterPct > 0.9, $"expected the 10 spikes to read as stutter, got {s.StutterPct}");
        Assert.True(s.P999FrameTimeMs >= 400.0 - 1e-9, "P99.9 frame time must reflect real spikes");
    }

    [Fact]
    public void ComputeFrameStats_FlatCapture_HasZeroStutterAndSaneLows()
    {
        var s = Statistics.ComputeFrameStats(Frames(2_000, 16.0));   // 62.5 fps vsync-ish
        Assert.Equal(62.5, s.AvgFps, 6);
        Assert.Equal(62.5, s.P1LowFps, 6);
        Assert.Equal(0, s.StutterPct);
        Assert.Equal(0, s.CaptureArtifactCount);
    }

    // ---- ComputeFrameStats: FG cadence de-interleaving ----

    [Fact]
    public void FrameGenStutter_BimodalCadence_CorrectsToNearZero_GlobalMetricDoesNot()
    {
        // A perfect 2x-FG cadence at ~120 rendered fps: rendered frames 4.17 ms apart, each followed by a
        // generated present half-way (≈2.08 ms later). Against the global mean both sub-intervals trip a
        // naive 2x-mean stutter gate on half the presents; the phase-aware metric must cancel the cadence.
        var ft = new List<double>(2_000);
        for (int i = 0; i < 1_000; i++) { ft.Add(1000.0 / 480.0); ft.Add(1000.0 / 240.0); }
        double global = Statistics.FrameGenAwareStutterFraction(ft.ToArray()) * 100.0;
        Assert.True(global < 1.0, $"phase-aware stutter should cancel a clean cadence, got {global:0.00}%");
    }

    [Fact]
    public void FrameGenStutter_GenuineHitchInsideCadence_StillCaught()
    {
        // Same clean cadence, but ONE cadence period hitches to 200 ms: the hitch delays whole periods,
        // so it must stand out within its phase instead of being cancelled with the cadence.
        var ft = new List<double>(2_000);
        for (int i = 0; i < 1_000; i++) { ft.Add(1000.0 / 480.0); ft.Add(1000.0 / 240.0); }
        ft[997] = 200.0;
        ft[998] = 200.0;
        double corrected = Statistics.FrameGenAwareStutterFraction(ft.ToArray()) * 100.0;
        Assert.True(corrected > 0.05, $"a genuine hitch inside an FG cadence must still register, got {corrected:0.000}%");
    }

    [Fact]
    public void FrameGenStutter_ShortOrFlatSeries_FallsBackSafely()
    {
        Assert.Equal(0, Statistics.FrameGenAwareStutterFraction(new double[] { 8, 8, 8 }));
        Assert.Equal(0, Statistics.FrameGenAwareStutterFraction(Enumerable.Repeat(8.0, 500).ToArray()));
    }

    // ---- Run-to-run variance helper ----

    [Fact]
    public void CoefficientOfVariation_Basics()
    {
        // fewer than two values → defined as 0 variance (never null)
        Assert.Equal(0, Statistics.CoefficientOfVariationPct(new List<double>())!.Value);
        Assert.Equal(0, Statistics.CoefficientOfVariationPct(new List<double> { 100.0 }));
        // population sd of {95,105} = 5 → CV = 5%
        Assert.Equal(5.0, Statistics.CoefficientOfVariationPct(new List<double> { 95.0, 105.0 })!.Value, 6);
    }

    // ---- Avg/Max/Min ignore NaN and nulls (telemetry series are nullable) ----

    [Fact]
    public void NullableAggregates_SkipNullsAndNaN()
    {
        IEnumerable<double?> series = new double?[] { 10.0, null, double.NaN, 30.0 };
        Assert.Equal(20.0, Statistics.Avg(series)!.Value, 6);
        Assert.Equal(30.0, Statistics.Max(series));
        Assert.Equal(10.0, Statistics.Min(series));
        Assert.Null(Statistics.Avg(new double?[] { null, double.NaN }));
    }
}
