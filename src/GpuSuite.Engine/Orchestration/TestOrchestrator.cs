using System.Diagnostics;
using GpuSuite.Core.Config;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Io;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Aggregation;
using GpuSuite.Engine.Automation;
using GpuSuite.Engine.Display;
using GpuSuite.Engine.Diagnostics;
using GpuSuite.Engine.Discovery;
using GpuSuite.Engine.Launch;
using GpuSuite.Engine.Scenes;
using GpuSuite.Engine.Validation;
using GpuSuite.Measurement;

namespace GpuSuite.Engine.Orchestration;

/// <summary>Structured, presentation-neutral progress for desktop/CLI hosts.</summary>
public sealed record BenchmarkProgress(
    string GameId, string GameName, string SceneId, string SceneName, string VariantId,
    string ResolutionName, int RepeatIndex, int RepeatTarget, int CellIndex, int TotalCells,
    string Phase, bool CellCompleted);

/// <summary>Signals that an OCR-gated route found an external account/service screen which cannot be repaired
/// by retrying input. The current game is stopped cleanly, while the campaign and its checkpoint continue.</summary>
internal sealed class GameSessionBlockedException : Exception
{
    public GameSessionBlockedException(string message) : base(message) { }
}

/// <summary>
/// (1) Test Orchestrator — the deterministic state machine. For every game × scene ×
/// resolution it runs N repeats (auto-repeating invalid/outlier runs within budget),
/// cools down between runs, cleans up processes, aggregates valid runs, and returns a
/// SuiteResult. Reporting is intentionally NOT referenced here (CLI owns that), keeping
/// the engine free of presentation concerns.
/// </summary>
public sealed class TestOrchestrator
{
    private readonly SuiteConfig _cfg;
    private readonly MeasurementFactory _factory;
    private readonly RunValidator _validator;
    private readonly ResultAggregator _aggregator;
    private readonly RunLogger _log;
    private readonly Action<BenchmarkProgress>? _progress;

    public TestOrchestrator(SuiteConfig cfg, MeasurementFactory factory, RunValidator validator, ResultAggregator aggregator, RunLogger log,
        Action<BenchmarkProgress>? progress = null)
    {
        _cfg = cfg; _factory = factory; _validator = validator; _aggregator = aggregator; _log = log; _progress = progress;
    }

    public async Task<SuiteResult> RunSuiteAsync(IReadOnlyList<GameProfile> games, IReadOnlyList<Resolution> resolutions,
        CancellationToken ct, bool enableCheckpoints = false, ResumeMode resumeMode = ResumeMode.Auto)
    {
        // LAZY RTSS (2026-07-02): tell the factory whether this plan actually contains an rtss-provider
        // game. An all-PresentMon plan (the Ratchet-safe RTSS-free group) then never starts RTSS — not for
        // frames and not for the OSD — even when the operator forgets --no-osd. Same per-game rule as
        // gameForcesRtss below.
        bool planUsesRtss = games.Any(GameUsesRtss);
        var probe = _factory.ProbeAll(planUsesRtss);
        foreach (var line in probe) _log.Info("Probe", line);

        var gpuName = _factory.DetectedGpuName;
        var paths = new RunPaths(_cfg.ResultsRoot, gpuName);
        var system = BuildSystemInfo();
        var sceneRunner = new SceneRunner(_cfg, _validator);
        var launcher = new GameLauncher(_log, _cfg.SimulateLaunch, _cfg.AttachToRunning, _cfg.MaxRefreshHz, _cfg.SimulateOnLaunchFailure, _cfg.ProfilesDir);
        var hwValidator = new GpuSuite.Engine.Validation.HardwareValidator(_cfg, _log);   // Milestone 2: pre-game hardware gate
        var aggregates = new List<SceneResolutionAggregate>();
        var outcomes = new List<GameOutcome>();   // per-game roster outcome (passed/failed/skipped) for the final summary

        // Crash-safe checkpoint (Milestone 1): in autonomous mode a RunState is saved after every completed
        // cell so a Windows crash / power loss loses at most the single in-progress benchmark, and a resume
        // continues exactly where it stopped.
        RunStateManager? stateMgr = null;
        if (enableCheckpoints)
        {
            string methodology = RunStateManager.ComputeMethodologyIdentity(
                games, _cfg, _cfg.ProfilesDir, typeof(TestOrchestrator).Assembly.ManifestModule.ModuleVersionId);
            var sig = RunStateManager.ComputePlanSignature(games.Select(g => g.Id), resolutions.Select(r => r.Name),
                _cfg.SelectedVariantIds.Concat(_cfg.GameVariantSelections
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => $"{kv.Key}:{string.Join('+', kv.Value)}")),   // per-game RUN-PLAN picks change the cell set → must change the resume signature
                _cfg.RepeatsPerScene, methodology);
            stateMgr = new RunStateManager(RunStateManager.PathFor(paths), _log);
            stateMgr.Initialize(resumeMode, gpuName, sig, _cfg.Unattended);
        }

        _log.Info("Suite", $"GPU: {gpuName} | Games: {games.Count} | Default resolutions: {string.Join(", ", resolutions.Select(r => r.Name))}");

        int totalCells = games.Sum(game =>
            game.Scenes.Count
            * ResolveGameVariants(game, EffectiveVariantSelection(game)).Count
            * ResolveGameResolutions(game, resolutions).Count);
        int cellIndex = 0;

        // Before any benchmark: auto-select where the menu-nav VISION model runs. Check if the GX10 is active —
        // if so, select it (off-bench); if not, drop to the local GPU. One upfront probe + announcement; the
        // per-game supervisor re-checks, so if the box drops mid-roster the next game falls back automatically.
        // The resolved choice is recorded on the SuiteResult below (report + roster summary traceability).
        string visionComputeLabel = "n/a (no vision nav)";
        if (string.Equals(_cfg.NavSupervisor?.Trim(), "vision", StringComparison.OrdinalIgnoreCase))
        {
            var backend = GpuSuite.Engine.Automation.VisionNavSupervisor.ResolveBackend(_cfg, _log);
            var visSource = backend.Source;
            visionComputeLabel = visSource switch
            {
                GpuSuite.Engine.Automation.VisionNavSupervisor.VisionComputeSource.Gx10 => $"Custom AI (off-bench, {backend.Endpoint})",
                GpuSuite.Engine.Automation.VisionNavSupervisor.VisionComputeSource.CustomLocal => $"Custom local Ollama ({backend.Endpoint})",
                GpuSuite.Engine.Automation.VisionNavSupervisor.VisionComputeSource.OpenAi => $"OpenAI API ({backend.Model})",
                GpuSuite.Engine.Automation.VisionNavSupervisor.VisionComputeSource.Anthropic => $"Anthropic API ({backend.Model})",
                GpuSuite.Engine.Automation.VisionNavSupervisor.VisionComputeSource.LocalCpu => "Local CPU",
                _ => "Local GPU"
            };
            _log.Info("Suite", $"Vision compute → {visionComputeLabel}.");
        }

        try
        {
        foreach (var game in games)
        {
            // Per-game overrides: resolutions and repeats. An explicit CLI --repeats N (RepeatsOverride) is honored
            // EXACTLY — including 1 for fast iteration; otherwise the default/profile value floors at 3 (TPU-style).
            var gameRes = ResolveGameResolutions(game, resolutions);
            if (gameRes.Count == 0)
            {
                string reason = $"none of the requested resolutions [{string.Join(",", resolutions.Select(r => r.Name))}] " +
                    $"are supported by this profile [{string.Join(",", game.SupportedResolutions)}]";
                _log.Warn("Game", $"=== {game.Name} ({game.Id}) === {reason} — skipped honestly.");
                var skip = GameOutcome.Skipped(game.Id, game.Name, reason);
                outcomes.Add(skip);
                stateMgr?.RecordGameOutcome(skip);
                continue;
            }
            int repeats = _cfg.RepeatsOverride is int ovr && ovr >= 1
                ? ovr
                : Math.Max(3, game.Repeats <= 0 ? _cfg.RepeatsPerScene : game.Repeats);

            // Graphics variants ("extra models") to benchmark this game in. A null entry = the implicit
            // profile-default model (game has no variants, or only the default applies).
            var variantSelection = EffectiveVariantSelection(game);
            var variants = ResolveGameVariants(game, variantSelection);
            if (variants.Count == 0)
            {
                _log.Warn("Game", $"=== {game.Name} ({game.Id}) === no matching/enabled variants for selection [{string.Join(",", variantSelection)}] — skipped.");
                var skip = GameOutcome.Skipped(game.Id, game.Name, "no enabled/matching variants for the model selection");
                outcomes.Add(skip);
                stateMgr?.RecordGameOutcome(skip);
                continue;
            }
            stateMgr?.SetCurrentGame(game.Id);
            _log.Info("Game", $"=== {game.Name} ({game.Id}) === repeats={repeats}, res={string.Join(",", gameRes.Select(r => r.Name))}, " +
                $"models={string.Join(",", variants.Select(v => v?.Id ?? "default"))}");

            bool gameUsesRtss = GameUsesRtss(game);
            // A safety prohibition always wins.  In particular, Ratchet is proven unstable when the
            // RTSS hook is injected, so a stale/conflicting lateRtss profile flag must never bypass
            // forbidRtss and silently reintroduce that hook during the measured window.
            bool effectiveLateRtss = game.LateRtss && !game.ForbidRtss;
            bool backendReady = _factory.PrepareFrameBackend(game.FrameProvider, game.ForbidRtss, effectiveLateRtss, out string backendDetail);
            var gameFramePlan = FrameCapturePreflightPlan.Build([game], _cfg.FrameProvider);
            _log.Info("Frames", $"{game.Id}: effective backend {(effectiveLateRtss ? "late RTSS" : gameUsesRtss ? "RTSS" : "PresentMon")} — {backendDetail}");

            // Hardware validation gate (Milestone 2): verify the bench is in a known-good state before this
            // game. A failed FATAL check aborts ONLY this benchmark — classified + skipped — and the roster
            // continues. Skipped for --attach (operator-prepped) and when every cell is already done on resume.
            bool anyCellPending = stateMgr is null || game.Scenes
                .SelectMany(s => variants.SelectMany(v => gameRes.Select(rr => RunStateManager.Cell(game.Id, s.Id, v?.Id, rr.Name))))
                .Any(k => !stateMgr.IsCellDone(k));

            // This is an unconditional fail-closed gate. Do not let the generic validator (which can
            // legitimately accept a different available backend) turn a missing late/forced RTSS request
            // into a PresentMon run. Nothing below this point may launch or create providers for the game.
            if (anyCellPending && TryCreateBackendPrecheckFailure(game, backendReady, backendDetail, out var backendFailure))
            {
                outcomes.Add(backendFailure);
                stateMgr?.RecordGameOutcome(backendFailure);
                _log.Error("Game", $"=== {game.Id} ABORTED — {backendFailure.Reason}; continuing to the next game. ===");
                continue;
            }

            // Long campaigns can run for one or two days. Refresh launcher/account/update readiness immediately
            // before every pending game so a logout or update queued after the initial UI pre-flight is caught at
            // the last safe point. Only known blockers skip the game; inconclusive online checks are logged as
            // warnings and remain visible evidence rather than being mislabeled as ready.
            if (!_cfg.SimulateLaunch && !_cfg.AttachToRunning && anyCellPending)
            {
                foreach (string message in await LauncherSessionPrimer.EnsureRunningAsync([game], ct).ConfigureAwait(false))
                    _log.Info("Preflight", message);
                var currentCatalog = new LauncherDiscovery(_log).DiscoverAll();
                var readiness = new GameReadinessChecker(_cfg.ProfilesDir).CheckOne(game, currentCatalog);
                foreach (var check in readiness.Checks.Where(c => c.Status is CheckStatus.Warn or CheckStatus.Blocker))
                    _log.Warn("Preflight", $"{game.Id} · {check.Name} [{check.Status}]: {check.Detail}");
                if (readiness.BlockerCount > 0)
                {
                    string reason = string.Join("; ", readiness.Checks
                        .Where(c => c.Status == CheckStatus.Blocker)
                        .Select(c => $"{c.Name}: {c.Detail}"));
                    var fail = new GameOutcome
                    {
                        GameId = game.Id, Name = game.Name, Status = GameStatus.Failed,
                        FailureClass = FailureClass.LauncherNotReady,
                        Reason = "just-in-time pre-flight failed — " + reason
                    };
                    outcomes.Add(fail);
                    stateMgr?.RecordGameOutcome(fail);
                    _log.Error("Game", $"=== {game.Id} ABORTED — launcher/update/login pre-flight failed; continuing to the next game. ===");
                    continue;
                }
            }
            if (_cfg.HardwareValidation.Enabled && !_cfg.AttachToRunning && anyCellPending)
            {
                var hw = hwValidator.Validate(_factory, gameFramePlan, backendReady);
                hwValidator.LogResult(game.Id, hw);
                if (!hw.Ok)
                {
                    var fail = new GameOutcome
                    {
                        GameId = game.Id, Name = game.Name, Status = GameStatus.Failed,
                        FailureClass = FailureClass.HardwarePrecheck,
                        Reason = "hardware validation failed — " + hw.FailureReason
                    };
                    outcomes.Add(fail);
                    stateMgr?.RecordGameOutcome(fail);
                    _log.Error("Game", $"=== {game.Id} ABORTED — hardware validation failed ({hw.FailureReason}); continuing to the next game. ===");
                    continue;
                }
            }

            // Per-game crash/hang isolation: if THIS game crashes (its process dies) or hangs (nav never
            // reaches the captured display), skip it and let the unattended campaign continue — instead of one
            // bad game cancelling the whole sweep. A per-game token (linked to the campaign token) is cancelled
            // by a hang-watchdog or a thrown crash; the campaign token (physical ESC) still stops EVERYTHING.
            var gameCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            int gameHangMs = Math.Max(0, _cfg.InputHangReleaseSeconds) * 1000;
            // A game declaring a LONGER cold-launch grace (anti-cheat init, shader pre-compile) must not be
            // hang-killed inside that window — widen the watchdog to the profile's StartupGraceSeconds.
            // 0 stays 0 (watchdog disabled by config is never re-enabled by a profile).
            if (gameHangMs > 0) gameHangMs = Math.Max(gameHangMs, game.StartupGraceSeconds * 1000);
            GpuSuite.Core.RunHeartbeat.Ping();   // fresh progress beacon at game start (don't carry the inter-game gap)
            var hangWatch = gameHangMs > 0 ? StartGameHangWatchdog(game, gameCts, ct, gameHangMs) : Task.CompletedTask;
            int aggsBefore = aggregates.Count;             // slice THIS game's aggregates for the outcome
            string? caughtReason = null;
            bool watchdogCancel = false, unexpectedError = false;
            try
            {
                foreach (var scene in game.Scenes)
                {
                    foreach (var variant in variants)
                    {
                        foreach (var res in gameRes)
                        {
                            cellIndex++;
                            var cellKey = RunStateManager.Cell(game.Id, scene.Id, variant?.Id, res.Name);
                            var sceneDir = paths.EnsureSceneDir(game.Id, scene.Id, res.Name, variant?.Id);
                            var aggPath = Path.Combine(sceneDir, "scene_aggregate.json");

                            // Resume: if this cell already completed in a prior run, reload its aggregate and skip it.
                            if (stateMgr is not null && stateMgr.IsCellDone(cellKey))
                            {
                                var reloaded = Json.Load<SceneResolutionAggregate>(aggPath);
                                if (reloaded is not null)
                                {
                                    aggregates.Add(reloaded);
                                    PublishProgress(game, scene, variant, res, reloaded.ValidRuns, repeats,
                                        cellIndex, totalCells, "Resumed", cellCompleted: true);
                                    _log.Info("Resume", $"Skipping completed cell {cellKey} — {reloaded.ValidRuns}/{reloaded.TotalRuns} valid, avg {reloaded.AvgFps:0.0} fps (from checkpoint).");
                                    continue;
                                }
                                _log.Warn("Resume", $"Cell {cellKey} was marked done but its aggregate file is missing — re-running it.");
                            }

                            var agg = scene.SingleLaunchRepeats
                                ? await RunSceneResolutionSingleLaunchAsync(game, scene, variant, res, repeats, gpuName, paths, sceneRunner, launcher, cellIndex, totalCells, gameCts.Token).ConfigureAwait(false)
                                : await RunSceneResolutionAsync(game, scene, variant, res, repeats, gpuName, paths, sceneRunner, launcher, cellIndex, totalCells, gameCts.Token).ConfigureAwait(false);
                            aggregates.Add(agg);
                            PublishProgress(game, scene, variant, res, Math.Min(repeats, agg.ValidRuns), repeats,
                                cellIndex, totalCells, "Completed", cellCompleted: true);
                            Json.Save(aggPath, agg);
                            var vlabel = variant is null ? "" : $"[{variant.Id}] ";
                            _log.Info("Aggregate", $"{game.Id}/{scene.Id}/{vlabel}{res.Name}: {agg.ValidRuns}/{agg.TotalRuns} valid, " +
                                $"avg {agg.AvgFps:0.0} fps, 1% low {agg.P1LowFps:0.0}, {agg.AvgGpuPowerW:0} W, var {agg.FpsVariancePct:0.0}%");
                            // Checkpoint immediately — at most this one cell is ever at risk if Windows crashes.
                            stateMgr?.MarkCellDone(cellKey, game.Id, scene.Id, variant?.Id, res.Name, agg);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // physical ESC cancelled the CAMPAIGN — stop the whole sweep
            }
            catch (OperationCanceledException)
            {
                watchdogCancel = true;
                caughtReason = $"crashed or hung — no render/nav progress for {gameHangMs / 1000}s";
                _log.Warn("Game", $"=== {game.Id} SKIPPED — {caughtReason}; continuing to the next game. (Inner cleanup has killed the game process.) ===");
            }
            catch (GameSessionBlockedException ex)
            {
                // An account/cloud/authentication screen was positively read from the game. Retrying bot input
                // cannot repair credentials, and continuing through every remaining cell would waste hours.
                // Leave those cells unfinished so a later resume re-runs them after the operator repairs the
                // launcher session; completed cells remain intact in the checkpoint.
                unexpectedError = true;
                caughtReason = ex.Message;
                _log.Warn("Game", $"=== {game.Id} STOPPED — external session is blocked: {caughtReason}. Remaining cells stay pending for a later resume. ===");
            }
            catch (Exception ex)
            {
                unexpectedError = true;
                caughtReason = "unexpected error: " + ex.Message;
                _log.Error("Game", $"=== {game.Id} FAILED — {caughtReason}; continuing to the next game. ===");
            }
            finally
            {
                gameCts.Cancel();                                           // stop the hang-watchdog
                try { await hangWatch.ConfigureAwait(false); } catch { }
                gameCts.Dispose();
                launcher.RestoreDesktopModeIfSwitched(game);               // borderless sub-native games leave the desktop switched — put it back to bench-native so the next game can't inherit it
            }

            // Record the per-game roster outcome (passed / failed + class + reason) for the final summary.
            var gameAggs = aggregates.Skip(aggsBefore).ToList();
            int expectedConfigs = game.Scenes.Count * variants.Count * gameRes.Count;
            var outcome = BuildGameOutcome(game, gameAggs, variants, repeats, expectedConfigs,
                caughtReason, watchdogCancel, unexpectedError);
            outcomes.Add(outcome);
            stateMgr?.RecordGameOutcome(outcome);
            _log.Info("Game", outcome.Status == GameStatus.Passed
                ? $"=== {game.Id}: PASSED — {outcome.Reason} ==="
                : $"=== {game.Id}: FAILED [{FailureClassifier.Label(outcome.FailureClass)}] — {outcome.Reason} ===");
        }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A user cancellation is not a reason to lose already-completed cells. Preserve an immutable
            // partial suite snapshot for Results/Bot diagnostics, while leaving the checkpoint unfinished
            // so a later run can resume the remaining cells.
            var partial = SaveSuiteSnapshot(paths, system, aggregates, outcomes,
                rosterCompleted: false, visionComputeLabel);
            _log.Warn("Suite", $"Campaign cancelled — saved partial result with {partial.Aggregates.Count} completed cell(s).");
            throw;
        }

        stateMgr?.Complete();   // whole roster finished — this checkpoint will not be auto-resumed

        var suite = SaveSuiteSnapshot(paths, system, aggregates, outcomes,
            rosterCompleted: true, visionComputeLabel);
        _log.Info("Suite", $"Overall performance index: {suite.OverallPerformanceIndex:0.0} (geo-mean avg FPS).");
        _log.Info("Suite", $"Roster: {outcomes.Count(o => o.Status == GameStatus.Passed)} passed, " +
            $"{outcomes.Count(o => o.Status == GameStatus.Failed)} failed, {outcomes.Count(o => o.Status == GameStatus.Skipped)} skipped.");
        return suite;
    }

    private SuiteResult SaveSuiteSnapshot(
        RunPaths paths,
        SystemInfo system,
        IReadOnlyList<SceneResolutionAggregate> aggregates,
        IReadOnlyList<GameOutcome> outcomes,
        bool rosterCompleted,
        string visionComputeLabel)
    {
        var suite = _aggregator.BuildSuite(system, aggregates);
        suite.GameOutcomes = outcomes.ToList();
        suite.RosterCompleted = rosterCompleted;
        suite.Unattended = _cfg.Unattended;
        suite.VisionCompute = visionComputeLabel;
        Directory.CreateDirectory(paths.SuiteHistoryDir);
        string historyPath = paths.SuiteHistoryJson(suite.GeneratedUtc);
        Json.Save(historyPath, suite);
        Json.Save(paths.SuiteResultJson, suite);
        _log.Info("Suite", $"Saved {(rosterCompleted ? "completed" : "partial")} result snapshot: {historyPath}");
        return suite;
    }

    /// <summary>
    /// Build the per-game roster outcome from its aggregates + how the game ended. Passed only when EVERY
    /// planned config reached the requested valid-repeat count; otherwise Failed while preserving any valid
    /// partial results. A launch failure recorded in the run issues
    /// takes precedence (most actionable); else the dominant validation issue; else the watchdog/exception
    /// reason. The exact reason text is always preserved so even an "Other" is actionable.
    /// </summary>
    internal static GameOutcome BuildGameOutcome(GameProfile game, List<SceneResolutionAggregate> gameAggs,
        IReadOnlyList<GameVariant?> variants, int requiredRepeats, int expectedConfigs,
        string? caughtReason, bool watchdog, bool unexpected)
    {
        int valid = gameAggs.Sum(a => a.ValidRuns);
        int total = gameAggs.Sum(a => a.TotalRuns);
        var variantIds = variants.Select(v => v?.Id ?? "default").ToList();
        var issues = gameAggs.SelectMany(a => a.Runs).Where(r => !r.IsValid).SelectMany(r => r.ValidationIssues).ToList();

        int passedConfigs = gameAggs.Count(a => a.ValidRuns >= requiredRepeats);
        bool complete = gameAggs.Count == expectedConfigs && passedConfigs == expectedConfigs;
        if (complete)
        {
            return new GameOutcome
            {
                GameId = game.Id, Name = game.Name, Status = GameStatus.Passed, FailureClass = FailureClass.None,
                Reason = $"{valid}/{total} runs valid across all {expectedConfigs} configs",
                ValidRuns = valid, TotalRuns = total, Variants = variantIds
            };
        }

        FailureClass fc;
        string reason;
        var launchIssue = issues.FirstOrDefault(i =>
        {
            var s = i.ToLowerInvariant();
            return s.Contains("did not launch") || s.Contains("launch failed") || s.Contains("did not appear");
        });
        if (launchIssue is not null) { fc = FailureClass.LaunchFailure; reason = launchIssue; }
        else if (issues.Count > 0) { (fc, reason) = FailureClassifier.FromIssues(issues); }
        else if (unexpected && caughtReason is not null)
        {
            fc = FailureClassifier.FromIssue(caughtReason);
            if (fc == FailureClass.Other) fc = FailureClass.Crash;   // an exception mid-run is effectively a crash
            reason = caughtReason;
        }
        else if (watchdog) { fc = FailureClass.Crash; reason = caughtReason ?? "crashed or hung (no render/nav progress)"; }
        else { fc = FailureClass.Other; reason = caughtReason ?? "required valid repeats were not completed (no run issue recorded)"; }

        string completion = $"{valid}/{total} runs valid; {passedConfigs}/{expectedConfigs} configs reached " +
            $"the required {requiredRepeats} valid repeat(s)";
        reason = $"{completion} — {reason}";

        return new GameOutcome
        {
            GameId = game.Id, Name = game.Name, Status = GameStatus.Failed, FailureClass = fc,
            Reason = reason, ValidRuns = valid, TotalRuns = total, Variants = variantIds
        };
    }

    /// <summary>
    /// Background hang-watchdog for ONE game: if the run's progress beacon (RunHeartbeat) stays stale for
    /// <paramref name="hangMs"/> — the game crashed (process died) or hung (nav never reached the captured
    /// display) — cancel the PER-GAME token so the orchestrator skips this game and continues. NEVER touches the
    /// campaign token (physical ESC), so one bad game can't abort an unattended sweep. Exits as soon as the game
    /// finishes (its token is cancelled in the per-game finally) or the campaign is cancelled.
    /// </summary>
    private Task StartGameHangWatchdog(GameProfile game, CancellationTokenSource gameCts, CancellationToken campaignCt, int hangMs)
        => Task.Run(async () =>
        {
            try
            {
                while (!gameCts.IsCancellationRequested && !campaignCt.IsCancellationRequested)
                {
                    await Task.Delay(2000, gameCts.Token).ConfigureAwait(false);
                    if (campaignCt.IsCancellationRequested || gameCts.IsCancellationRequested) return;
                    if (GpuSuite.Core.RunHeartbeat.IsStale(hangMs))
                    {
                        _log.Warn("Watchdog", $"{game.Id}: no render/nav progress for {hangMs / 1000}s — the game crashed or hung. Skipping it; the campaign continues.");
                        try { gameCts.Cancel(); } catch { }
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { /* game finished → its token was cancelled → exit cleanly */ }
        });

    /// <summary>
    /// The graphics variants ("extra models") to run a game in. No profile variants ⇒ a single null entry
    /// (the implicit profile-default model). With variants defined: run the ENABLED ones, or — when the
    /// operator passed an explicit selection (--variants / settings.selectedVariantIds) — exactly the ones
    /// whose id matches (an explicit pick overrides each variant's Enabled toggle). An explicit selection
    /// that matches none of a game's variants yields an empty list, and that game is skipped.
    /// </summary>
    /// <summary>
    /// The variant-id selection in effect for one game: its per-game RUN-PLAN entry
    /// (<see cref="GpuSuite.Core.Config.SuiteConfig.GameVariantSelections"/>, set by --plan / the App's
    /// Run tab) when present and non-empty, else the global --variants list. Case-insensitive on game id.
    /// </summary>
    private IReadOnlyList<string> EffectiveVariantSelection(GameProfile game)
        => _cfg.GameVariantSelections
               .FirstOrDefault(kv => string.Equals(kv.Key, game.Id, StringComparison.OrdinalIgnoreCase))
               .Value is { Count: > 0 } perGame
            ? perGame
            : _cfg.SelectedVariantIds;

    private static IReadOnlyList<GameVariant?> ResolveGameVariants(GameProfile game, IReadOnlyList<string> selected)
    {
        if (game.Variants.Count == 0) return new GameVariant?[] { null };   // implicit default; selection doesn't apply
        IEnumerable<GameVariant> pool = selected.Count > 0
            ? game.Variants.Where(v => selected.Contains(v.Id, StringComparer.OrdinalIgnoreCase))
            : game.Variants.Where(v => v.Enabled);
        return pool.Cast<GameVariant?>().ToList();
    }

    internal static IReadOnlyList<Resolution> ResolveGameResolutions(GameProfile game, IReadOnlyList<Resolution> requested)
    {
        // The game's own supported set (falls back to the requested set when the profile doesn't constrain).
        var supported = game.SupportedResolutions.Count == 0
            ? requested.ToList()
            : game.SupportedResolutions.Select(Resolution.FromName).Where(r => r is not null).Cast<Resolution>().ToList();
        if (supported.Count == 0) supported = requested.ToList();
        // Honor an explicit caller request (CLI --res / settings): test only the intersection so
        // `--res 1080p` runs one resolution instead of the profile's full supported set.
        var want = requested.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var intersect = supported.Where(r => want.Contains(r.Name)).ToList();
        return intersect;
    }

    private void PublishProgress(GameProfile game, SceneProfile scene, GameVariant? variant, Resolution res,
        int repeatIndex, int repeatTarget, int cellIndex, int totalCells, string phase, bool cellCompleted)
    {
        _progress?.Invoke(new BenchmarkProgress(
            game.Id, game.Name, scene.Id, scene.Name, variant?.Id ?? "default", res.Name,
            Math.Max(0, repeatIndex), Math.Max(1, repeatTarget), Math.Max(1, cellIndex), Math.Max(1, totalCells),
            phase, cellCompleted));
    }

    private async Task<SceneResolutionAggregate> RunSceneResolutionAsync(
        GameProfile game, SceneProfile scene, GameVariant? variant, Resolution res, int repeats, string gpuName,
        RunPaths paths, SceneRunner runner, GameLauncher launcher, int cellIndex, int totalCells, CancellationToken ct)
    {
        var vtag = variant is null ? "" : $"[{variant.Id}] ";
        _log.Info("Scene", $"--- {scene.Name} @ {vtag}{res.Name} ---");
        GpuSuite.Core.RunHeartbeat.Ping();   // entry milestone — the suite is actively setting up this game (resolution/variant apply) so the crash/hang clock doesn't carry over the inter-game gap
        var runs = new List<RunResult>();
        int targetValid = repeats;
        int warmups = Math.Max(0, scene.WarmupRepeats);
        int autoRepeatBudget = EffectiveAutoRepeatBudget(scene, _cfg.Validation.MaxAutoRepeats);
        int maxAttempts = warmups + repeats + autoRepeatBudget;
        using IGamepad? prelaunchPad = SceneUsesGamepad(scene) ? Gamepad.Create(_log) : null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            int validSoFar = runs.Count(r => r.Verdict == RunVerdict.Valid);
            if (validSoFar >= targetValid && attempt > warmups + repeats) break;
            // The first `warmups` passes are THROWAWAY: run fully (warming caches + GPU thermals) but discard the
            // result so the measured repeats are all steady-state. idx is the measured-run index (1-based; 0 for a
            // warmup) used for the run dir / RepeatIndex.
            bool isWarmup = attempt <= warmups;
            int idx = isWarmup ? 0 : attempt - warmups;
            PublishProgress(game, scene, variant, res, idx, repeats, cellIndex, totalCells,
                isWarmup ? "Warm-up" : "Running", cellCompleted: false);

            // (1) Prepare system state — cooldown between runs.
            if (attempt > 1) await CooldownAsync(ct).ConfigureAwait(false);

            var hint = BuildHint(game, scene, res, attempt);

            // (4) Apply resolution/preset BEFORE launch — config-file edits (e.g. Cyberpunk's
            // UserSettings.json) must be in place before the game reads them at startup, since a
            // game launched with its benchmark flag (-benchmark) starts the scripted scene at once.
            // For bot/launch-arg/none methods ApplyResolution is just a log, so reordering is safe.
            // If the apply FAILS (empty/changed settings file), do NOT launch: launching would
            // benchmark the wrong resolution and, for games that truncate their own settings on
            // launch, leave the file destroyed. Record one Invalid run and stop — the error is
            // deterministic, so auto-repeating would just waste launches.
            var applied = launcher.ApplyResolution(game, res);
            if (!applied.Ok)
            {
                runs.Add(InvalidPreLaunch(game, scene, variant, res, attempt, gpuName, paths,
                    "Resolution apply failed (game NOT launched): " + applied.Detail));
                _log.Error("Run", $"Run {attempt} INVALID — resolution apply failed; not launching. {applied.Detail}");
                break;
            }

            // (4b) Apply the graphics variant's settings (config-file knobs) BEFORE launch, same as
            // resolution — and with the same strictness: if a knob can't be applied AND verified, do NOT
            // launch (a wrong/silently-collapsed model would mislabel the data). Deterministic, so no retry.
            var vapply = launcher.ApplyVariant(game, variant, res);
            if (!vapply.Ok)
            {
                runs.Add(InvalidPreLaunch(game, scene, variant, res, attempt, gpuName, paths,
                    "Variant settings apply failed (game NOT launched): " + string.Join("; ", vapply.Issues)));
                _log.Error("Run", $"Run {attempt} INVALID — variant '{variant?.Id ?? "default"}' apply failed; not launching.");
                break;
            }
            // Menu-method knobs are realized in-game by the capture-card vision-nav engine, which is not
            // yet calibrated. Until it is, a variant that depends on menu settings CANNOT be applied
            // autonomously — so rather than launch and silently benchmark the wrong (unchanged) settings
            // — which would mislabel the data and let two models collapse to identical numbers — record a
            // deterministic Invalid run. (When the vision-nav applier lands it will set + verify these and
            // clear the pending list, so this guard passes.)
            // Menu-method knobs are realized in-game by the capture-card vision-nav applier AFTER launch.
            // That needs a calibrated MenuMap for this game. If one exists, defer to the post-launch apply
            // (step 4c below); if NOT, fall back to the fail-safe — record a deterministic Invalid run and
            // do NOT launch, so two models can never silently collapse to identical (unchanged) settings.
            if (vapply.PendingMenu.Count > 0 && game.MenuMap is null)
            {
                runs.Add(InvalidPreLaunch(game, scene, variant, res, attempt, gpuName, paths,
                    "Variant requires in-game menu settings but no menuMap is calibrated for this game (not launched, no mislabeled data): " + string.Join(", ", vapply.PendingMenu)));
                _log.Warn("Run", $"Run {attempt} INVALID — variant '{variant?.Id ?? "default"}' needs menu-driven settings ({string.Join(", ", vapply.PendingMenu)}); no menuMap calibrated. Not launching.");
                break;
            }
            if (vapply.PendingMenu.Count > 0)
                _log.Info("Run", $"Variant '{variant?.Id ?? "default"}' has {vapply.PendingMenu.Count} menu knob(s); will set + verify them in-game via vision-nav after launch ({string.Join(", ", vapply.PendingMenu)}).");

            // (3) Launch game (or simulate).
            var launch = await LaunchWithTransientRetryAsync(launcher, game, ct).ConfigureAwait(false);
            GpuSuite.Core.RunHeartbeat.Ping();   // launch milestone — resets the crash/hang clock so the window has time to appear before the bot's foreground OCR takes over the beacon
            bool launchedOrSim = launch.Launched || launch.Simulated;
            if (!launchedOrSim)
            {
                string issue = $"Game did not launch and was not simulated: {launch.Detail}.";
                runs.Add(InvalidPreLaunch(game, scene, variant, res, attempt, gpuName, paths, issue));
                _log.Error("Run", $"Run {attempt} INVALID — {issue} Skipping the cell immediately; no synthetic capture or bot deadline will run.");
                break;
            }

            // (4c) Realize the variant's menu-method knobs IN-GAME via the capture-card vision-nav applier
            // (only when really launched + a MenuMap is calibrated). It opens the settings page, sets each
            // knob, and OCR-VERIFIES it off the capture card. If any knob can't be set AND verified, the run
            // is Invalid and NOT benchmarked — a menu model that didn't actually change must never be recorded
            // as if it had. Deterministic, so don't auto-repeat.
            if (vapply.PendingMenu.Count > 0 && game.MenuMap is not null && launch.Launched && launch.Pid is int menuPid)
            {
                MenuApplyResult mres;
                var syncReader = new GpuSuite.Engine.Vision.ScreenReader(
                    new GpuSuite.Engine.Vision.CaptureCardGrabber(_cfg.FfmpegPath, _cfg.CaptureCardDevice, _log), _log);
                try
                {
                    var menuApplier = new VisionMenuApplier(syncReader, _log);
                    // NO-SIGNAL self-heal (run 21, 2026-07-05): the game's video-settings APPLY drops the
                    // Elgato's sync at the exact moment the applier starts its exit reads, so the applier gets
                    // a hook to re-sync IN PLACE: mode-nudge until real frames return, then restore the game
                    // window's GEOMETRY (style-preserving) — the nudge shrinks any window larger than the
                    // interim mode, which both under-renders (a "4K" number measured at 1080p) and lets
                    // desktop text into the OCR frame. Style stays untouched: the game's own bordered,
                    // DWM-composited window is the proven Elgato-safe present path for windowed titles.
                    menuApplier.ResyncOnNoSignal = async hookCt =>
                    {
                        bool ok = await GpuSuite.Engine.Display.CaptureSyncGuard.EnsureSyncedAsync(
                            syncReader, _cfg.MaxRefreshHz, _log, hookCt, maxNudges: 2, context: "menu-apply mid-session").ConfigureAwait(false);
                        await GpuSuite.Engine.Automation.GameWindowManager.RestoreWindowRectAsync(menuPid, _log, hookCt).ConfigureAwait(false);
                        return ok;
                    };
                    mres = await menuApplier.ApplyAsync(game, variant, menuPid, inject: true, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { launcher.Cleanup(game, launch); throw; }
                catch (Exception ex) { mres = new MenuApplyResult { Ok = false, Detail = "vision-nav menu apply threw: " + ex.Message }; }

                if (!mres.Ok)
                {
                    launcher.Cleanup(game, launch);
                    runs.Add(InvalidPreLaunch(game, scene, variant, res, attempt, gpuName, paths,
                        "Vision-nav could not set + verify the variant's menu settings (not benchmarked): " + mres.Detail));
                    // AUTO-REPEAT, don't break (2026-07-09): a menu-apply verify failure was assumed deterministic
                    // and killed the cell after ONE attempt — but it is usually a FLAKY capture-card OCR read of a
                    // dropdown VALUE (DOOM dlss-fg 1080p read the FG dropdown as '?' once and went 0/1 with no retry,
                    // while 1440p+4K read it cleanly and passed 3/3, fingerprint-confirming FG on). A transient read
                    // miss is exactly what the repeat budget absorbs; a genuinely-unsettable knob still exhausts the
                    // budget and records an honest 0/N (never faked). The deterministic pre-launch failures above
                    // (resolution/config-variant apply, no-menuMap) still break — those can't change on retry.
                    if (attempt <= warmups + repeats)
                        _log.Warn("Run", $"Run {attempt} INVALID — menu settings not applied/verified for '{variant?.Id ?? "default"}' (likely a transient OCR read); will auto-repeat (budget {autoRepeatBudget}). {mres.Detail}");
                    else
                        _log.Warn("Run", $"Run {attempt} INVALID — menu settings not applied/verified for '{variant?.Id ?? "default"}'; not benchmarked. {mres.Detail}");
                    continue;
                }
                _log.Info("Run", $"Menu settings set + verified in-game by vision-nav: {mres.Detail}");

                // POST-APPLY DISPLAY RE-SYNC (2026-07-04, root cause of 15 blind DOOM runs): confirming the
                // game's video-settings APPLY notice makes idTech re-init its swapchain, and since DOOM's
                // Ripatorium update that re-init DROPS THE ELGATO'S SYNC. Windows still reports 4K@60 so
                // RefreshGuard is blind to it, and the benchmark bot navigates blind against the card's
                // NO-SIGNAL slate. 2026-07-05 (run 21) upgraded the single blind nudge to the CLOSED-LOOP
                // CaptureSyncGuard: probe-first (free when the card is fine), nudge only while probes read
                // the slate, require two consecutive real frames (the sync can FLAP, not just drop), give
                // up loudly. The applier's own mid-session hook usually clears this before we get here, so
                // this pass is the cheap belt-and-braces check for the nav bot that starts next.
                bool captureSynced = await GpuSuite.Engine.Display.CaptureSyncGuard.EnsureSyncedAsync(
                    syncReader, _cfg.MaxRefreshHz, _log, ct, maxNudges: 3, context: "post-menu-apply").ConfigureAwait(false);
                if (!captureSynced)
                    _log.Warn("Run", "Proceeding without verified capture sync — the vision-gated benchmark nav will very likely time out; classify that as a capture-environment failure, not a game failure.");
                // Any nudge (the applier's hook or the guard above) shrinks a windowed game to the interim
                // mode's size; put the window back over the whole screen so the benchmark renders and OCRs
                // at the size the numbers claim. No-op when the geometry is already right.
                await GpuSuite.Engine.Automation.GameWindowManager.RestoreWindowRectAsync(menuPid, _log, ct).ConfigureAwait(false);
            }

            // Build providers AFTER launch: the live-PresentMon vs synthetic decision needs the game
            // process to already exist, otherwise it always fell back to synthetic frames. Pass the
            // resolved PID so PresentMon attaches to exactly the launched game.
            var target = new FrameCaptureTarget
            {
                ProcessName = game.PresentMonByPid ? "" : game.CaptureProcessName, Pid = launch.Pid, Hint = hint
            };

            // In "auto" frame mode, if the previous attempt hit a capture discontinuity (a PresentMon
            // trace gap — the classic short-window failure), retry with RTSS, which reads its shared-memory
            // ring buffer continuously and doesn't lose the start of a brief scene.
            bool fpAuto = FrameProviderPolicy.AllowsRtssFallback(_cfg.FrameProvider, game.FrameProvider, game.ForbidRtss);
            // Fall back to RTSS when ANY prior attempt's PresentMon capture FAILED — either a capture
            // discontinuity (a trace gap, the classic short-window failure) OR too few/zero frames (PresentMon's
            // ETW session intermittently attaches but records nothing on this bench; observed on Cyberpunk and
            // idTech 8). RTSS reads its own shared-memory ring buffer continuously and doesn't depend on ETW,
            // so it recovers these. STICKY PER CELL (2026-07-07): the original prev-attempt-only check un-stuck
            // after every RTSS success, so a bench whose PresentMon 0-frames on EVERY attempt alternated
            // PM-fail/RTSS-valid/PM-fail/… = a deterministic 2-valid-of-5 on every cell of two full campaigns
            // (proven attempt-by-attempt on ACM as-set@1440p, suite_20260706_223450.log). Once PresentMon fails
            // in a cell it stays failed for that cell — keep RTSS for the remaining attempts.
            bool anyCaptureFailed = runs.Any(r =>
                r.CaptureDiscontinuity || r.CapturedFrameCount < _cfg.Validation.MinFrameCount);
            bool retryWithRtss = fpAuto && anyCaptureFailed;
            if (retryWithRtss)
                _log.Info("Frames", runs[^1].CapturedFrameCount < _cfg.Validation.MinFrameCount
                    ? $"Previous attempt's PresentMon capture recorded only {runs[^1].CapturedFrameCount} frame(s) — RTSS backend for this and all remaining attempts of this cell (sticky)."
                    : "A prior attempt hit a capture failure — RTSS backend for this and all remaining attempts of this cell (sticky).");
            // A game profile may force the RTSS backend (AppContainer/Xbox titles whose render process
            // PresentMon can't trace without elevation) — so one sweep can mix sandboxed (RTSS) and normal
            // Win32 (PresentMon) games without per-game --frames flags.
            bool gameForcesRtss = GameUsesRtss(game);
            if (gameForcesRtss && attempt == 1)
                _log.Info("Frames", string.Equals(_cfg.FrameProvider, "rtss", StringComparison.OrdinalIgnoreCase)
                    ? $"{game.Id}: machine-wide RTSS backend selected."
                    : $"{game.Id}: profile forces the RTSS backend (sandboxed title — PresentMon would need elevation; RTSS hooks without it).");
            bool useRtss = retryWithRtss || gameForcesRtss;
            bool effectiveLateRtss = game.LateRtss && !game.ForbidRtss;
            var providers = _factory.CreateFor(target, useRtss, preferPresentMonOverride: !useRtss && !effectiveLateRtss,
                lateRtssOverride: effectiveLateRtss);

            // (5) Refresh-cap safety net: while the game runs, watch the live desktop refresh. If an
            // exclusive-fullscreen title switches the scanout above the 60 Hz cap (the Elgato then loses
            // sync and the panel blanks), kill it so the panel re-syncs at the safe desktop refresh.
            // Only for real launches we own — a simulated run has no pid, and attach must never kill the
            // operator's process.
            using var refreshGuard = (_cfg.EnforceRefreshCap && launch.Launched && !_cfg.AttachToRunning && launch.Pid is int guardPid)
                ? RefreshGuard.Start(_cfg.MaxRefreshHz, _cfg.RefreshGuardPollMs, hz => OnRefreshViolation(game, guardPid, hz), _log, ct, res.Width, res.Height)
                : null;

            RunResult run;
            try
            {
                run = await runner.RunAsync(game, scene, variant, res, idx, repeats, providers, hint, launchedOrSim,
                    launch.Launched, launch.Pid, gpuName, paths, _log, ct, sharedPad: prelaunchPad).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Error("Run", $"Run {(isWarmup ? "warmup" : idx.ToString())} crashed: {ex.Message}");
                run = new RunResult
                {
                    GpuName = gpuName, GameId = game.Id, SceneId = scene.Id, ResolutionName = res.Name, RepeatIndex = idx,
                    VariantId = variant?.Id ?? "", VariantName = variant?.Name ?? "", Verdict = RunVerdict.Invalid
                };
                run.ValidationIssues.Add("Run threw: " + ex.Message);
            }
            finally
            {
                launcher.Cleanup(game, launch);
            }

            if (isWarmup)
            {
                _log.Info("Run", $"Warmup pass {attempt}/{warmups} (avg {run.Frames.AvgFps:0.0} fps) — DISCARDED so the measured repeats are steady-state (not aggregated, not counted).");
                continue;   // thermal/cache warmup carries into the next (measured) cold launch
            }
            runs.Add(run);

            ThrowIfExternalSessionBlocked(run);

            if (run.Verdict == RunVerdict.Invalid && attempt <= warmups + repeats)
                _log.Warn("Run", $"Run {idx} invalid; will auto-repeat (budget {autoRepeatBudget}).");
        }

        // Cross-run outlier flagging, then aggregate.
        _validator.FlagOutliers(runs);
        return _aggregator.Aggregate(game.Id, scene.Id, variant?.Id ?? "", variant?.Name ?? "", res.Name, runs);
    }

    /// <summary>
    /// Single-launch variant of <see cref="RunSceneResolutionAsync"/> for launcher titles whose rapid
    /// kill→relaunch churns the EA/Xbox/Ubisoft online session (so the next cold launch intermittently stalls,
    /// costing valid repeats). Applies resolution + variant and launches ONCE, then measures every repeat
    /// against the SAME running game — the first via the normal start bot, later repeats via the scene's
    /// re-entrant ReRunBotScript (reRun=true) — and kills the game once at the end. If the game dies
    /// mid-sequence it transparently relaunches (and re-applies) so the sequence still completes.
    /// </summary>
    private async Task<SceneResolutionAggregate> RunSceneResolutionSingleLaunchAsync(
        GameProfile game, SceneProfile scene, GameVariant? variant, Resolution res, int repeats, string gpuName,
        RunPaths paths, SceneRunner runner, GameLauncher launcher, int cellIndex, int totalCells, CancellationToken ct)
    {
        var vtag = variant is null ? "" : $"[{variant.Id}] ";
        _log.Info("Scene", $"--- {scene.Name} @ {vtag}{res.Name} (single-launch: {repeats} repeat(s) from ONE launch — avoids launcher relaunch-churn) ---");
        GpuSuite.Core.RunHeartbeat.Ping();   // entry milestone (see RunSceneResolutionAsync) — don't carry the crash/hang clock over the inter-game gap
        var runs = new List<RunResult>();
        int targetValid = repeats;
        int warmups = Math.Max(0, scene.WarmupRepeats);
        int autoRepeatBudget = EffectiveAutoRepeatBudget(scene, _cfg.Validation.MaxAutoRepeats);
        int maxAttempts = warmups + repeats + autoRepeatBudget;
        using IGamepad? prelaunchPad = SceneUsesGamepad(scene) ? Gamepad.Create(_log) : null;

        LaunchResult? shared = null;
        try
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                int validSoFar = runs.Count(r => r.Verdict == RunVerdict.Valid);
                if (validSoFar >= targetValid && attempt > warmups + repeats) break;
                // First `warmups` passes are throwaway (the in-session cold-nav pass warms caches; later repeats
                // re-run warm) — run fully but discard. idx = measured-run index (0 for a warmup).
                bool isWarmup = attempt <= warmups;
                int idx = isWarmup ? 0 : attempt - warmups;
                PublishProgress(game, scene, variant, res, idx, repeats, cellIndex, totalCells,
                    isWarmup ? "Warm-up" : "Running", cellCompleted: false);
                if (attempt > 1) await CooldownAsync(ct).ConfigureAwait(false);

                var hint = BuildHint(game, scene, res, attempt);

                // (Re)launch only when we don't have a live game. Apply resolution + variant just before that
                // one launch (config-file edits must precede startup). A live game is reused as-is.
                bool justLaunched = false;
                if (shared is null || !IsGameAlive(shared))
                {
                    if (shared is not null) { _log.Warn("Run", "Single-launch game exited mid-sequence — relaunching."); launcher.Cleanup(game, shared); shared = null; }
                    var applied = launcher.ApplyResolution(game, res);
                    if (!applied.Ok)
                    {
                        runs.Add(InvalidPreLaunch(game, scene, variant, res, attempt, gpuName, paths, "Resolution apply failed (game NOT launched): " + applied.Detail));
                        _log.Error("Run", $"Run {attempt} INVALID — resolution apply failed; not launching. {applied.Detail}");
                        break;
                    }
                    var vapply = launcher.ApplyVariant(game, variant, res);
                    if (!vapply.Ok)
                    {
                        runs.Add(InvalidPreLaunch(game, scene, variant, res, attempt, gpuName, paths, "Variant settings apply failed (game NOT launched): " + string.Join("; ", vapply.Issues)));
                        _log.Error("Run", $"Run {attempt} INVALID — variant '{variant?.Id ?? "default"}' apply failed; not launching.");
                        break;
                    }
                    if (vapply.PendingMenu.Count > 0 && game.MenuMap is null)
                    {
                        runs.Add(InvalidPreLaunch(game, scene, variant, res, attempt, gpuName, paths, "Variant has menu knobs but no MenuMap (single-launch is for as-set games): " + string.Join(", ", vapply.PendingMenu)));
                        break;
                    }
                    shared = await LaunchWithTransientRetryAsync(launcher, game, ct).ConfigureAwait(false);
                    GpuSuite.Core.RunHeartbeat.Ping();   // launch milestone (see RunSceneResolutionAsync) — resets the crash/hang clock for the window to appear
                    justLaunched = true;
                    if (!shared.Launched && !shared.Simulated)
                    {
                        string issue = $"Game did not launch and was not simulated: {shared.Detail}.";
                        runs.Add(InvalidPreLaunch(game, scene, variant, res, attempt, gpuName, paths, issue));
                        _log.Error("Run", $"Run {attempt} INVALID — {issue} Skipping the single-launch cell immediately; no synthetic capture or bot deadline will run.");
                        shared = null;
                        break;
                    }
                }

                var launch = shared!;
                var target = new FrameCaptureTarget
                {
                    ProcessName = game.PresentMonByPid ? "" : game.CaptureProcessName, Pid = launch.Pid, Hint = hint
                };
                bool gameForcesRtss = GameUsesRtss(game);
                bool fpAuto = FrameProviderPolicy.AllowsRtssFallback(_cfg.FrameProvider, game.FrameProvider, game.ForbidRtss);
                // STICKY PER CELL (2026-07-07) — same fix as RunSceneResolutionAsync: once any attempt's
                // capture failed, keep RTSS for the rest of the cell (the prev-only check alternated
                // PM-fail/RTSS-valid on a bench whose PresentMon 0-frames every attempt ⇒ exact 2/5 cells).
                bool anyCaptureFailed = runs.Any(r => r.CaptureDiscontinuity || r.CapturedFrameCount < _cfg.Validation.MinFrameCount);
                bool useRtss = gameForcesRtss || (fpAuto && anyCaptureFailed);
                bool effectiveLateRtss = game.LateRtss && !game.ForbidRtss;
                var providers = _factory.CreateFor(target, useRtss, preferPresentMonOverride: !useRtss && !effectiveLateRtss,
                    lateRtssOverride: effectiveLateRtss);

                using var refreshGuard = (_cfg.EnforceRefreshCap && launch.Launched && !_cfg.AttachToRunning && launch.Pid is int guardPid)
                    ? RefreshGuard.Start(_cfg.MaxRefreshHz, _cfg.RefreshGuardPollMs, hz => OnRefreshViolation(game, guardPid, hz), _log, ct, res.Width, res.Height)
                    : null;

                RunResult run;
                try
                {
                    run = await runner.RunAsync(game, scene, variant, res, idx, repeats, providers, hint,
                        launch.Launched || launch.Simulated, launch.Launched, launch.Pid, gpuName, paths, _log, ct,
                        reRun: !justLaunched, sharedPad: prelaunchPad).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.Error("Run", $"Run {(isWarmup ? "warmup" : idx.ToString())} crashed: {ex.Message}");
                    run = new RunResult { GpuName = gpuName, GameId = game.Id, SceneId = scene.Id, ResolutionName = res.Name, RepeatIndex = idx, VariantId = variant?.Id ?? "", VariantName = variant?.Name ?? "", Verdict = RunVerdict.Invalid };
                    run.ValidationIssues.Add("Run threw: " + ex.Message);
                }
                if (isWarmup)
                {
                    _log.Info("Run", $"Warmup pass {attempt}/{warmups} (avg {run.Frames.AvgFps:0.0} fps) — DISCARDED so the measured re-runs are warm (not aggregated, not counted). Game kept alive for the re-runs.");
                    continue;   // single-launch: game stays alive; the warm cache carries into the re-run repeats
                }
                runs.Add(run);
                ThrowIfExternalSessionBlocked(run);
                if (run.Verdict == RunVerdict.Invalid && attempt <= warmups + repeats)
                {
                    _log.Warn("Run", $"Run {idx} invalid; will auto-repeat WITHOUT relaunch (budget {autoRepeatBudget}).");

                    // A same-launch re-run script assumes that the preceding route reached its post-run state
                    // (for MSFS this is the persistent "Ready to fly" scene).  If a fail-closed bot gate
                    // aborts before that state, repeatedly running the re-run script can only wait on an
                    // unreachable anchor for its whole timeout.  Reset the shared title so the next attempt
                    // applies the cold navigation bot again.  This trades one Xbox/launcher relaunch for a
                    // real recovery path and never treats a menu/loading screen as a benchmark.
                    bool botRouteAbort = run.ValidationIssues.Any(issue => issue.Contains("Bot navigation aborted", StringComparison.OrdinalIgnoreCase));
                    if (botRouteAbort && shared is not null)
                    {
                        _log.Warn("Run", "Single-launch route aborted before its reusable post-run state — restarting the game so the next retry uses the cold navigation bot rather than the re-run script.");
                        launcher.Cleanup(game, shared);
                        shared = null;
                    }
                }
            }
        }
        finally
        {
            if (shared is not null) launcher.Cleanup(game, shared);
        }

        _validator.FlagOutliers(runs);
        return _aggregator.Aggregate(game.Id, scene.Id, variant?.Id ?? "", variant?.Name ?? "", res.Name, runs);
    }

    /// <summary>Authentication is an external launcher/session dependency, not a route that an unattended bot
    /// can safely navigate. Stop this game as soon as OCR proves the stall, rather than spending its full retry
    /// budget across every pending cell.</summary>
    private static void ThrowIfExternalSessionBlocked(RunResult run)
    {
        string? issue = run.ValidationIssues.FirstOrDefault(issue =>
            issue.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
            issue.Contains("signing in", StringComparison.OrdinalIgnoreCase) ||
            issue.Contains("sign-in", StringComparison.OrdinalIgnoreCase) ||
            issue.Contains("fatal screen text", StringComparison.OrdinalIgnoreCase));
        if (issue is not null)
            throw new GameSessionBlockedException(issue);
    }

    /// <summary>
    /// Vision- and motion-gated bot routes can encounter several honest transient invalid attempts during a
    /// long unattended campaign. Give those scenes enough recovery room to still collect five valid samples;
    /// validity gates remain fail-closed, and a larger operator-configured budget is always respected.
    /// </summary>
    internal static int EffectiveAutoRepeatBudget(SceneProfile scene, int configuredBudget)
    {
        int configured = Math.Max(0, configuredBudget);
        bool requiresBotNavigation = scene.Kind == SceneKind.BotDriven ||
            string.Equals(scene.BenchmarkStart, "bot", StringComparison.OrdinalIgnoreCase);
        return requiresBotNavigation ? Math.Max(configured, 5) : configured;
    }

    /// <summary>
    /// Store launchers occasionally accept a protocol request but fail to spawn the game (Ubisoft Connect was
    /// observed doing this once between two otherwise-successful Mirage launches in a long sweep). A launch is
    /// expensive but a permanently missing cell is worse, so retry one genuinely transient process-start failure.
    /// Deterministic configuration/URI/executable failures remain fail-fast and are never retried.
    /// </summary>
    private async Task<LaunchResult> LaunchWithTransientRetryAsync(GameLauncher launcher, GameProfile game,
        CancellationToken ct)
    {
        var first = launcher.Launch(game);
        if (first.Launched || first.Simulated || !IsTransientLaunchFailure(first.Detail)) return first;

        _log.Warn("Launch", $"Transient launch failure for '{game.Name}' ({first.Detail}) — retrying the launcher request once after cleanup.");
        await Task.Delay(3000, ct).ConfigureAwait(false);
        var retry = launcher.Launch(game); // Launch() performs the one-game-at-a-time cleanup before re-requesting.
        if (retry.Launched || retry.Simulated)
            _log.Info("Launch", $"Transient launch retry recovered '{game.Name}'.");
        else
            _log.Error("Launch", $"Transient launch retry also failed for '{game.Name}' ({retry.Detail}).");
        return retry;
    }

    internal static bool IsTransientLaunchFailure(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return false;
        string s = detail.ToLowerInvariant();
        return s.Contains("did not appear within")
            || s.Contains("vanished during bootstrap stabilization")
            || s.Contains("gaming services activation returned no process");
    }

    /// <summary>True while the single-launch game's process is still running (so it can be reused for the next
    /// repeat without relaunching). A simulated launch is always "alive".</summary>
    private static bool IsGameAlive(LaunchResult launch)
    {
        if (launch.Simulated) return true;
        if (!launch.Launched || launch.Pid is not int pid) return false;
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    private bool GameUsesRtss(GameProfile game)
        => !game.ForbidRtss && (game.LateRtss ||
            FrameProviderPolicy.Resolve(_cfg.FrameProvider, game.FrameProvider, _factory.PresentMonLive, game.ForbidRtss) == "rtss");

    /// <summary>Creates the non-negotiable requested-frame-backend failure used before any game launch.</summary>
    internal static bool TryCreateBackendPrecheckFailure(
        GameProfile game,
        bool backendReady,
        string backendDetail,
        out GameOutcome failure)
    {
        if (backendReady)
        {
            failure = null!;
            return false;
        }

        failure = new GameOutcome
        {
            GameId = game.Id,
            Name = game.Name,
            Status = GameStatus.Failed,
            FailureClass = FailureClass.HardwarePrecheck,
            Reason = "requested FPS / frametime backend unavailable before launch — " + backendDetail
        };
        return true;
    }

    private static bool SceneUsesGamepad(SceneProfile scene) =>
        new[] { scene.BotScript, scene.StartBotScript, scene.ReRunBotScript, scene.Warmup?.Script }
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(BotScriptLibrary.Resolve)
            .Any(script => script?.InputDevice == BotInputDevice.Gamepad);

    /// <summary>Build and persist an Invalid run record for a pre-launch failure, tagged with the variant.
    /// Preparing the attempt directory first also removes stale evidence left by an older run_N slot.</summary>
    private static RunResult InvalidPreLaunch(GameProfile game, SceneProfile scene, GameVariant? variant, Resolution res,
        int attempt, string gpuName, RunPaths paths, string issue)
    {
        var bad = new RunResult
        {
            GpuName = gpuName, GameId = game.Id, SceneId = scene.Id, ResolutionName = res.Name,
            VariantId = variant?.Id ?? "", VariantName = variant?.Name ?? "",
            RepeatIndex = attempt, Verdict = RunVerdict.Invalid, StartedUtc = DateTime.UtcNow, EndedUtc = DateTime.UtcNow
        };
        bad.ValidationIssues.Add(issue);
        string runDir = paths.EnsureRunDir(game.Id, scene.Id, res.Name, attempt, variant?.Id);
        Json.Save(Path.Combine(runDir, "run_summary.json"), bad);
        Json.Save(Path.Combine(runDir, "validation.json"), new { bad.Verdict, bad.ValidationIssues });
        return bad;
    }

    private async Task CooldownAsync(CancellationToken ct)
    {
        if (_cfg.CooldownSeconds <= 0) return;
        _log.Trace("Cooldown", $"Cooldown {_cfg.CooldownSeconds}s...");
        await Task.Delay(TimeSpan.FromSeconds(_cfg.CooldownSeconds), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Refresh-cap breach handler (invoked by <see cref="RefreshGuard"/>): an exclusive-fullscreen game
    /// drove the panel above the 60 Hz cap, so the Elgato lost sync and the monitor blanked. Kill the
    /// game so the panel re-syncs at the safe desktop refresh; the run then ends Invalid, which is the
    /// correct outcome — frames captured at an unintended refresh shouldn't be recorded as a result.
    /// When KillGameOnRefreshViolation is false we only log (diagnostic mode).
    /// </summary>
    private void OnRefreshViolation(GameProfile game, int pid, int hz)
    {
        if (!_cfg.KillGameOnRefreshViolation)
        {
            _log.Warn("RefreshGuard", $"{game.Id} drove the panel to {hz}Hz (> {_cfg.MaxRefreshHz}Hz cap); KillGameOnRefreshViolation=false — logging only, panel stays blanked.");
            return;
        }
        try { using var p = Process.GetProcessById(pid); if (!p.HasExited) p.Kill(true); }
        catch { }
        var baseName = Path.GetFileNameWithoutExtension(game.CaptureProcessName);
        if (!string.IsNullOrEmpty(baseName))
            foreach (var stray in Process.GetProcessesByName(baseName))
                try { using (stray) stray.Kill(true); } catch { }
        _log.Error("RefreshGuard", $"Killed {game.Id} (pid {pid}) — it drove the panel to {hz}Hz, above the {_cfg.MaxRefreshHz}Hz Elgato cap.");
    }

    /// <summary>
    /// Derive a deterministic synthetic workload hint from run identity. Base FPS scales
    /// with resolution; GPU utilisation (and thus power) rises at higher resolution.
    /// Seed varies per attempt so repeats differ slightly (realistic run-to-run variance).
    /// </summary>
    private static WorkloadHint BuildHint(GameProfile game, SceneProfile scene, Resolution res, int attempt)
    {
        (double fps, double powerScale) = res.Name switch
        {
            "1080p" => (175.0, 0.82),
            "1440p" => (120.0, 0.93),
            "4K" => (68.0, 1.00),
            _ => (100.0, 0.9)
        };
        int seed = HashCode.Combine(game.Id, scene.Id, res.Name, attempt) & 0x7fffffff;
        // For built-in benchmark scenes, simulate a bench of CaptureSeconds so the synthetic
        // frame stream stops and activity-based finish detection can be exercised in degraded mode.
        double benchDuration = scene.Kind == SceneKind.BuiltInBenchmark ? scene.CaptureSeconds : 0;
        return new WorkloadHint
        {
            BaseFps = fps,
            BaseGpuPowerW = 300.0 * powerScale,
            BaseGpuTempC = 60,
            Seed = seed,
            BenchDurationSec = benchDuration
        };
    }

    private SystemInfo BuildSystemInfo() => new()
    {
        GpuName = _factory.DetectedGpuName,
        CpuName = _factory.DetectedCpuName,
        OsVersion = Environment.OSVersion.VersionString,
        PresentMonAvailable = true,
        LhmAvailable = true,
        PoweneticsConnected = false
    };
}
