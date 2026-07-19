using System.Security.Cryptography;
using System.Text;
using GpuSuite.Calibration.Persistence;
using GpuSuite.Calibration.Schema;
using GpuSuite.Core.Config;
using GpuSuite.Core.Models;

namespace GpuSuite.Calibration.Modules;

public interface ISharedPatternDistiller
{
    IReadOnlyList<SharedCalibrationPattern> Distill(IReadOnlyDictionary<string, MenuGraph> graphs);
}

/// <summary>
/// Distils only patterns corroborated by two or more games. Identifiers and labels are normalized, and
/// game-specific one-offs are deliberately discarded so the shared library cannot become disguised
/// per-game hard-coding.
/// </summary>
public sealed class DeterministicSharedPatternDistiller : ISharedPatternDistiller
{
    private sealed class Candidate(SharedPatternKind kind, string signature, string description)
    {
        public SharedPatternKind Kind { get; } = kind;
        public string Signature { get; } = signature;
        public string Description { get; } = description;
        public HashSet<string> Games { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Occurrences { get; set; }
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    { "with", "from", "this", "that", "game", "menu", "screen", "select", "option", "button", "value" };

    public IReadOnlyList<SharedCalibrationPattern> Distill(IReadOnlyDictionary<string, MenuGraph> graphs)
    {
        var candidates = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var (game, graph) in graphs)
        {
            var archetypes = graph.Nodes.ToDictionary(n => n.Id, n => Archetype(n), StringComparer.OrdinalIgnoreCase);
            foreach (var node in graph.Nodes)
            {
                var archetype = archetypes[node.Id];
                if (archetype != "unknown")
                    Add(SharedPatternKind.ScreenArchetype, $"screen:{archetype}",
                        $"Reusable '{archetype}' screen archetype.", game);

                foreach (var token in node.Controls.SelectMany(c => Words(c.Label)).Distinct(StringComparer.OrdinalIgnoreCase))
                    Add(SharedPatternKind.OcrAnchor, $"anchor:{token}",
                        $"Whole-word OCR anchor '{token}' recurs across independent games.", game);
            }

            foreach (var edge in graph.Edges)
            {
                var from = archetypes.GetValueOrDefault(edge.From, "unknown");
                var to = archetypes.GetValueOrDefault(edge.To, "unknown");
                var input = NormalizeInput(edge.Input);
                if (from == "unknown" || to == "unknown" || input.Length == 0) continue;
                Add(SharedPatternKind.RouteTransition, $"route:{from}|{input}|{to}",
                    $"Reusable transition {from} --[{input}]--> {to}.", game);
            }
        }

        return candidates.Values
            .Where(c => c.Games.Count >= 2)
            .OrderBy(c => c.Kind).ThenBy(c => c.Signature, StringComparer.OrdinalIgnoreCase)
            .Select(c => new SharedCalibrationPattern
            {
                Id = StableId(c.Kind + "|" + c.Signature), Kind = c.Kind, Signature = c.Signature,
                Description = c.Description, SourceGames = c.Games.OrderBy(x => x).ToList(), Occurrences = c.Occurrences,
                Confidence = Confidence.Of(Math.Min(.95, .72 + .06 * (c.Games.Count - 2) + .02 * Math.Min(5, c.Occurrences)),
                    c.Games.Count >= 3 ? ConfidenceBand.High : ConfidenceBand.Medium,
                    $"corroborated by {c.Games.Count} independent games / {c.Occurrences} observations"),
                UpdatedAtIso = DateTime.UtcNow.ToString("o"), RequiresHumanApproval = true
            }).ToList();

        void Add(SharedPatternKind kind, string signature, string description, string game)
        {
            var key = kind + "|" + signature;
            if (!candidates.TryGetValue(key, out var c)) candidates[key] = c = new(kind, signature, description);
            c.Games.Add(game); c.Occurrences++;
        }
    }

    private static string Archetype(MenuNode node)
    {
        var text = (node.Id + " " + node.SemanticDescription + " " + string.Join(' ', node.Controls.Select(c => c.Label))).ToLowerInvariant();
        if (Contains(text, "benchmark", "performance test")) return "benchmark";
        if (Contains(text, "result", "summary")) return "results";
        if (Contains(text, "display", "graphics", "video")) return "graphics-settings";
        if (Contains(text, "setting", "options")) return "settings";
        if (Contains(text, "accessibility")) return "accessibility";
        if (Contains(text, "main", "home", "title")) return "main-menu";
        if (Contains(text, "continue", "load", "save")) return "load-game";
        return "unknown";
    }

    private static bool Contains(string text, params string[] terms) => terms.Any(text.Contains);
    private static IEnumerable<string> Words(string text)
        => new string((text ?? "").ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4 && !StopWords.Contains(w) && !w.All(char.IsDigit));
    private static string NormalizeInput(string input)
        => string.Join('+', (input ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToUpperInvariant()));
    internal static string StableId(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..20];
}

/// <summary>Robust, conservative recommendations from live valid runs. Proposals are never applied.</summary>
public sealed class DataDrivenThresholdRecommender : IThresholdRecommender
{
    public Task<IReadOnlyList<ThresholdRecommendation>> RecommendAsync(
        IReadOnlyList<RunResult> history, ValidationThresholds current, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var runs = history.Where(r => r.IsValid && r.FrameSource == DataSourceMode.Live &&
                                      r.Frames.FrameCount > 0 && r.Frames.DurationSec > 0 &&
                                      double.IsFinite(r.Frames.AvgFps)).ToList();
        if (runs.Count < 6)
            return Task.FromResult<IReadOnlyList<ThresholdRecommendation>>(Array.Empty<ThresholdRecommendation>());

        var recs = new List<ThresholdRecommendation>();
        AddMinimum("validation.minCaptureSeconds", current.MinCaptureSeconds,
            runs.Select(r => r.Frames.DurationSec), v => Math.Clamp(v * .50, 5, 120), "seconds");
        AddMinimum("validation.minFrameCount", current.MinFrameCount,
            runs.Select(r => (double)r.Frames.FrameCount), v => Math.Clamp(Math.Floor(v * .25), 100, 20_000), "frames");
        AddMinimum("validation.minAvgFps", current.MinAvgFps,
            runs.Select(r => r.Frames.AvgFps), v => Math.Clamp(v * .25, 1, 30), "fps");
        AddMaximum("validation.maxStutterPct", current.MaxStutterPct,
            runs.Select(r => r.FrameGenActive == true ? r.Frames.FrameGenStutterPct : r.Frames.StutterPct),
            v => Math.Clamp(v * 2 + .5, 1, 50), "%");
        var loads = runs.Where(r => r.TelemetrySource == DataSourceMode.Live && r.Telemetry.GpuLoadAvgPct is double)
                        .Select(r => r.Telemetry.GpuLoadAvgPct!.Value).ToList();
        if (loads.Count >= 6)
            AddMinimum("validation.minGpuLoadPct", current.MinGpuLoadPct, loads, v => Math.Clamp(v - 15, 5, 80), "%");

        return Task.FromResult<IReadOnlyList<ThresholdRecommendation>>(recs);

        void AddMinimum(string key, double existing, IEnumerable<double> values, Func<double, double> propose, string unit)
            => Add(key, existing, values, propose(Quantile(values, .05)), unit, "5th percentile with a conservative safety margin");
        void AddMaximum(string key, double existing, IEnumerable<double> values, Func<double, double> propose, string unit)
            => Add(key, existing, values, propose(Quantile(values, .95)), unit, "95th percentile with a conservative safety margin");
        void Add(string key, double existing, IEnumerable<double> source, double proposed, string unit, string method)
        {
            var values = source.Where(double.IsFinite).OrderBy(x => x).ToArray();
            if (values.Length < 6 || !double.IsFinite(proposed)) return;
            proposed = Math.Round(proposed, key.EndsWith("FrameCount", StringComparison.Ordinal) ? 0 : 2);
            var relative = Math.Abs(proposed - existing) / Math.Max(1, Math.Abs(existing));
            if (relative < .10) return;
            recs.Add(new ThresholdRecommendation
            {
                Key = key, Scope = "observed live roster", CurrentValue = existing.ToString("0.##"),
                ProposedValue = proposed.ToString("0.##"), SampleCount = values.Length,
                ObservedRange = $"{values[0]:0.##}..{values[^1]:0.##} {unit}",
                Rationale = $"Derived from {values.Length} valid live windows using the {method}; review against deliberately short/light profiles before approval.",
                Confidence = Confidence.Of(Math.Min(.95, .72 + Math.Min(20, values.Length) * .01),
                    values.Length >= 12 ? ConfidenceBand.High : ConfidenceBand.Medium, "robust order statistic; no synthetic/invalid runs"),
                RequiresHumanApproval = true
            });
        }
    }

    private static double Quantile(IEnumerable<double> source, double q)
    {
        var values = source.Where(double.IsFinite).OrderBy(x => x).ToArray();
        if (values.Length == 0) return double.NaN;
        var p = Math.Clamp(q, 0, 1) * (values.Length - 1);
        var lo = (int)Math.Floor(p); var hi = (int)Math.Ceiling(p);
        return lo == hi ? values[lo] : values[lo] + (values[hi] - values[lo]) * (p - lo);
    }
}

public interface IRegressionAnalyzer
{
    RegressionAnalysis Analyze(SuiteResult baseline, SuiteResult current);
}

/// <summary>Compares only matching cells with compatible run-time settings fingerprints.</summary>
public sealed class DeterministicRegressionAnalyzer : IRegressionAnalyzer
{
    public RegressionAnalysis Analyze(SuiteResult baseline, SuiteResult current)
    {
        var analysis = new RegressionAnalysis
        {
            BaselineGeneratedUtc = baseline.GeneratedUtc.ToString("o"),
            CurrentGeneratedUtc = current.GeneratedUtc.ToString("o")
        };
        if (!Equal(baseline.System.GpuName, current.System.GpuName) || !Equal(baseline.System.CpuName, current.System.CpuName))
        {
            analysis.EnvironmentChanges.Add($"hardware changed: GPU '{baseline.System.GpuName}' → '{current.System.GpuName}', CPU '{baseline.System.CpuName}' → '{current.System.CpuName}'");
            analysis.EnvironmentComparable = false;
            analysis.Summary = "No performance comparison: GPU or CPU changed.";
            analysis.Confidence = Confidence.Abstain("hardware mismatch");
            return analysis;
        }
        analysis.EnvironmentComparable = true;
        if (!Equal(baseline.System.DriverVersion, current.System.DriverVersion))
            analysis.EnvironmentChanges.Add($"driver changed: {baseline.System.DriverVersion ?? "unknown"} → {current.System.DriverVersion ?? "unknown"}");
        if (!Equal(baseline.System.OsVersion, current.System.OsVersion))
            analysis.EnvironmentChanges.Add($"OS changed: {baseline.System.OsVersion ?? "unknown"} → {current.System.OsVersion ?? "unknown"}");
        if (!Equal(baseline.System.SuiteVersion, current.System.SuiteVersion))
            analysis.EnvironmentChanges.Add($"suite changed: {baseline.System.SuiteVersion} → {current.System.SuiteVersion}");

        var oldCells = baseline.Aggregates.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
        foreach (var now in current.Aggregates)
        {
            var key = Key(now);
            if (!oldCells.TryGetValue(key, out var old)) { analysis.SkippedCells++; continue; }
            if (!FingerprintCompatible(old, now))
            {
                analysis.SkippedCells++;
                analysis.Findings.Add(new RegressionFinding
                {
                    CellKey = key, Metric = "settings-fingerprint", Severity = RegressionSeverity.Info,
                    Classification = "non-comparable", Rationale = "Run-time render settings or frame-generation state changed; performance comparison suppressed.",
                    Confidence = Confidence.Of(.98, ConfidenceBand.High, "exact fingerprint mismatch")
                });
                continue;
            }
            analysis.ComparedCells++;
            var noise = Math.Max(5, 2 * ((old.FpsVariancePct ?? 0) + (now.FpsVariancePct ?? 0)));
            AddDrop(key, "avg-fps", old.AvgFps, now.AvgFps, noise, "performance");
            AddDrop(key, "1%-low", old.P1LowFps, now.P1LowFps, Math.Max(10, noise), "frametime");
            if (now.StutterPct > Math.Max(old.StutterPct + .5, old.StutterPct * 2) && now.StutterPct > 1)
                Add(key, "stutter", old.StutterPct, now.StutterPct, RegressionSeverity.Warning, "frametime",
                    $"stutter rose beyond both the 0.5-point and 2× noise guards ({old.StutterPct:0.##}% → {now.StutterPct:0.##}%).");
            if (now.ValidRuns < old.ValidRuns || (now.TotalRuns > 0 && (double)now.ValidRuns / now.TotalRuns < .75))
                Add(key, "valid-run-rate", old.TotalRuns == 0 ? 0 : 100d * old.ValidRuns / old.TotalRuns,
                    now.TotalRuns == 0 ? 0 : 100d * now.ValidRuns / now.TotalRuns,
                    now.ValidRuns == 0 ? RegressionSeverity.Critical : RegressionSeverity.Warning, "reliability",
                    $"valid runs changed {old.ValidRuns}/{old.TotalRuns} → {now.ValidRuns}/{now.TotalRuns}.");
        }

        var actionable = analysis.Findings.Count(f => f.Severity is RegressionSeverity.Warning or RegressionSeverity.Critical);
        analysis.Summary = $"Compared {analysis.ComparedCells} like-for-like cell(s); skipped {analysis.SkippedCells}; found {actionable} actionable regression(s).";
        analysis.Confidence = analysis.ComparedCells == 0
            ? Confidence.Abstain("no comparable cells")
            : Confidence.Of(analysis.EnvironmentChanges.Count == 0 ? .93 : .82,
                analysis.EnvironmentChanges.Count == 0 ? ConfidenceBand.High : ConfidenceBand.Medium,
                analysis.EnvironmentChanges.Count == 0 ? "matching environment and settings fingerprints" : "hardware matches; environment changes are disclosed");
        return analysis;

        void AddDrop(string key, string metric, double before, double after, double tolerancePct, string classification)
        {
            if (before <= 0 || !double.IsFinite(before) || !double.IsFinite(after)) return;
            var delta = 100 * (after - before) / before;
            if (delta >= -tolerancePct) return;
            Add(key, metric, before, after, delta <= -20 ? RegressionSeverity.Critical : RegressionSeverity.Warning,
                classification, $"{metric} fell {Math.Abs(delta):0.0}%, beyond the {tolerancePct:0.0}% repeat-variance guard.");
        }
        void Add(string key, string metric, double before, double after, RegressionSeverity severity, string classification, string rationale)
            => analysis.Findings.Add(new RegressionFinding
            {
                CellKey = key, Metric = metric, BaselineValue = before, CurrentValue = after,
                DeltaPct = before == 0 ? null : 100 * (after - before) / before,
                Severity = severity, Classification = classification, Rationale = rationale,
                Confidence = Confidence.Of(.90, ConfidenceBand.High, "like-for-like aggregate with variance guard")
            });
    }

    private static string Key(SceneResolutionAggregate a)
        => $"{a.GameId}|{a.SceneId}|{a.VariantId}|{a.ResolutionName}";
    private static bool Equal(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
    private static bool FingerprintCompatible(SceneResolutionAggregate a, SceneResolutionAggregate b)
    {
        static string? Signature(SceneResolutionAggregate x)
        {
            var run = x.Runs.FirstOrDefault(r => r.IsValid);
            if (run is null) return null;
            var fp = run.SettingsFingerprint;
            var settings = fp is null ? "<none>" : string.Join(';', fp.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value));
            return settings + "|fg=" + (run.FrameGenActive?.ToString() ?? "unknown");
        }
        return string.Equals(Signature(a), Signature(b), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Coordinates Phase 5 and persists only evidence plus inert approval requests.</summary>
public sealed class Phase5Coordinator
{
    private readonly ISharedPatternDistiller _distiller;
    private readonly IThresholdRecommender _thresholds;
    private readonly IRegressionAnalyzer _regression;
    private readonly ICalibrationDatabase _db;

    public Phase5Coordinator(ICalibrationDatabase db, ISharedPatternDistiller? distiller = null,
        IThresholdRecommender? thresholds = null, IRegressionAnalyzer? regression = null)
    {
        _db = db;
        _distiller = distiller ?? new DeterministicSharedPatternDistiller();
        _thresholds = thresholds ?? new DataDrivenThresholdRecommender();
        _regression = regression ?? new DeterministicRegressionAnalyzer();
    }

    public async Task<Phase5Analysis> AnalyzeAsync(string game, SuiteResult current, SuiteResult? baseline,
        ValidationThresholds currentThresholds, CancellationToken ct)
    {
        var graphs = await _db.GetAllBaselineGraphsAsync(ct).ConfigureAwait(false);
        var patterns = _distiller.Distill(graphs).ToList();
        await _db.SaveSharedPatternsAsync(patterns, ct).ConfigureAwait(false);
        var runs = current.Aggregates.SelectMany(a => a.Runs).ToList();
        var thresholdRecs = (await _thresholds.RecommendAsync(runs, currentThresholds, ct).ConfigureAwait(false)).ToList();
        var regression = baseline is null ? null : _regression.Analyze(baseline, current);
        if (regression is not null) await _db.AppendRegressionAsync(game, regression, ct).ConfigureAwait(false);

        var proposals = new List<Recommendation>();
        if (patterns.Count > 0)
            proposals.Add(Recommendation.For(RecommendationKind.Profile,
                "Review updated cross-game calibration library",
                $"Shared/Templates/patterns.json contains {patterns.Count} corroborated pattern(s)",
                Confidence.Of(patterns.Average(p => p.Confidence.Value),
                    patterns.Any(p => p.Confidence.Band == ConfidenceBand.High) ? ConfidenceBand.High : ConfidenceBand.Medium,
                    "every shared pattern has at least two independent source games"),
                "The library is persisted as inert evidence; approving it still does not edit any live game profile."));
        proposals.AddRange(thresholdRecs.Select(t => Recommendation.For(RecommendationKind.Threshold,
            $"Review {t.Key}", $"{t.CurrentValue} → {t.ProposedValue} ({t.Scope})", t.Confidence, t.Rationale)));
        if (regression is not null && regression.Findings.Any(f => f.Severity is RegressionSeverity.Warning or RegressionSeverity.Critical))
            proposals.Add(Recommendation.For(RecommendationKind.Profile, "Investigate detected benchmark regression",
                regression.Summary, regression.Confidence,
                "Regression evidence is pre-filled for review; no threshold, route, or profile is changed automatically."));

        var existing = await _db.GetApprovalsAsync(null, ct).ConfigureAwait(false);
        var approvals = new List<ApprovalRequest>();
        foreach (var proposal in proposals)
        {
            var id = DeterministicSharedPatternDistiller.StableId(game + "|" + proposal.Kind + "|" + proposal.ProposedChange);
            var prior = existing.FirstOrDefault(x => x.Id == id);
            if (prior is not null) { approvals.Add(prior); continue; }
            var request = new ApprovalRequest
            {
                Id = id, Game = game, Proposal = proposal, Status = ApprovalStatus.Pending,
                CreatedAtIso = DateTime.UtcNow.ToString("o")
            };
            await _db.QueueApprovalAsync(request, ct).ConfigureAwait(false);
            approvals.Add(request);
        }
        return new Phase5Analysis
        {
            SharedPatterns = patterns, ThresholdRecommendations = thresholdRecs,
            Regression = regression, ApprovalRequests = approvals
        };
    }
}
