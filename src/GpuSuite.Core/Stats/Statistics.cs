using GpuSuite.Core.Models;

namespace GpuSuite.Core.Stats;

/// <summary>
/// Frame-time / FPS statistics computed the way reputable reviewers (CapFrameX,
/// Hardware Unboxed, TechPowerUp) define them. Two distinct "low" definitions are
/// supported and both are reported, because the term "1% low" is overloaded:
///
///  * Percentile low  : FPS at the 99th/99.9th percentile of frame TIME. Simple, robust.
///  * Time-weighted low: average FPS over the slowest 1%/0.1% of total capture TIME.
///    This is the CapFrameX "1% low" and best captures sustained hitching.
///
/// The headline P1Low/P01Low fields use the time-weighted method (industry default).
/// </summary>
public static class Statistics
{
    public static FrameStats ComputeFrameStats(IReadOnlyList<FrameSample> frames,
        double artifactFloorMs = 100.0, double artifactMedianRatio = 20.0, double artifactMaxFraction = 0.005)
    {
        var stats = new FrameStats { FrameCount = frames.Count };
        if (frames.Count == 0) return stats;

        var ft = new double[frames.Count];
        for (int i = 0; i < frames.Count; i++) ft[i] = frames[i].FrameTimeMs;

        // Window length is computed over ALL frames so capture-duration validation is never softened
        // by the artifact rejection below.
        double totalMsAll = 0;
        foreach (var v in ft) totalMsAll += v;
        stats.DurationSec = totalMsAll / 1000.0;

        // ---- capture-artifact (trace-gap) rejection -------------------------------------------------
        // A PresentMon ETW trace can briefly drop — especially while its real-time session is still
        // spinning up over a short capture window — and the gap surfaces as ONE absurd frame time.
        // A single such value dominates the time-weighted 1% low (e.g. one 385 ms "frame" → a 2.6 fps
        // "1% low"). These are capture seams, NOT rendered frames. Reject a SMALL number of EXTREME
        // values (above an absolute floor AND a large multiple of the median) — but only when they are
        // rare (≤ artifactMaxFraction of frames). Many moderate spikes are genuine stutter and are NEVER
        // removed (the run then honestly fails the stutter / single-frame-time checks instead).
        double medianAll = Percentile((double[])SortedCopy(ft), 50);
        double ceiling = Math.Max(artifactFloorMs, medianAll * artifactMedianRatio);
        int artifactCount = 0; double artifactMs = 0;
        foreach (var v in ft) if (v > ceiling) { artifactCount++; artifactMs += v; }
        int maxArtifacts = Math.Max(2, (int)(ft.Length * artifactMaxFraction));
        bool exclude = artifactCount > 0 && artifactCount <= maxArtifacts && (ft.Length - artifactCount) >= 30;

        double[] use = ft;
        if (exclude)
        {
            use = ft.Where(v => v <= ceiling).ToArray();
            stats.CaptureArtifactCount = artifactCount;
            stats.CaptureArtifactSeconds = artifactMs / 1000.0;
        }
        // ---------------------------------------------------------------------------------------------

        var sorted = SortedCopy(use); // ascending frame times (rendered frames only)

        double totalMs = 0;
        foreach (var v in use) totalMs += v;
        double meanFt = totalMs / use.Length;
        stats.AvgFrameTimeMs = meanFt;
        stats.AvgFps = meanFt > 0 ? 1000.0 / meanFt : 0;
        stats.MinFps = sorted[^1] > 0 ? 1000.0 / sorted[^1] : 0;   // slowest rendered frame
        stats.MaxFps = sorted[0] > 0 ? 1000.0 / sorted[0] : 0;     // fastest frame

        stats.MedianFrameTimeMs = Percentile(sorted, 50);
        stats.P95FrameTimeMs = Percentile(sorted, 95);
        stats.P99FrameTimeMs = Percentile(sorted, 99);
        stats.P999FrameTimeMs = Percentile(sorted, 99.9);

        // Percentile-based lows (FPS at the Nth percentile frame time).
        stats.P1LowFpsPercentile = stats.P99FrameTimeMs > 0 ? 1000.0 / stats.P99FrameTimeMs : 0;
        stats.P01LowFpsPercentile = stats.P999FrameTimeMs > 0 ? 1000.0 / stats.P999FrameTimeMs : 0;

        // Time-weighted lows (headline). Slowest X% of total time.
        stats.P1LowFps = TimeWeightedLowFps(sorted, 0.01);
        stats.P01LowFps = TimeWeightedLowFps(sorted, 0.001);

        // Stutter: fraction of frames whose frame time exceeds 2x the mean.
        stats.StutterPct = StutterFraction(use, meanFt) * 100.0;

        // Cadence-robust stutter for frame-generated captures (the validator gates on THIS instead of
        // StutterPct when the run is flagged frame-gen-on — see RunValidator). Computed from the same
        // artifact-excluded series so the two are directly comparable.
        stats.FrameGenStutterPct = FrameGenAwareStutterFraction(use) * 100.0;

        // Standard deviation of frame time (consistency).
        double sumSq = 0;
        foreach (var v in use) sumSq += (v - meanFt) * (v - meanFt);
        stats.FrameTimeStdDevMs = Math.Sqrt(sumSq / use.Length);

        return stats;
    }

    private static double[] SortedCopy(double[] src)
    {
        var c = (double[])src.Clone();
        Array.Sort(c);
        return c;
    }

    /// <summary>
    /// Average FPS over the slowest <paramref name="fraction"/> of total capture time.
    /// Frames are ordered slowest-first; we accumulate their durations until we have
    /// covered fraction*totalTime, then return 1000 / (mean frame time of that window).
    /// </summary>
    public static double TimeWeightedLowFps(double[] sortedAscFrameTimes, double fraction)
    {
        if (sortedAscFrameTimes.Length == 0) return 0;
        double total = 0;
        foreach (var v in sortedAscFrameTimes) total += v;
        double budget = total * fraction;

        double acc = 0, accFrames = 0;
        // walk from the slowest (end of ascending array) downward
        for (int i = sortedAscFrameTimes.Length - 1; i >= 0; i--)
        {
            double v = sortedAscFrameTimes[i];
            acc += v;
            accFrames++;
            if (acc >= budget) break;
        }
        if (accFrames == 0) return 0;
        double meanWindowFt = acc / accFrames;
        return meanWindowFt > 0 ? 1000.0 / meanWindowFt : 0;
    }

    /// <summary>Linear-interpolation percentile over an ascending-sorted array. p in [0,100].</summary>
    public static double Percentile(double[] sortedAsc, double p)
    {
        int n = sortedAsc.Length;
        if (n == 0) return 0;
        if (n == 1) return sortedAsc[0];
        double rank = (p / 100.0) * (n - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        if (lo < 0) lo = 0;
        if (hi >= n) hi = n - 1;
        double frac = rank - lo;
        return sortedAsc[lo] + (sortedAsc[hi] - sortedAsc[lo]) * frac;
    }

    private static double StutterFraction(double[] frameTimes, double meanFt)
    {
        if (frameTimes.Length == 0 || meanFt <= 0) return 0;
        double threshold = 2.0 * meanFt;
        int stutters = 0;
        foreach (var v in frameTimes) if (v > threshold) stutters++;
        return (double)stutters / frameTimes.Length;
    }

    private static double GlobalStutterFraction(double[] ft)
    {
        if (ft.Length == 0) return 0;
        double mean = 0; foreach (var v in ft) mean += v; mean /= ft.Length;
        return StutterFraction(ft, mean);
    }

    /// <summary>
    /// Cadence-robust stutter fraction for frame-GENERATED captures. Frame generation presents an
    /// alternating rendered/generated cadence, so the frame-time series is BIMODAL: the interval to a
    /// generated frame and the interval to the next rendered frame differ systematically. Measured against
    /// the GLOBAL mean, the longer sub-interval structurally crosses 2x-mean for a large, FIXED fraction of
    /// presents — the naive <see cref="StutterFraction"/> then reads ~25-50% "stutter" on a perfectly smooth
    /// FG run (observed live: Cyberpunk FG-2x, 138 fps, 0.2% game-reported cross-check, yet 25% global
    /// stutter → the run was wrongly rejected). This de-interleaves the series into N phase sub-streams
    /// (index % N) so each sub-stream contains only same-phase presents, then counts a frame as stutter only
    /// if it exceeds 2x the mean of ITS OWN phase — the structural bimodal cadence cancels, while a GENUINE
    /// hitch (which delays a whole cadence period: the rendered frame AND its generated neighbours) still
    /// stands out within its phase and is caught.
    ///
    /// The cadence period N is DETECTED from the data (the stride in {2,3,4} — covering FG 2x/3x/4x — that
    /// best explains the frame-time variance, preferring the fundamental period over a multiple), so no
    /// knowledge of the exact multiplier is needed. If no clean periodic cadence is present (the variation
    /// isn't structural — e.g. a genuinely erratic capture), it FALLS BACK to the global metric, so a broken
    /// FG capture is still judged normally rather than blanket-passed. For a non-FG run this method is only
    /// ever computed, never gated on (the validator uses it solely when the run is flagged frame-gen-on).
    /// </summary>
    public static double FrameGenAwareStutterFraction(double[] frameTimes)
    {
        int n = frameTimes.Length;
        if (n < 12) return GlobalStutterFraction(frameTimes);   // too short to de-interleave meaningfully

        double globalMean = 0; foreach (var v in frameTimes) globalMean += v; globalMean /= n;
        double globalVar = 0; foreach (var v in frameTimes) globalVar += (v - globalMean) * (v - globalMean);
        globalVar /= n;
        if (globalVar <= 0) return 0;   // dead-flat series — no stutter and nothing to de-interleave

        // Detect the cadence: the stride whose phase-split best explains the variance. By the law of total
        // variance the within-phase variance can only shrink as phases are added, so we compare the FRACTION
        // explained and require a larger stride to be MEANINGFULLY better (+0.02) before preferring it — that
        // keeps a true 2x cadence from being mis-read as its 4x multiple.
        int bestStride = 0; double bestStructure = 0;
        foreach (int s in new[] { 2, 3, 4 })
        {
            if (s * 3 > n) continue;   // need >=3 samples per phase for a stable phase mean
            var sum = new double[s]; var cnt = new int[s];
            for (int i = 0; i < n; i++) { sum[i % s] += frameTimes[i]; cnt[i % s]++; }
            var mean = new double[s];
            for (int p = 0; p < s; p++) mean[p] = cnt[p] > 0 ? sum[p] / cnt[p] : 0;
            double within = 0;
            for (int i = 0; i < n; i++) { double d = frameTimes[i] - mean[i % s]; within += d * d; }
            within /= n;
            double structure = 1.0 - within / globalVar;   // fraction of variance explained by an s-phase cadence
            if (structure > bestStructure + 0.02) { bestStructure = structure; bestStride = s; }
        }

        // Apply the correction only when a clear cadence explains a substantial part of the variation.
        // Live RTSS MFG captures contain ordinary render-time variation inside the long phase, so requiring
        // 50% explained variance rejected clean 3X cadence captures. Live repeats measured 0.24-0.49
        // structure while their phase means still showed the unmistakable systematic 3X shape; falling back
        // reported the structural one-in-three long phase as ~31% global "stutter". Random/non-periodic
        // spread explains approximately zero phase variance, so 0.20 still rejects incidental alignment while
        // admitting the repeatedly observed noisy RTSS cadence.
        const double structureThreshold = 0.20;
        if (bestStride == 0 || bestStructure < structureThreshold)
            return GlobalStutterFraction(frameTimes);

        int stride = bestStride;
        var psum = new double[stride]; var pcnt = new int[stride];
        for (int i = 0; i < n; i++) { psum[i % stride] += frameTimes[i]; pcnt[i % stride]++; }
        int stutters = 0;
        for (int i = 0; i < n; i++)
        {
            int p = i % stride;
            double m = pcnt[p] > 0 ? psum[p] / pcnt[p] : 0;
            if (m > 0 && frameTimes[i] > 2.0 * m) stutters++;
        }
        return (double)stutters / n;
    }

    // ---- Generic helpers over nullable telemetry/power series ----

    public static double? Avg(IEnumerable<double?> values)
    {
        double sum = 0; int n = 0;
        foreach (var v in values) if (v is double d && !double.IsNaN(d)) { sum += d; n++; }
        return n > 0 ? sum / n : null;
    }

    public static double? Max(IEnumerable<double?> values)
    {
        double? best = null;
        foreach (var v in values) if (v is double d && !double.IsNaN(d) && (best is null || d > best)) best = d;
        return best;
    }

    public static double? Min(IEnumerable<double?> values)
    {
        double? best = null;
        foreach (var v in values) if (v is double d && !double.IsNaN(d) && (best is null || d < best)) best = d;
        return best;
    }

    /// <summary>Population standard deviation of non-null values.</summary>
    public static double? StdDev(IEnumerable<double?> values)
    {
        var list = values.Where(v => v is double d && !double.IsNaN(d)).Select(v => v!.Value).ToList();
        if (list.Count == 0) return null;
        double mean = list.Average();
        double sumSq = list.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sumSq / list.Count);
    }

    /// <summary>Coefficient of variation (stddev/mean) as a percentage — the run-to-run variance metric.</summary>
    public static double? CoefficientOfVariationPct(IEnumerable<double> values)
    {
        var list = values.Where(v => !double.IsNaN(v)).ToList();
        if (list.Count < 2) return 0;
        double mean = list.Average();
        if (Math.Abs(mean) < 1e-9) return null;
        double sumSq = list.Sum(v => (v - mean) * (v - mean));
        double sd = Math.Sqrt(sumSq / list.Count);
        return sd / mean * 100.0;
    }
}
