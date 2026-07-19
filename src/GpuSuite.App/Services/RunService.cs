using System.IO;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Io;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Aggregation;
using GpuSuite.Engine.Orchestration;
using GpuSuite.Engine.Validation;
using GpuSuite.Measurement;
using GpuSuite.Reporting;

namespace GpuSuite.App.Services;

/// <summary>Result of a sweep: cancelled flag, the suite result (when completed), and output paths.</summary>
public sealed record RunOutcome(bool Cancelled, SuiteResult? Suite, string ReportPath, string LogPath);

/// <summary>
/// Drives a benchmark sweep, mirroring the CLI `run` flow in Program.cs: build the engine
/// (MeasurementFactory + RunValidator + ResultAggregator + TestOrchestrator), run game × scene ×
/// resolution × enabled-variant, then generate the HTML report. Log entries stream live via the
/// RunLogger.EntryLogged hook. This actually LAUNCHES the selected games — it is meant to run on the
/// bench (Test PC).
/// </summary>
public sealed class RunService
{
    private readonly Workspace _ws;
    public RunService(Workspace ws) => _ws = ws;

    public async Task<RunOutcome> RunAsync(
        IReadOnlyList<GameProfile> games,
        IReadOnlyList<Resolution> resolutions,
        int repeats,
        Action<TimelineEntry> onLog,
        Action<BenchmarkProgress> onProgress,
        CancellationToken ct,
        Dictionary<string, List<string>>? variantSelections = null,
        int? exactRepeatsOverride = null)
    {
        var cfg = _ws.Config;
        int? previousRepeatsOverride = cfg.RepeatsOverride;
        cfg.RepeatsOverride = exactRepeatsOverride;
        // Per-game variant picks for THIS run (the Run tab's ticked boxes / a loaded plan). Refreshed on
        // every invocation — null clears any previous run's picks so they can't leak into the next sweep.
        cfg.GameVariantSelections = variantSelections ?? new();
        // The run-console "repeats" is a per-run BASELINE, not a hard override: take the MAX of it and each game's
        // own repeats, so a game configured for MORE (in its profile / Game detail) keeps its higher count while
        // every game still runs at least the baseline. The orchestrator additionally floors this at 3.
        if (exactRepeatsOverride is null && repeats > 0)
            foreach (var g in games) g.Repeats = Math.Max(repeats, g.Repeats);

        Directory.CreateDirectory(cfg.ResultsRoot);
        string logPath = cfg.DebugFullRunLog
            ? Path.Combine(cfg.ResultsRoot, "Logs", $"suite_full_{DateTime.Now:yyyyMMdd_HHmmss}.log")
            : Path.Combine(cfg.ResultsRoot, $"suite_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        using var log = new RunLogger(logPath, echoToConsole: false,
            minimumFileLevel: cfg.DebugFullRunLog ? LogLevel.Trace : LogLevel.Info);
        log.Info("Suite", cfg.DebugFullRunLog
            ? "Full debug logging ENABLED — trace-level timeline will be saved to " + logPath
            : "Standard run logging enabled — turn on Settings > Full debug run log for every trace/probe detail.");
        log.EntryLogged += onLog;
        try
        {
            using var factory = new MeasurementFactory(cfg);
            var validator = new RunValidator(cfg.Validation);
            var aggregator = new ResultAggregator();
            var orchestrator = new TestOrchestrator(cfg, factory, validator, aggregator, log, onProgress);

            SuiteResult suite;
            try
            {
                // Desktop runs get the same crash-safe per-cell checkpoints as the autonomous CLI.
                // ResumeMode.Auto resumes only when the plan + methodology fingerprint still match.
                suite = await orchestrator.RunSuiteAsync(
                    games, resolutions, ct,
                    enableCheckpoints: true,
                    resumeMode: ResumeMode.Auto).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new RunOutcome(true, null, "", logPath);
            }

            var report = new HtmlReportGenerator
            {
                GameNames = games.ToDictionary(g => g.Id, g => g.Name),
                SceneNames = games.SelectMany(g => g.Scenes).GroupBy(s => s.Id).ToDictionary(gr => gr.Key, gr => gr.First().Name)
            };
            suite.System.SuiteVersion = GpuSuite.BuildInfo.Version;
            var paths = new RunPaths(cfg.ResultsRoot, suite.GpuName);
            report.Save(paths.ReportPath, suite);
            return new RunOutcome(false, suite, paths.ReportPath, logPath);
        }
        finally
        {
            cfg.RepeatsOverride = previousRepeatsOverride;
            log.EntryLogged -= onLog;
        }
    }
}
