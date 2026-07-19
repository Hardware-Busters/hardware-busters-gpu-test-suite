namespace GpuSuite.Calibration.Schema;

/// <summary>
/// The kind of change a recommendation proposes. Split into two classes by the recovery boundary the
/// user fixed: a small set of REVERSIBLE, DETERMINISTIC recoveries that the engine's existing retry
/// logic may take automatically, and everything that CHANGES BENCHMARK BEHAVIOR, which always requires
/// human approval. See <see cref="RecoveryPolicy"/>.
/// </summary>
public enum RecommendationKind
{
    // ── Consequential: changes benchmark behavior → ALWAYS human-approved (P7) ──
    Route,
    Threshold,
    Profile,
    MenuMapping,
    GraphicsSetting,
    BotPath,

    // ── Auto-eligible: reversible + deterministic → may feed the engine's existing retry ──
    Retry,
    Relaunch,
    Skip,
    Wait,
    Recapture,
    RecheckLauncher
}

/// <summary>
/// The recovery boundary, encoded once. The auto-eligible whitelist is EXACTLY the set the user
/// approved — retry, relaunch, skip, wait, re-capture, re-check launcher state — all reversible and
/// deterministic. Everything else (routes, thresholds, profiles, menu mappings, graphics settings,
/// bot paths) changes benchmark behavior and must be human-approved. This is the single source of
/// truth; no other module re-decides it.
/// </summary>
public static class RecoveryPolicy
{
    private static readonly HashSet<RecommendationKind> AutoEligibleSet = new()
    {
        RecommendationKind.Retry,
        RecommendationKind.Relaunch,
        RecommendationKind.Skip,
        RecommendationKind.Wait,
        RecommendationKind.Recapture,
        RecommendationKind.RecheckLauncher
    };

    public static bool IsAutoEligible(RecommendationKind kind) => AutoEligibleSet.Contains(kind);
    public static bool RequiresHumanApproval(RecommendationKind kind) => !AutoEligibleSet.Contains(kind);
}

/// <summary>
/// An advisory proposal from the AI Calibration Engineer. It is INERT — it carries a description and a
/// proposed change as text/diff, never an executable action. <see cref="RequiresHumanApproval"/> is set
/// from <see cref="RecoveryPolicy"/> at construction so a consequential proposal can never be silently
/// auto-applied. Nothing in the framework executes a Recommendation; a human promotes it.
/// </summary>
public sealed class Recommendation
{
    public RecommendationKind Kind { get; set; }
    public string Summary { get; set; } = "";
    /// <summary>The exact proposed change, as a human-readable diff (e.g. "Down x7 → Down x8").</summary>
    public string ProposedChange { get; set; } = "";
    /// <summary>Set from the recovery policy. True for every behavior-changing kind.</summary>
    public bool RequiresHumanApproval { get; set; } = true;
    public Confidence Confidence { get; set; } = new();
    public string Rationale { get; set; } = "";
    public Evidence Evidence { get; set; } = new();

    public static Recommendation For(RecommendationKind kind, string summary, string proposedChange,
                                     Confidence confidence, string rationale, Evidence? evidence = null)
        => new()
        {
            Kind = kind,
            Summary = summary,
            ProposedChange = proposedChange,
            RequiresHumanApproval = RecoveryPolicy.RequiresHumanApproval(kind),
            Confidence = confidence,
            Rationale = rationale,
            Evidence = evidence ?? Evidence.None
        };
}

/// <summary>The GX10's semantic read of a screen. Advisory; <see cref="Abstained"/> when it declined.</summary>
public sealed class SemanticDescription
{
    public string? Screen { get; set; }
    public string Summary { get; set; } = "";
    public List<DetectedControl> Controls { get; set; } = new();
    public bool Abstained { get; set; }
    public Confidence Confidence { get; set; } = new();
    public string Rationale { get; set; } = "";
    public Evidence Evidence { get; set; } = new();

    public static SemanticDescription Abstain(string rationale)
        => new() { Abstained = true, Confidence = GpuSuite.Calibration.Confidence.Abstain(rationale), Rationale = rationale };
}

/// <summary>Classification of the current screen against the known set. Unknown/Abstained never guesses.</summary>
public sealed class MenuClassification
{
    public string? ScreenId { get; set; }
    public bool Unknown { get; set; }
    public bool Abstained { get; set; }
    public Confidence Confidence { get; set; } = new();
    public string Rationale { get; set; } = "";
    public Evidence Evidence { get; set; } = new();

    public static MenuClassification Abstain(string rationale)
        => new() { Unknown = true, Abstained = true, Confidence = GpuSuite.Calibration.Confidence.Abstain(rationale), Rationale = rationale };
}

/// <summary>The GX10's narration of the difference between two layouts (drift). Advisory.</summary>
public sealed class DriftAnalysis
{
    public List<LayoutChange> Changes { get; set; } = new();
    public bool Abstained { get; set; }
    public Confidence Confidence { get; set; } = new();
    public string Rationale { get; set; } = "";
    public Evidence Evidence { get; set; } = new();
}

/// <summary>A hypothesized cause for a failure. Advisory; may abstain.</summary>
public sealed class FailureHypothesis
{
    public string? Cause { get; set; }
    public string? FailureClass { get; set; }
    public bool Abstained { get; set; }
    public Confidence Confidence { get; set; } = new();
    public string Rationale { get; set; } = "";
    public Evidence Evidence { get; set; } = new();
}

/// <summary>The during-run gameplay verdict: is this window real, right-scene gameplay? (Phase 3.)</summary>
public sealed class GameplayVerdict
{
    public bool IsGameplay { get; set; }
    public bool SceneMatch { get; set; }
    public bool SpawnMatch { get; set; }
    public bool HudMatch { get; set; }
    /// <summary>True when cold shader hitches were observed but disappeared before the accepted window.</summary>
    public bool ShaderHitchesSettled { get; set; }
    /// <summary>True when scene/spawn/HUD evidence was supplied rather than relying on run metadata alone.</summary>
    public bool VisualEvidenceUsed { get; set; }
    public Confidence Confidence { get; set; } = new();
    public string Rationale { get; set; } = "";
    public Evidence Evidence { get; set; } = new();
}

/// <summary>
/// Phase-3 visual and temporal evidence produced by capture-card template comparison, OCR/vision, or a
/// reviewed recording. Similarities are normalized to [0,1]. The validator remains deterministic: it
/// consumes these measurements but never invents a scene/HUD/spawn claim when a required field is absent.
/// </summary>
public sealed class GameplayEvidence
{
    public string Source { get; set; } = "";
    public double? SceneSimilarity { get; set; }
    public double? SpawnSimilarity { get; set; }
    public bool? HudDetected { get; set; }
    /// <summary>"ignore" | "present" | "absent".</summary>
    public string HudExpectation { get; set; } = "ignore";
    public int StartupShaderHitches { get; set; }
    public int LateShaderHitches { get; set; }
    public double StableSecondsAfterLastHitch { get; set; }
    public Evidence Evidence { get; set; } = new();
}

/// <summary>A failure classified into the taxonomy, with whether it is transient (retry) vs structural.</summary>
public sealed class FailureClassification
{
    public string FailureClass { get; set; } = "";
    public bool Transient { get; set; }
    public Confidence Confidence { get; set; } = new();
    public string Rationale { get; set; } = "";
    public Evidence Evidence { get; set; } = new();
}

/// <summary>One step in a recovery plan, tagged with whether it is auto-eligible per the whitelist.</summary>
public sealed class RecoveryStep
{
    public RecommendationKind Kind { get; set; }
    public string Description { get; set; } = "";
    public bool AutoEligible { get; set; }
    public Confidence Confidence { get; set; } = new();
}

/// <summary>An ordered recovery plan. Only the auto-eligible steps may feed the engine's retry; the rest are advisory.</summary>
public sealed class RecoveryPlan
{
    public List<RecoveryStep> Steps { get; set; } = new();
    public Confidence Confidence { get; set; } = new();
}

/// <summary>A data-driven threshold-change proposal. Never auto-applied — written to the review report.</summary>
public sealed class ThresholdRecommendation
{
    public string Key { get; set; } = "";
    /// <summary>Optional cell/game scope. "global" means the suite-wide validation setting.</summary>
    public string Scope { get; set; } = "global";
    public string CurrentValue { get; set; } = "";
    public string ProposedValue { get; set; } = "";
    public int SampleCount { get; set; }
    public string ObservedRange { get; set; } = "";
    public string Rationale { get; set; } = "";
    public Confidence Confidence { get; set; } = new();
    public bool RequiresHumanApproval { get; set; } = true;
}

/// <summary>The pre-run route check verdict: does the recorded route still reach its goal on the live menu?</summary>
public sealed class RouteVerdict
{
    public bool Valid { get; set; }
    public string? Reason { get; set; }
    public Confidence Confidence { get; set; } = new();
    public Evidence Evidence { get; set; } = new();
}
