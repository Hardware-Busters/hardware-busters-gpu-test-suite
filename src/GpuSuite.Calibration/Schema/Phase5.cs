namespace GpuSuite.Calibration.Schema;

/// <summary>Game-agnostic knowledge distilled from at least two independently validated games.</summary>
public enum SharedPatternKind { ScreenArchetype, OcrAnchor, RouteTransition }

/// <summary>
/// A cross-game calibration pattern. Patterns are evidence, not executable automation: they may seed a
/// future draft but cannot be promoted into a live profile without an approval record.
/// </summary>
public sealed class SharedCalibrationPattern
{
    public string Id { get; set; } = "";
    public SharedPatternKind Kind { get; set; }
    public string Signature { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> SourceGames { get; set; } = new();
    public int Occurrences { get; set; }
    public Confidence Confidence { get; set; } = new();
    public string UpdatedAtIso { get; set; } = "";
    public bool RequiresHumanApproval { get; set; } = true;
}

public enum RegressionSeverity { Info, Warning, Critical }

/// <summary>One like-for-like metric change between a baseline and a current suite result.</summary>
public sealed class RegressionFinding
{
    public string CellKey { get; set; } = "";
    public string Metric { get; set; } = "";
    public double? BaselineValue { get; set; }
    public double? CurrentValue { get; set; }
    public double? DeltaPct { get; set; }
    public RegressionSeverity Severity { get; set; }
    public string Classification { get; set; } = "";
    public string Rationale { get; set; } = "";
    public Confidence Confidence { get; set; } = new();
}

/// <summary>
/// Fingerprint-safe comparison of two completed suite results. Non-comparable cells are reported rather
/// than coerced into a performance conclusion.
/// </summary>
public sealed class RegressionAnalysis
{
    public string BaselineGeneratedUtc { get; set; } = "";
    public string CurrentGeneratedUtc { get; set; } = "";
    public bool EnvironmentComparable { get; set; }
    public List<string> EnvironmentChanges { get; set; } = new();
    public int ComparedCells { get; set; }
    public int SkippedCells { get; set; }
    public List<RegressionFinding> Findings { get; set; } = new();
    public Confidence Confidence { get; set; } = new();
    public string Summary { get; set; } = "";
}

public enum ApprovalStatus { Pending, Approved, Rejected }

/// <summary>
/// Durable human gate for a consequential Phase-5 proposal. Approving changes only this record's status;
/// it never edits a profile, route, threshold, or bot automatically.
/// </summary>
public sealed class ApprovalRequest
{
    public string Id { get; set; } = "";
    public string Game { get; set; } = "";
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public Recommendation Proposal { get; set; } = new();
    public string CreatedAtIso { get; set; } = "";
    public string? DecidedAtIso { get; set; }
    public string? DecisionNote { get; set; }
}

/// <summary>Complete Phase-5 output included in both JSON and HTML calibration reports.</summary>
public sealed class Phase5Analysis
{
    public List<SharedCalibrationPattern> SharedPatterns { get; set; } = new();
    public List<ThresholdRecommendation> ThresholdRecommendations { get; set; } = new();
    public RegressionAnalysis? Regression { get; set; }
    public List<ApprovalRequest> ApprovalRequests { get; set; } = new();
}
