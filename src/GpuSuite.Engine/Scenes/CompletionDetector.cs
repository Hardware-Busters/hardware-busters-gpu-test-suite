using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Models;

namespace GpuSuite.Engine.Scenes;

/// <summary>Outcome of detecting a benchmark's start and finish within a capture.</summary>
public sealed class CompletionResult
{
    public bool StartDetected { get; set; }
    public double StartOffsetSec { get; set; }
    public bool FinishDetected { get; set; }
    public double FinishOffsetSec { get; set; }
    /// <summary>The game's own reported avg FPS (from its result file), if parsed — for cross-check.</summary>
    public double? CrossCheckFps { get; set; }
    /// <summary>Game-reported render width/height (from its result file), if parsed — to VERIFY the
    /// benchmark actually rendered at the requested resolution.</summary>
    public int? RenderWidth { get; set; }
    public int? RenderHeight { get; set; }
    public string Mode { get; set; } = "";
    public List<string> Notes { get; } = new();
    public double WindowSeconds => Math.Max(0, FinishOffsetSec - StartOffsetSec);
}

/// <summary>
/// Detects the start and finish of a scene WITHOUT relying on a fixed sleep:
///   * log-file  — watch the game's benchmark log for start/finish markers, parse its result.
///   * activity  — derive the window from PresentMon frame activity (first frames ⇒ start;
///                 frames stalled for ActivityIdleSeconds ⇒ finish). Used as a fallback.
///   * duration  — fixed window (only for explicit FixedWindow scenes).
/// Min-valid-duration and max-timeout are always enforced.
/// </summary>
public sealed class CompletionDetector
{
    private readonly RunLogger _log;
    private const int PollMs = 200;
    public CompletionDetector(RunLogger log) => _log = log;

    public async Task<CompletionResult> DetectAsync(
        CompletionDetection c, double captureSeconds, DateTime captureStartUtc,
        Func<int> liveFrameCount, CancellationToken ct, int frameBaseline = 0)
    {
        string method = (c.Method ?? "duration").ToLowerInvariant();
        string? logPath = string.IsNullOrWhiteSpace(c.LogFilePath) ? null : Environment.ExpandEnvironmentVariables(c.LogFilePath);

        if (method == "log-file" && logPath is not null)
        {
            var r = await DetectViaLogAsync(c, logPath, captureStartUtc, ct).ConfigureAwait(false);
            if (r.StartDetected) return r;
            if (c.FallbackToActivity)
            {
                _log.Warn("Completion", "Log markers not found in time — falling back to PresentMon activity.");
                var act = await DetectViaActivityAsync(c, captureStartUtc, liveFrameCount, frameBaseline, ct).ConfigureAwait(false);
                act.Notes.Add("fell back from log-file");
                return act;
            }
            r.Notes.Add("log markers not found; activity fallback disabled");
            return r;
        }

        if (method == "result-file")
            return await DetectViaResultFileAsync(c, captureStartUtc, liveFrameCount, frameBaseline, ct).ConfigureAwait(false);

        if (method == "activity")
            return await DetectViaActivityAsync(c, captureStartUtc, liveFrameCount, frameBaseline, ct).ConfigureAwait(false);

        return await DetectViaDurationAsync(captureSeconds, captureStartUtc, ct).ConfigureAwait(false);
    }

    private double Elapsed(DateTime startUtc) => (DateTime.UtcNow - startUtc).TotalSeconds;

    private async Task<CompletionResult> DetectViaLogAsync(CompletionDetection c, string logPath, DateTime startUtc, CancellationToken ct)
    {
        var res = new CompletionResult { Mode = "log-file" };
        _log.Info("Completion", $"Watching benchmark log: {logPath}");
        // Timeout budget from detection start (post-nav), not captureStartUtc — see DetectViaActivityAsync.
        var detectStartUtc = DateTime.UtcNow;

        // Phase 1: start marker (or implicit start at capture begin if no StartPattern).
        if (string.IsNullOrEmpty(c.StartPattern)) { res.StartDetected = true; res.StartOffsetSec = 0; }
        else
        {
            while (Elapsed(detectStartUtc) < c.MaxTimeoutSeconds)
            {
                ct.ThrowIfCancellationRequested();
                if (FileMatches(logPath, c.StartPattern))
                {
                    res.StartDetected = true; res.StartOffsetSec = Elapsed(startUtc);
                    _log.Info("Completion", $"Benchmark START detected at +{res.StartOffsetSec:0.0}s.");
                    break;
                }
                await Task.Delay(PollMs, ct).ConfigureAwait(false);
            }
            if (!res.StartDetected) { res.Notes.Add("start marker timeout"); return res; }
        }

        // Phase 2: finish marker, gated by MinValidSeconds.
        while (Elapsed(detectStartUtc) < c.MaxTimeoutSeconds)
        {
            ct.ThrowIfCancellationRequested();
            double since = Elapsed(startUtc) - res.StartOffsetSec;
            if (!string.IsNullOrEmpty(c.FinishPattern) && since >= c.MinValidSeconds && FileMatches(logPath, c.FinishPattern))
            {
                res.FinishDetected = true; res.FinishOffsetSec = Elapsed(startUtc);
                _log.Info("Completion", $"Benchmark FINISH detected at +{res.FinishOffsetSec:0.0}s (window {res.WindowSeconds:0.0}s).");
                break;
            }
            await Task.Delay(PollMs, ct).ConfigureAwait(false);
        }
        if (!res.FinishDetected) { res.FinishOffsetSec = Elapsed(startUtc); res.Notes.Add("finish marker timeout"); }

        res.CrossCheckFps = ParseResultFps(c, logPath);
        return res;
    }

    private async Task<CompletionResult> DetectViaActivityAsync(CompletionDetection c, DateTime startUtc, Func<int> liveFrameCount, int frameBaseline, CancellationToken ct)
    {
        var res = new CompletionResult { Mode = "activity" };
        const int startThreshold = 20;
        // The MaxTimeoutSeconds budget must run from when DETECTION begins (now) — NOT from captureStartUtc,
        // which is stamped BEFORE the menu-navigation bot runs. A slow cold-launch nav (Forza's title->HOME
        // shader pre-compile can take >2min) would otherwise consume the entire timeout before the benchmark
        // even starts, so the activity window is never found ("start not detected", window 0.0s — observed
        // live on the RTX 5070 where nav took ~135s vs the 110s cap). Offsets (StartOffsetSec/FinishOffsetSec)
        // stay relative to startUtc=captureStartUtc so they remain aligned with frame TimeSec for window trimming.
        var detectStartUtc = DateTime.UtcNow;

        // Start: frames begin arriving (beyond any pre-benchmark/menu-navigation frames).
        while (Elapsed(detectStartUtc) < c.MaxTimeoutSeconds)
        {
            ct.ThrowIfCancellationRequested();
            if (liveFrameCount() - frameBaseline >= startThreshold)
            {
                res.StartDetected = true; res.StartOffsetSec = Elapsed(startUtc);
                _log.Info("Completion", $"Activity START at +{res.StartOffsetSec:0.0}s ({liveFrameCount() - frameBaseline} benchmark frames).");
                break;
            }
            await Task.Delay(PollMs, ct).ConfigureAwait(false);
        }
        if (!res.StartDetected) { res.Notes.Add("no frame activity — start not detected"); return res; }

        // Finish: frames stall for ActivityIdleSeconds (after min duration), or timeout.
        int lastCount = liveFrameCount();
        double lastChange = Elapsed(startUtc);
        while (Elapsed(detectStartUtc) < c.MaxTimeoutSeconds)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(PollMs, ct).ConfigureAwait(false);
            int now = liveFrameCount();
            double t = Elapsed(startUtc);
            if (now > lastCount) { lastCount = now; lastChange = t; }
            double sinceStart = lastChange - res.StartOffsetSec;
            if ((t - lastChange) >= c.ActivityIdleSeconds && sinceStart >= c.MinValidSeconds)
            {
                res.FinishDetected = true; res.FinishOffsetSec = lastChange;
                _log.Info("Completion", $"Activity FINISH at +{res.FinishOffsetSec:0.0}s (frames stalled {c.ActivityIdleSeconds:0.#}s; window {res.WindowSeconds:0.0}s).");
                break;
            }
        }
        if (!res.FinishDetected)
        {
            res.FinishOffsetSec = Elapsed(startUtc);
            // Frames kept arriving right up to MaxTimeoutSeconds with no idle gap. For a fixed-length,
            // continuously-rendering benchmark that writes NO result file (e.g. DOOM: The Dark Ages's
            // Benchmark Mode, whose flythrough renders straight into an on-screen results screen), reaching
            // the cap with a full window of frames IS the measurement — not a failed finish. Treat it as a
            // valid finish once the benchmark actually started and the window is at least MinValidSeconds.
            if (res.StartDetected && res.WindowSeconds >= c.MinValidSeconds)
            {
                res.FinishDetected = true;
                res.Notes.Add($"window capped at maxTimeout ({c.MaxTimeoutSeconds:0}s); continuous-render benchmark, no idle/result-file — treated as a valid fixed-length window");
                _log.Info("Completion", $"Activity window CAPPED at maxTimeout (+{res.FinishOffsetSec:0.0}s, window {res.WindowSeconds:0.0}s) — frames never idled (continuous-render benchmark); treating as a valid fixed-length measurement.");
            }
            else res.Notes.Add("activity finish timeout");
        }
        return res;
    }

    /// <summary>
    /// Built-in benchmarks that write a fresh, timestamped result file on completion (e.g. Cyberpunk
    /// 2077's benchmarkResults folder). START is taken from frame activity so the trimmed window
    /// excludes pre-benchmark loading; FINISH is the first NEW file appearing in the watched directory
    /// (after the minimum valid window) — a clean, filename-independent end marker. The game's own
    /// reported avg FPS is parsed from that file as a cross-check.
    /// </summary>
    private async Task<CompletionResult> DetectViaResultFileAsync(CompletionDetection c, DateTime startUtc, Func<int> liveFrameCount, int frameBaseline, CancellationToken ct)
    {
        var res = new CompletionResult { Mode = "result-file" };
        string? dir = string.IsNullOrWhiteSpace(c.ResultDirPath) ? null : Environment.ExpandEnvironmentVariables(c.ResultDirPath);
        string glob = string.IsNullOrWhiteSpace(c.ResultFileGlob) ? "*.*" : c.ResultFileGlob!;
        if (dir is null) { res.Notes.Add("no resultDirPath configured"); return res; }
        _log.Info("Completion", $"Watching for a new result file in: {dir} ({glob})");

        // Only react to a file created AFTER capture began.
        var seen = SnapshotFiles(dir, glob);
        const int startThreshold = 20;
        // Timeout budget from detection start (post-nav), not captureStartUtc — see DetectViaActivityAsync.
        var detectStartUtc = DateTime.UtcNow;

        // START: benchmark frames begin arriving (beyond any pre-benchmark/menu frames), or, for very
        // short benches, a result file appears at once.
        while (Elapsed(detectStartUtc) < c.MaxTimeoutSeconds)
        {
            ct.ThrowIfCancellationRequested();
            if (liveFrameCount() - frameBaseline >= startThreshold)
            {
                res.StartDetected = true; res.StartOffsetSec = Elapsed(startUtc);
                _log.Info("Completion", $"Benchmark START at +{res.StartOffsetSec:0.0}s ({liveFrameCount() - frameBaseline} benchmark frames).");
                break;
            }
            if (NewestNewFile(dir, glob, seen) is not null) { res.StartDetected = true; res.StartOffsetSec = 0; break; }
            await Task.Delay(PollMs, ct).ConfigureAwait(false);
        }
        if (!res.StartDetected) { res.Notes.Add("no frame activity or result file before timeout"); return res; }

        // FINISH: whichever comes first — a NEW result file (the preferred, clean marker) OR, when
        // FallbackToActivity is on, frames stalling for ActivityIdleSeconds (benchmark ended / process
        // exited / no result file written). Both are gated by MinValidSeconds. Watching them together
        // (rather than waiting the full timeout for the file first) keeps the window tight and robust.
        string? finished = null;
        int lastCount = liveFrameCount();
        double lastChange = res.StartOffsetSec;
        while (Elapsed(detectStartUtc) < c.MaxTimeoutSeconds)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(PollMs, ct).ConfigureAwait(false);
            double t = Elapsed(startUtc);
            double since = t - res.StartOffsetSec;

            var nf = NewestNewFile(dir, glob, seen);
            if (nf is not null && since >= c.MinValidSeconds && await IsStableAsync(nf, ct).ConfigureAwait(false))
            {
                finished = nf; res.FinishDetected = true; res.FinishOffsetSec = Elapsed(startUtc);
                _log.Info("Completion", $"Benchmark FINISH — result file '{Path.GetFileName(nf)}' at +{res.FinishOffsetSec:0.0}s (window {res.WindowSeconds:0.0}s).");
                break;
            }

            int now = liveFrameCount();
            if (now > lastCount) { lastCount = now; lastChange = t; }
            if (c.FallbackToActivity && (t - lastChange) >= c.ActivityIdleSeconds && since >= c.MinValidSeconds)
            {
                // Rendering has ended — fix the measured window at the last frame. Many built-in
                // benchmarks (Cyberpunk) write their result file a while AFTER rendering stops, with
                // the process still alive, so optionally wait up to ResultGraceSeconds to capture it
                // for the cross-check WITHOUT extending the measured window.
                res.FinishDetected = true; res.FinishOffsetSec = lastChange;
                _log.Info("Completion", $"Benchmark render END (frame stall) at +{res.FinishOffsetSec:0.0}s (window {res.WindowSeconds:0.0}s).");
                if (c.ResultGraceSeconds > 0)
                {
                    _log.Info("Completion", $"Waiting up to {c.ResultGraceSeconds:0}s for the game's result file (window already fixed)...");
                    double graceDeadline = Elapsed(startUtc) + c.ResultGraceSeconds;
                    while (Elapsed(startUtc) < graceDeadline)
                    {
                        ct.ThrowIfCancellationRequested();
                        var gf = NewestNewFile(dir, glob, seen);
                        if (gf is not null && await IsStableAsync(gf, ct).ConfigureAwait(false)) { finished = gf; break; }
                        await Task.Delay(PollMs, ct).ConfigureAwait(false);
                    }
                    res.Notes.Add(finished is null ? "result file not written within grace window" : $"result file captured: {Path.GetFileName(finished)}");
                }
                break;
            }
        }
        if (!res.FinishDetected) { res.FinishOffsetSec = Elapsed(startUtc); res.Notes.Add("no result file / no frame stall before timeout"); return res; }

        if (finished is not null)
        {
            if (!string.IsNullOrEmpty(c.ResultFpsPattern)) res.CrossCheckFps = ParseFpsFromFile(finished, c.ResultFpsPattern!);
            if (!string.IsNullOrEmpty(c.ResultWidthPattern)) res.RenderWidth = ParseIntFromFile(finished, c.ResultWidthPattern!);
            if (!string.IsNullOrEmpty(c.ResultHeightPattern)) res.RenderHeight = ParseIntFromFile(finished, c.ResultHeightPattern!);
            if (res.RenderWidth is int rw && res.RenderHeight is int rh)
                _log.Info("Completion", $"Game-reported render resolution (cross-check): {rw}x{rh}.");
        }
        return res;
    }

    private int? ParseIntFromFile(string path, string pattern)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var m = Regex.Match(File.ReadAllText(path), pattern, RegexOptions.IgnoreCase);
            if (m.Success && m.Groups.Count > 1 &&
                int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                return v;
        }
        catch { }
        return null;
    }

    // Search recursively: built-in benchmarks often write their result into a fresh per-run
    // SUBFOLDER (e.g. Cyberpunk 2077's benchmarkResults\benchmark_<timestamp>\summary.json).
    private static HashSet<string> SnapshotFiles(string dir, string glob)
    {
        try { return Directory.Exists(dir) ? new HashSet<string>(Directory.GetFiles(dir, glob, SearchOption.AllDirectories), StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase); }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>Newest file under dir (recursively) matching glob that wasn't in the pre-capture snapshot, or null.</summary>
    private static string? NewestNewFile(string dir, string glob, HashSet<string> seen)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            return Directory.GetFiles(dir, glob, SearchOption.AllDirectories)
                .Where(f => !seen.Contains(f))
                .OrderByDescending(f => { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; } })
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Confirm the file size is stable (fully written) before parsing it.</summary>
    private static async Task<bool> IsStableAsync(string path, CancellationToken ct)
    {
        try
        {
            long a = new FileInfo(path).Length;
            await Task.Delay(250, ct).ConfigureAwait(false);
            return a == new FileInfo(path).Length;
        }
        catch { return false; }
    }

    private double? ParseFpsFromFile(string path, string fpsPattern)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var m = Regex.Match(File.ReadAllText(path), fpsPattern, RegexOptions.IgnoreCase);
            if (m.Success && m.Groups.Count > 1 &&
                double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
            {
                _log.Info("Completion", $"Game-reported avg FPS (cross-check): {fps:0.0}");
                return fps;
            }
        }
        catch { }
        return null;
    }

    private async Task<CompletionResult> DetectViaDurationAsync(double captureSeconds, DateTime startUtc, CancellationToken ct)
    {
        var res = new CompletionResult { Mode = "duration", StartDetected = true, StartOffsetSec = 0 };
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.1, captureSeconds)), ct).ConfigureAwait(false);
        res.FinishDetected = true;
        res.FinishOffsetSec = Elapsed(startUtc);
        return res;
    }

    private static bool FileMatches(string path, string pattern)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var text = sr.ReadToEnd();
            return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);
        }
        catch { return false; }
    }

    private double? ParseResultFps(CompletionDetection c, string logPath)
    {
        if (string.IsNullOrEmpty(c.ResultFpsPattern)) return null;
        var path = string.IsNullOrWhiteSpace(c.ResultFilePath) ? logPath : Environment.ExpandEnvironmentVariables(c.ResultFilePath);
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            var m = Regex.Match(text, c.ResultFpsPattern, RegexOptions.IgnoreCase);
            if (m.Success && m.Groups.Count > 1 &&
                double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
            {
                _log.Info("Completion", $"Game-reported avg FPS (cross-check): {fps:0.0}");
                return fps;
            }
        }
        catch { }
        return null;
    }
}
