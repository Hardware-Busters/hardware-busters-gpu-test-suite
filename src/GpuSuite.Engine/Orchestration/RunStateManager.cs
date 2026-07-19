using System.Security.Cryptography;
using System.Text;
using GpuSuite.Core.Config;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Io;
using GpuSuite.Core.Models;

namespace GpuSuite.Engine.Orchestration;

/// <summary>How an autonomous run treats a pre-existing, interrupted checkpoint.</summary>
public enum ResumeMode
{
    /// <summary>Auto-detect: resume an interrupted matching run if one exists, else start fresh (the default).</summary>
    Auto,
    /// <summary>Explicit --resume: resume if possible; if nothing resumable, start fresh.</summary>
    Resume,
    /// <summary>Explicit --fresh / --discard: ignore + overwrite any prior checkpoint.</summary>
    Fresh
}

/// <summary>
/// Owns the crash-safe <see cref="RunState"/> checkpoint for an autonomous run: decides resume-vs-fresh,
/// records completed cells (saving after EVERY one), and exposes the skip/reload decision the orchestrator
/// uses to continue exactly where an interrupted run stopped. All writes are atomic-ish (temp + move) so a
/// crash mid-write can't corrupt the checkpoint.
/// </summary>
public sealed class RunStateManager
{
    private readonly string _path;
    private readonly RunLogger _log;
    private readonly object _lock = new();

    public RunState State { get; private set; } = new();
    public bool Resuming { get; private set; }

    public RunStateManager(string path, RunLogger log) { _path = path; _log = log; }

    /// <summary>The checkpoint file path for a given results layout.</summary>
    public static string PathFor(RunPaths paths) => Path.Combine(paths.GpuDir, "run_state.json");

    /// <summary>A stable signature of the planned work — resume only matches an identical plan.</summary>
    public static string ComputePlanSignature(IEnumerable<string> gameIds, IEnumerable<string> resolutionNames,
                                              IEnumerable<string> selectedVariantIds, int repeats,
                                              string methodologyIdentity = "")
    {
        var raw = "games=" + string.Join(",", gameIds.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                + "|res=" + string.Join(",", resolutionNames.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                + "|variants=" + string.Join(",", selectedVariantIds.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                + "|repeats=" + repeats
                + "|methodology=" + methodologyIdentity;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash, 0, 8);   // short, stable
    }

    /// <summary>
    /// Hash every input that can change benchmark methodology. This prevents a resumed campaign from mixing
    /// cells produced by different code, thresholds, profiles, bots, or routes under one report.
    /// </summary>
    public static string ComputeMethodologyIdentity(IEnumerable<GameProfile> games, SuiteConfig cfg,
                                                     string profilesDir, Guid engineBuildId)
    {
        var raw = new StringBuilder();
        raw.Append("engine=").Append(engineBuildId.ToString("N"));
        raw.Append("|config=").Append(Json.ToString(cfg));
        foreach (var game in games.OrderBy(g => g.Id, StringComparer.OrdinalIgnoreCase))
            raw.Append("|game:").Append(game.Id).Append('=').Append(Json.ToString(game));

        try
        {
            if (Directory.Exists(profilesDir))
            {
                string root = Path.GetFullPath(profilesDir);
                foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                                              .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                    byte[] bytes = File.ReadAllBytes(file);
                    raw.Append("|file:").Append(rel).Append('=').Append(Convert.ToHexString(SHA256.HashData(bytes)));
                }
            }
        }
        catch (Exception ex)
        {
            // An unreadable methodology input must not accidentally match a previous clean signature.
            raw.Append("|profile-hash-error=").Append(ex.GetType().Name).Append(':').Append(ex.Message);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw.ToString())), 0, 16);
    }

    public static string Cell(string gameId, string sceneId, string? variantId, string resolutionName)
        => $"{gameId}|{sceneId}|{variantId ?? "default"}|{resolutionName}";

    /// <summary>
    /// Decide resume vs. fresh from the mode + an existing checkpoint, and prepare <see cref="State"/>.
    /// Logs the decision prominently so an unattended operator sees it in the run log.
    /// </summary>
    public void Initialize(ResumeMode mode, string gpuName, string planSignature, bool unattended)
    {
        RunState? existing = null;
        try { existing = File.Exists(_path) ? Json.Load<RunState>(_path) : null; }
        catch (Exception ex) { _log.Warn("Resume", $"Could not read prior checkpoint ({ex.Message}) — starting fresh."); existing = null; }

        bool hasInterrupted = existing is not null && !existing.Completed;
        bool signatureMatches = existing is not null && string.Equals(existing.PlanSignature, planSignature, StringComparison.Ordinal);

        if (mode == ResumeMode.Fresh)
        {
            if (existing is not null) _log.Warn("Resume", "--fresh / --discard: ignoring the previous run checkpoint and starting over.");
            StartFresh(gpuName, planSignature, unattended);
            return;
        }

        if (hasInterrupted && !signatureMatches)
        {
            _log.Warn("Resume", "An interrupted run exists but the plan changed (game/resolution/variant/repeats differ) — cannot resume it; starting fresh.");
            StartFresh(gpuName, planSignature, unattended);
            return;
        }

        if (hasInterrupted && signatureMatches && (mode == ResumeMode.Resume || mode == ResumeMode.Auto))
        {
            State = existing!;
            Resuming = true;
            _log.Info("Resume", $"RESUMING the interrupted run {State.RunId} — {State.CompletedCells.Count} benchmark cell(s) already complete across " +
                $"{State.CompletedGames.Count} game(s); continuing exactly where it stopped. (--fresh to discard and start over.)");
            return;
        }

        if (mode == ResumeMode.Resume)
            _log.Info("Resume", existing is null ? "--resume: no prior run found — starting fresh." : "--resume: the prior run already completed — starting fresh.");

        StartFresh(gpuName, planSignature, unattended);
    }

    private void StartFresh(string gpuName, string planSignature, bool unattended)
    {
        State = new RunState
        {
            RunId = "run_" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            StartedUtc = DateTime.UtcNow.ToString("o"),
            UpdatedUtc = DateTime.UtcNow.ToString("o"),
            GpuName = gpuName,
            PlanSignature = planSignature,
            Unattended = unattended
        };
        Resuming = false;
        Save();
    }

    public bool IsCellDone(string key)
    {
        lock (_lock) return State.CompletedCells.Any(c => string.Equals(c.Key, key, StringComparison.Ordinal));
    }

    /// <summary>Record a completed cell + roll-ups and CHECKPOINT (save) immediately — at most one benchmark is ever at risk.</summary>
    public void MarkCellDone(string key, string gameId, string sceneId, string? variantId, string resolutionName, SceneResolutionAggregate agg)
    {
        lock (_lock)
        {
            if (State.CompletedCells.Any(c => c.Key == key)) return;
            State.CompletedCells.Add(new CompletedCell
            {
                Key = key, GameId = gameId, SceneId = sceneId, VariantId = variantId ?? "default", ResolutionName = resolutionName,
                ValidRuns = agg.ValidRuns, TotalRuns = agg.TotalRuns, AvgFps = agg.AvgFps,
                CompletedUtc = DateTime.UtcNow.ToString("o")
            });
            AddDistinct(State.CompletedResolutions, resolutionName);
            AddDistinct(State.CompletedVariants, variantId ?? "default");
            State.UpdatedUtc = DateTime.UtcNow.ToString("o");
            Save();
        }
    }

    public void SetCurrentGame(string gameId)
    {
        lock (_lock) { State.CurrentGame = gameId; State.UpdatedUtc = DateTime.UtcNow.ToString("o"); Save(); }
    }

    public void RecordGameOutcome(GameOutcome outcome)
    {
        lock (_lock)
        {
            State.GameOutcomes.RemoveAll(o => string.Equals(o.GameId, outcome.GameId, StringComparison.OrdinalIgnoreCase));
            State.GameOutcomes.Add(outcome);
            if (outcome.Status == GameStatus.Passed) AddDistinct(State.CompletedGames, outcome.GameId);
            if (outcome.Status == GameStatus.Failed) { State.LastFailure = $"{outcome.GameId}: {outcome.Reason}"; State.RetryCount++; }
            State.CurrentGame = null;
            State.UpdatedUtc = DateTime.UtcNow.ToString("o");
            Save();
        }
    }

    public void Complete()
    {
        lock (_lock) { State.Completed = true; State.CurrentGame = null; State.UpdatedUtc = DateTime.UtcNow.ToString("o"); Save(); }
    }

    private static void AddDistinct(List<string> list, string value)
    {
        if (!list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, Json.ToString(State));
            // atomic-ish replace so a crash mid-write can't truncate the live checkpoint
            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else File.Move(tmp, _path);
        }
        catch (Exception ex) { _log.Warn("Resume", $"Failed to write checkpoint: {ex.Message}"); }
    }
}
