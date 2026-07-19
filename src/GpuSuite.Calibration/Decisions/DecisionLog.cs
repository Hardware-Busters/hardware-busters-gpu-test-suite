using GpuSuite.Core.Diagnostics;

namespace GpuSuite.Calibration.Decisions;

/// <summary>
/// The kinds of decision recorded on the calibration audit trail. Each calibration run must be able to
/// answer, after the fact, EXACTLY why every consequential branch was taken — the user's standing
/// requirement: why OCR was accepted, why vision escalation was triggered, why the GX10 was consulted,
/// why a recommendation was generated, and why human approval was required.
/// </summary>
public enum CalibrationDecisionKind
{
    /// <summary>A literal OCR read was accepted without escalation (cheap path was confident enough).</summary>
    OcrAccepted,
    /// <summary>OCR was not enough → the vision model was invoked.</summary>
    VisionEscalationTriggered,
    /// <summary>The GX10 LLM (the AI Calibration Engineer) was consulted for semantic reasoning.</summary>
    Gx10Consulted,
    /// <summary>An advisory recommendation was produced (always inert until a human approves it).</summary>
    RecommendationGenerated,
    /// <summary>A consequential change needs human sign-off before anything is committed (P7).</summary>
    HumanApprovalRequired,
    /// <summary>A source/model abstained — evidence too weak; routed to a human rather than guessed.</summary>
    Abstained,
    /// <summary>A pure deterministic rule decided the branch (no model spend).</summary>
    DeterministicAccepted
}

/// <summary>
/// One immutable entry on the calibration audit trail. The <see cref="Why"/> is mandatory and is the
/// whole point — a decision with no recorded reason is a bug, not a log line.
/// </summary>
public sealed class CalibrationDecision
{
    public DateTime Time { get; set; } = DateTime.Now;
    public CalibrationDecisionKind Kind { get; set; }
    /// <summary>The pipeline stage the decision was made in (e.g. "MenuDiscovery", "DriftDetection").</summary>
    public string Stage { get; set; } = "";
    /// <summary>The screen/node under consideration, if any.</summary>
    public string? Screen { get; set; }
    /// <summary>Mandatory human-readable reason this branch was taken.</summary>
    public string Why { get; set; } = "";
    /// <summary>The confidence that drove the decision (null for purely procedural entries).</summary>
    public Confidence? Confidence { get; set; }
    /// <summary>Evidence references (screenshot paths, OCR snippets, prompt ids) supporting the decision.</summary>
    public List<string> Evidence { get; set; } = new();
}

/// <summary>Records every consequential decision a calibration session makes, with its reason.</summary>
public interface IDecisionLog
{
    void Record(CalibrationDecision decision);
    IReadOnlyList<CalibrationDecision> Entries { get; }
}

/// <summary>
/// Default audit-trail implementation. Holds the ordered decisions for the session, mirrors each to the
/// suite <see cref="RunLogger"/> timeline, and is serialized into the calibration report so a reviewer can
/// trace the full chain of "why". The convenience methods make the five required decision kinds one-liners
/// at the call sites so they can never be forgotten.
/// </summary>
public sealed class DecisionLog : IDecisionLog
{
    private readonly List<CalibrationDecision> _entries = new();
    private readonly RunLogger? _log;
    private const string LogStage = "Calib";

    public DecisionLog(RunLogger? log = null) => _log = log;

    public IReadOnlyList<CalibrationDecision> Entries => _entries;

    public void Record(CalibrationDecision d)
    {
        if (string.IsNullOrWhiteSpace(d.Why))
            d.Why = "(no reason recorded — this is a defect)";
        _entries.Add(d);
        var level = d.Kind switch
        {
            CalibrationDecisionKind.HumanApprovalRequired => LogLevel.Warn,
            CalibrationDecisionKind.Abstained => LogLevel.Warn,
            _ => LogLevel.Info
        };
        string conf = d.Confidence is not null ? $" [{d.Confidence}]" : "";
        string screen = string.IsNullOrEmpty(d.Screen) ? "" : $" «{d.Screen}»";
        _log?.Log(level, LogStage, $"{d.Kind}{screen}{conf}: {d.Why}");
    }

    // ── The five required "record why" call sites, as one-liners ──────────────────────────────

    public void OcrAccepted(string stage, string? screen, string why, Confidence? c = null, params string[] evidence)
        => Record(new CalibrationDecision { Kind = CalibrationDecisionKind.OcrAccepted, Stage = stage, Screen = screen, Why = why, Confidence = c, Evidence = evidence.ToList() });

    public void VisionEscalationTriggered(string stage, string? screen, string why, Confidence? c = null, params string[] evidence)
        => Record(new CalibrationDecision { Kind = CalibrationDecisionKind.VisionEscalationTriggered, Stage = stage, Screen = screen, Why = why, Confidence = c, Evidence = evidence.ToList() });

    public void Gx10Consulted(string stage, string? screen, string why, Confidence? c = null, params string[] evidence)
        => Record(new CalibrationDecision { Kind = CalibrationDecisionKind.Gx10Consulted, Stage = stage, Screen = screen, Why = why, Confidence = c, Evidence = evidence.ToList() });

    public void RecommendationGenerated(string stage, string? screen, string why, Confidence? c = null, params string[] evidence)
        => Record(new CalibrationDecision { Kind = CalibrationDecisionKind.RecommendationGenerated, Stage = stage, Screen = screen, Why = why, Confidence = c, Evidence = evidence.ToList() });

    public void HumanApprovalRequired(string stage, string? screen, string why, Confidence? c = null, params string[] evidence)
        => Record(new CalibrationDecision { Kind = CalibrationDecisionKind.HumanApprovalRequired, Stage = stage, Screen = screen, Why = why, Confidence = c, Evidence = evidence.ToList() });

    public void Abstained(string stage, string? screen, string why, Confidence? c = null, params string[] evidence)
        => Record(new CalibrationDecision { Kind = CalibrationDecisionKind.Abstained, Stage = stage, Screen = screen, Why = why, Confidence = c, Evidence = evidence.ToList() });

    public void DeterministicAccepted(string stage, string? screen, string why, Confidence? c = null, params string[] evidence)
        => Record(new CalibrationDecision { Kind = CalibrationDecisionKind.DeterministicAccepted, Stage = stage, Screen = screen, Why = why, Confidence = c, Evidence = evidence.ToList() });
}
