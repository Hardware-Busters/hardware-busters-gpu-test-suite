using System.IO;
using GpuSuite.Core.Models;

namespace GpuSuite.App.Services;

public sealed class BotDiagnosticRow
{
    public string GameId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Status { get; init; } = "Unknown";
    public string FailureClass { get; init; } = "—";
    public string Detail { get; init; } = "";
    public int ValidRuns { get; init; }
    public int TotalRuns { get; init; }
    public int InvalidRuns => Math.Max(0, TotalRuns - ValidRuns);
    public int RetryPressure => InvalidRuns;
    public int IncidentCount { get; init; }
    public string RunSummary => $"{ValidRuns}/{TotalRuns} valid · {RetryPressure} retry/invalid · {IncidentCount} incident(s)";
    public string MotionSummary { get; init; } = "Not armed";
    public string LastSuccessfulRoute { get; init; } = "No valid route recorded";
    public string EvidencePath { get; init; } = "";
    public string LatestEvidence { get; init; } = "";
    public DateTime CheckedUtc { get; init; }
    public bool IsHealthy => string.Equals(Status, "Passed", StringComparison.OrdinalIgnoreCase) && InvalidRuns == 0 && IncidentCount == 0;
}

/// <summary>Builds a compact bot/runtime-health audit from the latest saved result for EACH game. A targeted
/// rerun therefore refreshes that game's row without making the other games disappear.</summary>
public sealed class BotDiagnosticsService
{
    private readonly ResultsService _results;

    public BotDiagnosticsService(ResultsService results) => _results = results;

    public async Task<IReadOnlyList<BotDiagnosticRow>> LoadAsync()
    {
        var entries = await _results.LoadAllAsync().ConfigureAwait(false);
        if (entries.Count == 0) return Array.Empty<BotDiagnosticRow>();

        var ids = entries.SelectMany(entry => entry.Result.GameOutcomes.Select(o => o.GameId)
                .Concat(entry.Result.Aggregates.Select(a => a.GameId)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase);
        var rows = new List<BotDiagnosticRow>();
        foreach (var id in ids)
        {
            var latest = entries
                .Where(entry => entry.Result.GameOutcomes.Any(o => string.Equals(o.GameId, id, StringComparison.OrdinalIgnoreCase))
                             || entry.Result.Aggregates.Any(a => string.Equals(a.GameId, id, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(entry => entry.Result.GeneratedUtc)
                .First();
            var outcome = latest.Result.GameOutcomes.FirstOrDefault(o => string.Equals(o.GameId, id, StringComparison.OrdinalIgnoreCase));
            var aggs = latest.Result.Aggregates.Where(a => string.Equals(a.GameId, id, StringComparison.OrdinalIgnoreCase)).ToList();
            int valid = aggs.Sum(a => a.ValidRuns);
            int total = aggs.Sum(a => a.TotalRuns);
            var invalidIssues = aggs.SelectMany(a => a.Runs)
                .Where(r => !r.IsValid)
                .SelectMany(r => r.ValidationIssues)
                .Where(issue => !string.IsNullOrWhiteSpace(issue))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var allRuns = aggs.SelectMany(a => a.Runs).ToList();
            var motion = allRuns.Where(run => run.IsValid && run.MeasuredMotion is not null)
                .Select(run => run.MeasuredMotion!).ToList();
            var traversals = allRuns.Where(run => run.IsValid)
                .SelectMany(run => run.BotTraversals ?? new List<BotTraversalStats>()).ToList();
            string motionSummary;
            if (traversals.Count > 0)
            {
                int probes = traversals.Sum(t => t.MotionProbes);
                int healthy = traversals.Sum(t => t.HealthyMotionProbes);
                motionSummary = $"Adaptive route · {healthy}/{probes} healthy probes" +
                    $" · {traversals.Sum(t => t.RecoveryTurns)} recovery turn(s)";
            }
            else
            {
                motionSummary = motion.Count == 0
                    ? "Not armed for this route"
                    : $"{motion.Sum(m => m.Probes)} probes · mean {motion.Average(m => m.MeanScore):0.000} · min {motion.Min(m => m.MinScore):0.000}";
            }
            var lastValid = allRuns.Where(run => run.IsValid).OrderByDescending(run => run.EndedUtc).FirstOrDefault();
            string lastRoute = lastValid is null
                ? "No valid route recorded"
                : $"{lastValid.SceneId} · {lastValid.ResolutionName} · {(string.IsNullOrWhiteSpace(lastValid.VariantId) ? "default" : lastValid.VariantId)} · {lastValid.Frames.DurationSec:0.0}s";

            string gameDir = FindGameDirectory(latest.GpuDir, id) ?? latest.GpuDir;
            var evidence = Directory.Exists(gameDir)
                ? Directory.EnumerateFiles(gameDir, "*", SearchOption.AllDirectories)
                    .Where(IsEvidenceFile)
                    .Select(path => new FileInfo(path))
                    .Where(file => EvidenceBelongsToRuns(file, allRuns))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ToList()
                : new List<FileInfo>();
            int incidents = evidence.Count(file => string.Equals(file.Name, "health_incident.json", StringComparison.OrdinalIgnoreCase));
            string status = outcome?.Status.ToString() ?? (valid > 0 ? "Passed" : "Unknown");
            string detail = !string.IsNullOrWhiteSpace(outcome?.Reason) ? outcome.Reason
                : invalidIssues.Count > 0 ? invalidIssues[0]
                : total > 0 ? "All recorded runs passed validation." : "No measured runs were recorded.";

            rows.Add(new BotDiagnosticRow
            {
                GameId = id,
                Name = string.IsNullOrWhiteSpace(outcome?.Name) ? id : outcome.Name,
                Status = status,
                FailureClass = outcome is null || outcome.FailureClass == FailureClass.None ? "—" : outcome.FailureClass.ToString(),
                Detail = detail,
                ValidRuns = valid,
                TotalRuns = total,
                IncidentCount = incidents,
                MotionSummary = motionSummary,
                LastSuccessfulRoute = lastRoute,
                EvidencePath = gameDir,
                LatestEvidence = evidence.FirstOrDefault()?.FullName ?? "",
                CheckedUtc = latest.Result.GeneratedUtc
            });
        }
        return rows;
    }

    private static string? FindGameDirectory(string root, string gameId)
    {
        if (!Directory.Exists(root)) return null;
        var direct = Path.Combine(root, gameId);
        if (Directory.Exists(direct)) return direct;
        return Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .FirstOrDefault(dir => string.Equals(Path.GetFileName(dir), gameId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsEvidenceFile(string path)
    {
        string name = Path.GetFileName(path);
        string ext = Path.GetExtension(path);
        return string.Equals(name, "health_incident.json", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "validation.json", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool EvidenceBelongsToRuns(FileInfo file, IReadOnlyList<RunResult> runs)
    {
        if (runs.Count == 0) return true;
        var started = runs.Min(run => run.StartedUtc).AddMinutes(-2);
        var ended = runs.Max(run => run.EndedUtc).AddMinutes(2);
        return file.LastWriteTimeUtc >= started && file.LastWriteTimeUtc <= ended;
    }
}
