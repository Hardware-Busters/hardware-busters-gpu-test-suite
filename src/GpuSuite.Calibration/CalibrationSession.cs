using GpuSuite.Core.Diagnostics;
using GpuSuite.Calibration.Schema;
using GpuSuite.Calibration.Observation;
using GpuSuite.Calibration.Llm;
using GpuSuite.Calibration.Persistence;
using GpuSuite.Calibration.Graph;
using GpuSuite.Calibration.Modules;
using GpuSuite.Calibration.Reporting;
using GpuSuite.Calibration.Decisions;
using GpuSuite.Calibration.Plugins;
using GpuSuite.Core.Config;
using GpuSuite.Core.Models;

namespace GpuSuite.Calibration;

/// <summary>What a calibration session is doing.</summary>
public enum CalibrationMode { Calibrate, ValidateRoute, Learn, GameplayValidate, Phase5 }

/// <summary>A request to run a calibration session.</summary>
public sealed class CalibrationRequest
{
    public string Game { get; set; } = "";
    public CalibrationMode Mode { get; set; } = CalibrationMode.Calibrate;
    public EnvFingerprint Env { get; set; } = new();
    public string? Goal { get; set; }
    public RouteRecord? Route { get; set; }
    public RunResult? RunResult { get; set; }
    public string? SceneDescriptor { get; set; }
    public bool AllowStaticScene { get; set; }
    public GameplayEvidence? GameplayEvidence { get; set; }
    public bool RequireVisualEvidence { get; set; }
    public NavRecording? NavRecording { get; set; }
    public double MinimumSceneSimilarity { get; set; } = 0.75;
    public double MinimumSpawnSimilarity { get; set; } = 0.70;
    public int MaximumLateShaderHitches { get; set; }
    public double MinimumStableSecondsAfterShaderHitch { get; set; } = 5;
    public SuiteResult? CurrentSuiteResult { get; set; }
    public SuiteResult? BaselineSuiteResult { get; set; }
    public ValidationThresholds CurrentValidationThresholds { get; set; } = new();
}

/// <summary>The outcome of a calibration session — the draft graph, decisions, drift, recommendations, report.</summary>
public sealed class CalibrationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public MenuGraph Graph { get; set; } = new();
    public DriftAnalysis? Drift { get; set; }
    public List<Recommendation> Recommendations { get; set; } = new();
    public List<CalibrationDecision> Decisions { get; set; } = new();
    public ReportRefs? Report { get; set; }
    public RouteVerdict? RouteVerdict { get; set; }
    public BotDraft? BotDraft { get; set; }
    public GameplayVerdict? GameplayVerdict { get; set; }
    public FailureClassification? Failure { get; set; }
    public RecoveryPlan? Recovery { get; set; }
    public Phase5Analysis? Phase5 { get; set; }
}

/// <summary>The calibration session orchestrator (module 6) — the analogue of the TestOrchestrator.</summary>
public interface ICalibrationSession
{
    Task<CalibrationResult> RunAsync(CalibrationRequest request, CancellationToken ct);
}

/// <summary>
/// The Phase-2 calibration session. It OBSERVES a game's menus (here via a stub observer; the real
/// capture-card observer drops in behind <see cref="IScreenObserver"/>), reads literal OCR, runs the
/// confidence-gated escalation (OCR-first; the vision/LLM tier only when OCR is not confident — and the
/// GX10 abstains rather than guess), assembles the menu graph, persists every artifact + a full decision
/// audit trail, runs a deterministic drift check against the stored baseline, and emits the human review
/// report.
///
/// It is a strict SIDECAR (P1/P2): it drives NOTHING in the game and depends on nothing in the execution
/// engine. The deterministic benchmark path is untouched whether this runs, fails, or is absent.
/// </summary>
public sealed class CalibrationSession : ICalibrationSession
{
    private const string Stage = "Calibrate";

    private readonly IScreenObserver _observer;
    private readonly IOcrEngine _ocr;
    private readonly ILLMReasoner _llm;
    private readonly IConfidenceEngine _confidence;
    private readonly IMenuGraphBuilder _graph;
    private readonly ILayoutComparer _comparer;
    private readonly ICalibrationDatabase _db;
    private readonly ICalibrationReportGenerator _report;
    private readonly DecisionLog _decisions;
    private readonly IGameCalibrationPlugin _plugin;
    private readonly RunLogger _log;
    private readonly IRouteValidator _routeValidator;
    private readonly IBotCalibrator _botCalibrator;
    private readonly IGameplayValidator _gameplayValidator;
    private readonly IFailureClassifier _failureClassifier;
    private readonly IRecoveryAdvisor _recoveryAdvisor;
    private readonly Phase5Coordinator _phase5;

    public CalibrationSession(
        IScreenObserver observer, IOcrEngine ocr, ILLMReasoner llm, IConfidenceEngine confidence,
        IMenuGraphBuilder graph, ILayoutComparer comparer, ICalibrationDatabase db,
        ICalibrationReportGenerator report, DecisionLog decisions, IGameCalibrationPlugin plugin, RunLogger log,
        IRouteValidator? routeValidator = null, IBotCalibrator? botCalibrator = null,
        IGameplayValidator? gameplayValidator = null, IFailureClassifier? failureClassifier = null,
        IRecoveryAdvisor? recoveryAdvisor = null, Phase5Coordinator? phase5 = null)
    {
        _observer = observer; _ocr = ocr; _llm = llm; _confidence = confidence;
        _graph = graph; _comparer = comparer; _db = db; _report = report;
        _decisions = decisions; _plugin = plugin; _log = log;
        _routeValidator = routeValidator ?? new RouteValidator();
        _botCalibrator = botCalibrator ?? new GraphBotCalibrator();
        _gameplayValidator = gameplayValidator ?? new DeterministicGameplayValidator();
        _failureClassifier = failureClassifier ?? new DeterministicFailureClassifier();
        _recoveryAdvisor = recoveryAdvisor ?? new DeterministicRecoveryAdvisor();
        _phase5 = phase5 ?? new Phase5Coordinator(db);
    }

    public async Task<CalibrationResult> RunAsync(CalibrationRequest request, CancellationToken ct)
    {
        if (request.Mode == CalibrationMode.ValidateRoute)
            return await ValidateRouteAsync(request, ct).ConfigureAwait(false);
        if (request.Mode == CalibrationMode.GameplayValidate)
            return await ValidateGameplayAsync(request, ct).ConfigureAwait(false);
        if (request.Mode == CalibrationMode.Phase5)
            return await RunPhase5Async(request, ct).ConfigureAwait(false);

        var game = _plugin.Game;
        var env = request.Env;
        _log.Info(Stage, $"AI Calibration Engineer (advisory) observing «{_plugin.Name}» — read-only, drives nothing.");

        // Capture the prior per-screen baselines BEFORE we write anything new (for the drift dry-run).
        var priorLayouts = new Dictionary<string, LayoutSnapshot?>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _plugin.ExpectedScreens)
            priorLayouts[s.Id] = await _db.GetLayoutAsync(game, s.Id, ct).ConfigureAwait(false);

        var session = _graph.BeginGraph(game, env, null);
        var driftChanges = new List<LayoutChange>();

        foreach (var screen in _plugin.ExpectedScreens)
        {
            ct.ThrowIfCancellationRequested();

            var frame = await _observer.ObserveAsync(screen.Id, ct).ConfigureAwait(false);
            frame.CapturedAtIso = DateTime.Now.ToString("o");

            // Foreground / contamination guard: never reason over a frame that isn't the game.
            if (!frame.GameIsForeground)
            {
                _decisions.Abstained(Stage, screen.Id, "captured frame was not the game foreground window — possible desktop/chat contamination; skipping this screen.",
                    Confidence.Abstain("not foreground"));
                continue;
            }

            var ocr = await _ocr.ReadAsync(frame, ct).ConfigureAwait(false);

            // Deterministic, cheap, FIRST: whole-word anchor match (the PLAY⊄gameplay-safe path).
            var found = screen.Anchors.Where(a => ocr.FindWordWhole(a) is not null).ToList();
            double ratio = screen.Anchors.Length == 0 ? 0.5 : (double)found.Count / screen.Anchors.Length;
            var ocrConf = _confidence.Normalize(new RawScore(ratio, SourceKind.Ocr));
            var gate = _confidence.Gate(ocrConf, TaskKind.ScreenClassify);

            var node = new MenuNode
            {
                Id = screen.Id,
                SemanticDescription = screen.SemanticHint,
                Controls = DetectControls(screen, ocr),
                Confidence = ocrConf
            };

            string anchorEvidence = $"anchors {found.Count}/{screen.Anchors.Length}: [{string.Join(", ", found)}]";
            switch (gate.Outcome)
            {
                case EscalationOutcome.AcceptDeterministic:
                    _decisions.OcrAccepted(Stage, screen.Id,
                        $"matched {anchorEvidence} → {gate.Reason}", ocrConf, anchorEvidence);
                    break;

                case EscalationOutcome.EscalateTier:
                    _decisions.VisionEscalationTriggered(Stage, screen.Id,
                        $"OCR not confident ({ocrConf.Value:0.00}) on {anchorEvidence} — escalating to the vision/LLM tier.", ocrConf);
                    var desc = await _llm.DescribeScreenAsync(
                        new VisionContext { Frame = frame, Ocr = ocr, KnownScreens = _plugin.ExpectedScreens.Select(e => e.Id).ToList() }, ct).ConfigureAwait(false);
                    _decisions.Gx10Consulted(Stage, screen.Id, "asked the GX10 to describe/classify the screen.", desc.Confidence);
                    if (desc.Abstained)
                        _decisions.HumanApprovalRequired(Stage, screen.Id,
                            "OCR insufficient and the GX10 abstained (it did not guess) — this screen needs human review.", desc.Confidence);
                    else
                        node.SemanticDescription = string.IsNullOrWhiteSpace(desc.Summary) ? node.SemanticDescription : desc.Summary;
                    break;

                default: // AskHuman (Abstain band)
                    _decisions.HumanApprovalRequired(Stage, screen.Id,
                        $"insufficient evidence on {anchorEvidence} — {gate.Reason}", ocrConf);
                    break;
            }

            _graph.AddObservation(session, string.IsNullOrEmpty(screen.InputFromPrevious) ? null : screen.InputFromPrevious, node);

            // Persist the per-screen layout snapshot, and diff it against the captured baseline (drift).
            var snapshot = new LayoutSnapshot
            {
                Screen = screen.Id,
                Env = env,
                Ocr = ocr.Words,
                Controls = node.Controls,
                SemanticDescription = node.SemanticDescription
            };
            if (priorLayouts.TryGetValue(screen.Id, out var prior) && prior is not null)
            {
                var diff = _comparer.Compare(prior, snapshot);
                if (diff.AnyChange)
                {
                    driftChanges.AddRange(diff.Changes);
                    _decisions.DeterministicAccepted(Stage, screen.Id, $"drift detected vs. baseline: {diff.Changes.Count} change(s) — deterministic layout diff.");
                }
            }
            await _db.SaveLayoutAsync(game, snapshot, ct).ConfigureAwait(false);
        }

        // Assemble + persist the graph and the game record.
        var graph = _graph.Build(session);
        await _db.SaveGraphAsync(game, graph, ct).ConfigureAwait(false);
        await _db.SaveGameAsync(new GameCalibration
        {
            Game = game,
            Name = _plugin.Name,
            Env = env,
            CurrentGraphRef = $"menus/graph_{env.Key()}.json",
            Screens = _plugin.ExpectedScreens.Select(s => s.Id).ToList()
        }, ct).ConfigureAwait(false);

        // Benchmark detection (deterministic): is the benchmark launcher screen in the graph?
        if (_plugin.BenchmarkScreenId is { } benchId)
        {
            if (graph.FindNode(benchId) is not null)
                _decisions.DeterministicAccepted(Stage, benchId, "benchmark launcher screen is present in the menu graph.");
            else
                _decisions.HumanApprovalRequired(Stage, benchId, "the expected benchmark launcher screen was NOT found in the graph — needs human review.");
        }

        // Drift analysis (deterministic diff; GX10 narration intentionally abstains in the skeleton).
        DriftAnalysis? drift = null;
        if (priorLayouts.Values.Any(v => v is not null))
        {
            drift = new DriftAnalysis
            {
                Changes = driftChanges,
                Abstained = false,
                Confidence = driftChanges.Count == 0
                    ? Confidence.Of(0.85, ConfidenceBand.High, "no layout changes vs. baseline (clean true-negative)")
                    : Confidence.Of(0.7, ConfidenceBand.Medium, "deterministic layout diff vs. baseline"),
                Rationale = driftChanges.Count == 0
                    ? "No drift: the live layout matches the stored baseline."
                    : $"{driftChanges.Count} layout change(s) detected vs. baseline (GX10 narration not wired — deterministic diff only)."
            };
            await _db.AppendDriftAsync(game, new DriftEvent
            {
                Time = DateTime.Now.ToString("o"),
                BaselineRef = "layouts/* (prior)",
                CurrentRef = $"menus/graph_{env.Key()}.json",
                Changes = driftChanges,
                Confidence = drift.Confidence
            }, ct).ConfigureAwait(false);
        }
        else
        {
            _decisions.DeterministicAccepted(Stage, null, "no prior baseline — this run establishes the baseline of record.");
        }

        var recs = new List<Recommendation>();
        BotDraft? learnedDraft = null;
        if (request.Mode == CalibrationMode.Learn)
        {
            var goal = request.NavRecording?.Goal ?? request.Goal ?? _plugin.BenchmarkScreenId ?? _plugin.ExpectedScreens.LastOrDefault()?.Id ?? "";
            learnedDraft = request.NavRecording is null
                ? _botCalibrator.SynthesizeRoute(graph, goal)
                : _botCalibrator.CalibrateFromRecording(graph, request.NavRecording);
            if (!learnedDraft.Confidence.IsAbstain)
            {
                await _db.SaveBotDraftAsync(game, learnedDraft, ct).ConfigureAwait(false);
                var learnedRoute = new RouteRecord { Goal = goal, Env = env, Steps = learnedDraft.Steps, Confidence = learnedDraft.Confidence };
                var verdict = await _routeValidator.ValidateAsync(graph, learnedRoute, ct).ConfigureAwait(false);
                if (verdict.Valid)
                {
                    learnedRoute.LastValidated = DateTime.Now.ToString("o");
                    learnedRoute.Confidence = verdict.Confidence;
                    await _db.SaveRouteAsync(game, learnedRoute, ct).ConfigureAwait(false);
                    recs.Add(Recommendation.For(RecommendationKind.BotPath,
                        $"Promote learned route to '{goal}'", learnedDraft.Rationale, learnedDraft.Confidence,
                        "The graph-derived route is saved as a draft and passed structural validation; promotion changes benchmark navigation."));
                    _decisions.RecommendationGenerated("Learn", goal, "synthesized and persisted a graph-derived bot draft.", learnedDraft.Confidence);
                    _decisions.HumanApprovalRequired("Learn", goal, "bot-route promotion changes benchmark behavior (P7).", learnedDraft.Confidence);
                }
                else
                    _decisions.Abstained("Learn", goal, verdict.Reason ?? "learned route failed structural validation", verdict.Confidence);
                await _db.AppendValidationAsync(game, new ValidationEvent
                {
                    Time = DateTime.Now.ToString("o"), Kind = "route", Verdict = verdict.Valid ? "pass" : "fail",
                    Confidence = verdict.Confidence, Rationale = verdict.Reason ?? learnedDraft.Rationale
                }, ct).ConfigureAwait(false);
            }
            else
                _decisions.Abstained("Learn", goal, learnedDraft.Rationale, learnedDraft.Confidence);
        }
        else
        {
            recs.Add(Recommendation.For(
                RecommendationKind.Profile,
                "Promote this calibration as the menu-graph baseline of record",
                $"game.json currentGraphRef → menus/graph_{env.Key()}.json",
                Confidence.Of(0.82, ConfidenceBand.High, "graph built + persisted; promotion is a human decision"),
                "The menu graph was observed and saved as a draft. Promoting it to the baseline-of-record changes what future runs trust, so it requires your approval (P7)."));
            _decisions.RecommendationGenerated(Stage, null, "generated a baseline-promotion recommendation (advisory; inert until approved).", recs[0].Confidence);
            _decisions.HumanApprovalRequired(Stage, null, "promoting the baseline-of-record changes benchmark behavior → requires human approval (P7).", recs[0].Confidence);
        }

        await _db.AppendValidationAsync(game, new ValidationEvent
        {
            Time = DateTime.Now.ToString("o"),
            Kind = "settings",
            Verdict = graph.Nodes.Count == _plugin.ExpectedScreens.Count ? "pass" : "warn",
            Confidence = Confidence.Of(graph.Nodes.Count == _plugin.ExpectedScreens.Count ? 0.85 : 0.5,
                                       graph.Nodes.Count == _plugin.ExpectedScreens.Count ? ConfidenceBand.High : ConfidenceBand.Medium,
                                       "screens mapped vs. expected"),
            Rationale = $"mapped {graph.Nodes.Count}/{_plugin.ExpectedScreens.Count} expected screens."
        }, ct).ConfigureAwait(false);

        // Human review report.
        var model = new CalibrationReportModel
        {
            Game = game,
            Name = _plugin.Name,
            SuiteVersion = env.SuiteVersion,
            GeneratedAtIso = DateTime.Now.ToString("o"),
            Env = env,
            Graph = graph,
            Drift = drift,
            Decisions = _decisions.Entries.ToList(),
            Recommendations = recs
        };
        var refs = await _report.GenerateAsync(model, _db.HistoryDirectory(game), $"calibration_report_{DateTime.Now:yyyyMMdd-HHmmss}", ct).ConfigureAwait(false);

        _log.Info(Stage, $"Calibration complete — {graph.Nodes.Count} screen(s) mapped, {_decisions.Entries.Count} decision(s) logged.");
        _log.Info(Stage, $"Report: {refs.HtmlPath}");

        var learnSucceeded = request.Mode != CalibrationMode.Learn || learnedDraft is { Confidence.IsAbstain: false } && recs.Count > 0;
        return new CalibrationResult
        {
            Success = learnSucceeded,
            Message = learnSucceeded
                ? $"Observed {graph.Nodes.Count} screens; {recs.Count} recommendation(s) awaiting approval."
                : $"Observed {graph.Nodes.Count} screens, but no structurally valid route could be learned.",
            Graph = graph,
            Drift = drift,
            Recommendations = recs,
            Decisions = _decisions.Entries.ToList(),
            Report = refs,
            BotDraft = learnedDraft
        };
    }

    private async Task<CalibrationResult> ValidateRouteAsync(CalibrationRequest request, CancellationToken ct)
    {
        const string stage = "ValidateRoute";
        var game = _plugin.Game;
        var graph = await _db.GetBaselineGraphAsync(game, ct).ConfigureAwait(false);
        if (graph is null) return Failure("No baseline menu graph exists; run calibrate first.");
        var goal = request.Goal ?? request.Route?.Goal ?? _plugin.BenchmarkScreenId ?? "";
        var route = request.Route ?? await _db.GetRouteAsync(game, goal, ct).ConfigureAwait(false);
        if (route is null) return Failure($"No recorded route exists for goal '{goal}'; run learn first or supply a route.", graph);

        var verdict = await _routeValidator.ValidateAsync(graph, route, ct).ConfigureAwait(false);
        await _db.AppendValidationAsync(game, new ValidationEvent
        {
            Time = DateTime.Now.ToString("o"), Kind = "route", Verdict = verdict.Valid ? "pass" : "fail",
            Confidence = verdict.Confidence, Rationale = verdict.Reason ?? "route matches the live graph"
        }, ct).ConfigureAwait(false);
        _decisions.DeterministicAccepted(stage, goal, verdict.Valid ? "route path validated against every graph transition." : verdict.Reason!, verdict.Confidence);

        var recs = new List<Recommendation>();
        BotDraft? repair = null;
        if (!verdict.Valid)
        {
            repair = _botCalibrator.RepairRoute(graph, route, goal);
            await _db.SaveBotDraftAsync(game, repair, ct).ConfigureAwait(false);
            recs.Add(Recommendation.For(RecommendationKind.Route, "Repair stale route", repair.DiffAgainst ?? repair.Rationale,
                repair.Confidence, "A replacement graph path was drafted; promotion remains gated."));
            _decisions.RecommendationGenerated(stage, goal, "route invalid; saved a deterministic repair draft.", repair.Confidence);
            _decisions.HumanApprovalRequired(stage, goal, "route repair changes benchmark navigation (P7).", repair.Confidence);
        }
        var report = await GenerateReportAsync(request, graph, recs, $"route_validation_{DateTime.Now:yyyyMMdd-HHmmss}", ct).ConfigureAwait(false);
        return new CalibrationResult
        {
            Success = verdict.Valid, Message = verdict.Valid ? "Route is valid." : verdict.Reason ?? "Route is invalid.",
            Graph = graph, RouteVerdict = verdict, BotDraft = repair, Recommendations = recs,
            Decisions = _decisions.Entries.ToList(), Report = report
        };

        CalibrationResult Failure(string message, MenuGraph? g = null) => new() { Success = false, Message = message, Graph = g ?? new() };
    }

    private async Task<CalibrationResult> ValidateGameplayAsync(CalibrationRequest request, CancellationToken ct)
    {
        const string stage = "GameplayValidate";
        var graph = await _db.GetBaselineGraphAsync(_plugin.Game, ct).ConfigureAwait(false) ?? new MenuGraph { Env = request.Env };
        var verdict = await _gameplayValidator.ValidateWindowAsync(new GameplayValidationContext
        {
            Run = request.RunResult, SceneDescriptor = request.SceneDescriptor ?? "",
            ExpectedGameId = _plugin.Game, ExpectedSceneId = request.SceneDescriptor ?? "",
            AllowStaticScene = request.AllowStaticScene, Evidence = request.GameplayEvidence,
            RequireVisualEvidence = request.RequireVisualEvidence,
            MinimumSceneSimilarity = request.MinimumSceneSimilarity,
            MinimumSpawnSimilarity = request.MinimumSpawnSimilarity,
            MaximumLateShaderHitches = request.MaximumLateShaderHitches,
            MinimumStableSecondsAfterShaderHitch = request.MinimumStableSecondsAfterShaderHitch
        }, ct).ConfigureAwait(false);
        await _db.AppendValidationAsync(_plugin.Game, new ValidationEvent
        {
            Time = DateTime.Now.ToString("o"), Kind = "gameplay", Verdict = verdict.IsGameplay && verdict.SceneMatch ? "pass" : "fail",
            Confidence = verdict.Confidence, Rationale = verdict.Rationale, Evidence = verdict.Evidence
        }, ct).ConfigureAwait(false);
        _decisions.DeterministicAccepted(stage, request.RunResult?.SceneId, verdict.Rationale, verdict.Confidence);

        FailureClassification? failure = null;
        RecoveryPlan? recovery = null;
        var recs = new List<Recommendation>();
        if (!verdict.IsGameplay || !verdict.SceneMatch || !verdict.SpawnMatch || !verdict.HudMatch)
        {
            failure = await _failureClassifier.ClassifyAsync(new FailureContextLite
            {
                Summary = verdict.Rationale,
                Signals = request.RunResult?.ValidationIssues.ToList() ?? new List<string>()
            }, ct).ConfigureAwait(false);
            recovery = await _recoveryAdvisor.AdviseAsync(failure, ct).ConfigureAwait(false);
            recs.AddRange(recovery.Steps.Select(step => Recommendation.For(step.Kind,
                $"Recovery: {step.Description}", step.Kind.ToString(), step.Confidence,
                $"Suggested for deterministic failure class '{failure.FailureClass}'.")));
            _decisions.DeterministicAccepted(stage, request.RunResult?.SceneId,
                $"classified as {failure.FailureClass}; recovery: {string.Join(", ", recovery.Steps.Select(s => s.Kind))}.", failure.Confidence);
        }
        var report = await GenerateReportAsync(request, graph, recs, $"gameplay_validation_{DateTime.Now:yyyyMMdd-HHmmss}", ct,
            verdict, failure, recovery).ConfigureAwait(false);
        return new CalibrationResult
        {
            Success = verdict.IsGameplay && verdict.SceneMatch && verdict.SpawnMatch && verdict.HudMatch,
            Message = verdict.Rationale, Graph = graph,
            GameplayVerdict = verdict, Failure = failure, Recovery = recovery,
            Recommendations = recs, Decisions = _decisions.Entries.ToList(), Report = report
        };
    }

    private Task<ReportRefs> GenerateReportAsync(CalibrationRequest request, MenuGraph graph,
        List<Recommendation> recs, string stem, CancellationToken ct, GameplayVerdict? gameplay = null,
        FailureClassification? failure = null, RecoveryPlan? recovery = null)
        => _report.GenerateAsync(new CalibrationReportModel
        {
            Game = _plugin.Game, Name = _plugin.Name, SuiteVersion = request.Env.SuiteVersion,
            GeneratedAtIso = DateTime.Now.ToString("o"), Env = request.Env, Graph = graph,
            Decisions = _decisions.Entries.ToList(), Recommendations = recs,
            Gameplay = gameplay, Failure = failure, Recovery = recovery
        }, _db.HistoryDirectory(_plugin.Game), stem, ct);

    private async Task<CalibrationResult> RunPhase5Async(CalibrationRequest request, CancellationToken ct)
    {
        const string stage = "Phase5";
        if (request.CurrentSuiteResult is null)
            return new CalibrationResult { Success = false, Message = "Phase 5 requires a current SuiteResult." };

        var analysis = await _phase5.AnalyzeAsync(_plugin.Game, request.CurrentSuiteResult,
            request.BaselineSuiteResult, request.CurrentValidationThresholds, ct).ConfigureAwait(false);
        var recs = analysis.ApprovalRequests.Select(a => a.Proposal).ToList();
        foreach (var approval in analysis.ApprovalRequests)
        {
            _decisions.RecommendationGenerated(stage, null, approval.Proposal.Summary, approval.Proposal.Confidence);
            _decisions.HumanApprovalRequired(stage, null,
                $"queued approval {approval.Id}; Phase 5 never applies consequential changes automatically.", approval.Proposal.Confidence);
        }

        var graph = await _db.GetBaselineGraphAsync(_plugin.Game, ct).ConfigureAwait(false) ?? new MenuGraph { Env = request.Env };
        var report = await _report.GenerateAsync(new CalibrationReportModel
        {
            Game = _plugin.Game, Name = _plugin.Name, SuiteVersion = request.Env.SuiteVersion,
            GeneratedAtIso = DateTime.Now.ToString("o"), Env = request.Env, Graph = graph,
            Decisions = _decisions.Entries.ToList(), Recommendations = recs, Phase5 = analysis
        }, _db.HistoryDirectory(_plugin.Game), $"phase5_report_{DateTime.Now:yyyyMMdd-HHmmss}", ct).ConfigureAwait(false);

        var regressions = analysis.Regression?.Findings.Count(f => f.Severity is RegressionSeverity.Warning or RegressionSeverity.Critical) ?? 0;
        var message = $"Phase 5 distilled {analysis.SharedPatterns.Count} shared pattern(s), generated {analysis.ThresholdRecommendations.Count} threshold proposal(s), and found {regressions} actionable regression(s); {analysis.ApprovalRequests.Count} proposal(s) are in the human approval queue.";
        _log.Info(stage, message);
        return new CalibrationResult
        {
            Success = true, Message = message, Graph = graph, Phase5 = analysis,
            Recommendations = recs, Decisions = _decisions.Entries.ToList(), Report = report
        };
    }

    /// <summary>Deterministically detect the plugin's settings controls in this screen's OCR (whole-word, all tokens present).</summary>
    private List<DetectedControl> DetectControls(ExpectedScreen screen, OcrFrame ocr)
    {
        var labels = screen.Controls.Length > 0 ? screen.Controls : Array.Empty<string>();
        var result = new List<DetectedControl>();
        foreach (var label in labels)
        {
            var tokens = label.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool present = tokens.All(t => ocr.FindWordWhole(t) is not null);
            result.Add(new DetectedControl
            {
                Label = label,
                Type = "value",
                Confidence = _confidence.Normalize(new RawScore(present ? 0.8 : 0.0, SourceKind.Ocr)),
                Sources = present ? new List<string> { "ocr" } : new List<string>()
            });
        }
        return result;
    }
}
