using GpuSuite.Calibration.Schema;
using GpuSuite.Calibration.Observation;
using GpuSuite.Core.Config;
using GpuSuite.Core.Models;

namespace GpuSuite.Calibration.Modules;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  Layout Comparison Engine — drift detection. Phase-2-relevant, deterministic, READ-ONLY → real.
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Deterministically compares two layout snapshots of the same screen (narration is the LLM's job).</summary>
public interface ILayoutComparer
{
    LayoutDiff Compare(LayoutSnapshot baseline, LayoutSnapshot current);
}
/// <summary>
/// Default layout comparer: diffs the set of control labels and the set of OCR words between two
/// snapshots. Pure and safe — it reads two records and returns the differences; it never touches a game.
/// This is what powers the Cyberpunk Phase-2 drift dry-run (clean true-negative on an unchanged game,
/// correct true-positive on an injected change).
/// </summary>
public sealed class LayoutComparer : ILayoutComparer
{
    public LayoutDiff Compare(LayoutSnapshot baseline, LayoutSnapshot current)
    {
        var diff = new LayoutDiff();
        var screen = current.Screen;

        var baseControls = baseline.Controls.Select(c => c.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var curControls = current.Controls.Select(c => c.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var added in curControls.Except(baseControls))
            diff.Changes.Add(new LayoutChange { Screen = screen, Change = $"control added: '{added}'", Impact = "may shift a route step count", Confidence = Confidence.Of(0.7, ConfidenceBand.Medium, "deterministic control-set diff") });
        foreach (var removed in baseControls.Except(curControls))
            diff.Changes.Add(new LayoutChange { Screen = screen, Change = $"control removed: '{removed}'", Impact = "a route targeting it may break", Confidence = Confidence.Of(0.7, ConfidenceBand.Medium, "deterministic control-set diff") });

        var baseWords = baseline.Ocr.Select(w => w.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var curWords = current.Ocr.Select(w => w.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var added in curWords.Except(baseWords))
            diff.Changes.Add(new LayoutChange { Screen = screen, Change = $"text appeared: '{added}'", Impact = "anchor set changed", Confidence = Confidence.Of(0.55, ConfidenceBand.Medium, "deterministic OCR-word diff") });
        foreach (var removed in baseWords.Except(curWords))
            diff.Changes.Add(new LayoutChange { Screen = screen, Change = $"text disappeared: '{removed}'", Impact = "anchor may be gone", Confidence = Confidence.Of(0.55, ConfidenceBand.Medium, "deterministic OCR-word diff") });

        return diff;
    }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  Route Validator — pre-run check. Phase-2-relevant, deterministic, READ-ONLY → real.
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Checks a recorded route still reaches its goal on the live menu graph, before a run.</summary>
public interface IRouteValidator
{
    Task<RouteVerdict> ValidateAsync(MenuGraph live, RouteRecord route, CancellationToken ct);
}

/// <summary>
/// Default route validator: confirms every screen the route expects to pass through (and its goal) exists
/// as a node in the live graph. Advisory — a failed check WARNS (and optionally pauses for a human); it
/// never edits the route mid-run. Read-only.
/// </summary>
public sealed class RouteValidator : IRouteValidator
{
    public Task<RouteVerdict> ValidateAsync(MenuGraph live, RouteRecord route, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(route.Goal) || live.FindNode(route.Goal) is null)
            return Task.FromResult(Fail($"goal screen '{route.Goal}' is absent from the live graph"));
        if (route.Steps.Count == 0)
            return Task.FromResult(Fail("route has no steps"));

        var missing = route.Steps
            .Select(s => s.ExpectScreen)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(s => live.FindNode(s!) is null)
            .ToList();

        if (missing.Count > 0)
            return Task.FromResult(Fail($"live graph is missing {missing.Count} expected screen(s): {string.Join(", ", missing)}"));

        var expected = route.Steps.Where(s => !string.IsNullOrWhiteSpace(s.ExpectScreen)).ToList();
        if (expected.Count == 0 || !string.Equals(expected[^1].ExpectScreen, route.Goal, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Fail($"route does not terminate at goal '{route.Goal}'"));

        var incoming = live.Edges.Select(e => e.To).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = live.Nodes.FirstOrDefault(n => !incoming.Contains(n.Id))?.Id;
        if (root is not null && !HasEdge(root, expected[0]))
            return Task.FromResult(Fail($"missing initial transition {root} --[{expected[0].Input}]--> {expected[0].ExpectScreen}"));

        for (var i = 1; i < expected.Count; i++)
        {
            var from = expected[i - 1].ExpectScreen!;
            var step = expected[i];
            if (!HasEdge(from, step))
                return Task.FromResult(Fail($"missing transition {from} --[{step.Input}]--> {step.ExpectScreen}"));
        }

        return Task.FromResult(new RouteVerdict
        {
            Valid = true,
            Confidence = Confidence.Of(0.92, ConfidenceBand.High, "goal, screens, inputs, and graph transitions all match")
        });

        static RouteVerdict Fail(string reason) => new()
        {
            Valid = false,
            Reason = reason,
            Confidence = Confidence.Of(0.95, ConfidenceBand.High, "deterministic graph path check")
        };

        bool HasEdge(string from, RouteStep step) => live.Edges.Any(e =>
            string.Equals(e.From, from, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.To, step.ExpectScreen, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Input, step.Input, StringComparison.OrdinalIgnoreCase));
    }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  Gameplay Validation Engine — Phase 3, deterministic measured-window evidence.
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>During-run check: is the measured window real, right-scene gameplay? (Phase 3.)</summary>
public interface IGameplayValidator
{
    Task<GameplayVerdict> ValidateWindowAsync(GameplayValidationContext context, CancellationToken ct);
    Task<GameplayVerdict> MatchSceneAsync(GameplayValidationContext context, CancellationToken ct);
}

public sealed class GameplayValidationContext
{
    public string SceneDescriptor { get; set; } = "";
    public string ExpectedGameId { get; set; } = "";
    public string ExpectedSceneId { get; set; } = "";
    public RunResult? Run { get; set; }
    public int MinimumFrames { get; set; } = 120;
    public double MinimumDurationSeconds { get; set; } = 5;
    public double MinimumAverageFps { get; set; } = 1;
    public double MinimumGpuLoadPct { get; set; } = 10;
    public bool AllowStaticScene { get; set; }
    public GameplayEvidence? Evidence { get; set; }
    public bool RequireVisualEvidence { get; set; }
    public double MinimumSceneSimilarity { get; set; } = 0.75;
    public double MinimumSpawnSimilarity { get; set; } = 0.70;
    public int MaximumLateShaderHitches { get; set; }
    public double MinimumStableSecondsAfterShaderHitch { get; set; } = 5;
}

/// <summary>Phase-3 validator using the suite's measured-window provenance, cadence, telemetry, and motion evidence.</summary>
public sealed class DeterministicGameplayValidator : IGameplayValidator
{
    public async Task<GameplayVerdict> ValidateWindowAsync(GameplayValidationContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var run = context.Run;
        if (run is null)
            return Reject("no RunResult was supplied");
        if (!double.IsFinite(context.MinimumSceneSimilarity) || !double.IsFinite(context.MinimumSpawnSimilarity) ||
            !double.IsFinite(context.MinimumStableSecondsAfterShaderHitch) ||
            context.MinimumSceneSimilarity is < 0 or > 1 || context.MinimumSpawnSimilarity is < 0 or > 1 ||
            context.MaximumLateShaderHitches < 0 || context.MinimumStableSecondsAfterShaderHitch < 0)
            return Reject("invalid Phase-3 thresholds (similarities must be 0..1; hitch limits must be non-negative)");

        var checks = new List<string>();
        void Check(bool pass, string message) { if (!pass) checks.Add(message); }
        Check(run.FrameSource is DataSourceMode.Live or DataSourceMode.Replay, "frame source is synthetic");
        if (!string.IsNullOrWhiteSpace(context.ExpectedGameId))
            Check(string.Equals(run.GameId, context.ExpectedGameId, StringComparison.OrdinalIgnoreCase),
                $"game id '{run.GameId}' does not match '{context.ExpectedGameId}'");
        if (!string.IsNullOrWhiteSpace(context.ExpectedSceneId))
            Check(string.Equals(run.SceneId, context.ExpectedSceneId, StringComparison.OrdinalIgnoreCase),
                $"scene id '{run.SceneId}' does not match '{context.ExpectedSceneId}'");
        Check(run.Verdict == RunVerdict.Valid, $"run verdict is {run.Verdict}");
        Check(run.ValidationIssues.Count == 0, string.Join("; ", run.ValidationIssues));
        Check(run.Frames.FrameCount >= context.MinimumFrames, $"only {run.Frames.FrameCount} frames");
        Check(run.Frames.DurationSec >= context.MinimumDurationSeconds, $"window is only {run.Frames.DurationSec:0.0}s");
        Check(double.IsFinite(run.Frames.AvgFps) && run.Frames.AvgFps >= context.MinimumAverageFps, $"average FPS is {run.Frames.AvgFps:0.0}");
        if (run.Telemetry.GpuLoadAvgPct is double load)
            Check(load >= context.MinimumGpuLoadPct, $"GPU load is only {load:0.0}%");
        if (!context.AllowStaticScene && run.MeasuredMotion is { Probes: > 0 } motion)
            Check(motion.MeanScore >= motion.FloorScore && motion.BelowFloor < motion.Probes,
                $"motion {motion.MeanScore:0.000} is below the calibrated floor {motion.FloorScore:0.000}");

        if (checks.Count > 0)
            return Reject(string.Join("; ", checks));

        GameplayVerdict visual;
        if (context.Evidence is not null)
            visual = await MatchSceneAsync(context, ct).ConfigureAwait(false);
        else if (context.RequireVisualEvidence)
            return Reject("scene/spawn/HUD evidence is required but was not supplied");
        else
            visual = new GameplayVerdict
            {
                IsGameplay = true,
                SceneMatch = true,
                SpawnMatch = true,
                HudMatch = true,
                Confidence = Confidence.Of(0.80, ConfidenceBand.High, "run metadata only; visual evidence was optional")
            };

        if (!visual.IsGameplay || !visual.SceneMatch || !visual.SpawnMatch || !visual.HudMatch)
            return visual;

        var descriptor = string.IsNullOrWhiteSpace(context.SceneDescriptor) ? run.SceneId : context.SceneDescriptor;
        return new GameplayVerdict
        {
            IsGameplay = true,
            SceneMatch = visual.SceneMatch,
            SpawnMatch = visual.SpawnMatch,
            HudMatch = visual.HudMatch,
            ShaderHitchesSettled = visual.ShaderHitchesSettled,
            VisualEvidenceUsed = context.Evidence is not null,
            Confidence = Confidence.Of(context.Evidence is null ? 0.82 : 0.94, ConfidenceBand.High,
                context.Evidence is null ? "measured-window health + exact run metadata" : "measured-window health + visual/spawn/HUD evidence"),
            Rationale = $"'{descriptor}' passed provenance, verdict, cadence, duration, telemetry, motion, scene, spawn, HUD, and shader-settle checks.",
            Evidence = MergeEvidence(context.Evidence?.Evidence,
                $"frames={run.Frames.FrameCount}; duration={run.Frames.DurationSec:0.00}s; avgFps={run.Frames.AvgFps:0.00}; source={run.FrameSource}")
        };
    }

    public Task<GameplayVerdict> MatchSceneAsync(GameplayValidationContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var evidence = context.Evidence;
        if (evidence is null) return Task.FromResult(Reject("no visual gameplay evidence was supplied"));
        var failures = new List<string>();
        if (!double.IsFinite(context.MinimumSceneSimilarity) || !double.IsFinite(context.MinimumSpawnSimilarity) ||
            !double.IsFinite(context.MinimumStableSecondsAfterShaderHitch) ||
            context.MinimumSceneSimilarity is < 0 or > 1 || context.MinimumSpawnSimilarity is < 0 or > 1 ||
            context.MaximumLateShaderHitches < 0 || context.MinimumStableSecondsAfterShaderHitch < 0)
            failures.Add("invalid Phase-3 thresholds");
        if (evidence.StartupShaderHitches < 0 || evidence.LateShaderHitches < 0 ||
            !double.IsFinite(evidence.StableSecondsAfterLastHitch) || evidence.StableSecondsAfterLastHitch < 0)
            failures.Add("invalid shader-hitch evidence (counts and stable seconds must be finite and non-negative)");
        bool scene = ScorePass(evidence.SceneSimilarity, context.MinimumSceneSimilarity, "scene", failures);
        bool spawn = ScorePass(evidence.SpawnSimilarity, context.MinimumSpawnSimilarity, "spawn", failures);

        var expectation = (evidence.HudExpectation ?? "ignore").Trim().ToLowerInvariant();
        bool hud = expectation switch
        {
            "ignore" => true,
            "present" when evidence.HudDetected == true => true,
            "absent" when evidence.HudDetected == false => true,
            "present" or "absent" => false,
            _ => false
        };
        if (!hud)
            failures.Add(expectation is "present" or "absent"
                ? $"HUD expected {expectation} but observed {(evidence.HudDetected is null ? "unknown" : evidence.HudDetected.Value ? "present" : "absent")}"
                : $"unsupported HUD expectation '{evidence.HudExpectation}'");

        bool startupSettled = evidence.StartupShaderHitches == 0 ||
            evidence.StableSecondsAfterLastHitch >= context.MinimumStableSecondsAfterShaderHitch;
        if (!startupSettled)
            failures.Add($"startup shader hitches did not settle ({evidence.StableSecondsAfterLastHitch:0.0}s stable; need {context.MinimumStableSecondsAfterShaderHitch:0.0}s)");
        bool lateHitchesAcceptable = evidence.LateShaderHitches <= context.MaximumLateShaderHitches;
        if (!lateHitchesAcceptable)
            failures.Add($"late/structural shader hitches={evidence.LateShaderHitches} exceed {context.MaximumLateShaderHitches}");
        bool hitchesSettled = startupSettled && lateHitchesAcceptable;

        if (failures.Count > 0)
            return Task.FromResult(new GameplayVerdict
            {
                IsGameplay = false,
                SceneMatch = scene,
                SpawnMatch = spawn,
                HudMatch = hud,
                ShaderHitchesSettled = hitchesSettled,
                VisualEvidenceUsed = true,
                Confidence = Confidence.Of(0.94, ConfidenceBand.High, "deterministic Phase-3 evidence check failed"),
                Rationale = string.Join("; ", failures),
                Evidence = evidence.Evidence
            });

        return Task.FromResult(new GameplayVerdict
        {
            IsGameplay = true,
            SceneMatch = true,
            SpawnMatch = true,
            HudMatch = true,
            ShaderHitchesSettled = hitchesSettled,
            VisualEvidenceUsed = true,
            Confidence = Confidence.Of(0.94, ConfidenceBand.High, "scene, spawn, HUD, and shader evidence passed"),
            Rationale = $"scene={evidence.SceneSimilarity:0.000}; spawn={evidence.SpawnSimilarity:0.000}; HUD={expectation}; startupHitches={evidence.StartupShaderHitches}; lateHitches={evidence.LateShaderHitches}",
            Evidence = evidence.Evidence
        });

        static bool ScorePass(double? score, double minimum, string label, List<string> failures)
        {
            if (score is not double value || !double.IsFinite(value) || value < 0 || value > 1)
            { failures.Add($"{label} similarity is missing or invalid"); return false; }
            if (value < minimum)
            { failures.Add($"{label} similarity {value:0.000} is below {minimum:0.000}"); return false; }
            return true;
        }
    }

    private static GameplayVerdict Reject(string reason) => new()
    {
        IsGameplay = false,
        SceneMatch = false,
        SpawnMatch = false,
        HudMatch = false,
        Confidence = Confidence.Of(0.95, ConfidenceBand.High, "deterministic gameplay evidence check failed"),
        Rationale = reason,
        Evidence = new Evidence { Note = reason }
    };

    private static Evidence MergeEvidence(Evidence? visual, string note)
        => new()
        {
            Screenshots = visual?.Screenshots.ToList() ?? new(),
            Ocr = visual?.Ocr.ToList() ?? new(),
            PromptId = visual?.PromptId,
            Note = string.IsNullOrWhiteSpace(visual?.Note) ? note : visual!.Note + "; " + note
        };
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  Bot Calibration Engine — Phase 3, graph-derived drafts (drafts-only by design).
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Synthesizes/repairs bot route DRAFTS from a graph (Phase 3). Output is never executed (P7).</summary>
public interface IBotCalibrator
{
    BotDraft SynthesizeRoute(MenuGraph graph, string goalNode);
    BotDraft RepairRoute(MenuGraph graph, RouteRecord stale, string goalNode);
    BotDraft CalibrateFromRecording(MenuGraph graph, NavRecording recording);
}

/// <summary>Builds the shortest reachable route and repairs stale routes from the current graph.</summary>
public sealed class GraphBotCalibrator : IBotCalibrator
{
    public BotDraft SynthesizeRoute(MenuGraph graph, string goalNode)
    {
        if (graph.FindNode(goalNode) is null)
            return Abstain(goalNode, "goal is absent from the graph");

        var incoming = graph.Edges.Select(e => e.To).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var start = graph.Nodes.FirstOrDefault(n => !incoming.Contains(n.Id))?.Id ?? graph.Nodes.FirstOrDefault()?.Id;
        if (start is null) return Abstain(goalNode, "graph has no nodes");

        var queue = new Queue<string>();
        var previous = new Dictionary<string, MenuEdge>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        queue.Enqueue(start);
        while (queue.Count > 0 && !seen.Contains(goalNode))
        {
            var current = queue.Dequeue();
            foreach (var edge in graph.Edges.Where(e => string.Equals(e.From, current, StringComparison.OrdinalIgnoreCase)))
                if (seen.Add(edge.To)) { previous[edge.To] = edge; queue.Enqueue(edge.To); }
        }
        if (!seen.Contains(goalNode)) return Abstain(goalNode, $"no path from '{start}' to '{goalNode}'");

        var path = new List<MenuEdge>();
        for (var at = goalNode; !string.Equals(at, start, StringComparison.OrdinalIgnoreCase);)
        {
            var edge = previous[at];
            path.Add(edge);
            at = edge.From;
        }
        path.Reverse();
        var reproduced = path.Count(e => e.Reproducible);
        return new BotDraft
        {
            Goal = goalNode,
            Env = graph.Env,
            Steps = path.Select(e => new RouteStep { Input = e.Input, ExpectScreen = e.To }).ToList(),
            Rationale = $"shortest graph path from '{start}' to '{goalNode}' ({path.Count} step(s), {reproduced} reproducible edge(s)).",
            CreatedFrom = "graph",
            Confidence = Confidence.Of(path.All(e => e.Reproducible) ? 0.90 : 0.72,
                path.All(e => e.Reproducible) ? ConfidenceBand.High : ConfidenceBand.Medium,
                "deterministic breadth-first graph route")
        };
    }

    public BotDraft RepairRoute(MenuGraph graph, RouteRecord stale, string goalNode)
    {
        var draft = SynthesizeRoute(graph, goalNode);
        draft.CreatedFrom = "repair";
        draft.DiffAgainst = DescribeDiff(stale.Steps, draft.Steps);
        draft.Rationale = $"Recomputed the shortest valid graph path. {draft.DiffAgainst}";
        return draft;
    }

    public BotDraft CalibrateFromRecording(MenuGraph graph, NavRecording recording)
    {
        if (graph.FindNode(recording.StartScreen) is null || graph.FindNode(recording.Goal) is null)
            return Abstain(recording.Goal, "recording start or goal is absent from the graph");
        var current = recording.StartScreen;
        var steps = new List<RouteStep>();
        foreach (var recorded in recording.Steps)
        {
            var edge = graph.Edges.FirstOrDefault(e =>
                string.Equals(e.From, current, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.To, recorded.ObservedScreen, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Input, recorded.Input, StringComparison.OrdinalIgnoreCase));
            if (edge is null)
                return Abstain(recording.Goal, $"recording diverges at {current} --[{recorded.Input}]--> {recorded.ObservedScreen}");
            var anchor = SelectRobustAnchor(recorded, recording);
            steps.Add(new RouteStep { Input = edge.Input, ExpectScreen = edge.To, GuardAnchor = anchor });
            current = edge.To;
        }
        if (!string.Equals(current, recording.Goal, StringComparison.OrdinalIgnoreCase))
            return Abstain(recording.Goal, $"recording ended at '{current}', not goal '{recording.Goal}'");
        return new BotDraft
        {
            Goal = recording.Goal,
            Env = graph.Env,
            Steps = steps,
            CreatedFrom = "recording",
            Rationale = $"Aligned {steps.Count} recorded step(s) to graph edges; retained {steps.Count(s => s.GuardAnchor is not null)} unique whole-word OCR guard(s).",
            Confidence = Confidence.Of(steps.All(s => s.GuardAnchor is not null) ? 0.90 : 0.82, ConfidenceBand.High,
                "recording and graph transitions agree; OCR guards are collision-checked within the recording")
        };
    }

    private static string? SelectRobustAnchor(RecordedNavStep step, NavRecording recording)
        => step.OcrAnchors.Select(a => a.Trim()).FirstOrDefault(candidate =>
            candidate.Length > 0 && candidate.All(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_') &&
            recording.Steps.Count(s => s.OcrAnchors.Any(a => string.Equals(a.Trim(), candidate, StringComparison.OrdinalIgnoreCase))) == 1);

    private static BotDraft Abstain(string goal, string rationale) => new()
    {
        Goal = goal,
        Rationale = rationale,
        Confidence = Confidence.Abstain(rationale)
    };

    private static string DescribeDiff(IReadOnlyList<RouteStep> oldSteps, IReadOnlyList<RouteStep> newSteps)
    {
        static string Render(IEnumerable<RouteStep> s) => string.Join(" | ", s.Select(x => $"{x.Input} -> {x.ExpectScreen}"));
        return $"Old [{Render(oldSteps)}]; new [{Render(newSteps)}].";
    }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  Recovery Engine — Phase 3 deterministic taxonomy + recovery whitelist.
// ─────────────────────────────────────────────────────────────────────────────────────────────

public interface IFailureClassifier { Task<FailureClassification> ClassifyAsync(FailureContextLite ctx, CancellationToken ct); }
public interface IRecoveryAdvisor { Task<RecoveryPlan> AdviseAsync(FailureClassification classification, CancellationToken ct); }
public interface IThresholdRecommender
{
    Task<IReadOnlyList<ThresholdRecommendation>> RecommendAsync(
        IReadOnlyList<RunResult> history, ValidationThresholds current, CancellationToken ct);
}

/// <summary>A minimal failure context (kept here to avoid coupling the LLM context type into recovery).</summary>
public sealed class FailureContextLite
{
    public string Summary { get; set; } = "";
    public List<string> Signals { get; set; } = new();
}

/// <summary>Deterministically classifies known failure signatures and abstains on unknown input.</summary>
public sealed class DeterministicFailureClassifier : IFailureClassifier
{
    public Task<FailureClassification> ClassifyAsync(FailureContextLite ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var text = string.Join(" ", ctx.Signals.Prepend(ctx.Summary)).ToLowerInvariant();
        var (kind, transient, rationale) = text switch
        {
            var t when Has(t, "late/structural shader", "shader instability") => ("shader-instability", false, "late shader-hitch signal"),
            var t when Has(t, "shader", "compile hitch") => ("shader-hitch", true, "transient shader-compile signal"),
            var t when Has(t, "launch", "process", "executable", "launcher") => ("launch", true, "launch/process signal"),
            var t when Has(t, "navigation", "route", "screen", "anchor", "ocr") => ("navigation-drift", false, "route/screen signal"),
            var t when Has(t, "capture", "presentmon", "rtss", "frame source", "trace") => ("capture", true, "capture-provider signal"),
            var t when Has(t, "resolution", "fingerprint", "setting", "config") => ("settings-drift", false, "settings/fingerprint signal"),
            var t when Has(t, "motion", "gameplay", "static", "gpu load", "fps") => ("gameplay-window", true, "gameplay-health signal"),
            var t when Has(t, "temperature", "power", "telemetry", "device", "hardware") => ("hardware-telemetry", true, "hardware/telemetry signal"),
            _ => ("unknown", false, "no taxonomy signal matched")
        };
        return Task.FromResult(new FailureClassification
        {
            FailureClass = kind,
            Transient = transient,
            Confidence = kind == "unknown" ? Confidence.Abstain(rationale) : Confidence.Of(0.86, ConfidenceBand.High, rationale),
            Rationale = rationale,
            Evidence = new Evidence { Note = string.Join("; ", ctx.Signals) }
        });

        static bool Has(string text, params string[] terms) => terms.Any(text.Contains);
    }
}

/// <summary>
/// Produces an ordered plan from the reversible recovery whitelist. A behavior-changing recovery can
/// never be marked auto-eligible because the tag always comes from <see cref="RecoveryPolicy"/>.
/// </summary>
public sealed class DeterministicRecoveryAdvisor : IRecoveryAdvisor
{
    public Task<RecoveryPlan> AdviseAsync(FailureClassification classification, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var kinds = classification.FailureClass switch
        {
            "launch" => new[] { RecommendationKind.RecheckLauncher, RecommendationKind.Relaunch, RecommendationKind.Skip },
            "navigation-drift" => new[] { RecommendationKind.Retry, RecommendationKind.Skip },
            "capture" => new[] { RecommendationKind.Recapture, RecommendationKind.Retry, RecommendationKind.Skip },
            "settings-drift" => new[] { RecommendationKind.Retry, RecommendationKind.Skip },
            "gameplay-window" => new[] { RecommendationKind.Wait, RecommendationKind.Retry, RecommendationKind.Recapture },
            "shader-hitch" => new[] { RecommendationKind.Wait, RecommendationKind.Retry, RecommendationKind.Recapture },
            "shader-instability" => new[] { RecommendationKind.Skip },
            "hardware-telemetry" => new[] { RecommendationKind.Wait, RecommendationKind.Retry, RecommendationKind.Skip },
            _ => new[] { RecommendationKind.Skip }
        };
        return Task.FromResult(new RecoveryPlan
        {
            Steps = kinds.Select(k => new RecoveryStep
            {
                Kind = k,
                Description = k.ToString(),
                AutoEligible = RecoveryPolicy.IsAutoEligible(k),
                Confidence = classification.Confidence
            }).ToList(),
            Confidence = classification.Confidence
        });
    }
}
