// NOTE: confidence types live in the ROOT GpuSuite.Calibration namespace on purpose. A class named
// `Confidence` inside a namespace ending in `.Confidence` collides (the simple name is ambiguous between
// the namespace and the type). Living in the root means every sub-namespace sees these via parent scope.
namespace GpuSuite.Calibration;

/// <summary>Where a confidence signal came from. Drives normalization and the corroboration caps.</summary>
public enum SourceKind
{
    /// <summary>Deterministic, cheap, first-line: literal OCR word recognition.</summary>
    Ocr,
    /// <summary>Vision model detection (a control / screen of some kind is present).</summary>
    Vision,
    /// <summary>LLM semantic understanding (this screen IS x; this drift narration is correct).</summary>
    Llm,
    /// <summary>Menu-graph structural signal (controls found, edge reproducible).</summary>
    MenuDetection,
    /// <summary>During-run gameplay signals (fps-band margin, GPU-load, frame-time variance).</summary>
    Gameplay,
    /// <summary>Recovery classifier signature / history support.</summary>
    Recovery,
    /// <summary>A pure deterministic rule (not a model) — e.g. a config read-back match.</summary>
    Deterministic
}

/// <summary>
/// The four trust bands (principle P5). High accepts deterministically; Medium accepts but is
/// recorded for review; Low escalates one tier; Abstain ALWAYS routes to a human — the framework
/// never guesses when evidence is weak.
/// </summary>
public enum ConfidenceBand { Abstain, Low, Medium, High }

/// <summary>The calibration sub-task a confidence is gating — lets thresholds differ per task.</summary>
public enum TaskKind { ScreenClassify, ControlDetect, DriftNarrate, RouteValidate, GameplayValidate, Recommend, FailureClassify }

/// <summary>A single raw, un-normalized score from one source (0..1, clamped on normalize).</summary>
public readonly record struct RawScore(double Value, SourceKind Source);

/// <summary>One contributing signal recorded inside a fused <see cref="Confidence"/> (for explainability, P6).</summary>
public sealed class ConfidenceSource
{
    public SourceKind Kind { get; set; }
    public double Value { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// A confidence value with its band, the signals that produced it, and a human-readable rationale.
/// Every observation and recommendation that leaves the calibration plane carries one of these —
/// nothing is trusted without a score AND the evidence behind it (P5/P6).
/// </summary>
public sealed class Confidence
{
    /// <summary>Fused confidence in [0,1].</summary>
    public double Value { get; set; }
    public ConfidenceBand Band { get; set; } = ConfidenceBand.Abstain;
    /// <summary>The contributing signals (for "why is it this confident?").</summary>
    public List<ConfidenceSource> Sources { get; set; } = new();
    public string Rationale { get; set; } = "";

    /// <summary>The safe default: zero confidence, abstain band — "I am not sure, escalate to a human."</summary>
    public static Confidence Abstain(string rationale = "insufficient evidence")
        => new() { Value = 0, Band = ConfidenceBand.Abstain, Rationale = rationale };

    public static Confidence Of(double value, ConfidenceBand band, string rationale, params ConfidenceSource[] sources)
        => new() { Value = Math.Clamp(value, 0, 1), Band = band, Rationale = rationale, Sources = sources.ToList() };

    public bool IsAbstain => Band == ConfidenceBand.Abstain;
    public override string ToString() => $"{Band} ({Value:0.00})";
}

/// <summary>What the escalation gate decided for a given confidence + task.</summary>
public enum EscalationOutcome
{
    /// <summary>High (or Medium) band — accept on deterministic evidence, no model/human needed.</summary>
    AcceptDeterministic,
    /// <summary>Low band — spend the next, more expensive tier (OCR→Vision→LLM).</summary>
    EscalateTier,
    /// <summary>Abstain — never act; route to a human.</summary>
    AskHuman
}

/// <summary>The gate's verdict, with the reason (logged on every escalation, per the audit requirement).</summary>
public sealed class EscalationDecision
{
    public EscalationOutcome Outcome { get; set; }
    /// <summary>True when accepted but in the Medium band — accepted yet flagged for the review report.</summary>
    public bool FlaggedForReview { get; set; }
    public string Reason { get; set; } = "";
    public Confidence Confidence { get; set; } = Confidence.Abstain();
}

/// <summary>Per-task confidence thresholds. Live in settings (tunable, never hard-coded) — τ_high/τ_low.</summary>
public sealed class CalibrationThresholds
{
    /// <summary>At/above this fused value ⇒ High band: accept deterministically, no model spend.</summary>
    public double TauHigh { get; set; } = 0.80;
    /// <summary>Below this fused value ⇒ Low band: escalate a tier. Strictly above 0 and below TauHigh.</summary>
    public double TauLow { get; set; } = 0.50;
    /// <summary>A value at/below this is treated as Abstain (the model declined / no evidence).</summary>
    public double AbstainAtOrBelow { get; set; } = 0.05;
}

/// <summary>
/// The arbiter of trust (module 10). Normalizes heterogeneous scores onto one scale, fuses them
/// (corroboration rewards agreement; caps prevent a vision text-claim outrunning OCR support), and
/// runs the escalation gate that decides when the cheap deterministic path suffices vs. when to spend
/// a vision/LLM call. Pure policy: depends on nothing else in the calibration plane.
/// </summary>
public interface IConfidenceEngine
{
    Confidence Normalize(RawScore raw);
    /// <summary>Fuse independent signals about ONE claim (corroboration via noisy-OR, floored at the strongest single source).</summary>
    Confidence Fuse(params Confidence[] signals);
    /// <summary>Fuse, then cap the result so it cannot exceed <paramref name="cap"/> — e.g. a vision text-claim capped by OCR support.</summary>
    Confidence FuseWithCap(double cap, params Confidence[] signals);
    ConfidenceBand Band(double value);
    EscalationDecision Gate(Confidence c, TaskKind task);
}

/// <summary>
/// Default confidence policy. Banding by τ thresholds; fusion by noisy-OR so corroborating sources
/// raise trust above any single one, while a single weak source stays weak. Conflict handling is the
/// caller's job (it lowers an input's value before fusing) — this keeps the engine a pure, predictable
/// function. Consensus across N model samples is folded in the same way (each sample is one signal).
/// </summary>
public sealed class ConfidenceEngine : IConfidenceEngine
{
    private readonly CalibrationThresholds _t;
    public ConfidenceEngine(CalibrationThresholds? thresholds = null) => _t = thresholds ?? new CalibrationThresholds();

    public Confidence Normalize(RawScore raw)
    {
        double v = Math.Clamp(raw.Value, 0, 1);
        var band = Band(v);
        return new Confidence
        {
            Value = v,
            Band = band,
            Rationale = $"normalized {raw.Source} score {v:0.00} → {band}",
            Sources = { new ConfidenceSource { Kind = raw.Source, Value = v } }
        };
    }

    public Confidence Fuse(params Confidence[] signals) => FuseWithCap(1.0, signals);

    public Confidence FuseWithCap(double cap, params Confidence[] signals)
    {
        var live = (signals ?? Array.Empty<Confidence>()).Where(s => s is not null).ToList();
        if (live.Count == 0) return Confidence.Abstain("no signals to fuse");

        // Noisy-OR: 1 - Π(1 - v_i). Two independent agreeing sources exceed either alone (corroboration).
        double noisyOr = 1 - live.Aggregate(1.0, (acc, s) => acc * (1 - Math.Clamp(s.Value, 0, 1)));
        double strongest = live.Max(s => s.Value);
        double fused = Math.Min(Math.Max(noisyOr, strongest), Math.Clamp(cap, 0, 1));

        var sources = live.SelectMany(s => s.Sources).ToList();
        if (sources.Count == 0)
            sources = live.Select(s => new ConfidenceSource { Kind = SourceKind.Deterministic, Value = s.Value }).ToList();

        string why = live.Count == 1
            ? $"single source {fused:0.00}"
            : $"fused {live.Count} sources (noisy-OR {noisyOr:0.00}, strongest {strongest:0.00}){(cap < 1 ? $", capped at {cap:0.00}" : "")} → {fused:0.00}";

        return new Confidence { Value = fused, Band = Band(fused), Sources = sources, Rationale = why };
    }

    public ConfidenceBand Band(double value)
    {
        if (value <= _t.AbstainAtOrBelow) return ConfidenceBand.Abstain;
        if (value >= _t.TauHigh) return ConfidenceBand.High;
        if (value < _t.TauLow) return ConfidenceBand.Low;
        return ConfidenceBand.Medium;
    }

    public EscalationDecision Gate(Confidence c, TaskKind task)
    {
        return c.Band switch
        {
            ConfidenceBand.High => new EscalationDecision
            {
                Outcome = EscalationOutcome.AcceptDeterministic,
                Reason = $"{task}: High band ({c.Value:0.00} ≥ {_t.TauHigh:0.00}) — accepted on deterministic evidence, no model spend.",
                Confidence = c
            },
            ConfidenceBand.Medium => new EscalationDecision
            {
                Outcome = EscalationOutcome.AcceptDeterministic,
                FlaggedForReview = true,
                Reason = $"{task}: Medium band ({c.Value:0.00}) — accepted but flagged for human review.",
                Confidence = c
            },
            ConfidenceBand.Low => new EscalationDecision
            {
                Outcome = EscalationOutcome.EscalateTier,
                Reason = $"{task}: Low band ({c.Value:0.00} < {_t.TauLow:0.00}) — escalating to the next tier (OCR→Vision→LLM).",
                Confidence = c
            },
            _ => new EscalationDecision
            {
                Outcome = EscalationOutcome.AskHuman,
                Reason = $"{task}: Abstain ({c.Value:0.00}) — insufficient evidence, routing to a human (never guess).",
                Confidence = c
            }
        };
    }
}
