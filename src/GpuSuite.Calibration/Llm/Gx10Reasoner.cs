using System.Text.Json;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Remote;
using GpuSuite.Calibration.Schema;
using GpuSuite.Calibration.Observation;

namespace GpuSuite.Calibration.Llm;

/// <summary>
/// The REAL AI Calibration Engineer (module 4), backed by the lab GX10 / GB10 box via <see cref="Gx10Client"/>.
/// It replaces <see cref="AbstainingLLMReasoner"/> once the box is confirmed reachable — but it keeps the exact
/// same contract: <b>observe → reason → explain, never act.</b> No method here drives the game.
///
/// The four anti-hallucination guarantees the user locked in are enforced HERE, not just promised by the
/// interface:
/// <list type="number">
/// <item><b>Structured output only</b> — every call asks Ollama for <c>format:"json"</c> and parses a strict
///   shape; an unparseable reply is treated as an abstention, never salvaged into a guess.</item>
/// <item><b>Mandatory abstention</b> — the prompt forces an explicit <c>{"abstain":true}</c> escape hatch and
///   the model is told to take it whenever unsure; any error/empty/timeout also abstains.</item>
/// <item><b>OCR corroboration cap</b> — a screen/description claim cannot be MORE confident than the literal
///   OCR supports: confidence is capped by the fraction of the model's claimed key tokens that actually appear
///   in the frame's OCR. The model can never out-vote the deterministic text.</item>
/// <item><b>N-sample consensus</b> — high-stakes calls run <see cref="Gx10ReasonerOptions.ConsensusSamples"/>
///   independent samples; agreement raises confidence (fused), disagreement collapses to abstain.</item>
/// </list>
/// Any failure path returns the abstaining result — so wiring the real GX10 in NEVER weakens "never guess".
/// </summary>
public sealed class Gx10Reasoner : ILLMReasoner
{
    private readonly Gx10ReasonerOptions _opts;
    private readonly Gx10Client _client;
    private readonly IConfidenceEngine _confidence;
    private readonly RunLogger _log;

    public Gx10Reasoner(Gx10ReasonerOptions opts, RunLogger log, Gx10Client? client = null, IConfidenceEngine? confidence = null)
    {
        _opts = opts ?? new Gx10ReasonerOptions();
        _client = client ?? new Gx10Client(_opts.Endpoint, log);
        _confidence = confidence ?? new ConfidenceEngine();
        _log = log;
    }

    public string Endpoint => _client.Endpoint;
    public string Model => _opts.Model;

    // ───────────────────────── screen description ─────────────────────────

    public async Task<SemanticDescription> DescribeScreenAsync(VisionContext ctx, CancellationToken ct)
    {
        var images = ImagesFor(ctx.Frame);
        var ocrText = OcrText(ctx.Ocr);
        string prompt =
            "You are an automation engineer LOOKING at a single video-game menu/screen capture. Describe it.\n" +
            "Return ONLY JSON (no prose) with this exact shape:\n" +
            "{\"abstain\": <true|false>, \"reason\": <string>, \"summary\": <one short sentence naming the screen>, " +
            "\"controls\": [<short label strings for the on-screen options/buttons you can read>]}\n" +
            "Rules: only mention text you can actually read; if the image is unclear, loading, or you are unsure, " +
            "set abstain=true and leave summary empty. Do NOT invent controls.\n" +
            "Literal OCR already read from this frame (ground truth — do not contradict it):\n----\n" + ocrText + "\n----";

        var samples = await SampleAsync(prompt, images, ct).ConfigureAwait(false);
        var parsed = samples.Select(ParseDescribe).Where(p => p is not null).Select(p => p!).ToList();
        var agreeing = parsed.Where(p => !p.Abstain && !string.IsNullOrWhiteSpace(p.Summary)).ToList();

        if (agreeing.Count == 0)
            return SemanticDescription.Abstain(AbstainReason(parsed, "no usable description"));

        // Pick the modal summary; consensus strength = how many of N samples agreed on it.
        var best = agreeing
            .GroupBy(p => Canon(p.Summary), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .First();
        double agreement = (double)best.Count() / Math.Max(1, samples.Count);

        // OCR corroboration cap: claimed key tokens must appear in the OCR.
        var claimTokens = best.SelectMany(p => Tokens(p.Summary)).Concat(best.SelectMany(p => p.Controls.SelectMany(Tokens))).ToList();
        double corroboration = CorroborationRatio(claimTokens, ctx.Ocr);

        var consensusConf = _confidence.Normalize(new RawScore(agreement, SourceKind.Llm));
        var capped = _confidence.FuseWithCap(corroboration, consensusConf);

        var rep = best.First();
        var controls = best.SelectMany(p => p.Controls).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c => new DetectedControl { Label = c, Type = "value", Sources = new List<string> { "gx10" }, Confidence = capped })
            .ToList();

        _log.Trace("Gx10", $"describe «{ctx.Frame.Label}»: {agreeing.Count}/{samples.Count} agree, OCR corroboration {corroboration:0.00} → {capped}");
        return new SemanticDescription
        {
            Screen = ctx.Frame.Label,
            Summary = rep.Summary,
            Controls = controls,
            Abstained = capped.IsAbstain,
            Confidence = capped,
            Rationale = $"GX10 consensus {best.Count()}/{samples.Count} on \"{rep.Summary}\"; OCR corroboration {corroboration:0.00} (cap).",
            Evidence = EvidenceFor(ctx)
        };
    }

    // ───────────────────────── screen classification ─────────────────────────

    public async Task<MenuClassification> ClassifyScreenAsync(VisionContext ctx, IReadOnlyList<string> knownScreens, CancellationToken ct)
    {
        var images = ImagesFor(ctx.Frame);
        var ocrText = OcrText(ctx.Ocr);
        string known = knownScreens.Count == 0 ? "(none)" : string.Join(", ", knownScreens);
        string prompt =
            "Classify the current game screen as EXACTLY ONE of the known screen ids, or unknown.\n" +
            "Return ONLY JSON: {\"abstain\": <true|false>, \"reason\": <string>, \"screenId\": <one of the ids or \"unknown\">}.\n" +
            "If the screen does not clearly match one id, set screenId=\"unknown\". If you are unsure, set abstain=true.\n" +
            "Known screen ids: [" + known + "]\n" +
            "Literal OCR from this frame (ground truth):\n----\n" + ocrText + "\n----";

        var samples = await SampleAsync(prompt, images, ct).ConfigureAwait(false);
        var ids = samples.Select(ParseClassify).Where(s => s is not null).Select(s => s!).ToList();
        var picks = ids.Where(s => !s.Abstain && !string.IsNullOrWhiteSpace(s.ScreenId) && !s.ScreenId.Equals("unknown", StringComparison.OrdinalIgnoreCase)).ToList();

        if (picks.Count == 0)
            return MenuClassification.Abstain(AbstainReason(ids.Select(p => (Abstain: p.Abstain, Reason: p.Reason)).ToList(), "no confident classification"));

        var top = picks.GroupBy(p => p.ScreenId, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).First();
        // Only accept an id the caller actually knows about (never invent a screen).
        string chosen = knownScreens.FirstOrDefault(k => k.Equals(top.Key, StringComparison.OrdinalIgnoreCase)) ?? "";
        if (string.IsNullOrEmpty(chosen))
            return MenuClassification.Abstain($"GX10 named an unknown screen id '{top.Key}' — abstaining rather than inventing one.");

        double agreement = (double)top.Count() / Math.Max(1, samples.Count);
        var conf = _confidence.Normalize(new RawScore(agreement, SourceKind.Llm));
        return new MenuClassification
        {
            ScreenId = chosen,
            Unknown = false,
            Abstained = conf.IsAbstain,
            Confidence = conf,
            Rationale = $"GX10 consensus {top.Count()}/{samples.Count} → '{chosen}'.",
            Evidence = EvidenceFor(ctx)
        };
    }

    // ───────────────────────── drift narration ─────────────────────────

    public async Task<DriftAnalysis> ExplainDifferenceAsync(LayoutSnapshot baseline, LayoutSnapshot current, CancellationToken ct)
    {
        // The deterministic diff is the source of truth (computed upstream by the LayoutComparer); the GX10 only
        // NARRATES it. If the box can't, we abstain — the deterministic changes still stand on their own.
        string prompt =
            "Two snapshots of the SAME game settings screen were captured at different times. Explain, in one short " +
            "sentence, what changed and why it might matter for an automated benchmark route. Be conservative.\n" +
            "Return ONLY JSON: {\"abstain\": <true|false>, \"reason\": <string>, \"narration\": <one sentence>}.\n" +
            "BASELINE controls: [" + string.Join(", ", baseline.Controls.Select(c => c.Label)) + "]\n" +
            "BASELINE ocr: " + string.Join(" ", baseline.Ocr.Select(w => w.Text)) + "\n" +
            "CURRENT  controls: [" + string.Join(", ", current.Controls.Select(c => c.Label)) + "]\n" +
            "CURRENT  ocr: " + string.Join(" ", current.Ocr.Select(w => w.Text));

        var samples = await SampleAsync(prompt, null, ct).ConfigureAwait(false);
        var narr = samples.Select(s => ParseField(s, "narration")).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        bool anyAbstain = samples.Count == 0 || samples.All(s => ParseAbstain(s) ?? true);
        if (string.IsNullOrWhiteSpace(narr) || anyAbstain)
            return new DriftAnalysis { Abstained = true, Confidence = Confidence.Abstain("GX10 declined to narrate the drift"), Rationale = "abstained" };

        return new DriftAnalysis
        {
            Abstained = false,
            Confidence = Confidence.Of(0.6, ConfidenceBand.Medium, "GX10 narration of a deterministic diff (advisory)"),
            Rationale = narr!
        };
    }

    // ───────────────────────── failure hypothesis ─────────────────────────

    public async Task<FailureHypothesis> HypothesizeFailureAsync(FailureContext ctx, CancellationToken ct)
    {
        string prompt =
            "A benchmark run failed. From the evidence, hypothesize the most likely cause. Be conservative; if the " +
            "evidence is thin, abstain rather than guess.\n" +
            "Return ONLY JSON: {\"abstain\": <true|false>, \"reason\": <string>, \"cause\": <one sentence>, " +
            "\"failureClass\": <one of: LaunchFailure, Crash, FrozenCapture, NoFrames, InvalidFps, MenuNavFailure, " +
            "GameplayGateFailure, ShaderHitch, LauncherNotReady, Other>}.\n" +
            (string.IsNullOrWhiteSpace(ctx.KnownFailureClass) ? "" : $"A deterministic classifier already said: {ctx.KnownFailureClass}.\n") +
            "Summary: " + ctx.Summary + "\nEvidence:\n- " + string.Join("\n- ", ctx.Evidence);

        var samples = await SampleAsync(prompt, null, ct).ConfigureAwait(false);
        var parsed = samples.Select(ParseFailure).Where(p => p is not null).Select(p => p!).ToList();
        var picks = parsed.Where(p => !p.Abstain && !string.IsNullOrWhiteSpace(p.Cause)).ToList();
        if (picks.Count == 0)
            return new FailureHypothesis { Abstained = true, Confidence = Confidence.Abstain("GX10 had no confident hypothesis"), Rationale = "abstained" };

        var top = picks.GroupBy(p => p.Cause, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).First();
        double agreement = (double)top.Count() / Math.Max(1, samples.Count);
        var conf = _confidence.Normalize(new RawScore(agreement, SourceKind.Recovery));
        return new FailureHypothesis
        {
            Cause = top.First().Cause,
            FailureClass = top.First().FailureClass,
            Abstained = conf.IsAbstain,
            Confidence = conf,
            Rationale = $"GX10 consensus {top.Count()}/{samples.Count}: {top.First().Cause}"
        };
    }

    // ───────────────────────── recommendation ─────────────────────────

    public async Task<Recommendation> RecommendAsync(RecommendationRequest req, CancellationToken ct)
    {
        string prompt =
            "Propose ONE advisory change for an automated benchmark. It will NOT be auto-applied — a human reviews it. " +
            "Be specific and conservative; abstain if unsure.\n" +
            "Return ONLY JSON: {\"abstain\": <true|false>, \"reason\": <string>, \"summary\": <short title>, " +
            "\"change\": <the exact proposed change as a human-readable diff>}.\n" +
            $"Recommendation kind: {req.Kind}\nContext: {req.Context}";

        var samples = await SampleAsync(prompt, null, ct).ConfigureAwait(false);
        var s = samples.Select(ParseRecommend).FirstOrDefault(p => p is not null && !p.Abstain && !string.IsNullOrWhiteSpace(p.Summary));
        if (s is null)
            return Recommendation.For(req.Kind, "(abstained)", "", Confidence.Abstain("GX10 had no confident recommendation"),
                "The GX10 declined to propose a change — routed to human review, nothing auto-applied.");

        // Consequential kinds ALWAYS require human approval (RecoveryPolicy decides this at construction).
        return Recommendation.For(req.Kind, s.Summary, s.Change,
            Confidence.Of(0.55, ConfidenceBand.Medium, "GX10 proposal (advisory; never auto-applied)"),
            $"GX10 proposed: {s.Summary}. Requires human approval before it can change benchmark behavior.");
    }

    // ───────────────────────── sampling + parsing helpers ─────────────────────────

    /// <summary>Run N independent samples (self-consistency). Returns the raw JSON strings that came back.</summary>
    private async Task<List<string>> SampleAsync(string prompt, IReadOnlyList<string>? images, CancellationToken ct)
    {
        int n = Math.Clamp(_opts.ConsensusSamples, 1, 7);
        var results = new List<string>(n);
        for (int i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            // Vary the seed per sample so "self-consistency" sees independent draws (deterministic, not Math.Random).
            var options = new Dictionary<string, object>
            {
                ["temperature"] = _opts.Temperature,
                ["seed"] = 1000 + i
            };
            var txt = await _client.GenerateAsync(_opts.Model, prompt, images, options, jsonFormat: true, ct: ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(txt)) results.Add(txt!);
        }
        return results;
    }

    private static List<string>? ImagesFor(CaptureFrame frame)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(frame.ImagePath) || !File.Exists(frame.ImagePath)) return null;
            return new List<string> { Convert.ToBase64String(File.ReadAllBytes(frame.ImagePath)) };
        }
        catch { return null; }
    }

    private static string OcrText(OcrFrame ocr) => ocr.Words.Count == 0 ? "(no OCR)" : string.Join(" ", ocr.Words.Select(w => w.Text));

    private static Evidence EvidenceFor(VisionContext ctx) => new()
    {
        Screenshots = string.IsNullOrWhiteSpace(ctx.Frame.ImagePath) ? new() : new() { Path.GetFileName(ctx.Frame.ImagePath) },
        Ocr = ctx.Ocr.Words.Take(12).Select(w => w.Text).ToList(),
        PromptId = "gx10-describe",
        Note = "GX10 advisory observation (consensus + OCR-capped)"
    };

    /// <summary>Fraction of claimed key tokens that appear (whole-word) in the frame's OCR — the corroboration cap.</summary>
    private static double CorroborationRatio(IReadOnlyList<string> claimTokens, OcrFrame ocr)
    {
        var keys = claimTokens.Where(t => t.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (keys.Count == 0) return 0.5;   // nothing specific claimed → neutral cap (consensus alone governs)
        int hit = keys.Count(k => ocr.FindWordWhole(k) is not null || ocr.FindWord(k) is not null);
        return Math.Clamp((double)hit / keys.Count, 0, 1);
    }

    private static IEnumerable<string> Tokens(string s) =>
        (s ?? "").Split(new[] { ' ', ',', '.', ':', ';', '/', '-', '(', ')', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);

    private static string Canon(string s) => string.Join(" ", Tokens(s)).ToLowerInvariant();

    private static string AbstainReason(IReadOnlyList<(bool Abstain, string Reason)> parsed, string fallback)
    {
        var r = parsed.FirstOrDefault(p => p.Abstain && !string.IsNullOrWhiteSpace(p.Reason)).Reason;
        return string.IsNullOrWhiteSpace(r) ? fallback : r!;
    }

    private static string AbstainReason(IReadOnlyList<DescribeReply> parsed, string fallback)
        => AbstainReason(parsed.Select(p => (p.Abstain, p.Reason)).ToList(), fallback);

    // --- strict JSON shapes (an unparseable reply ⇒ null ⇒ treated as abstention) ---

    private sealed record DescribeReply(bool Abstain, string Reason, string Summary, List<string> Controls);
    private sealed record ClassifyReply(bool Abstain, string Reason, string ScreenId);
    private sealed record FailureReply(bool Abstain, string Reason, string Cause, string FailureClass);
    private sealed record RecommendReply(bool Abstain, string Reason, string Summary, string Change);

    private DescribeReply? ParseDescribe(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var controls = new List<string>();
            if (r.TryGetProperty("controls", out var c) && c.ValueKind == JsonValueKind.Array)
                controls = c.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "").Where(s => s.Length > 0).ToList();
            return new DescribeReply(GetBool(r, "abstain"), GetStr(r, "reason"), GetStr(r, "summary"), controls);
        }
        catch (Exception ex) { _log.Trace("Gx10", $"describe parse failed (abstaining): {ex.Message}"); return null; }
    }

    private ClassifyReply? ParseClassify(string json)
    {
        try { using var doc = JsonDocument.Parse(json); var r = doc.RootElement; return new ClassifyReply(GetBool(r, "abstain"), GetStr(r, "reason"), GetStr(r, "screenId")); }
        catch { return null; }
    }

    private FailureReply? ParseFailure(string json)
    {
        try { using var doc = JsonDocument.Parse(json); var r = doc.RootElement; return new FailureReply(GetBool(r, "abstain"), GetStr(r, "reason"), GetStr(r, "cause"), GetStr(r, "failureClass")); }
        catch { return null; }
    }

    private RecommendReply? ParseRecommend(string json)
    {
        try { using var doc = JsonDocument.Parse(json); var r = doc.RootElement; return new RecommendReply(GetBool(r, "abstain"), GetStr(r, "reason"), GetStr(r, "summary"), GetStr(r, "change")); }
        catch { return null; }
    }

    private static bool? ParseAbstain(string json)
    {
        try { using var doc = JsonDocument.Parse(json); return GetBool(doc.RootElement, "abstain"); }
        catch { return null; }
    }

    private static string? ParseField(string json, string field)
    {
        try { using var doc = JsonDocument.Parse(json); return GetStr(doc.RootElement, field); }
        catch { return null; }
    }

    private static bool GetBool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));

    private static string GetStr(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
