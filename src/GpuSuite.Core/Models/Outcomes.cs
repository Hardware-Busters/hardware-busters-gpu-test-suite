namespace GpuSuite.Core.Models;

/// <summary>Per-game roster outcome in an autonomous full-roster run.</summary>
public enum GameStatus
{
    /// <summary>Every planned config produced the required number of VALID measured runs.</summary>
    Passed,
    /// <summary>The game was attempted but produced no valid result (classified by <see cref="FailureClass"/>).</summary>
    Failed,
    /// <summary>The game was not attempted (e.g. no enabled variants / nothing to run).</summary>
    Skipped
}

/// <summary>
/// The clear failure classes an unattended run records so the operator can read, at a glance, WHY each
/// failed game failed. These mirror the real roster failure modes the suite has hit.
/// </summary>
public enum FailureClass
{
    None,
    /// <summary>The game process never started (store/launcher request failed, or process never appeared).</summary>
    LaunchFailure,
    /// <summary>The game (or its bot) threw / the process died mid-run.</summary>
    Crash,
    /// <summary>A measured window with no real rendering (paused/focus-lost game re-presenting a frozen frame).</summary>
    FrozenCapture,
    /// <summary>The capture produced zero / too few frames (capture backend never recorded the scene).</summary>
    NoFrames,
    /// <summary>Frames captured but the FPS is implausible / fails the validity bounds.</summary>
    InvalidFps,
    /// <summary>Menu/settings navigation never reached the benchmark or could not apply+verify settings.</summary>
    MenuNavFailure,
    /// <summary>The gameplay-band gate never settled into a real measured window.</summary>
    GameplayGateFailure,
    /// <summary>Severe shader-compile / stutter instability broke the measured window.</summary>
    ShaderHitch,
    /// <summary>The store launcher / online session wasn't ready (e.g. sign-in stall) so the game couldn't start.</summary>
    LauncherNotReady,
    /// <summary>A pre-run hardware-validation check failed (wrong GPU, low VRAM, no monitor, over-temp, low disk, …).</summary>
    HardwarePrecheck,
    /// <summary>A runtime health check tripped (GPU stuck idle, 0 fps, frozen render, telemetry stopped, hung, timeout).</summary>
    RuntimeHealth,
    /// <summary>An unclassified failure (the raw reason is preserved in <see cref="GameOutcome.Reason"/>).</summary>
    Other
}

/// <summary>
/// The outcome of one game in an autonomous full-roster run: did it pass, fail, or get skipped, and — when
/// it failed — the failure class and the exact reason. Preserved in the suite result + the final summary so
/// a failed game never silently disappears.
/// </summary>
public sealed class GameOutcome
{
    public string GameId { get; set; } = "";
    public string Name { get; set; } = "";
    public GameStatus Status { get; set; }
    public FailureClass FailureClass { get; set; } = FailureClass.None;
    /// <summary>Human-readable reason (the dominant validation issue, the crash message, or the skip cause).</summary>
    public string Reason { get; set; } = "";
    public int ValidRuns { get; set; }
    public int TotalRuns { get; set; }
    /// <summary>The graphics variants ("models") that were attempted for this game.</summary>
    public List<string> Variants { get; set; } = new();

    /// <summary>
    /// ADVISORY only: the GX10 AI Calibration Engineer's hypothesis for WHY this game failed (null when not
    /// consulted or when it abstained). It NEVER changes the deterministic <see cref="FailureClass"/>/<see
    /// cref="Reason"/> — it is a human-reviewed note added post-run, exactly per the sidecar rule.
    /// </summary>
    public string? AdvisoryCause { get; set; }
    /// <summary>The GX10's confidence band for <see cref="AdvisoryCause"/> (e.g. "Medium (0.66)"); null when none.</summary>
    public string? AdvisoryConfidence { get; set; }

    public static GameOutcome Skipped(string id, string name, string reason)
        => new() { GameId = id, Name = name, Status = GameStatus.Skipped, Reason = reason };
}
