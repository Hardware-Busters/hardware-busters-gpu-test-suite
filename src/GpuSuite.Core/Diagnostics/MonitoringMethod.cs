namespace GpuSuite.Core.Diagnostics;

public enum MethodState { Running, Done, Aborted }

/// <summary>
/// Summary of the guided benchmark plan for read-only monitoring: identities, counts and short labels, never a plan body,
/// a profile or a measurement. One step is one benchmark cell (game x scene x variant x resolution); repeats happen inside it.
/// </summary>
public sealed record MonitoringMethod(
    string RulesetId,
    MethodUnit Unit,
    MethodPlan Plan,
    MethodSession Session,
    MethodProgress Progress,
    MethodWindow? LastWindow,
    MethodResults Results,
    IReadOnlyList<MethodIssue> Issues,
    IReadOnlyDictionary<string, object?> Extra,
    DateTime UpdatedUtc)
{
    /// <summary>How many recent issues a reader is given.</summary>
    public const int MaxIssues = 10;
}

/// <summary>The unit under test. <paramref name="Details"/> holds scalar values only (string, number, bool or null).</summary>
public sealed record MethodUnit(string Id, string? Hash, IReadOnlyDictionary<string, object?> Details);

/// <param name="Steps">The plan as a checklist, in plan order; a prefix of the plan when it is longer than the block can list, or null.</param>
public sealed record MethodPlan(string Id, string? Hash, int TotalSteps, IReadOnlyList<MethodStep>? Steps = null);

public enum StepState { Done, Now, Todo, Skipped }

/// <summary>One entry of the checklist: a short label and where the step stands.</summary>
public sealed record MethodStep(string Label, StepState State);

/// <summary><paramref name="Index"/> is the 1-based number of the step in progress, or null when none is.</summary>
public sealed record MethodSession(MethodState State, string? Step, string? Phase, int? Index);

public sealed record MethodProgress(int Completed, int Total);

/// <summary>The verdict on the most recently completed measurement window. A reason is given whenever it is not valid.</summary>
public sealed record MethodWindow(bool Valid, string? Reason);

/// <summary><paramref name="Provisional"/> holds scalar values only; <paramref name="Final"/> means the result was saved.</summary>
public sealed record MethodResults(IReadOnlyDictionary<string, object?>? Provisional, bool Final);

public sealed record MethodIssue(string? Field, string Message, DateTime Utc);
