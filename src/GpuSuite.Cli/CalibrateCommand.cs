using System.Diagnostics;
using System.Runtime.InteropServices;
using GpuSuite.Core.Config;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Io;
using GpuSuite.Core.Models;
using GpuSuite.Core.Remote;
using GpuSuite.Engine.Profiles;
using GpuSuite.Calibration;
using GpuSuite.Calibration.Schema;
using GpuSuite.Calibration.Observation;
using GpuSuite.Calibration.Llm;
using GpuSuite.Calibration.Persistence;
using GpuSuite.Calibration.Graph;
using GpuSuite.Calibration.Modules;
using GpuSuite.Calibration.Reporting;
using GpuSuite.Calibration.Decisions;
using GpuSuite.Calibration.Plugins;
using EngineVision = GpuSuite.Engine.Vision;

namespace GpuSuite.Cli;

internal static partial class Program
{
    /// <summary>
    /// `gpusuite calibrate` — runs the AI-assisted calibration plane.
    /// Calibrate/Learn observe menus, ValidateRoute checks a persisted or supplied route against the graph,
    /// and GameplayValidate judges a measured RunResult using deterministic evidence.
    /// escalation (OCR-first; the GX10 abstains rather than guess), builds + persists the menu graph and a
    /// full decision audit trail, runs a deterministic drift check vs. the stored baseline, and writes the
    /// human review report. It is ADVISORY and READ-ONLY: it drives nothing and never touches the
    /// deterministic benchmark path. See docs/ai-calibration-architecture.md.
    ///
    /// Flags: --game &lt;id&gt; (default cyberpunk-2077) · --calib-root &lt;dir&gt; (default Calibration) ·
    ///        --game-version --gpu --driver --res --preset (env fingerprint overrides).
    /// </summary>
    private static async Task<int> Calibrate(SuiteConfig cfg, ArgMap a)
    {
        string game = a.Get("--game") ?? "cyberpunk-2077";
        var profiles = new ProfileManager(cfg.ProfilesDir).LoadAll();
        var profile = profiles.FirstOrDefault(p => string.Equals(p.Id, game, StringComparison.OrdinalIgnoreCase)
                                                || string.Equals(p.Name, game, StringComparison.OrdinalIgnoreCase));
        var plugin = CalibrationPluginRegistry.Resolve(game) ?? (profile is null ? null : new ProfileCalibrationPlugin(profile));
        if (plugin is null)
        {
            Console.WriteLine($"No calibration plugin or game profile for '{game}'.");
            return 2;
        }

        var modeText = (a.Get("--mode") ?? "calibrate").Trim().ToLowerInvariant();
        var mode = modeText switch
        {
            "calibrate" or "observe" => CalibrationMode.Calibrate,
            "validate-route" or "route" => CalibrationMode.ValidateRoute,
            "learn" => CalibrationMode.Learn,
            "gameplay" or "phase3" or "gameplay-validate" => CalibrationMode.GameplayValidate,
            "phase5" or "improve" or "self-improve" => CalibrationMode.Phase5,
            _ => (CalibrationMode?)null
        };
        if (mode is null)
        {
            Console.Error.WriteLine("--mode must be calibrate, validate-route, learn, gameplay, or phase5.");
            return 2;
        }

        string calibRoot = a.Get("--calib-root") ?? "Calibration";

        using var log = new RunLogger(Path.Combine(Path.GetTempPath(), "gpusuite_calibrate.log"), echoToConsole: true);

        var thresholds = new CalibrationThresholds();
        var confidence = new ConfidenceEngine(thresholds);
        var db = new JsonCalibrationDatabase(calibRoot);
        if (a.Has("--list-approvals"))
        {
            var approvals = await db.GetApprovalsAsync(null, CancellationToken.None);
            foreach (var item in approvals)
                Console.WriteLine($"{item.Id}  {item.Status,-8}  {item.Game}  {item.Proposal.Kind}: {item.Proposal.Summary}");
            return 0;
        }
        if (a.Get("--approval") is { } approvalId)
        {
            var decisionText = (a.Get("--decision") ?? "").Trim().ToLowerInvariant();
            var decision = decisionText switch { "approve" or "approved" => ApprovalStatus.Approved, "reject" or "rejected" => ApprovalStatus.Rejected, _ => (ApprovalStatus?)null };
            if (decision is null) { Console.Error.WriteLine("--approval requires --decision approve|reject."); return 2; }
            var decided = await db.DecideApprovalAsync(approvalId, decision.Value, a.Get("--note"), CancellationToken.None);
            if (decided is null) { Console.Error.WriteLine($"Approval '{approvalId}' not found."); return 2; }
            Console.WriteLine($"{decided.Id}: {decided.Status}. No profile, route, bot, or threshold was auto-applied.");
            return 0;
        }
        // Observer + OCR. Default = the offline stub (scripted screens, no hardware). With --live the REAL
        // capture-card observer + ScreenReader OCR drop in (CLI composition root), so the GX10 reasoner gets an
        // actual screenshot per screen. --live captures whatever the card currently shows (the sidecar drives
        // nothing), so the operator preps the game/screen.
        IScreenObserver observer;
        IOcrEngine ocr;
        bool guided = a.Has("--guided");
        bool driveDry = a.Has("--drive-dry");
        if (mode == CalibrationMode.Phase5)
        {
            observer = new StubScreenObserver(plugin.BuildSyntheticScript());
            ocr = new StubOcrEngine();
        }
        else if (a.Has("--drive") || driveDry)
        {
            // BOT-DRIVEN: the deterministic input engine injects each screen's Drive sequence (engine drives,
            // NOT the LLM) so calibration is hands-off; the plane only observes. --drive-dry logs the planned
            // injections without sending input (offline-safe). Requires the game running (in its menu) for --drive.
            if (string.IsNullOrWhiteSpace(cfg.CaptureCardDevice))
            { Console.Error.WriteLine("--drive needs a capture device (settings.captureCardDevice)."); return 2; }
            var grabber = new EngineVision.CaptureCardGrabber(cfg.FfmpegPath, cfg.CaptureCardDevice, log);
            if (!grabber.FfmpegResolved)
            { Console.Error.WriteLine("--drive needs ffmpeg (settings.ffmpegPath)."); return 2; }
            var reader = new EngineVision.ScreenReader(grabber, log);

            int? drivePid = null;
            var prof = profiles.FirstOrDefault(p => string.Equals(p.Id, plugin.Game, StringComparison.OrdinalIgnoreCase));
            if (prof is not null && !string.IsNullOrWhiteSpace(prof.CaptureProcessName))
            {
                var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(prof.CaptureProcessName));
                if (procs.Length > 0) drivePid = procs[0].Id;
            }
            if (!driveDry && drivePid is null)
            { Console.Error.WriteLine($"--drive: '{plugin.Game}' is not running — start it (in its menu) or use --drive-dry / --guided."); return 2; }

            var driveFor = plugin.ExpectedScreens.ToDictionary(s => s.Id, s => s.Drive, StringComparer.OrdinalIgnoreCase);
            observer = new BotDrivenObserver(grabber, Path.Combine(Path.GetTempPath(), "gpusuite_calibrate_live"), log, driveFor, drivePid, driveDry);
            ocr = new CaptureCardOcrEngine(reader);
            Console.WriteLine(driveDry
                ? "  Observer       : BOT-DRIVEN (dry) — logs planned nav injections; no input sent; the ENGINE would drive."
                : $"  Observer       : BOT-DRIVEN — injects each screen's Drive sequence (engine drives, not the LLM) to pid {drivePid}.");
        }
        else if (a.Has("--live") || guided)
        {
            if (string.IsNullOrWhiteSpace(cfg.CaptureCardDevice))
            { Console.Error.WriteLine("--live needs a capture device (settings.captureCardDevice)."); return 2; }
            var grabber = new EngineVision.CaptureCardGrabber(cfg.FfmpegPath, cfg.CaptureCardDevice, log);
            if (!grabber.FfmpegResolved)
            { Console.Error.WriteLine("--live needs ffmpeg (settings.ffmpegPath)."); return 2; }
            var reader = new EngineVision.ScreenReader(grabber, log);
            observer = new CaptureCardObserver(grabber, Path.Combine(Path.GetTempPath(), "gpusuite_calibrate_live"), log, guided);
            ocr = new CaptureCardOcrEngine(reader);
            Console.WriteLine(guided
                ? "  Observer       : LIVE + GUIDED — you navigate to each screen; the plane observes (it drives NOTHING)."
                : "  Observer       : LIVE capture card — real screenshots + OCR (GX10 sees the actual screen).");
        }
        else
        {
            observer = new StubScreenObserver(plugin.BuildSyntheticScript());
            ocr = new StubOcrEngine();
        }

        // The AI Calibration Engineer. Use the REAL GX10 reasoner when the box is reachable; otherwise fall back
        // to the abstaining reasoner so "never guess" always holds. Endpoint: --gx10 or settings.gx10Endpoint;
        // --no-gx10 forces the safe stub. The reasoner OBSERVES + RECOMMENDS only — it never drives the game.
        string gx10Endpoint = a.Get("--gx10") ?? cfg.Gx10Endpoint;
        ILLMReasoner llm;
        if (mode == CalibrationMode.Phase5)
        {
            Console.WriteLine("  GX10 reasoner  : not needed — Phase 5 statistics and fingerprint comparisons are deterministic.");
            llm = new AbstainingLLMReasoner();
        }
        else if (a.Has("--no-gx10"))
        {
            Console.WriteLine("  GX10 reasoner  : disabled (--no-gx10) — abstaining reasoner (never guesses).");
            llm = new AbstainingLLMReasoner();
        }
        else
        {
            var gx10Status = await new Gx10Client(gx10Endpoint, log).ProbeAsync(CancellationToken.None, 5000);
            if (gx10Status.Reachable)
            {
                Console.WriteLine($"  GX10 reasoner  : ONLINE — {gx10Status.Summary()}");
                llm = new Gx10Reasoner(new Gx10ReasonerOptions { Endpoint = gx10Endpoint, Model = cfg.NavSupervisorVisionModel }, log);
            }
            else
            {
                Console.WriteLine($"  GX10 reasoner  : offline ({gx10Status.Error}) — abstaining reasoner (never guesses).");
                llm = new AbstainingLLMReasoner();
            }
        }

        var graph = new MenuGraphBuilder();
        var comparer = new LayoutComparer();
        var report = new HtmlCalibrationReportGenerator();
        var decisions = new DecisionLog(log);

        var session = new CalibrationSession(observer, ocr, llm, confidence, graph, comparer, db, report, decisions, plugin, log);

        var env = new EnvFingerprint
        {
            GameVersion = a.Get("--game-version") ?? "unknown",
            SuiteVersion = GpuSuite.BuildInfo.DisplayVersion,
            Gpu = a.Get("--gpu") ?? (string.IsNullOrWhiteSpace(cfg.PreferredGpu) ? "unknown" : cfg.PreferredGpu),
            DriverVersion = a.Get("--driver") ?? "unknown",
            WindowsVersion = RuntimeInformation.OSDescription,
            Resolution = a.Get("--res") ?? "3840x2160",
            Preset = a.Get("--preset") ?? "Ray Tracing: Ultra",
            Language = "en-US",
            Timestamp = DateTime.Now.ToString("o")
        };

        RouteRecord? route = null;
        if (a.Get("--route") is { } routePath)
        {
            route = Json.Load<RouteRecord>(routePath);
            if (route is null) { Console.Error.WriteLine($"Could not read route: {routePath}"); return 2; }
        }
        RunResult? runResult = null;
        if (a.Get("--run-result") is { } resultPath)
        {
            runResult = Json.Load<RunResult>(resultPath);
            if (runResult is null) { Console.Error.WriteLine($"Could not read RunResult: {resultPath}"); return 2; }
        }
        if (mode == CalibrationMode.GameplayValidate && runResult is null)
        {
            Console.Error.WriteLine("gameplay mode requires --run-result <result.json>.");
            return 2;
        }
        GameplayEvidence? gameplayEvidence = null;
        if (a.Get("--evidence") is { } evidencePath)
        {
            gameplayEvidence = Json.Load<GameplayEvidence>(evidencePath);
            if (gameplayEvidence is null) { Console.Error.WriteLine($"Could not read GameplayEvidence: {evidencePath}"); return 2; }
        }
        NavRecording? recording = null;
        if (a.Get("--recording") is { } recordingPath)
        {
            recording = Json.Load<NavRecording>(recordingPath);
            if (recording is null) { Console.Error.WriteLine($"Could not read NavRecording: {recordingPath}"); return 2; }
        }
        SuiteResult? currentSuite = null;
        SuiteResult? baselineSuite = null;
        if (mode == CalibrationMode.Phase5)
        {
            var currentPath = a.Get("--suite-result") ?? a.Get("--current") ??
                (Directory.Exists(cfg.ResultsRoot)
                    ? Directory.GetFiles(cfg.ResultsRoot, "suite_result.json", SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                    : null);
            if (string.IsNullOrWhiteSpace(currentPath) || (currentSuite = Json.Load<SuiteResult>(currentPath)) is null)
            { Console.Error.WriteLine("phase5 mode requires --suite-result <suite_result.json> (or a saved result under resultsRoot)."); return 2; }
            if (a.Get("--baseline") is { } baselinePath && (baselineSuite = Json.Load<SuiteResult>(baselinePath)) is null)
            { Console.Error.WriteLine($"Could not read baseline SuiteResult: {baselinePath}"); return 2; }
        }

        Console.WriteLine($"\nAI Calibration Engineer — {plugin.Name} [{mode}]");
        Console.WriteLine(mode is CalibrationMode.Calibrate or CalibrationMode.Learn
            ? "Observe → OCR → confidence gate → menu graph → persist evidence → report.\n"
            : "Load evidence → deterministic validation → classify/recover on failure → persist report.\n");

        var result = await session.RunAsync(
            new CalibrationRequest
            {
                Game = game, Mode = mode.Value, Env = env, Goal = a.Get("--goal"), Route = route,
                RunResult = runResult, SceneDescriptor = a.Get("--scene"), AllowStaticScene = a.Has("--allow-static"),
                GameplayEvidence = gameplayEvidence, RequireVisualEvidence = a.Has("--require-visual-evidence"),
                NavRecording = recording,
                MinimumSceneSimilarity = a.GetDouble("--scene-min") ?? 0.75,
                MinimumSpawnSimilarity = a.GetDouble("--spawn-min") ?? 0.70,
                MaximumLateShaderHitches = a.GetInt("--max-late-hitches") ?? 0,
                MinimumStableSecondsAfterShaderHitch = a.GetDouble("--shader-stable-seconds") ?? 5,
                CurrentSuiteResult = currentSuite, BaselineSuiteResult = baselineSuite,
                CurrentValidationThresholds = cfg.Validation
            },
            CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine($"  Screens mapped : {result.Graph.Nodes.Count}");
        Console.WriteLine($"  Transitions    : {result.Graph.Edges.Count}");
        Console.WriteLine($"  Decisions      : {result.Decisions.Count} (full audit trail in the report)");
        Console.WriteLine($"  Recommendations: {result.Recommendations.Count}");
        if (result.RouteVerdict is not null)
            Console.WriteLine($"  Route          : {(result.RouteVerdict.Valid ? "valid" : "invalid — " + result.RouteVerdict.Reason)}");
        if (result.BotDraft is not null)
            Console.WriteLine($"  Learned draft  : {result.BotDraft.Steps.Count} step(s) to {result.BotDraft.Goal}");
        if (result.GameplayVerdict is not null)
            Console.WriteLine($"  Gameplay       : {(result.GameplayVerdict.IsGameplay && result.GameplayVerdict.SceneMatch && result.GameplayVerdict.SpawnMatch && result.GameplayVerdict.HudMatch ? "pass" : "fail")} — {result.GameplayVerdict.Rationale}");
        if (result.Recovery is not null)
            Console.WriteLine($"  Recovery       : {string.Join(" → ", result.Recovery.Steps.Select(s => s.Kind))}");
        if (result.Phase5 is not null)
        {
            Console.WriteLine($"  Shared patterns: {result.Phase5.SharedPatterns.Count}");
            Console.WriteLine($"  Threshold drafts: {result.Phase5.ThresholdRecommendations.Count}");
            Console.WriteLine($"  Approval queue : {result.Phase5.ApprovalRequests.Count}");
            if (result.Phase5.Regression is not null) Console.WriteLine($"  Regression     : {result.Phase5.Regression.Summary}");
        }
        if (result.Drift is not null)
            Console.WriteLine($"  Drift          : {(result.Drift.Changes.Count == 0 ? "none — matches baseline" : result.Drift.Changes.Count + " change(s) vs. baseline")}");
        else
            Console.WriteLine($"  Drift          : n/a — baseline established this run");
        Console.WriteLine($"  Report (HTML)  : {result.Report?.HtmlPath}");
        Console.WriteLine($"  Report (JSON)  : {result.Report?.JsonPath}");
        Console.WriteLine($"  Store          : {Path.GetFullPath(db.GameDirectory(plugin.Game))}");
        Console.WriteLine($"\n  {result.Message}");

        return result.Success ? 0 : 1;
    }
}
