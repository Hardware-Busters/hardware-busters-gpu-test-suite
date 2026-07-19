using GpuSuite.Core.Config;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Io;
using GpuSuite.Core.Models;
using GpuSuite.Core.Stats;
using GpuSuite.Engine.Automation;
using GpuSuite.Engine.Validation;
using GpuSuite.Engine.Vision;
using GpuSuite.Measurement;

namespace GpuSuite.Engine.Scenes;

/// <summary>
/// (3) Scene Runner. Executes one repeat of one scene at one resolution end-to-end:
/// settings apply+verify → start telemetry+power → start frame capture → trigger benchmark →
/// detect START → (measured window) → detect FINISH → stop all → trim to window → stats →
/// save raw + summary → validate. Benchmark scenes are marker/activity-driven (no fixed sleep).
/// </summary>
public sealed class SceneRunner
{
    private readonly SuiteConfig _cfg;
    private readonly RunValidator _validator;

    public SceneRunner(SuiteConfig cfg, RunValidator validator)
    {
        _cfg = cfg;
        _validator = validator;
    }

    /// <summary>Resolve the nav DESKTOP-guard anchor list from settings: empty ⇒ the built-in default shell anchors;
    /// "none"/"off" ⇒ disabled (empty); otherwise the comma-separated list. Set on every nav <c>InputAutomationEngine</c>.</summary>
    private IReadOnlyList<string> ResolveDesktopAnchors()
    {
        var raw = (_cfg.DesktopGuardAnchors ?? "").Trim();
        if (raw.Equals("none", StringComparison.OrdinalIgnoreCase) || raw.Equals("off", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<string>();
        return raw.Length == 0
            ? GpuSuite.Engine.Vision.DesktopDetector.DefaultAnchors
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task<RunResult> RunAsync(
        GameProfile game, SceneProfile scene, GameVariant? variant, Resolution res, int repeat, int totalRepeats,
        ProviderSet providers, WorkloadHint hint, bool gameLaunchedOrSimulated, bool realGame, int? gamePid,
        string gpuName, RunPaths paths, RunLogger log, CancellationToken ct, bool reRun = false, IGamepad? sharedPad = null)
    {
        var runDir = paths.EnsureRunDir(game.Id, scene.Id, res.Name, repeat, variant?.Id);
        var run = new RunResult
        {
            GpuName = gpuName, GameId = game.Id, SceneId = scene.Id,
            VariantId = variant?.Id ?? "", VariantName = variant?.Name ?? "",
            ResolutionName = res.Name, RepeatIndex = repeat, StartedUtc = DateTime.UtcNow,
            FrameSource = providers.FrameMode, PowerSource = providers.PowerMode, TelemetrySource = providers.TelemetryMode,
            FrameProviderName = providers.Frames.Name, PowerProviderName = providers.Power.Name,
            TelemetryProviderName = providers.Telemetry.Name
        };
        var target = new FrameCaptureTarget
        {
            ProcessName = game.PresentMonByPid ? "" : game.CaptureProcessName, Pid = gamePid, Hint = hint
        };

        // Defensive fail-fast for any caller that hands us a genuine launch failure. A real run must never
        // spend the scene deadline running an inject=false bot over synthetic frames after the process failed
        // to appear (live Black Myth launch stall, 2026-07-14).
        if (!gameLaunchedOrSimulated)
        {
            run.ValidationIssues.Add("Game did not launch and was not simulated.");
            run.Verdict = RunVerdict.Invalid;
            run.EndedUtc = DateTime.UtcNow;
            Persist(runDir, run);
            log.Error("Scene", $"Run {repeat} INVALID — game process absent; skipping readiness, capture, and bot deadline.");
            return run;
        }

        // (3) Apply graphics preset + resolution, then verify. Failure ⇒ Invalid, no capture.
        var settings = await new SettingsNavigator(log).ApplyAndVerifyAsync(game, res, realGame, gamePid, ct).ConfigureAwait(false);
        if (!settings.Ok)
        {
            run.ValidationIssues.AddRange(settings.Issues);
            run.Verdict = RunVerdict.Invalid;
            run.EndedUtc = DateTime.UtcNow;
            Persist(runDir, run);
            log.Warn("Validate", $"Run {repeat} INVALID — {string.Join("; ", settings.Issues)}");
            return run;
        }

        // (5) readiness
        await WaitReadinessAsync(scene, gameLaunchedOrSimulated, log, ct).ConfigureAwait(false);

        // (5b) For a windowed-by-necessity game (must stay at the 60 Hz desktop refresh so an exclusive >60 Hz
        // mode can't blank the Elgato — e.g. DOOM), force the window to fill the screen. The default windowed
        // box only covers part of the 4K capture, leaving the desktop visible, which (a) feeds the capture-card
        // OCR nav stray on-screen log text (WaitForText false-positives) and (b) under-renders. Borderless-full
        // keeps it DWM-composited at 60 Hz (Elgato-safe) while giving a clean full-screen game for OCR + a
        // full-res render. Opt-in per profile; exclusive-fullscreen games already fill the screen and skip this.
        if (game.Launch.ForceBorderlessFullscreen && realGame && gamePid is int bpid)
            await GpuSuite.Engine.Automation.GameWindowManager.ForceBorderlessFullscreenAsync(bpid, log, ct, waitForWindowSeconds: 90).ConfigureAwait(false);

        // (6/7) continuous telemetry + power for the whole scene
        log.Info("Capture", $"Starting telemetry ({providers.Telemetry.Name}) + power ({providers.Power.Name}).");
        await using var telSession = await providers.Telemetry.StartAsync(_cfg.TelemetryIntervalMs, hint, ct).ConfigureAwait(false);
        await using var pwrSession = await providers.Power.StartAsync(_cfg.PowerLogIntervalMs, hint, ct).ConfigureAwait(false);

        var osdMeta = new BenchmarkOsd.Meta
        {
            GameName = variant is null ? game.Name : $"{game.Name} · {variant.Id}",
            ResolutionName = res.Name, Width = res.Width, Height = res.Height,
            Pass = repeat, TotalPasses = totalRepeats, FrameSource = providers.Frames.Name,
            PowerProvenance = PowerProvenance.Label(providers.Power.Measurement)
        };

        IReadOnlyList<FrameSample> frames;
        IReadOnlyList<PowerSample> power;
        IReadOnlyList<TelemetrySample> telemetry;
        CompletionResult completion;
        // Set when a Required bot gate timed out (nav desynced; the window is a menu/loading screen, not the
        // benchmark). Forces the run Invalid below so the suite never persists a false menu-measured pass.
        bool botAborted = false; string? botAbortReason = null;
        var botTraversals = new List<BotTraversalStats>();

        // Runtime health watchdog (Milestone 3): observes the live sessions + process during the measured
        // window and records an incident (GPU idle, frozen render, telemetry/power stopped, process exited,
        // timeout) with a screenshot + log. A recorded incident invalidates the run below → auto-repeat recovery.
        HealthIncident? healthIncident = null;
        MotionStats? measuredMotion = null;   // scene-static sensor stats (report trust signal)
        double healthTimeout = _cfg.RuntimeHealth.BenchmarkTimeoutSeconds > 0
            ? _cfg.RuntimeHealth.BenchmarkTimeoutSeconds
            : (scene.Kind == SceneKind.BuiltInBenchmark ? Math.Max(scene.Completion.MaxTimeoutSeconds, scene.CaptureSeconds) : scene.CaptureSeconds) + 180;
        // Gate the runtime watchdog's render-health (gpu-idle / frames-frozen) to the MEASURED window: a scripted-
        // scene bot CLOSES it during nav (static cold-launch screens are legitimately GPU-idle) and OPENS it at
        // MarkStart. Starts OPEN, so a builtin benchmark or a route-only attach scene (no bot MarkStart) is armed
        // throughout. Fixes the AW2 cold-launch false "frozen capture" (2026-06-29).
        var measuredGate = new MeasuredWindowGate(initiallyOpen: true);

        if (scene.Kind == SceneKind.BuiltInBenchmark)
        {
            log.Info("Capture", $"Starting frame capture ({providers.Frames.Name}); detecting benchmark window via '{scene.Completion.Method}'.");
            await using var frameSession = await providers.Frames.StartAsync(target, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(frameSession.Diagnostics))
                log.Info("Capture", $"{providers.Frames.Name} startup: {frameSession.Diagnostics}");
            await using var osd = BenchmarkOsd.Start(_cfg, osdMeta, frameSession, pwrSession, telSession, log);
            osd.Phase = "WAIT";
            var captureStartUtc = DateTime.UtcNow;
            // Hold the window borderless-fullscreen across the WHOLE nav+benchmark: a one-shot force is undone
            // when the game re-applies its own windowed geometry on a menu transition (idTech 8 / DOOM), which
            // re-exposes the desktop and breaks the OCR nav. The keeper snaps it back within ~0.75s and is a
            // no-op during the steady-state flythrough. Cancelled after capture stops.
            using var keeperCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task? windowKeeper = (game.Launch.ForceBorderlessFullscreen && realGame && gamePid is int kpid)
                ? Task.Run(() => GpuSuite.Engine.Automation.GameWindowManager.KeepBorderlessFullscreenAsync(kpid, log, keeperCts.Token), CancellationToken.None)
                : null;
            var (bot, padHold) = await TriggerBenchmarkAsync(scene, realGame, gamePid, log, ct, reRun, sharedPad).ConfigureAwait(false);
            if (bot is not null) botTraversals.AddRange(bot.Traversals);
            if (bot?.Aborted == true) { botAborted = true; botAbortReason = bot.AbortReason; }
            try
            {
                // For a menu-navigation bot, the frames captured WHILE it drove the menu are not the
                // benchmark — take the frame count after navigation as the baseline so START detection (and
                // therefore the trimmed measured window) begins only when the benchmark itself renders.
                int frameBaseline = bot is not null ? frameSession.SampleCount : 0;
                if (frameBaseline > 0) log.Info("Capture", $"Menu navigation done; ignoring {frameBaseline} pre-benchmark frame(s) for window detection.");

                // SKIP the completion-watch when the bot already ABORTED (2026-07-09): the run is rejected
                // regardless, so waiting the full completion window for a result the aborted/dead game will
                // never produce is pure dead time. Worse, for result-file games (F1) a DEAD process left the
                // run sitting the whole 180s result-file wait with no heartbeat — long enough for the InputGuard's
                // last-resort crash/hang auto-release to fire an ESC that cancelled the ENTIRE campaign (one dead
                // game killed all remaining cells; live 2026-07-09, F1 EA-wedge). Rejecting fast here lets the
                // orchestrator relaunch/continue and re-ping the heartbeat before the InputGuard ever trips —
                // the crash stays isolated to the game (its hang-watchdog skips just it), which is the intent.
                if (botAborted)
                {
                    log.Warn("Capture", $"Bot aborted ({botAbortReason}) — SKIPPING the benchmark completion-watch; the run is already rejected and a dead/desynced game won't produce a result. Not polling out the {scene.CaptureSeconds}s window.");
                    completion = new CompletionResult { Mode = "aborted", StartDetected = false, FinishDetected = false };
                }
                else
                {
                    osd.Phase = "RUN";
                    var health = RuntimeHealthMonitor.Start(_cfg.RuntimeHealth, _cfg, log, runDir,
                        () => frameSession.SampleCount, () => telSession.Latest?.GpuLoadPct, () => pwrSession.Latest?.GpuTotalW,
                        () => telSession.SampleCount, realGame ? gamePid : null, healthTimeout, ct, () => measuredGate.IsOpen,
                        realGame ? MeasuredMotionFloorFor(scene) : 0, scene.MeasuredMotionDelaySeconds, providers.Frames.Name);
                    completion = await new CompletionDetector(log)
                        .DetectAsync(scene.Completion, scene.CaptureSeconds, captureStartUtc, () => frameSession.SampleCount, ct, frameBaseline)
                        .ConfigureAwait(false);
                    if (health is not null) { healthIncident = await health.StopAsync().ConfigureAwait(false); measuredMotion = health.Motion; }
                }

                osd.Phase = "DONE";
                frames = await frameSession.StopAsync().ConfigureAwait(false);
                if (frames.Count == 0 && frameSession.Diagnostics is { Length: > 0 } diag)
                    log.Warn("Capture", $"{providers.Frames.Name} captured 0 frames — provider diagnostics: {diag}");
                power = await pwrSession.StopAsync().ConfigureAwait(false);
                telemetry = await telSession.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                // Stop holding the window borderless once capture is done (before disposing the pad).
                keeperCts.Cancel();
                if (windowKeeper is not null) { try { await windowKeeper.ConfigureAwait(false); } catch { /* keeper is best-effort */ } }
                // Disconnect the kept-alive virtual pad ONLY after capture stops — so a controller-required
                // game's "controller disconnected" modal can't pop over the measured flythrough.
                padHold?.Dispose();
            }
        }
        else
        {
            // === FIRST-CLASS WARM-UP PHASE (operator spec 2026-06-29 — generic, profile-driven) ===
            // A cold GPU renders a scripted-scene game's heavy gameplay frames BELOW steady-state (cold shader/
            // asset caches) while trivial load/black screens render very fast — so a cold autonomous run would
            // measure shader COMPILATION, not GPU rendering (proven on Ratchet: 117 fps warm vs <75 cold; black
            // load = 247 fps). When the profile opts in, drive a DETERMINISTIC warm-up BEFORE the measured capture
            // so the numbers are steady-state. Frames are NOT captured here — the measured frame session starts
            // AFTER (below); telemetry/power run continuously but are windowed to [MarkStart,MarkEnd] so warm-up
            // samples are excluded from every reported number. Logged for traceability. Three modes:
            // none / shaderPrecompile (wait for the launch compile screen) / gameplay (moving traversal).
            bool ranGameplayWarmup = false;
            if (scene.Warmup is { Enabled: true } wu && realGame && !reRun)
            {
                var wmode = (wu.Mode ?? "gameplay").Trim().ToLowerInvariant();
                var warmStartUtc = DateTime.UtcNow;
                log.Info("Warmup", $"WARM-UP phase START — mode={wmode}, target {wu.DurationSeconds:0}s, script='{wu.Script ?? "(fixed wait)"}'. Frames/power are NOT in the reported numbers; warming shader+asset caches so the MEASURED window is steady-state GPU rendering, not shader-compile.");
                if (wmode == "shaderprecompile")
                    await WaitForShaderPrecompileAsync(scene, wu, realGame, gamePid, log, ct).ConfigureAwait(false);
                else if (wmode != "none")
                {
                    await RunWarmupGameplayAsync(scene, wu, realGame, gamePid, log, ct, sharedPad).ConfigureAwait(false);
                    ranGameplayWarmup = true;
                }
                double warmEl = (DateTime.UtcNow - warmStartUtc).TotalSeconds;
                log.Info("Warmup", $"WARM-UP phase COMPLETE after {warmEl:0.0}s — starting the MEASURED capture now (fresh frame window; warm-up frames never captured).");
            }

            if (scene.WarmupSeconds > 0 && !ranGameplayWarmup)
            {
                log.Trace("Capture", $"Warmup {scene.WarmupSeconds}s (frames discarded).");
                await Task.Delay(TimeSpan.FromSeconds(scene.WarmupSeconds), ct).ConfigureAwait(false);
            }
            // Warm-up engine (Milestone 4): static render + discard + stabilize. Skipped when the gameplay warm-up
            // already ran (that warmed the caches via real movement, which a static render cannot).
            if (repeat <= 1 && !reRun && !ranGameplayWarmup)
                await new WarmupEngine(log).WarmUpAsync(_cfg.Warmup, providers, target, ct).ConfigureAwait(false);

            log.Info("Capture", $"Starting frame capture ({providers.Frames.Name}) for {scene.CaptureSeconds}s.");
            await using var frameSession = await providers.Frames.StartAsync(target, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(frameSession.Diagnostics))
                log.Info("Capture", $"{providers.Frames.Name} startup: {frameSession.Diagnostics}");
            await using var osd = BenchmarkOsd.Start(_cfg, osdMeta, frameSession, pwrSession, telSession, log);
            osd.Phase = "CAPTURE";
            var health = RuntimeHealthMonitor.Start(_cfg.RuntimeHealth, _cfg, log, runDir,
                () => frameSession.SampleCount, () => telSession.Latest?.GpuLoadPct, () => pwrSession.Latest?.GpuTotalW,
                () => telSession.SampleCount, realGame ? gamePid : null, healthTimeout, ct, () => measuredGate.IsOpen,
                realGame ? MeasuredMotionFloorFor(scene) : 0, scene.MeasuredMotionDelaySeconds, providers.Frames.Name);
            var botResult = await RunSceneBodyAsync(scene, realGame, gamePid, log, ct, reRun, () => frameSession.SampleCount, measuredGate, sharedPad).ConfigureAwait(false);
            if (botResult is not null) botTraversals.AddRange(botResult.Traversals);
            if (health is not null) { healthIncident = await health.StopAsync().ConfigureAwait(false); measuredMotion = health.Motion; }
            if (botResult?.Aborted == true) { botAborted = true; botAbortReason = botResult.AbortReason; }
            // NEVER-FAKE GUARD (2026-06-30): a BotDriven scripted-scene bot whose script DEFINES a measured
            // window (has a MarkStart) but reported NONE means its cold-launch nav never reached gameplay —
            // it "finished" on the menu/loading screen and the duration fallback below would otherwise measure
            // the WHOLE nav/menu window and FALSE-PASS it (caught live on TLOU 2026-06-30: the vision nav
            // stalled, 0 actions ran, 122s of menu → "Valid"). Reject like a gate abort. Route-only --attach
            // bots (no MarkStart in their script) are EXEMPT — for them, an unmarked whole window is correct.
            if (!botAborted && scene.Kind == SceneKind.BotDriven && botResult is not null && botResult.StartOffsetSec is null)
            {
                var markBotId = reRun && !string.IsNullOrWhiteSpace(scene.ReRunBotScript) ? scene.ReRunBotScript : scene.BotScript;
                var markScript = BotScriptLibrary.Resolve(markBotId);
                if (markScript is not null && markScript.Actions.Any(a => a.Type == BotActionType.MarkStart))
                {
                    botAborted = true;
                    botAbortReason = $"bot '{markBotId}' reported no measured window — MarkStart never fired, so the cold-launch nav did not reach gameplay (the window would be a menu/loading screen)";
                    log.Warn("Capture", $"NEVER-FAKE: {botAbortReason} → run REJECTED (Invalid), not measured as a duration false-pass.");
                }
            }

            osd.Phase = "DONE";
            frames = await frameSession.StopAsync().ConfigureAwait(false);
            if (frames.Count == 0 && frameSession.Diagnostics is { Length: > 0 } diag)
                log.Warn("Capture", $"{providers.Frames.Name} captured 0 frames — provider diagnostics: {diag}");
            power = await pwrSession.StopAsync().ConfigureAwait(false);
            telemetry = await telSession.StopAsync().ConfigureAwait(false);
            // If the bot reported MarkStart/MarkEnd (a scripted-scene bot that navigated to in-world BEFORE the
            // measured route), bound the window to [MarkStart, MarkEnd] — trims the menu/loading frames. Offsets
            // are relative to the bot-run start, which is ~capture-start (capture begins just before the bot runs),
            // so they align with frame TimeSec. Otherwise measure the whole fixed window (legacy route-only scenes
            // preconditioned in-world via --attach).
            bool botMarked = botResult?.StartOffsetSec is not null;
            completion = new CompletionResult
            {
                Mode = botMarked ? "bot-marked" : "duration", StartDetected = true,
                StartOffsetSec = botMarked ? botResult!.StartOffsetSec!.Value : 0,
                FinishDetected = true,
                FinishOffsetSec = botResult?.EndOffsetSec ?? (frames.Count > 0 ? frames[^1].TimeSec : 0)
            };
            if (botMarked) log.Info("Capture", $"Bot-marked window: +{completion.StartOffsetSec:0.0}s..+{completion.FinishOffsetSec:0.0}s (nav/loading frames before MarkStart trimmed).");
        }

        // Authoritative frametimes from the game's OWN result file (e.g. DOOM windowed: the RTSS present-rate
        // is DWM-capped to ~60 fps and useless — DOOM's benchmark-*.json carries the real per-frame render
        // times; F1 25 / Codemasters EGO writes a "Benchmark Mode Frame Times" CSV). Replace the captured
        // frames; the game file IS the clean benchmark window so trimming is skipped. JSON is tried first; the
        // CSV (FrametimesCsvDir) is the fallback/alternative for games that emit per-frame CSV instead.
        bool jsonSourced = false;
        Dictionary<string, string>? resultFileSettings = null;   // render settings the game's own result file recorded
        bool wantJson = scene.Kind == SceneKind.BuiltInBenchmark && !string.IsNullOrWhiteSpace(scene.Completion.FrametimesJsonDir);
        bool wantCsv  = scene.Kind == SceneKind.BuiltInBenchmark && !string.IsNullOrWhiteSpace(scene.Completion.FrametimesCsvDir);
        if (wantJson || wantCsv)
        {
            int? jw = null, jh = null;
            string src = "JSON";
            List<FrameSample>? jf = wantJson ? TryLoadResultFrametimes(scene.Completion, run.StartedUtc, log, out jw, out jh, out resultFileSettings) : null;
            if ((jf is null || jf.Count == 0) && wantCsv) { jf = TryLoadResultFrametimesCsv(scene.Completion, run.StartedUtc, log); src = "CSV"; }
            if (jf is { Count: > 0 })
            {
                // Codemasters EGO writes one CSV row per RENDERED frame even when frame generation is on,
                // while its XML headline avg_fps counts GENERATED output presents (F1 25 live proof:
                // 18,704 CSV rows / 118s = 158 fps, XML avg_fps=315 with frame_gen=1). Reconstruct the
                // output cadence only when the selected model explicitly requests FG AND the independent
                // XML/CSV ratio is close to an integer 2x..4x. Splitting each render interval preserves the
                // real benchmark duration and yields honest output-FPS/lows; a failed config edit (ratio ~1)
                // is left untouched and the later fingerprint check rejects the mislabeled run.
                if (src == "CSV" && VariantRequestsFrameGeneration(variant) && completion.CrossCheckFps is double gameFps)
                {
                    var normalized = ExpandFrameGenerationCadence(jf, gameFps, out int multiplier);
                    if (!ReferenceEquals(normalized, jf))
                    {
                        log.Info("Capture", $"Frame-generation result normalization: game XML {gameFps:0.0} fps is {multiplier}x the " +
                            $"CSV rendered-frame cadence — expanded {jf.Count} render samples to {normalized.Count} generated-output presents " +
                            $"while preserving the {jf[^1].TimeSec:0.0}s benchmark duration.");
                        jf = normalized;
                    }
                }
                log.Info("Capture", $"Authoritative frametimes from game result {src}: {jf.Count} frames " +
                    $"(replacing {frames.Count} captured present samples).");
                frames = jf;
                jsonSourced = true;
                completion.StartOffsetSec = 0;
                completion.FinishOffsetSec = jf[^1].TimeSec;
                if (jw is int && jh is int) { completion.RenderWidth = jw; completion.RenderHeight = jh; }
            }
            else log.Warn("Capture", "No fresh game result frametimes parsed since capture start — keeping captured frames (number may be DWM-capped in windowed mode).");
        }

        run.EndedUtc = DateTime.UtcNow;
        run.CompletionMode = completion.Mode;
        run.CapturedFrameCount = frames.Count;
        run.GameReportedFps = completion.CrossCheckFps;

        // Fix the measured window for benchmark scenes: detection window -> gameplay-band isolation ->
        // warm-up settle, computed as one [finalStart, finalEnd] (original capture time) then trimmed ONCE.
        bool trim = !jsonSourced && (scene.Kind == SceneKind.BuiltInBenchmark || completion.Mode == "bot-marked") && completion.FinishOffsetSec > completion.StartOffsetSec;
        double finalStart = completion.StartOffsetSec, finalEnd = completion.FinishOffsetSec;

        // (a) Gameplay-band isolation — marker-less, menu-driven benchmarks (DOOM) still carry the uncapped
        // menu/load screen + the load hitch inside the activity window; Cyberpunk's -benchmark window opens on
        // its uncapped loading screen. Narrow to the longest contiguous real-gameplay band (drops the leading
        // menu/load screen, the trailing results screen, and the boundary hitch), leaving only the flythrough.
        if (trim && scene.Completion.GameplayBandCeilingFps > 0)
        {
            var windowed = TrimFrames(frames, finalStart, finalEnd); // window-relative (TimeSec from 0)
            var band = windowed.Count > 0 ? IsolateGameplayBand(windowed, scene.Completion.GameplayBandCeilingFps) : null;
            if (band is null || band.Count == 0)
                log.Warn("Capture", $"Gameplay-band isolation found no sub-{scene.Completion.GameplayBandCeilingFps:0}fps band " +
                    "(benchmark flythrough may not have started — keeping the full window; run will fail-safe on the hitch/avg checks).");
            else if (band.Count < windowed.Count)
            {
                finalStart = completion.StartOffsetSec + band[0].TimeSec;
                finalEnd = completion.StartOffsetSec + band[^1].TimeSec;
                log.Info("Capture", $"Gameplay-band isolation: kept {band.Count}/{windowed.Count} frames " +
                    $"(+{finalStart:0.0}s..+{finalEnd:0.0}s, {finalEnd - finalStart:0.0}s real render) — dropped " +
                    $"menu/load/results frames above {scene.Completion.GameplayBandCeilingFps:0} fps + load hitches.");
                completion.Notes.Add($"gameplay-band isolated ({band.Count} frames, {finalEnd - finalStart:0.0}s) " +
                    $"from a {completion.WindowSeconds:0.0}s window");
            }
            else
                log.Trace("Capture", $"Gameplay-band isolation: window already clean (whole {windowed.Count}-frame window is below " +
                    $"{scene.Completion.GameplayBandCeilingFps:0} fps — no menu/load pollution to trim).");
        }

        // (b) Warm-up settle — exclude the first MeasuredWarmupSeconds of the (post-band) measured window: the
        // flythrough streams its scene in at the start, so the first seconds carry streaming/shader-compile
        // stutter spikes (real frames, but they poison the 1%/0.1% lows without moving the avg). Only when
        // enough window remains so a short bench is never starved below MinValidSeconds.
        double warm = scene.Completion.MeasuredWarmupSeconds;
        if (trim && warm > 0 && (finalEnd - finalStart) > warm + Math.Max(1.0, scene.Completion.MinValidSeconds))
        {
            finalStart += warm;
            log.Info("Capture", $"Measured warm-up: excluded the first {warm:0.#}s of the measured window (scene streaming-in / settle).");
            completion.Notes.Add($"warm-up: first {warm:0.#}s excluded");
        }

        var mFrames = trim ? TrimFrames(frames, finalStart, finalEnd) : frames.ToList();
        var mPower = trim ? TrimByTime(power, finalStart, finalEnd, p => p.TimeSec) : power.ToList();
        var powerMeasurement = CloneMeasurement(providers.Power.Measurement);
        var mTel = trim ? TrimByTime(telemetry, finalStart, finalEnd, t => t.TimeSec) : telemetry.ToList();

        log.Info("Capture", $"Captured {frames.Count} frames; measured {mFrames.Count} ({completion.Mode}, window {finalEnd - finalStart:0.0}s), " +
                            $"{mPower.Count} power, {mTel.Count} telemetry.");

        // Power-source fallback (powerSource=auto/lhm): if the Powenetics PMD under-delivered for this run —
        // e.g. its COM firmware stalled mid-campaign (a known PMD failure mode) so mPower is near-empty while
        // LHM telemetry kept streaming — synthesize the power series from LibreHardwareMonitor's own GPU
        // board-power reading (GpuBoardPowerW), which tracks the PMD within a few watts. This keeps the run
        // VALID on a PMD dropout instead of rejecting it, honoring powerSource=auto's documented "fall back to
        // LHM and keep running" intent (re-plug the PMD to restore rail-level board power). Skipped when the
        // user pinned powerSource=powenetics (PMD-or-nothing).
        if (mPower.Count < _cfg.Validation.MinPowerSamples &&
            !string.Equals(_cfg.PowerSource, "powenetics", StringComparison.OrdinalIgnoreCase))
        {
            var lhmPower = mTel.Where(t => t.GpuBoardPowerW is > 0)
                               .Select(t => new PowerSample { TimeSec = t.TimeSec, GpuTotalW = t.GpuBoardPowerW, CpuTotalW = t.CpuPowerW })
                               .ToList();
            if (lhmPower.Count >= _cfg.Validation.MinPowerSamples)
            {
                log.Warn("Power", $"Powenetics under-delivered this run ({mPower.Count} < {_cfg.Validation.MinPowerSamples} samples — the PMD likely stalled mid-campaign). " +
                    $"Falling back to LibreHardwareMonitor GPU board power ({lhmPower.Count} samples; LHM tracks the PMD within a few W) so the run stays valid (powerSource={_cfg.PowerSource}). Re-plug the PMD to restore rail-level power.");
                mPower = lhmPower;
                run.PowerProviderName = "LHM GPU board power (runtime fallback)";
                run.PowerSource = DataSourceMode.Live;
                powerMeasurement = new PowerMeasurementMetadata
                {
                    Kind = PowerMeasurementKind.GpuReportedTelemetry,
                    Scope = "GPU-reported board-power telemetry",
                    HasPerRailData = false,
                    HardwareBustersVerifiedPowerEligible = false,
                    QualificationNote = "APPROXIMATE · GPU TELEMETRY — runtime fallback after Powenetics PMD under-delivered."
                };
            }
        }

        run.Frames = Statistics.ComputeFrameStats(mFrames);
        run.Power = BuildPowerStats(mPower, run.Frames.FrameCount, run.PowerSource, powerMeasurement);
        run.Telemetry = BuildTelemetryStats(mTel);

        if (completion.CrossCheckFps is double gf && run.Frames.AvgFps > 0)
        {
            double diffPct = Math.Abs(gf - run.Frames.AvgFps) / run.Frames.AvgFps * 100;
            log.Info("Completion", $"Cross-check: game {gf:0.0} fps vs measured {run.Frames.AvgFps:0.0} fps ({diffPct:0.0}% diff).");
        }

        // (10) Save raw measured-window data.
        RawCsv.WriteFrames(Path.Combine(runDir, run.RawFramesFile), mFrames);
        RawCsv.WritePower(Path.Combine(runDir, run.RawPowerFile), mPower);
        Json.Save(Path.Combine(runDir, run.PowerMetadataFile), run.Power.Measurement);
        RawCsv.WriteTelemetry(Path.Combine(runDir, run.RawTelemetryFile), mTel);

        // (11) Validate — completion issues first so they fold into the verdict.
        if (scene.Kind == SceneKind.BuiltInBenchmark)
        {
            if (!completion.StartDetected) run.ValidationIssues.Add("Benchmark start was not detected.");
            if (!completion.FinishDetected) run.ValidationIssues.Add("Benchmark finish was not detected (timeout).");
            if (run.Frames.DurationSec < scene.Completion.MinValidSeconds)
                run.ValidationIssues.Add($"Measured window {run.Frames.DurationSec:0.0}s < min {scene.Completion.MinValidSeconds}s.");
            // Authoritative resolution check: the game's own result file says what it actually rendered.
            // If it doesn't match the requested resolution, the data point is mislabeled — reject it.
            if (completion.RenderWidth is int rw && completion.RenderHeight is int rh && (rw != res.Width || rh != res.Height))
                run.ValidationIssues.Add($"Rendered at {rw}x{rh} but {res.Name} ({res.Width}x{res.Height}) was requested — resolution not applied " +
                    "(mode unavailable on this display? enable NVIDIA DSR/DLDSR for above-native resolutions, or ensure WindowMode=Fullscreen).");
        }
        // A required bot gate timed out → the nav never reached the benchmark/in-world scene, so whatever was
        // captured is a menu/loading screen, not the intended content. Reject the run outright (a false pass
        // here is worse than no data — see the Forza/Ratchet menu false-passes the recorded videos caught).
        if (botAborted)
            run.ValidationIssues.Add($"Bot navigation aborted ({botAbortReason}) — a required on-screen gate was never reached, so the measured window is a menu/loading screen, not the benchmark/gameplay. Run rejected to avoid a false pass.");
        run.MeasuredMotion = measuredMotion;   // scene-static sensor stats → persisted + reported (trust signal)
        run.BotTraversals = botTraversals;     // adaptive route stats persist even when that bot owns the motion sensor
        // Render-settings fingerprint: what the game was ACTUALLY set to render for this number (upscaler /
        // render res / frame gen), read off its own config. The game is still up here, so the file reflects
        // the as-launched state. FG-on flags the run: AvgFps then counts generated presents, not renders.
        (run.SettingsFingerprint, run.FrameGenActive) = Launch.SettingsFingerprinter.Extract(game, log);
        if (resultFileSettings is { Count: > 0 })
        {
            // The game's OWN benchmark result file recorded its render settings (DOOM's JSON has a settings[]
            // block) — the most authoritative fingerprint there is, and the only one for games whose settings
            // store is unreadable externally (idTech8 keeps upscaler/FG in the menu). Merged over any
            // config-file fingerprint; result-file values win.
            run.SettingsFingerprint ??= new();
            foreach (var kv in resultFileSettings) run.SettingsFingerprint[kv.Key] = kv.Value;
            bool dlssFgOn = resultFileSettings.TryGetValue("DLSS Frame Generation", out var dfg) && dfg is not ("0" or "-1" or "OFF");
            bool fsrFgOn = resultFileSettings.TryGetValue("FSR Frame Generation", out var ffg) && ffg is not ("0" or "-1" or "OFF");
            if (resultFileSettings.ContainsKey("DLSS Frame Generation") || resultFileSettings.ContainsKey("FSR Frame Generation"))
                run.FrameGenActive = dlssFgOn || fsrFgOn;
            log.Info("Fingerprint", $"Game result-file render settings: {string.Join(", ", resultFileSettings.Select(p => $"{p.Key}={p.Value}"))}" +
                (run.FrameGenActive == true ? "  ⚠ FRAME GEN ON — fps counts generated presents." : ""));
        }
        RunValidator.ValidateResolutionFingerprint(run, game, res);
        RunValidator.ValidateFrameGenerationFingerprint(run, variant);
        // Synthetic frames are legitimate only when explicitly forced, or when THIS launch is actually
        // simulated. Merely enabling "simulate on launch failure" must not excuse a synthetic provider
        // during a successfully launched real game.
        bool allowSyntheticFrames = _cfg.ForceSyntheticFrames || !realGame;
        _validator.ValidateSingle(run, mFrames, mPower, mTel, gameLaunchedOrSimulated, allowSyntheticFrames);
        if (botAborted) run.Verdict = RunVerdict.Invalid;
        // A runtime-health incident during the measured window invalidates the run (with the recorded reason +
        // screenshot/log) so the deterministic auto-repeat retries; a persistent one classifies RuntimeHealth.
        if (healthIncident is not null)
        {
            run.ValidationIssues.Add($"runtime health: {healthIncident.Kind} — {healthIncident.Detail}" +
                (healthIncident.ScreenshotFile is null ? "" : $" (screenshot: {healthIncident.ScreenshotFile})"));
            run.Verdict = RunVerdict.Invalid;
        }

        // Surface capture seams (excluded from the distribution) and flag a capture discontinuity so the
        // orchestrator can switch the next auto-repeat to RTSS — reliable over short/awkward windows
        // where PresentMon's ETW session drops frames.
        if (run.Frames.CaptureArtifactCount > 0)
            log.Info("Capture", $"Excluded {run.Frames.CaptureArtifactCount} capture-seam frame(s) " +
                $"({run.Frames.CaptureArtifactSeconds:0.00}s, PresentMon trace gap) from frame-time stats; lows computed on rendered frames.");
        run.CaptureDiscontinuity = run.Frames.CaptureArtifactCount > 0
            || LargestTimeGap(mFrames) > _cfg.Validation.MaxCaptureGapSeconds
            || MaxFrameTimeMs(mFrames) > _cfg.Validation.MaxSingleFrameTimeMs;

        Persist(runDir, run);

        var lvl = run.Verdict == RunVerdict.Valid ? LogLevel.Info : LogLevel.Warn;
        log.Log(lvl, "Validate", $"Run {repeat} verdict: {run.Verdict}" +
            (run.ValidationIssues.Count > 0 ? " — " + string.Join("; ", run.ValidationIssues) : ""));
        return run;
    }

    private static void Persist(string runDir, RunResult run)
    {
        Json.Save(Path.Combine(runDir, "run_summary.json"), run);
        Json.Save(Path.Combine(runDir, "validation.json"), new { run.Verdict, run.ValidationIssues, run.CompletionMode, run.CapturedFrameCount });
    }

    /// <summary>
    /// Load per-frame frametimes from the game's OWN benchmark result JSON (DOOM: The Dark Ages writes one per
    /// run with a frames[] array of {frame, gpu} ms + a resolution field). Returns the NEWEST file written
    /// at/after <paramref name="sinceUtc"/> (so a stale prior-run file is ignored), parsed to FrameSamples
    /// (TimeSec = cumulative sum of frametimes). Null when no fresh file is found or it can't be parsed.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex RenderSettingKey = new(
        @"upscal|dlss|fsr|xess|path\s*tracing|pathtracing|overall|resolution scal|frame gen",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static List<FrameSample>? TryLoadResultFrametimes(CompletionDetection c, DateTime sinceUtc, RunLogger log, out int? width, out int? height,
        out Dictionary<string, string>? renderSettings)
    {
        width = null; height = null; renderSettings = null;
        try
        {
            var dir = Environment.ExpandEnvironmentVariables(c.FrametimesJsonDir!);
            if (!Directory.Exists(dir)) { log.Trace("Capture", $"Frametimes JSON dir not found: {dir}"); return null; }
            var glob = string.IsNullOrWhiteSpace(c.FrametimesJsonGlob) ? "*.json" : c.FrametimesJsonGlob!;
            var file = new DirectoryInfo(dir).GetFiles(glob)
                .Where(f => f.LastWriteTimeUtc >= sinceUtc.AddSeconds(-3))
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (file is null) return null;

            string framesField = string.IsNullOrWhiteSpace(c.FrametimesJsonFramesField) ? "frames" : c.FrametimesJsonFramesField!;
            string msField     = string.IsNullOrWhiteSpace(c.FrametimesJsonFrameMsField) ? "frame" : c.FrametimesJsonFrameMsField!;
            string gpuField    = string.IsNullOrWhiteSpace(c.FrametimesJsonGpuMsField) ? "gpu" : c.FrametimesJsonGpuMsField!;
            string resField    = string.IsNullOrWhiteSpace(c.FrametimesJsonResolutionField) ? "resolution" : c.FrametimesJsonResolutionField!;

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file.FullName));
            var root = doc.RootElement;
            if (!root.TryGetProperty(framesField, out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

            var list = new List<FrameSample>(arr.GetArrayLength());
            double t = 0;
            foreach (var fe in arr.EnumerateArray())
            {
                if (fe.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (!fe.TryGetProperty(msField, out var mEl) || mEl.ValueKind != System.Text.Json.JsonValueKind.Number) continue;
                double ms = mEl.GetDouble();
                if (ms <= 0 || ms > 10000) continue;   // guard against garbage
                t += ms / 1000.0;
                var fs = new FrameSample(t, ms);
                if (fe.TryGetProperty(gpuField, out var gEl) && gEl.ValueKind == System.Text.Json.JsonValueKind.Number) fs.GpuBusyMs = gEl.GetDouble();
                list.Add(fs);
            }
            if (root.TryGetProperty(resField, out var rEl) && rEl.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var m = System.Text.RegularExpressions.Regex.Match(rEl.GetString() ?? "", @"(\d+)\s*[x×]\s*(\d+)");
                if (m.Success) { width = int.Parse(m.Groups[1].Value); height = int.Parse(m.Groups[2].Value); }
            }
            // Render settings the game itself recorded (DOOM's JSON has a settings[] of {name,value}) — kept
            // for the run's settings fingerprint. Only render-relevant keys (upscaler/FG/PT/preset/scale);
            // the rest (FOV, film grain, audio, …) is noise for a fingerprint.
            if (root.TryGetProperty("settings", out var sEl) && sEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var se in sEl.EnumerateArray())
                {
                    if (se.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    if (!se.TryGetProperty("name", out var nEl) || nEl.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                    if (!se.TryGetProperty("value", out var vEl)) continue;
                    var name = nEl.GetString() ?? "";
                    if (!RenderSettingKey.IsMatch(name)) continue;
                    (renderSettings ??= new())[name] = vEl.ValueKind == System.Text.Json.JsonValueKind.String ? vEl.GetString() ?? "" : vEl.ToString();
                }
            }
            log.Info("Capture", $"Parsed game result JSON '{file.Name}': {list.Count} frames{(width is int ? $", reported {width}x{height}" : "")}{(renderSettings is { Count: > 0 } ? $", {renderSettings.Count} render setting(s)" : "")}.");
            return list.Count > 0 ? list : null;
        }
        catch (Exception ex) { log.Warn("Capture", $"Result-frametimes JSON parse failed: {ex.Message}"); return null; }
    }

    /// <summary>Load a game-written per-frame CSV (Codemasters EGO "Benchmark Mode Frame Times": a short banner,
    /// a "Num frames,N" line, then a "Frame,Time (ms),Running time (s)" header and one row per frame) as the
    /// authoritative measured window — the CSV counterpart of <see cref="TryLoadResultFrametimes"/>. The
    /// frame-time column is located by header text (the cell containing "(ms)" but not "running"), so the exact
    /// column order isn't hard-coded and the banner/preamble rows are skipped. Only files written since capture
    /// start are considered (so a stale prior result is never picked up).</summary>
    private static List<FrameSample>? TryLoadResultFrametimesCsv(CompletionDetection c, DateTime sinceUtc, RunLogger log)
    {
        try
        {
            var dir = Environment.ExpandEnvironmentVariables(c.FrametimesCsvDir!);
            if (!Directory.Exists(dir)) { log.Trace("Capture", $"Frametimes CSV dir not found: {dir}"); return null; }
            var glob = string.IsNullOrWhiteSpace(c.FrametimesCsvGlob) ? "*.csv" : c.FrametimesCsvGlob!;
            var file = new DirectoryInfo(dir).GetFiles(glob)
                .Where(f => f.LastWriteTimeUtc >= sinceUtc.AddSeconds(-3))
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (file is null) return null;

            var lines = File.ReadAllLines(file.FullName);
            // Locate the header row + the frame-time column by header text (robust to banner/preamble lines and
            // column reordering): the cell that mentions "(ms)" but isn't the "Running time (s)" column.
            int headerIdx = -1, msCol = -1;
            for (int i = 0; i < lines.Length && headerIdx < 0; i++)
            {
                var cells = lines[i].Split(',');
                for (int j = 0; j < cells.Length; j++)
                {
                    var h = cells[j].Trim();
                    if (h.Contains("(ms)", StringComparison.OrdinalIgnoreCase) && !h.Contains("running", StringComparison.OrdinalIgnoreCase))
                    { headerIdx = i; msCol = j; break; }
                }
            }
            if (headerIdx < 0 || msCol < 0) { log.Warn("Capture", $"Frametimes CSV '{file.Name}': no '...(ms)' frame-time column found."); return null; }

            var list = new List<FrameSample>(Math.Max(16, lines.Length - headerIdx));
            double t = 0;
            for (int i = headerIdx + 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var cells = lines[i].Split(',');
                if (cells.Length <= msCol) continue;
                if (!double.TryParse(cells[msCol].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ms)) continue;
                if (ms <= 0 || ms > 10000) continue;   // guard against garbage / stray banner rows
                t += ms / 1000.0;
                list.Add(new FrameSample(t, ms));
            }
            if (list.Count == 0) return null;
            log.Info("Capture", $"Parsed game result CSV '{file.Name}': {list.Count} frames.");
            return list;
        }
        catch (Exception ex) { log.Warn("Capture", $"Result-frametimes CSV parse failed: {ex.Message}"); return null; }
    }

    internal static bool VariantRequestsFrameGeneration(GameVariant? variant)
    {
        if (variant is null || !variant.Settings.TryGetValue("frameGen", out var value)) return false;
        return value.Trim() is not ("0" or "Off" or "OFF" or "False" or "false" or "Disabled" or "disabled" or "None" or "none");
    }

    /// <summary>
    /// Expand a rendered-frame result series into generated output presents when the independent game headline
    /// proves a clean integer FG multiplier. Returns the original list when the evidence is absent/ambiguous.
    /// </summary>
    internal static List<FrameSample> ExpandFrameGenerationCadence(List<FrameSample> rendered,
        double gameReportedFps, out int multiplier)
    {
        multiplier = 1;
        if (rendered.Count < 30 || gameReportedFps <= 0) return rendered;

        double renderedFps = Statistics.ComputeFrameStats(rendered).AvgFps;
        if (renderedFps <= 0) return rendered;
        double ratio = gameReportedFps / renderedFps;
        int candidate = (int)Math.Round(ratio);
        if (candidate is < 2 or > 4 || Math.Abs(ratio - candidate) / candidate > 0.08)
            return rendered;

        multiplier = candidate;
        var output = new List<FrameSample>(rendered.Count * candidate);
        double t = 0;
        foreach (var frame in rendered)
        {
            double sliceMs = frame.FrameTimeMs / candidate;
            for (int i = 0; i < candidate; i++)
            {
                t += sliceMs / 1000.0;
                output.Add(new FrameSample(t, sliceMs)
                {
                    DisplayedTimeMs = frame.DisplayedTimeMs is double displayed ? displayed / candidate : null,
                    GpuBusyMs = frame.GpuBusyMs
                });
            }
        }
        return output;
    }

    private static async Task WaitReadinessAsync(SceneProfile scene, bool launched, RunLogger log, CancellationToken ct)
    {
        int settle = launched ? Math.Min(scene.ReadinessTimeoutSeconds, 8) : 1;
        await Task.Delay(TimeSpan.FromSeconds(settle), ct).ConfigureAwait(false);
        log.Trace("Scene", "Scene ready.");
    }

    /// <summary>Build the capture-card OCR reader that backs vision-gated bot waits (WaitForText). Null when
    /// not a real game, or when no capture device / ffmpeg / ocr.ps1 is available — the input engine then
    /// degrades WaitForText to a bounded blind wait. Cheap (config-holding objects), built per bot run.</summary>
    private ScreenReader? BuildVision(bool realGame, RunLogger log)
    {
        if (!realGame || string.IsNullOrWhiteSpace(_cfg.CaptureCardDevice)) return null;
        var grabber = new CaptureCardGrabber(_cfg.FfmpegPath, _cfg.CaptureCardDevice, log);
        if (!grabber.FfmpegResolved) return null;
        var reader = new ScreenReader(grabber, log);
        return reader.OcrScriptResolved ? reader : null;
    }

    /// <summary>Build the LLM nav driver for the smart-bot MENU/DESKTOP nav (settings.navSupervisor):
    /// "vision" = a multimodal model that SEES the screen and goal-seeks (GPU; the primary navigator for a bot with
    /// a visionGoal, unloaded before the measured window); "ollama" = a text model that suggests a button from OCR
    /// only when the graph is LOST (CPU). Null = deterministic-only.</summary>
    private INavSupervisor? BuildSupervisor(RunLogger log)
    {
        var mode = _cfg.NavSupervisor?.Trim() ?? "none";
        if (string.Equals(mode, "vision", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(_cfg.CaptureCardDevice)) { log.Warn("Bot", "navSupervisor=vision but no captureCardDevice set — vision nav disabled."); return null; }
            var grabber = new CaptureCardGrabber(_cfg.FfmpegPath, _cfg.CaptureCardDevice, log);
            if (!grabber.FfmpegResolved) { log.Warn("Bot", "navSupervisor=vision but ffmpeg not resolved — vision nav disabled."); return null; }
            var backend = VisionNavSupervisor.ResolveBackend(_cfg, log);
            log.Info("Bot", $"Nav supervisor ENABLED — VISION model '{backend.Model}' on {backend.Source} via {backend.Endpoint} (sees the screen + goal-seeks menus/desktop; unloaded before the benchmark window).");
            return new VisionNavSupervisor(backend.Endpoint, backend.Model, grabber, _cfg.NavSupervisorVisionWidth, backend.Gpu, log, backend.Provider, backend.ApiKey, backend.Effort);
        }
        if (string.Equals(mode, "ollama", StringComparison.OrdinalIgnoreCase))
        {
            log.Info("Bot", $"Nav supervisor ENABLED — local LLM '{_cfg.NavSupervisorModel}' via {_cfg.NavSupervisorEndpoint} (CPU; engages only when the graph is lost).");
            return new OllamaNavSupervisor(_cfg.NavSupervisorEndpoint, _cfg.NavSupervisorModel, log);
        }
        return null;
    }

    /// <summary>True if a script uses the Tier-1 in-world navigator (a Gx10Traverse action) — so we only pay the GX10
    /// probe + grabber setup for bots that actually drive in-world movement.</summary>
    private static bool ScriptUsesInWorldNav(BotScript? s) =>
        s?.Actions.Any(a => a.Type == BotActionType.Gx10Traverse) == true;

    /// <summary>Effective measured-window MOTION floor for the runtime-health scene-static check. Per-scene
    /// override (<see cref="SceneProfile.MeasuredMotionFloor"/>, 0 = off) beats the suite default; a scene whose
    /// bot already senses motion in-world (SmartTraverse / Gx10Traverse — those navigators own the exclusive
    /// capture device during the window and self-correct) skips the redundant monitor probe automatically.</summary>
    private double MeasuredMotionFloorFor(SceneProfile scene)
    {
        if (!_cfg.RuntimeHealth.MotionEnabled) return 0;
        if (scene.MeasuredMotionFloor is double f) return Math.Max(0, f);
        foreach (var id in new[] { scene.BotScript, scene.StartBotScript, scene.ReRunBotScript })
            if (BotScriptLibrary.Resolve(id) is { } s &&
                s.Actions.Any(a => a.Type is BotActionType.SmartTraverse or BotActionType.Gx10Traverse))
                return 0;
        return _cfg.RuntimeHealth.MotionFloorScore;
    }

    /// <summary>Build the TIER-1 in-world navigator (GX10 vision → movement) for a live run: the capture-card grabber +
    /// the GX10 endpoint chosen by the same compute resolver the menu nav uses. Null on a dry-run or when no capture
    /// device / ffmpeg is available (Gx10Traverse then degrades to blind-forward, never wedging).</summary>
    private Gx10InWorldNavigator? BuildInWorldNavigator(bool realGame, RunLogger log)
    {
        if (!realGame) return null;
        if (string.IsNullOrWhiteSpace(_cfg.CaptureCardDevice)) { log.Warn("Bot", "Gx10Traverse: no captureCardDevice set — in-world navigator disabled (blind-forward)."); return null; }
        var grabber = new CaptureCardGrabber(_cfg.FfmpegPath, _cfg.CaptureCardDevice, log);
        if (!grabber.FfmpegResolved) { log.Warn("Bot", "Gx10Traverse: ffmpeg not resolved — in-world navigator disabled (blind-forward)."); return null; }
        var backend = VisionNavSupervisor.ResolveBackend(_cfg, log);
        if (backend.Provider != VisionNavSupervisor.VisionApiProvider.Ollama)
        {
            log.Warn("Bot", $"Gx10Traverse is not enabled for {backend.Source}; using its bounded SmartTraverse fallback instead. Cloud vision is available for menu navigation.");
            return null;
        }
        if (Uri.TryCreate(backend.Endpoint, UriKind.Absolute, out var endpointUri) && endpointUri.IsLoopback)
        {
            log.Warn("Bot", $"Gx10Traverse rejected loopback inference at {backend.Endpoint}: in-world AI must run on another machine so it cannot contaminate benchmark GPU/CPU results. Using bounded SmartTraverse fallback.");
            return null;
        }
        log.Info("Bot", $"In-world navigator ENABLED — '{backend.Model}' on {backend.Source} via {backend.Endpoint} (drives in-world movement; perception OFF-BENCH so the bench GPU stays on the game).");
        return new Gx10InWorldNavigator(backend.Endpoint, backend.Model, grabber, _cfg.NavInWorldVisionWidth, log) { StuckThreshold = _cfg.NavStuckThreshold };
    }

    /// <summary>Trigger the built-in benchmark. Returns the bot's marker result and, when the start-bot drives
    /// a virtual gamepad on a real game, the engine holding that pad CONNECTED (KeepPadAlive) — the caller must
    /// dispose it only AFTER capture stops, so a ForzaTech "controller disconnected" modal can't pop over the
    /// measured flythrough. PadHold is null for keyboard bots / non-bot starts (the engine is disposed here).</summary>
    private async Task<(BotRunResult? Bot, IDisposable? PadHold)> TriggerBenchmarkAsync(SceneProfile scene, bool realGame, int? gamePid, RunLogger log, CancellationToken ct, bool reRun = false, IGamepad? sharedPad = null)
    {
        switch ((scene.BenchmarkStart ?? "auto").ToLowerInvariant())
        {
            case "bot":
                // On a no-relaunch re-measure (SingleLaunchRepeats), use the re-entrant ReRunBotScript that
                // re-triggers the benchmark from the post-run state; fall back to the cold-launch StartBotScript.
                var botId = reRun && !string.IsNullOrWhiteSpace(scene.ReRunBotScript) ? scene.ReRunBotScript : scene.StartBotScript;
                var s = BotScriptLibrary.Resolve(botId);
                if (s is not null)
                {
                    log.Info("Scene", $"Triggering benchmark via '{botId}' (inject={realGame}{(reRun ? ", re-run" : "")}).");
                    bool keepPad = realGame && s.InputDevice == BotInputDevice.Gamepad;
                    InputAutomationEngine? engine = new InputAutomationEngine(log, inject: realGame)
                        { TargetPid = gamePid, Vision = BuildVision(realGame, log), KeepPadAlive = keepPad, SharedPad = sharedPad, Supervisor = BuildSupervisor(log),
                          Gx10Navigator = ScriptUsesInWorldNav(s) ? BuildInWorldNavigator(realGame, log) : null,
                          DesktopGuardAnchors = ResolveDesktopAnchors(), DesktopGuardMinHits = _cfg.DesktopGuardMinHits, StripOsdFromOcr = _cfg.StripOsdFromOcr };
                    try
                    {
                        var res = await engine.RunAsync(s, TimeSpan.FromSeconds(scene.ReadinessTimeoutSeconds), ct).ConfigureAwait(false);
                        if (keepPad) { var held = engine; engine = null; return (res, held); }  // caller disposes after capture stops
                        return (res, null);
                    }
                    finally { engine?.Dispose(); }   // dispose on the non-keepPad path AND on any throw — never leak a connected pad
                }
                log.Warn("Scene", "BenchmarkStart=bot but no startBotScript resolved; assuming auto-start.");
                break;
            case "arg":
                log.Trace("Scene", "Benchmark started via launch argument.");
                break;
            default:
                log.Trace("Scene", "Benchmark auto-starts.");
                break;
        }
        return (null, null);
    }

    private async Task<BotRunResult?> RunSceneBodyAsync(SceneProfile scene, bool realGame, int? gamePid, RunLogger log, CancellationToken ct, bool reRun = false, Func<int>? liveSampleCount = null, MeasuredWindowGate? measuredGate = null, IGamepad? sharedPad = null)
    {
        var window = TimeSpan.FromSeconds(scene.CaptureSeconds);
        if (scene.Kind == SceneKind.BotDriven)
        {
            // On a no-relaunch re-measure, use ReRunBotScript (e.g. a re-mark of a still-loaded static scene)
            // instead of the cold-launch nav bot; fall back to BotScript.
            var botId = reRun && !string.IsNullOrWhiteSpace(scene.ReRunBotScript) ? scene.ReRunBotScript : scene.BotScript;
            var script = BotScriptLibrary.Resolve(botId);
            if (script is null) { log.Warn("Scene", $"No bot script '{botId}'; fixed window."); await Task.Delay(window, ct).ConfigureAwait(false); return null; }
            // A scripted-scene bot may begin with a vision-gated NAV-TO-IN-WORLD prologue (title -> Continue/Load
            // -> WaitForText an in-world objective), then MarkStart, the measured route, and MarkEnd. The returned
            // offsets let the caller TRIM the menu/loading frames out of the measured window — one pad connection
            // for the whole run, so a controller-required RE Engine title never sees a disconnect between the nav
            // and the route. When the bot reports no markers (legacy route-only scenes preconditioned in-world via
            // --attach) the caller falls back to measuring the whole fixed window.
            using var engine = new InputAutomationEngine(log, inject: realGame) { TargetPid = gamePid, Vision = BuildVision(realGame, log), LiveSampleCount = liveSampleCount, SharedPad = sharedPad, Supervisor = BuildSupervisor(log),
                Gx10Navigator = ScriptUsesInWorldNav(script) ? BuildInWorldNavigator(realGame, log) : null,
                DesktopGuardAnchors = ResolveDesktopAnchors(), DesktopGuardMinHits = _cfg.DesktopGuardMinHits, StripOsdFromOcr = _cfg.StripOsdFromOcr, MeasuredGate = measuredGate };
            return await engine.RunAsync(script, window, ct).ConfigureAwait(false);
        }
        await Task.Delay(window, ct).ConfigureAwait(false);   // FixedWindow
        return null;
    }

    /// <summary>
    /// "gameplay" warm-up mode: run the configured warm-up bot (nav-to-in-world + a deterministic moving
    /// traversal) on its own InputAutomationEngine, with NO frame/power capture, so cold shaders compile + asset
    /// caches warm before the measured route. Best-effort — a warm-up that throws is logged and swallowed; the
    /// measured run's own validity gates (GPU-idle floor / outlier flagging / never-fake) still protect the number.
    /// </summary>
    private async Task RunWarmupGameplayAsync(SceneProfile scene, SceneWarmupConfig wu, bool realGame, int? gamePid, RunLogger log, CancellationToken ct, IGamepad? sharedPad = null)
    {
        // Keep the per-game hang-watchdog (RunHeartbeat) fresh for the whole warm-up: the warm-up is a known
        // DRIVING phase, but its post-nav traversal injects pad movement with NO OCR/frame progress (and frames
        // aren't captured during warm-up), which would otherwise starve the watchdog and false-kill a healthy
        // warm-up (live 2026-06-29: the watchdog fired mid-traversal). Pings only while the game PROCESS is alive.
        using var hb = StartWarmupHeartbeat(gamePid, ct);
        if (string.IsNullOrWhiteSpace(wu.Script))
        {
            log.Info("Warmup", $"No warm-up script set — fixed in-place render wait {wu.DurationSeconds:0}s (uncaptured).");
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, wu.DurationSeconds)), ct).ConfigureAwait(false);
            return;
        }
        var script = BotScriptLibrary.Resolve(wu.Script);
        if (script is null)
        {
            log.Warn("Warmup", $"Warm-up script '{wu.Script}' not found — falling back to a {wu.DurationSeconds:0}s wait.");
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, wu.DurationSeconds)), ct).ConfigureAwait(false);
            return;
        }
        // Generous bound: nav-to-in-world (readiness) + the warm-up traversal + margin.
        var timeout = TimeSpan.FromSeconds(scene.ReadinessTimeoutSeconds + wu.DurationSeconds + 60);
        try
        {
            using var engine = new InputAutomationEngine(log, inject: realGame)
            { TargetPid = gamePid, Vision = BuildVision(realGame, log), SharedPad = sharedPad, Supervisor = BuildSupervisor(log),
              Gx10Navigator = ScriptUsesInWorldNav(script) ? BuildInWorldNavigator(realGame, log) : null,
              DesktopGuardAnchors = ResolveDesktopAnchors(), DesktopGuardMinHits = _cfg.DesktopGuardMinHits, StripOsdFromOcr = _cfg.StripOsdFromOcr };
            await engine.RunAsync(script, timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { log.Warn("Warmup", $"Warm-up traversal error (continuing to the measured run): {ex.Message}"); }
    }

    /// <summary>
    /// "shaderPrecompile" warm-up mode (v1): some Nixxes ports compile shaders on a LAUNCH screen before
    /// gameplay; wait for that to finish, then gameplay is warm from frame 1 and the gameplay warm-up is skipped.
    /// v1 is a timed wait of <see cref="SceneWarmupConfig.DurationSeconds"/>; an OCR-gated
    /// "shaderScreenGone:TOKEN" CompletionCondition (wait until the compile screen's text clears) is the planned
    /// refinement.
    /// </summary>
    private async Task WaitForShaderPrecompileAsync(SceneProfile scene, SceneWarmupConfig wu, bool realGame, int? gamePid, RunLogger log, CancellationToken ct)
    {
        using var hb = StartWarmupHeartbeat(gamePid, ct);   // keep the hang-watchdog fed through the precompile wait
        log.Info("Warmup", $"shaderPrecompile mode: waiting {wu.DurationSeconds:0}s for the game's launch shader-compilation to finish; gameplay is then measured warm (no gameplay warm-up). CompletionCondition='{wu.CompletionCondition}' (OCR-gated detection is a future refinement; v1 is a timed wait).");
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, wu.DurationSeconds)), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Keep the per-game hang-watchdog (<c>RunHeartbeat</c>) fresh for the duration of a WARM-UP phase. Warm-up is
    /// a known driving/loading phase whose quiet stretches — a fixed wait, or a pad-only traversal with no OCR /
    /// frame progress — would otherwise let the heartbeat go stale and false-classify a healthy warm-up as a
    /// crash/hang (observed live 2026-06-29: the watchdog fired while the bot was mid-traversal). Pings every ~15 s,
    /// but ONLY while the game PROCESS is alive — a genuinely crashed warm-up (process gone) stops pinging so the
    /// watchdog still catches it, and a stuck-but-alive warm-up is caught downstream by the measured run's own
    /// watchdog + validity gates. Dispose the returned handle to stop pinging.
    /// </summary>
    private static IDisposable StartWarmupHeartbeat(int? gamePid, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    bool alive = true;
                    if (gamePid is int gp)
                    {
                        try { using var p = System.Diagnostics.Process.GetProcessById(gp); alive = !p.HasExited; }
                        catch { alive = false; }
                    }
                    if (alive) GpuSuite.Core.RunHeartbeat.Ping();
                    await Task.Delay(15000, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* stopped normally */ }
            catch { /* best-effort */ }
        }, CancellationToken.None);
        return new CancelOnDispose(cts);
    }

    /// <summary>Cancels + disposes a CancellationTokenSource on Dispose (so <c>using</c> stops a background loop).</summary>
    private sealed class CancelOnDispose : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        public CancelOnDispose(CancellationTokenSource cts) => _cts = cts;
        public void Dispose() { try { _cts.Cancel(); } catch { } _cts.Dispose(); }
    }

    private static double LargestTimeGap(IReadOnlyList<FrameSample> frames)
    {
        double max = 0;
        for (int i = 1; i < frames.Count; i++)
        {
            double gap = frames[i].TimeSec - frames[i - 1].TimeSec;
            if (gap > max) max = gap;
        }
        return max;
    }

    private static double MaxFrameTimeMs(IReadOnlyList<FrameSample> frames)
    {
        double max = 0;
        foreach (var f in frames) if (f.FrameTimeMs > max) max = f.FrameTimeMs;
        return max;
    }

    /// <summary>
    /// Longest contiguous run of "real gameplay" frames within an over-long activity window. A frame is
    /// gameplay when its instantaneous FPS is at or below <paramref name="ceilingFps"/> (so uncapped
    /// menu / load / results screens rendering at hundreds–thousands of fps are excluded) and it is not a
    /// load/stall hitch. A single hitch frame (slower than the hard-hitch threshold) HARD-breaks the run,
    /// so a level-load hitch becomes a band boundary rather than poisoning the band's first frame; brief
    /// fast spikes inside genuine gameplay are bridged. Returns the band (a sublist of
    /// <paramref name="frames"/>, still window-relative), or null if no gameplay band exists.
    /// </summary>
    private static List<FrameSample>? IsolateGameplayBand(IReadOnlyList<FrameSample> frames, double ceilingFps)
    {
        if (frames.Count == 0 || ceilingFps <= 0) return null;
        double floorMs = 1000.0 / ceilingFps; // faster than this ⇒ menu/load/results screen
        const double hardHitchMs = 250.0;     // slower than this ⇒ load/stall hitch ⇒ hard break
        const int maxBridgeFrames = 60;        // tolerate brief fast spikes within real gameplay

        // 2 = hitch (hard break), 1 = gameplay, 0 = fast (menu-rate; bridgeable)
        static int Cls(double ft, double floorMs, double hitchMs) => ft > hitchMs ? 2 : (ft >= floorMs ? 1 : 0);

        int bestStart = -1, bestEnd = -1; double bestSpan = -1;
        int i = 0, n = frames.Count;
        while (i < n)
        {
            if (Cls(frames[i].FrameTimeMs, floorMs, hardHitchMs) != 1) { i++; continue; }
            int segStart = i, lastGood = i, bridge = 0, j = i;
            while (j < n)
            {
                int c = Cls(frames[j].FrameTimeMs, floorMs, hardHitchMs);
                if (c == 2) break;                          // hitch: never bridge — ends this band
                if (c == 1) { lastGood = j; bridge = 0; }
                else if (++bridge > maxBridgeFrames) break; // sustained menu-rate region: ends this band
                j++;
            }
            double span = frames[lastGood].TimeSec - frames[segStart].TimeSec;
            if (span > bestSpan) { bestSpan = span; bestStart = segStart; bestEnd = lastGood; }
            i = j + 1;
        }
        if (bestStart < 0) return null;
        var band = new List<FrameSample>(bestEnd - bestStart + 1);
        for (int k = bestStart; k <= bestEnd; k++) band.Add(frames[k]);
        return band;
    }

    private static List<FrameSample> TrimFrames(IReadOnlyList<FrameSample> frames, double start, double end)
    {
        var win = new List<FrameSample>();
        foreach (var f in frames)
            if (f.TimeSec >= start && f.TimeSec <= end)
                win.Add(new FrameSample(f.TimeSec - start, f.FrameTimeMs) { DisplayedTimeMs = f.DisplayedTimeMs, GpuBusyMs = f.GpuBusyMs });
        return win.Count > 0 ? win : frames.ToList();
    }

    private static List<T> TrimByTime<T>(IReadOnlyList<T> samples, double start, double end, Func<T, double> time)
    {
        var win = samples.Where(s => time(s) >= start && time(s) <= end).ToList();
        return win.Count > 0 ? win : samples.ToList();
    }

    private static PowerStats BuildPowerStats(IReadOnlyList<PowerSample> p, int frameCount, DataSourceMode source, PowerMeasurementMetadata measurement)
    {
        var s = new PowerStats { SampleCount = p.Count, Source = source, Measurement = measurement };
        if (p.Count == 0) return s;
        s.DurationSec = p[^1].TimeSec - p[0].TimeSec;
        if (p.Count > 1 && s.DurationSec > 0)
            s.Measurement.EffectiveSampleHz = (p.Count - 1) / s.DurationSec;
        s.AvgGpuPowerW = Statistics.Avg(p.Select(x => x.GpuTotalW));
        s.PeakGpuPowerW = Statistics.Max(p.Select(x => x.GpuTotalW));
        s.MinGpuPowerW = Statistics.Min(p.Select(x => x.GpuTotalW));
        s.AvgSystemPowerW = Statistics.Avg(p.Select(x => x.SystemTotalW));
        s.PeakSystemPowerW = Statistics.Max(p.Select(x => x.SystemTotalW));
        if (PowerProvenance.IsDirectEfficiencyEligible(s.Measurement) && s.AvgGpuPowerW is double avg)
        {
            double dur = s.DurationSec > 0 ? s.DurationSec : p.Count * 0.01;
            s.GpuEnergyJoules = avg * dur;
            s.EnergyPerFrameJ = frameCount > 0 ? s.GpuEnergyJoules / frameCount : null;
        }
        return s;
    }

    private static PowerMeasurementMetadata CloneMeasurement(PowerMeasurementMetadata source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        Kind = source.Kind,
        Scope = source.Scope,
        EffectiveSampleHz = source.EffectiveSampleHz,
        HasPerRailData = source.HasPerRailData,
        HardwareBustersVerifiedPowerEligible = source.HardwareBustersVerifiedPowerEligible,
        LegacyInferred = source.LegacyInferred,
        QualificationNote = source.QualificationNote
    };

    private static TelemetryStats BuildTelemetryStats(IReadOnlyList<TelemetrySample> t)
    {
        var s = new TelemetryStats { SampleCount = t.Count };
        if (t.Count == 0) return s;
        s.GpuTempAvgC = Statistics.Avg(t.Select(x => x.GpuTempC));
        s.GpuTempMaxC = Statistics.Max(t.Select(x => x.GpuTempC));
        s.GpuHotspotAvgC = Statistics.Avg(t.Select(x => x.GpuHotspotC));
        s.GpuHotspotMaxC = Statistics.Max(t.Select(x => x.GpuHotspotC));
        s.GpuVramTempMaxC = Statistics.Max(t.Select(x => x.GpuVramTempC));
        s.GpuCoreClockAvgMhz = Statistics.Avg(t.Select(x => x.GpuCoreClockMhz));
        s.GpuMemClockAvgMhz = Statistics.Avg(t.Select(x => x.GpuMemClockMhz));
        s.GpuLoadAvgPct = Statistics.Avg(t.Select(x => x.GpuLoadPct));
        s.GpuBoardPowerAvgW = Statistics.Avg(t.Select(x => x.GpuBoardPowerW));
        s.FanRpmAvg = Statistics.Avg(t.Select(x => x.MaxFanRpm));
        s.FanRpmMax = Statistics.Max(t.Select(x => x.MaxFanRpm));
        s.CpuTempAvgC = Statistics.Avg(t.Select(x => x.CpuTempC));
        s.CpuLoadAvgPct = Statistics.Avg(t.Select(x => x.CpuLoadPct));
        return s;
    }
}
