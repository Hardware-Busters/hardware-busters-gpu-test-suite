using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Models;
using GpuSuite.Measurement;

namespace GpuSuite.Engine.Scenes;

/// <summary>
/// Warm-up engine (Milestone 4). Runs BEFORE the first measured run of a cell: renders for a configurable
/// duration (frames discarded), optionally waits for shader compilation, then — the key part — gates the
/// official measurement on frame-time STABILIZATION, so the recorded numbers are steady-state rather than
/// contaminated by cold-cache / shader-compile spikes. All warm-up frames are discarded.
///
/// It observes a throwaway frame-capture probe; the stabilization gate polls the probe's newest frame time
/// into a sliding window and proceeds once the coefficient of variation stays at/below the threshold for the
/// configured window (or the max wait is hit). Opt-in via settings (disabled by default).
/// </summary>
public sealed class WarmupEngine
{
    private readonly RunLogger _log;
    public WarmupEngine(RunLogger log) => _log = log;

    public async Task WarmUpAsync(WarmupConfig cfg, ProviderSet providers, FrameCaptureTarget target, CancellationToken ct)
    {
        if (!cfg.Enabled) return;

        _log.Info("Warmup", "Warm-up engine: " +
            $"render {cfg.DurationSeconds:0}s" +
            (cfg.ShaderCompileWaitSeconds > 0 ? $" + shader-compile wait {cfg.ShaderCompileWaitSeconds:0}s" : "") +
            (cfg.CacheWarmup ? " + cache warm" : "") +
            (cfg.WaitForStabilization ? $" + stabilize to ≤{cfg.StabilizationVariancePct:0}% CoV" : "") +
            " (all warm-up frames discarded).");

        await using var probe = await providers.Frames.StartAsync(target, ct).ConfigureAwait(false);

        if (cfg.DurationSeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(cfg.DurationSeconds), ct).ConfigureAwait(false);

        if (cfg.ShaderCompileWaitSeconds > 0)
        {
            _log.Info("Warmup", $"Waiting {cfg.ShaderCompileWaitSeconds:0}s for shader compilation to finish.");
            await Task.Delay(TimeSpan.FromSeconds(cfg.ShaderCompileWaitSeconds), ct).ConfigureAwait(false);
        }

        if (cfg.WaitForStabilization)
            await WaitForStabilizationAsync(probe, cfg, ct).ConfigureAwait(false);

        var discarded = await probe.StopAsync().ConfigureAwait(false);
        _log.Info("Warmup", $"Warm-up complete — discarded {discarded.Count} warm-up frame(s); starting official measurement (steady-state).");
    }

    private async Task WaitForStabilizationAsync(ISampleSession<FrameSample> probe, WarmupConfig cfg, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        DateTime? stableSince = null;
        var window = new Queue<double>();
        const int windowSize = 40;
        double lastTimeSec = -1;

        while ((DateTime.UtcNow - start).TotalSeconds < cfg.MaxStabilizationWaitSeconds)
        {
            await Task.Delay(250, ct).ConfigureAwait(false);

            var latest = probe.Latest;
            if (latest is { FrameTimeMs: > 0 } && Math.Abs(latest.TimeSec - lastTimeSec) > 1e-6)
            {
                lastTimeSec = latest.TimeSec;
                window.Enqueue(latest.FrameTimeMs);
                while (window.Count > windowSize) window.Dequeue();
            }

            if (window.Count < 10) continue;   // not enough fresh frames yet

            double cov = CoefficientOfVariation(window);
            if (cov <= cfg.StabilizationVariancePct)
            {
                stableSince ??= DateTime.UtcNow;
                if ((DateTime.UtcNow - stableSince.Value).TotalSeconds >= cfg.StabilizationWindowSeconds)
                {
                    _log.Info("Warmup", $"Frame times stabilized (CoV {cov:0.0}% ≤ {cfg.StabilizationVariancePct:0}% held {cfg.StabilizationWindowSeconds:0}s) — warm.");
                    return;
                }
            }
            else stableSince = null;
        }
        _log.Warn("Warmup", $"Stabilization not reached within {cfg.MaxStabilizationWaitSeconds:0}s — proceeding to measure anyway.");
    }

    private static double CoefficientOfVariation(IEnumerable<double> xs)
    {
        var a = xs.ToArray();
        if (a.Length < 2) return 100;
        double mean = a.Average();
        if (mean <= 0) return 100;
        double variance = a.Select(x => (x - mean) * (x - mean)).Sum() / a.Length;
        return Math.Sqrt(variance) / mean * 100.0;
    }
}
