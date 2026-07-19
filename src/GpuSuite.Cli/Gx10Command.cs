using GpuSuite.Core.Config;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Models;
using GpuSuite.Core.Remote;
using GpuSuite.Calibration.Llm;
using GpuSuite.Calibration.Observation;
using GpuSuite.Engine.Orchestration;
using EngineVision = GpuSuite.Engine.Vision;

namespace GpuSuite.Cli;

internal static partial class Program
{
    /// <summary>
    /// ADVISORY GX10 failure-triage: for each FAILED game in the result, ask the GX10 AI Calibration Engineer to
    /// hypothesize the likely cause from the deterministic evidence (failure class, reason, valid/total runs,
    /// variants). It NEVER changes the deterministic class/reason — it only attaches a human-reviewed note
    /// (<see cref="GameOutcome.AdvisoryCause"/>) shown in the report + roster summary. Skips silently when the box
    /// is offline or there are no failures; the GX10 may abstain (then no note is added). Sidecar-pure.
    /// </summary>
    private static async Task Gx10TriageFailures(SuiteResult suite, SuiteConfig cfg, bool enabled, RunLogger log)
    {
        if (!enabled) return;
        var failed = suite.GameOutcomes.Where(o => o.Status == GameStatus.Failed).ToList();
        if (failed.Count == 0) return;

        string endpoint = cfg.Gx10Endpoint;
        var status = await new Gx10Client(endpoint, log).ProbeAsync(CancellationToken.None, 4000);
        if (!status.Reachable)
        {
            log.Info("Triage", $"GX10 offline ({status.Error}) — skipping advisory failure-triage (deterministic reasons stand).");
            return;
        }

        // Triage reasons over TEXT (failure class/reason/run counts), so use a fast TEXT model + 1 sample instead of
        // the slow 4K vision model that made the original triage ~4 min/failure. Fall back to the vision model only if
        // the text model isn't installed on the box (so triage still works on a box without gpt-oss).
        string triageModel = status.HasModel(cfg.Gx10TriageModel) ? cfg.Gx10TriageModel : cfg.NavSupervisorVisionModel;
        int triageSamples = Math.Max(1, cfg.Gx10TriageSamples);
        Console.WriteLine($"\n  GX10 failure-triage (advisory) — hypothesizing {failed.Count} failure(s) via '{triageModel}' ×{triageSamples}…");
        var reasoner = new Gx10Reasoner(new Gx10ReasonerOptions { Endpoint = endpoint, Model = triageModel, ConsensusSamples = triageSamples }, log);
        foreach (var o in failed)
        {
            var ctx = new FailureContext
            {
                KnownFailureClass = FailureClassifier.Label(o.FailureClass),
                Summary = $"{o.Name} ({o.GameId}) produced no valid benchmark result.",
                Evidence = new List<string>
                {
                    $"deterministic failure class: {o.FailureClass} ({FailureClassifier.Label(o.FailureClass)})",
                    $"valid runs: {o.ValidRuns} of {o.TotalRuns}",
                    $"variants attempted: {(o.Variants.Count > 0 ? string.Join(", ", o.Variants) : "default")}",
                    $"recorded reason: {o.Reason}"
                }
            };
            var h = await reasoner.HypothesizeFailureAsync(ctx, CancellationToken.None);
            if (!h.Abstained && !string.IsNullOrWhiteSpace(h.Cause))
            {
                o.AdvisoryCause = h.Cause;
                o.AdvisoryConfidence = h.Confidence?.ToString();
                log.Info("Triage", $"{o.GameId}: GX10 hypothesis — {h.Cause} [{o.AdvisoryConfidence}] (advisory).");
            }
            else
            {
                log.Info("Triage", $"{o.GameId}: GX10 abstained — no confident hypothesis (deterministic reason stands).");
            }
        }
    }

    /// <summary>
    /// `gpusuite gx10` — connect to the lab GX10 / GB10 box and print its live statistics so the operator can
    /// confirm it is up and serving the right models BEFORE starting a test run. Read-only: it never loads,
    /// pulls, or unloads a model. Exit 0 when reachable, 1 when not (so it scripts cleanly in a pre-run check).
    ///
    /// Flags: --gx10 &lt;endpoint&gt; (override settings.gx10Endpoint) · --model &lt;id&gt; (check a specific model is present).
    /// </summary>
    private static async Task<int> Gx10StatusCommand(SuiteConfig cfg, ArgMap a)
    {
        if (a.Has("--describe")) return await Gx10DescribeLive(cfg, a);

        string endpoint = a.Get("--gx10") ?? cfg.Gx10Endpoint;
        string wantModel = a.Get("--model") ?? cfg.NavSupervisorVisionModel;

        Console.WriteLine($"\nGX10 connection — {Gx10Client.Normalize(endpoint)}");
        Console.WriteLine("Probing (read-only: version, models, loaded/VRAM)…\n");

        var status = await new Gx10Client(endpoint).ProbeAsync(CancellationToken.None, 6000);

        if (!status.Reachable)
        {
            Console.WriteLine($"  Status         : OFFLINE — {status.Error}");
            Console.WriteLine($"  Vision compute : the GX10 is unavailable → the LOCAL GPU will handle the vision model.");
            Console.WriteLine("\n  (Is the box on the LAN and is Ollama serving? Try: ping the host, or open the endpoint in a browser.)");
            return 1;
        }

        Console.WriteLine($"  Status         : ONLINE");
        Console.WriteLine($"  Version        : {status.Version}");
        Console.WriteLine($"  Latency        : {status.LatencyMs:0} ms");
        Console.WriteLine($"  Models         : {status.Models.Count} installed ({status.VisionModels.Count()} vision-capable)");
        foreach (var m in status.Models)
        {
            string tag = m.IsVision ? " [vision]" : "";
            string meta = string.Join(" ", new[] { m.ParameterSize, m.Quantization }.Where(s => !string.IsNullOrWhiteSpace(s)));
            Console.WriteLine($"      - {m.Name,-34} {m.SizeGb,5:0.0} GB  {meta}{tag}");
        }

        if (status.Loaded.Count == 0)
            Console.WriteLine($"  Loaded now     : none resident (cold — first request will load a model)");
        else
        {
            Console.WriteLine($"  Loaded now     : {status.Loaded.Count} resident");
            foreach (var l in status.Loaded)
                Console.WriteLine($"      - {l.Name,-34} {l.VramGb,5:0.0} GB VRAM ({l.GpuFraction * 100:0}% on GPU)");
        }

        bool hasWanted = status.HasModel(wantModel);
        Console.WriteLine($"  Vision model   : '{wantModel}' is {(hasWanted ? "PRESENT" : "NOT installed")}" +
            (hasWanted ? "" : $"  → pull it on the box:  ollama pull {wantModel}"));
        Console.WriteLine($"  Vision compute : the GX10 is available — set visionCompute=gx10 (or auto) to run the vision model OFF-BENCH here.");
        return 0;
    }

    /// <summary>
    /// `gpusuite gx10 --describe` — grab ONE live capture-card frame and ask the GX10 vision reasoner what is
    /// on screen, sending the REAL screenshot (not just OCR text) through the full anti-hallucination contract
    /// (structured JSON · N-sample consensus · OCR-corroboration cap · abstain-when-unsure). Proves the
    /// capture → OCR → GX10 describe path end-to-end. Read-only; drives nothing.
    /// </summary>
    private static async Task<int> Gx10DescribeLive(SuiteConfig cfg, ArgMap a)
    {
        string endpoint = a.Get("--gx10") ?? cfg.Gx10Endpoint;
        string model = a.Get("--model") ?? cfg.NavSupervisorVisionModel;
        using var log = new RunLogger(Path.Combine(Path.GetTempPath(), "gpusuite_gx10_describe.log"), echoToConsole: false);

        if (string.IsNullOrWhiteSpace(cfg.CaptureCardDevice))
        { Console.Error.WriteLine("No capture device (set settings.captureCardDevice or --device)."); return 1; }

        var grabber = new EngineVision.CaptureCardGrabber(cfg.FfmpegPath, cfg.CaptureCardDevice, log);
        if (!grabber.FfmpegResolved) { Console.Error.WriteLine("ffmpeg not resolved (set settings.ffmpegPath)."); return 1; }

        var status = await new Gx10Client(endpoint, log).ProbeAsync(CancellationToken.None, 5000);
        if (!status.Reachable) { Console.WriteLine($"GX10 offline ({status.Error}) — cannot describe."); return 1; }
        if (!status.HasModel(model)) { Console.WriteLine($"GX10 is online but the vision model '{model}' is not pulled. Run: ollama pull {model}"); return 1; }

        Console.WriteLine($"\nGX10 describe — grabbing a live capture-card frame and asking '{model}' what is on screen…\n");

        var reader = new EngineVision.ScreenReader(grabber, log);
        var shotDir = Path.Combine(Path.GetTempPath(), "gpusuite_gx10_describe");
        var observer = new CaptureCardObserver(grabber, shotDir, log);
        var ocr = new CaptureCardOcrEngine(reader);

        var frame = await observer.ObserveAsync("live", CancellationToken.None);
        if (string.IsNullOrWhiteSpace(frame.ImagePath)) { Console.WriteLine("Capture-card grab failed."); return 1; }
        var ocrFrame = await ocr.ReadAsync(frame, CancellationToken.None);

        var reasoner = new Gx10Reasoner(new Gx10ReasonerOptions { Endpoint = endpoint, Model = model }, log);
        var desc = await reasoner.DescribeScreenAsync(
            new VisionContext { Frame = frame, Ocr = ocrFrame, KnownScreens = new() }, CancellationToken.None);

        Console.WriteLine($"  Screenshot : {frame.ImagePath}");
        Console.WriteLine($"  OCR words  : {ocrFrame.Words.Count}{(ocrFrame.Words.Count > 0 ? "  (e.g. " + string.Join(", ", ocrFrame.Words.Take(6).Select(w => w.Text)) + ")" : "")}");
        Console.WriteLine($"  Abstained  : {desc.Abstained}");
        Console.WriteLine($"  Summary    : {(string.IsNullOrWhiteSpace(desc.Summary) ? "(none)" : desc.Summary)}");
        if (desc.Controls.Count > 0) Console.WriteLine($"  Controls   : {string.Join(", ", desc.Controls.Select(c => c.Label))}");
        Console.WriteLine($"  Confidence : {desc.Confidence}");
        Console.WriteLine($"  Rationale  : {desc.Rationale}");
        return desc.Abstained ? 2 : 0;
    }
}
