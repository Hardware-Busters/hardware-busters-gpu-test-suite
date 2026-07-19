namespace GpuSuite.Core.Models;

/// <summary>
/// One completed benchmark cell (game × scene × variant × resolution) — the finest unit of progress a
/// resumable run checkpoints. Its aggregate is already on disk (scene_aggregate.json); this record lets a
/// resume know the cell is done without re-running it.
/// </summary>
public sealed class CompletedCell
{
    /// <summary>Stable key: "game|scene|variant|resolution".</summary>
    public string Key { get; set; } = "";
    public string GameId { get; set; } = "";
    public string SceneId { get; set; } = "";
    public string VariantId { get; set; } = "";
    public string ResolutionName { get; set; } = "";
    public int ValidRuns { get; set; }
    public int TotalRuns { get; set; }
    public double AvgFps { get; set; }
    public string CompletedUtc { get; set; } = "";
}

/// <summary>
/// Crash-safe checkpoint of an autonomous run. Persisted to <c>Results\&lt;gpu&gt;\run_state.json</c> after
/// EVERY completed cell, so a Windows crash / power loss loses at most the single in-progress benchmark. A
/// later <c>autonomous --resume</c> (or auto-detect) reloads this and continues exactly where it stopped.
/// </summary>
public sealed class RunState
{
    public string RunId { get; set; } = "";
    public string StartedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
    public string GpuName { get; set; } = "";

    /// <summary>
    /// A signature of the planned work (game ids + resolutions + selected variants + repeats). A resume only
    /// proceeds when the current plan's signature matches — so changing the roster starts a fresh run rather
    /// than resuming a stale, mismatched one.
    /// </summary>
    public string PlanSignature { get; set; } = "";

    public bool Unattended { get; set; }
    /// <summary>True once the whole roster finished — a completed run is never auto-resumed.</summary>
    public bool Completed { get; set; }

    /// <summary>The game in progress when the state was last saved (for the resume banner + diagnostics).</summary>
    public string? CurrentGame { get; set; }
    /// <summary>How many times the current game has been retried/attempted.</summary>
    public int RetryCount { get; set; }
    /// <summary>The most recent failure reason recorded (failure state), if any.</summary>
    public string? LastFailure { get; set; }

    public List<CompletedCell> CompletedCells { get; set; } = new();
    /// <summary>Per-game outcomes accumulated so far (restored into the final summary on resume).</summary>
    public List<GameOutcome> GameOutcomes { get; set; } = new();

    // Convenience roll-ups (also persisted, per the requirement to record completed games/res/variants).
    public List<string> CompletedGames { get; set; } = new();
    public List<string> CompletedResolutions { get; set; } = new();
    public List<string> CompletedVariants { get; set; } = new();
}
