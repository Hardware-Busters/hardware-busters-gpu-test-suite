using GpuSuite.Calibration.Schema;
using GpuSuite.Calibration.Observation;

namespace GpuSuite.Calibration.Llm;

/// <summary>The context handed to the LLM for screen reasoning (frame + literal OCR + the known screen set).</summary>
public sealed class VisionContext
{
    public CaptureFrame Frame { get; set; } = new();
    public OcrFrame Ocr { get; set; } = new();
    public List<string> KnownScreens { get; set; } = new();
}

/// <summary>The context handed to the LLM to hypothesize a failure cause.</summary>
public sealed class FailureContext
{
    public string? KnownFailureClass { get; set; }
    public string Summary { get; set; } = "";
    public List<string> Evidence { get; set; } = new();
}

/// <summary>A request for an advisory recommendation (route repair, threshold change, …).</summary>
public sealed class RecommendationRequest
{
    public RecommendationKind Kind { get; set; }
    public string Context { get; set; } = "";
}

/// <summary>Tuning for the GX10 reasoning calls (endpoint, model, consensus, temperature).</summary>
public sealed class Gx10ReasonerOptions
{
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen2.5vl:7b";
    /// <summary>N independent samples for high-stakes calls (self-consistency). Disagreement lowers confidence.</summary>
    public int ConsensusSamples { get; set; } = 3;
    public double Temperature { get; set; } = 0.1;
}

/// <summary>
/// The AI Calibration Engineer (module 4). Observe → reason → explain. It NEVER acts: there is no
/// Apply/Press/Click/Execute method here, by design. Every method is a pure function from context to
/// advisory data, every result carries a Confidence + Rationale + Evidence, and every result MAY abstain
/// ("I am not sure") rather than fabricate. The anti-hallucination contract (structured output only,
/// mandatory abstention, OCR corroboration caps, N-sample consensus on high-stakes calls) is enforced by
/// the concrete GX10 implementation; the interface guarantees only that nothing here is executable.
/// </summary>
public interface ILLMReasoner
{
    Task<SemanticDescription> DescribeScreenAsync(VisionContext ctx, CancellationToken ct);
    Task<MenuClassification> ClassifyScreenAsync(VisionContext ctx, IReadOnlyList<string> knownScreens, CancellationToken ct);
    Task<DriftAnalysis> ExplainDifferenceAsync(LayoutSnapshot baseline, LayoutSnapshot current, CancellationToken ct);
    Task<FailureHypothesis> HypothesizeFailureAsync(FailureContext ctx, CancellationToken ct);
    Task<Recommendation> RecommendAsync(RecommendationRequest req, CancellationToken ct);
}

/// <summary>
/// The safe offline/disabled fallback: it abstains on every call. The CLI selects this when the GX10 is
/// disabled or unreachable; the real <c>Gx10Reasoner</c> implements the same interface when online.
/// </summary>
public sealed class AbstainingLLMReasoner : ILLMReasoner
{
    private const string Reason = "GX10 reasoning is disabled or unavailable — abstaining rather than guessing.";

    public Task<SemanticDescription> DescribeScreenAsync(VisionContext ctx, CancellationToken ct)
        => Task.FromResult(SemanticDescription.Abstain(Reason));

    public Task<MenuClassification> ClassifyScreenAsync(VisionContext ctx, IReadOnlyList<string> knownScreens, CancellationToken ct)
        => Task.FromResult(MenuClassification.Abstain(Reason));

    public Task<DriftAnalysis> ExplainDifferenceAsync(LayoutSnapshot baseline, LayoutSnapshot current, CancellationToken ct)
        => Task.FromResult(new DriftAnalysis { Abstained = true, Confidence = Confidence.Abstain(Reason), Rationale = Reason });

    public Task<FailureHypothesis> HypothesizeFailureAsync(FailureContext ctx, CancellationToken ct)
        => Task.FromResult(new FailureHypothesis { Abstained = true, Confidence = Confidence.Abstain(Reason), Rationale = Reason });

    public Task<Recommendation> RecommendAsync(RecommendationRequest req, CancellationToken ct)
        => Task.FromResult(Recommendation.For(req.Kind, "(abstained)", "", Confidence.Abstain(Reason), Reason));
}
