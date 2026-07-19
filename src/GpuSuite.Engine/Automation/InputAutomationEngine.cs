using System.Diagnostics;
using System.Runtime.InteropServices;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Vision;

namespace GpuSuite.Engine.Automation;

/// <summary>Outcome of running a bot script: where (if anywhere) the script marked the benchmark
/// scene boundaries, relative to the bot-run start. Lets the scene runner bound the measured window
/// to the real scene (excluding menu navigation / loading frames).</summary>
public sealed class BotRunResult
{
    public double? StartOffsetSec { get; set; }
    public double? EndOffsetSec { get; set; }
    public int Iterations { get; set; }
    /// <summary>True when the bot hit a <see cref="BotAction.Required"/> gate that timed out — the nav desynced
    /// and never reached the benchmark/in-world scene, so the measured window is NOT the intended content. The
    /// scene runner rejects the run (Invalid) instead of persisting a false pass off a menu/loading screen.</summary>
    public bool Aborted { get; set; }
    public string? AbortReason { get; set; }
    public List<BotTraversalStats> Traversals { get; } = new();
}

/// <summary>Thrown internally when a <see cref="BotAction.Required"/> WaitForText/PressUntilText gate times out,
/// so <see cref="InputAutomationEngine.RunAsync"/> can abort the bot and flag the run rather than proceed onto
/// the wrong screen and measure it as a (false) pass.</summary>
internal sealed class BotGateAbortException : Exception
{
    public BotGateAbortException(string message) : base(message) { }
}

/// <summary>
/// (4) Bot/Input Automation Engine. Executes a <see cref="BotScript"/> — a camera macro or an in-game
/// menu-navigation sequence (keyboard + absolute/relative mouse) — for the duration of a scene capture.
///
/// SAFETY: input is only injected into the real desktop when <see cref="Inject"/> is true (set by the
/// orchestrator only when a real game process is the foreground target). With no game present (degraded
/// mode) it runs in DRY-RUN: every action is written to the timeline log so the scripted scene is fully
/// auditable, but NO keystrokes/mouse events are sent to the user's machine.
/// </summary>
public sealed class InputAutomationEngine : IDisposable
{
    private readonly RunLogger _log;
    public bool Inject { get; set; }
    /// <summary>When set (and <see cref="Inject"/> is true), the engine restores + foregrounds this
    /// process's main window before injecting. Exclusive-fullscreen games (e.g. DOOM: The Dark Ages)
    /// MINIMIZE when they lose focus — once minimized, SendInput goes to the wrong window and the bot
    /// silently does nothing. Restoring focus first makes injected keyboard/mouse reliable. Null = inject
    /// to whatever window is already foreground (legacy behaviour).</summary>
    public int? TargetPid { get; set; }
    /// <summary>When true, scripted waits are compressed (capped at 25ms) so a dry-run replays the whole
    /// sequence quickly for validation. Has no effect on injected (real) runs — those keep true timing.</summary>
    public bool FastForward { get; set; }
    /// <summary>Validation-only mode used by bot-dryrun: parse and summarize graph/vision navigation without
    /// polling OCR or a supervisor that cannot exist in a non-injected run.</summary>
    public bool SkipInteractiveNavigation { get; set; }
    /// <summary>Optional cap for legacy looping action scripts. Null preserves normal runtime behavior.</summary>
    public int? MaxActionIterations { get; set; }
    /// <summary>Capture-card OCR "eye" used by <see cref="BotActionType.WaitForText"/> gates to wait until an
    /// expected on-screen string appears/disappears, instead of a blind fixed wait. Null (dry-run, or no
    /// capture device wired) ⇒ WaitForText degrades to a bounded blind wait. Set by the scene runner /
    /// CLI when a real game is the target so menu-nav bots are robust to variable load/shader-compile time.</summary>
    public ScreenReader? Vision { get; set; }
    /// <summary>When true, the virtual gamepad is NOT disconnected when <see cref="RunAsync"/> returns — it is
    /// released to neutral but stays connected until the engine is <see cref="Dispose"/>d. Required for
    /// controller-required games (ForzaTech) whose built-in benchmark must keep a controller connected through
    /// the MEASURED window: the start-bot must return promptly (so the frame-baseline + completion detector
    /// bound the flythrough), but the pad disconnecting then would throw an in-game "CONTROLLER DISCONNECTED"
    /// modal over the run. The scene runner disposes the engine only after capture stops.</summary>
    public bool KeepPadAlive { get; set; }
    /// <summary>Live total-frames-captured accessor (the capture session's running SampleCount), wired by the
    /// scene runner. <see cref="BotActionType.WaitForGameplayBand"/> samples it over time to derive the live
    /// frame-rate and gate MarkStart on the load→gameplay transition. Null (dry-run / no capture) ⇒ that gate
    /// degrades to a bounded blind wait.</summary>
    public Func<int>? LiveSampleCount { get; set; }
    /// <summary>Optional gate the runtime health watchdog reads: if THIS script has a MarkStart, the engine CLOSES
    /// it at run start (so a static cold-launch screen sitting GPU-idle during NAV can't false-trip the watchdog's
    /// gpu-idle / frames-frozen check), OPENS it at MarkStart, and CLOSES it at MarkEnd. Null ⇒ render-health stays
    /// armed throughout (builtin benchmark / route-only attach). Set by the scene runner. Fixes the AW2 cold-launch
    /// false "frozen capture" (2026-06-29).</summary>
    public MeasuredWindowGate? MeasuredGate { get; set; }
    /// <summary>Optional LLM fallback for <see cref="BotScript.Graph"/> nav: consulted when the deterministic graph
    /// is LOST on an unknown screen. Null ⇒ deterministic-only (recover/abort). Set by the scene runner / CLI.</summary>
    public INavSupervisor? Supervisor { get; set; }
    /// <summary>Optional TIER-1 in-world navigator (GX10 vision → movement). When set and a script uses
    /// <see cref="BotActionType.Gx10Traverse"/>, the engine streams capture-card frames to the GX10 and drives the
    /// character's movement from the box's decisions — perception OFF-BENCH so the bench GPU is untouched. Null ⇒
    /// Gx10Traverse degrades to blind-forward chunks (dry-run, or the box unreachable). Set by the scene runner / CLI
    /// on a live run; only built when a script actually uses Gx10Traverse.</summary>
    public Gx10InWorldNavigator? Gx10Navigator { get; set; }
    /// <summary>When true, an injected step must reach the TARGET GAME or the script ABORTS
    /// (<see cref="BotRunResult.Aborted"/>) — never "injecting to current foreground". The legacy fallback is
    /// correct for nav bots mid-launch, but for the MENU APPLIER it is the 2026-07-03 DOOM defect: an
    /// intermittent window-resolve miss sprayed pad taps into the SHELL, popping Search/touch-keyboard over the
    /// bench. Strict mode retries the resolve+focus (incl. the shell-overlay killer), then fails LOUDLY so the
    /// variant is flagged instead of half-applied. Default false preserves every existing bot's behaviour.</summary>
    public bool StrictForeground { get; set; }
    /// <summary>When true, keyboard injection sends VIRTUAL-KEY events (wVk) instead of DirectInput SCANCODES.
    /// Most fullscreen / raw-input games want scancodes (the default), and every existing bot relies on that.
    /// But some MOUSE-FIRST UI menus accept directional SCANCODES yet ignore a scancode "accept" — they read the
    /// confirm off the Windows message queue (a virtual key). Live-validated 2026-06-28: Cyberpunk's main menu
    /// moves on scancode arrows but does NOT confirm on scancode Enter/Space/F. This flag routes the script through
    /// virtual keys so such a menu receives a usable confirm. Exposed for live calibration via `send-input --vk`.
    /// Default false (scancode) preserves all existing bot behaviour bit-for-bit.</summary>
    public bool UseVirtualKeys { get; set; }
    /// <summary>Desktop-frame guard anchors (the <see cref="IsTargetForeground"/> backstop): if a freshly-OCR'd nav
    /// frame matches ≥ <see cref="DesktopGuardMinHits"/> of these Windows-shell phrases it is treated as the DESKTOP
    /// — the runner WAITs instead of acting, so it can't false-match menu words off desktop icons / the operator's
    /// chat window (diagnosed 2026-06-27). Null/empty disables. Set by the scene runner from settings. Every
    /// OCR-consuming path (graph, vision-nav, WaitForText, PressUntilText) honors it.</summary>
    public IReadOnlyList<string>? DesktopGuardAnchors { get; set; }
    /// <summary>How many <see cref="DesktopGuardAnchors"/> must appear before a frame is judged the desktop. Default 2.</summary>
    public int DesktopGuardMinHits { get; set; } = 2;
    /// <summary>Strip RTSS / benchmark OSD lines (the fps/frametime/pass overlay) from a nav OCR frame before anchor
    /// matching, so the top-left overlay can't clutter or false-match menu text. Default true. See
    /// <see cref="Vision.OcrFrame.WithoutOsdLines"/>.</summary>
    public bool StripOsdFromOcr { get; set; } = true;

    public InputAutomationEngine(RunLogger log, bool inject = false)
    {
        _log = log;
        Inject = inject;
    }

    /// <summary>True when this OCR frame looks like the Windows desktop (≥ <see cref="DesktopGuardMinHits"/> shell
    /// anchors) — the runner should WAIT rather than act on it (see <see cref="DesktopGuardAnchors"/>).</summary>
    private bool IsDesktopFrame(OcrFrame? f) =>
        DesktopGuardAnchors is { Count: > 0 } && DesktopDetector.LooksLikeDesktop(f, DesktopGuardAnchors, DesktopGuardMinHits);

    /// <summary>Apply the OCR guards to a freshly-read nav frame: strip the RTSS/OSD overlay (#6) so it can't
    /// false-match menu text, then return NULL for a Windows-desktop frame (#5) so the existing "null ⇒ re-poll /
    /// don't act" path treats it as not-ready. A normal game frame passes through (possibly OSD-stripped).</summary>
    private OcrFrame? GuardOcr(OcrFrame? frame)
    {
        if (frame is null) return null;
        if (StripOsdFromOcr) frame = frame.WithoutOsdLines();
        if (IsDesktopFrame(frame))
        {
            _log.Trace("Bot", "OCR guard: capture looks like the Windows desktop — treating as not-ready (waiting).");
            return null;
        }
        return frame;
    }

    private Task Delay(int ms, CancellationToken ct) => Task.Delay(FastForward ? Math.Min(ms, 25) : ms, ct);

    /// <summary>Virtual gamepad for Gamepad-device scripts; null for keyboard scripts or dry-runs.</summary>
    private IGamepad? _pad;
    private bool _ownsPad;
    /// <summary>A caller-owned pad connected before game launch. Controller-first games bind this device during
    /// startup, so bot engines must reuse it instead of hot-plugging another controller afterwards.</summary>
    public IGamepad? SharedPad { get; set; }
    /// <summary>The currently-held keyboard translation keys (W/A/S/D) for the persistent <see cref="SetTranslation"/>
    /// move driver, so a new direction releases the previous one cleanly.</summary>
    private List<string> _translationKeys = new();
    /// <summary>Pad actions inject only when a real virtual pad is actually connected.</summary>
    private bool PadInject => Inject && _pad is { Connected: true };

    public async Task<BotRunResult> RunAsync(BotScript script, TimeSpan window, CancellationToken ct)
    {
        bool padMode = script.InputDevice == BotInputDevice.Gamepad;
        // REUSE a still-connected pad (KeepPadAlive across per-step RunAsync calls) instead of re-creating one
        // per call. The old unconditional create meant every one-shot step was a pad CONNECT→taps→DISCONNECT —
        // per-step controller churn that pops the Windows touch keyboard over the game (DOOM 2026-07-03) and
        // throws controller-disconnect modals in games that watch for it (CP, F1). One session = one pad.
        if (padMode && Inject && _pad is not { Connected: true })
        {
            if (SharedPad is { Connected: true })
            {
                _pad = SharedPad;
                _ownsPad = false;
                _log.Info("Pad", "Reusing the pre-launch virtual Xbox controller bound by the game at startup.");
            }
            else
            {
                _pad = Gamepad.Create(_log);
                _ownsPad = true;
            }
            if (!_pad.Connected)
                _log.Warn("Bot", $"Script '{script.Id}' needs a virtual gamepad but none is available ({_pad.Unavailable}) — " +
                                  "replaying DRY (no input sent). Install ViGEmBus and verify with 'gpusuite vigem-check'.");
        }
        _log.Info("Bot", $"Running script '{script.Id}' for {window.TotalSeconds:0.#}s (inject={Inject}, device={script.InputDevice}, loop={script.Loop})");
        if (Inject) EnsureGameForeground();
        var result = new BotRunResult();
        var sw = Stopwatch.StartNew();
        var deadline = DateTime.UtcNow + window;
        int iteration = 0;
        // If this bot carries a measured ROUTE (a MarkStart in Actions or a graph MarkStart screen), DISARM the
        // runtime render-health watchdog for the NAV phase: the cold-launch logos / disclaimer / "PRESS TO
        // CONTINUE" / load screens legitimately sit GPU-idle and a slow one can hold past the 6s idle grace,
        // false-failing the run (live-caught on AW2 cold). MarkStart re-arms it; MarkEnd disarms again. A bot
        // with NO MarkStart (route-only) leaves it armed.
        bool gatesMeasuredWindow = script.Actions.Any(a => a.Type == BotActionType.MarkStart)
            || (script.Graph?.Screens.Any(s => s.MarkStartHere) ?? false);
        if (gatesMeasuredWindow) MeasuredGate?.Close();
        try
        {
            // A reactive nav GRAPH (if present) runs first, steering the branching/stalled front-end to its goal
            // (e.g. cold-launch → main menu / the built-in-benchmark row). For a menu-nav bot that's the whole job
            // and Actions is empty. But a SCRIPTED-SCENE bot (a game with NO built-in benchmark — AW2, Ratchet,
            // TLOU…) also carries a deterministic measured ROUTE in Actions: the graph handles the fragile nav, then
            // the route (MarkStart…fixed motion…MarkEnd) runs ONCE so the measured window stays reproducible. So:
            // run the graph (if any), THEN run the Actions list. (Looping applies ONLY to a legacy Actions-only bot;
            // a graph-driven route never loops — its window is bounded by the route's own marks.)
            // NAV PHASE — steer the fragile cold-launch / menu / desktop nav to its goal. Prefer the VISION navigator
            // (it SEES the screen, runs on the GPU, and self-adapts) when the bot declares a visionGoal and one is
            // wired; otherwise the deterministic OCR-signature graph. The vision nav reaches the in-world scene; the
            // fixed measured ROUTE in Actions then runs once with the GPU all to the game.
            if (SkipInteractiveNavigation && (!string.IsNullOrWhiteSpace(script.VisionGoal) || script.Graph is not null))
            {
                iteration = 1;
                _log.Info("Bot", "Dry-run validation: interactive vision/graph navigation parsed and intentionally skipped.");
            }
            else if (!string.IsNullOrWhiteSpace(script.VisionGoal) && Supervisor is IVisionNavigator vnav)
            {
                iteration = 1;
                await RunVisionNavAsync(script.VisionGoal!, vnav, script.VisionHoldMs, script.VisionTimeoutSeconds, script.VisionDoneConfirmations, script.VkConfirm, sw, result, deadline, ct).ConfigureAwait(false);
            }
            else if (script.Graph is not null)
            {
                iteration = 1;
                await RunGraphAsync(script.Graph, sw, result, deadline, ct).ConfigureAwait(false);
            }
            // Hand-off: normally free the menu-vision model before the measured route. A script that continues with
            // Gx10Traverse deliberately reuses the SAME remote/off-bench model for in-world steering, so keep it warm;
            // unloading here adds a 20-40s cold start and leaves the character grinding forward while it reloads.
            // SceneRunner only wires Gx10Navigator for a non-loopback endpoint, so this cannot retain inference on the
            // benchmark machine/GPU.
            if (Supervisor is not null && !ct.IsCancellationRequested)
            {
                if (Gx10Navigator is null)
                    await Supervisor.UnloadAsync(ct).ConfigureAwait(false);
                else
                    _log.Info("Bot", $"VisionNav: keeping remote model '{Gx10Navigator.Model}' warm for Phase 3 in-world steering (off-bench at {Gx10Navigator.Endpoint}).");
            }
            if (script.Actions.Count > 0)
            {
                bool mayLoop = script.Loop && script.Graph is null;
                do
                {
                    iteration++;
                    foreach (var a in script.Actions)
                    {
                        if (ct.IsCancellationRequested || DateTime.UtcNow >= deadline) break;
                        await ExecuteAsync(a, sw, result, ct).ConfigureAwait(false);
                    }
                }
                while (mayLoop
                       && (!MaxActionIterations.HasValue || iteration < MaxActionIterations.Value)
                       && !ct.IsCancellationRequested
                       && DateTime.UtcNow < deadline);
            }
        }
        // A required gate timed out: the nav desynced and never reached the benchmark/in-world scene. Flag the
        // result so the scene runner rejects the run (Invalid) instead of measuring whatever is on screen.
        catch (BotGateAbortException ex)
        {
            result.Aborted = true;
            result.AbortReason = ex.Message;
        }
        // Re-throw a cancellation the CALLER actually requested (Ctrl+C / orchestrator timeout) so the scene
        // runner doesn't capture+persist a cancelled run as if it completed; only swallow internal/benign OCEs.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        finally
        {
            ReleaseAllHeldKeys();
            if ((KeepPadAlive || !_ownsPad) && _pad is { Connected: true })
            {
                try { _pad.Neutral(); } catch { }   // release buttons/sticks but stay CONNECTED until Dispose(); never let teardown escape
                _padHeld.Clear();
                _log.Trace("Bot", $"Script '{script.Id}' finished after {iteration} iteration(s); virtual pad kept connected (KeepPadAlive).");
            }
            else
            {
                if (_ownsPad) _pad?.Dispose(); // caller-owned pre-launch pads stay connected until their cell ends
                _pad = null;
                _log.Trace("Bot", $"Script '{script.Id}' finished after {iteration} iteration(s).");
            }
            result.Iterations = iteration;
        }
        return result;
    }

    /// <summary>Disconnect the kept-alive virtual pad (no-op if already released). Lets the scene runner hold
    /// the pad connected across the start-bot's return AND the whole measured window, then tear it down only
    /// after capture stops — so a controller-disconnect modal can't pop over the benchmark flythrough.</summary>
    public void Dispose()
    {
        if (_pad is not null)
        {
            try { if (_ownsPad) _pad.Dispose(); else _pad.Neutral(); } catch { }
            _pad = null;
        }
    }

    private readonly HashSet<string> _held = new();

    private async Task ExecuteAsync(BotAction a, Stopwatch sw, BotRunResult result, CancellationToken ct)
    {
        string label = a.Note is null ? "" : $" — {a.Note}";
        switch (a.Type)
        {
            case BotActionType.KeyDown:
                // Re-assert foreground at EVERY injection (no-op when already foreground): an unfocused game ignores
                // injected input — incl. the global ViGEm pad, which the game only POLLS while its window is active —
                // and focus can be stolen at any moment mid-nav by an operator/remote window (TeamViewer-restored
                // session GUI on the bench display, 2026-07-02: BMW menu ate 3/3 A-taps 4 of 5 runs, FH6 ate 25).
                _log.Trace("Bot", $"KeyDown {a.Key}{(a.Vk ? " [vk]" : "")}{label}"); if (Inject && a.Key is not null) { EnsureGameForeground(); KeyDown(a.Key, a.Vk); _held.Add(a.Key); }
                break;
            case BotActionType.KeyUp:
                _log.Trace("Bot", $"KeyUp {a.Key}{(a.Vk ? " [vk]" : "")}{label}"); if (Inject && a.Key is not null) { EnsureGameForeground(); KeyUp(a.Key, a.Vk); _held.Remove(a.Key); }
                break;
            case BotActionType.KeyTap:
                _log.Trace("Bot", $"KeyTap {a.Key} {a.DurationMs}ms{(a.Vk ? " [vk]" : "")}{label}");
                if (Inject && a.Key is not null) { EnsureGameForeground(); KeyDown(a.Key, a.Vk); try { await Delay(a.DurationMs, ct).ConfigureAwait(false); } finally { KeyUp(a.Key, a.Vk); } }
                else await Delay(a.DurationMs, ct).ConfigureAwait(false);
                break;
            case BotActionType.MouseMove:
                _log.Trace("Bot", $"MouseMove dx={a.Dx} dy={a.Dy} {a.DurationMs}ms{label}");
                if (Inject) MouseMoveRelative(a.Dx, a.Dy);
                await Delay(a.DurationMs, ct).ConfigureAwait(false);
                break;
            case BotActionType.MouseMoveAbs:
                _log.Trace("Bot", $"MouseMoveAbs x={a.X:0.000} y={a.Y:0.000} {a.DurationMs}ms{label}");
                if (Inject) MouseMoveAbsolute(a.X, a.Y);
                await Delay(a.DurationMs, ct).ConfigureAwait(false);
                break;
            case BotActionType.MouseClick:
                _log.Trace("Bot", $"MouseClick {a.Button ?? "left"} {a.DurationMs}ms{label}");
                if (Inject) { EnsureGameForeground(); MouseButton(a.Button, true); await Delay(Math.Max(10, a.DurationMs), ct).ConfigureAwait(false); MouseButton(a.Button, false); }
                else await Delay(a.DurationMs, ct).ConfigureAwait(false);
                break;
            case BotActionType.Wait:
                await Delay(a.DurationMs, ct).ConfigureAwait(false);
                break;
            case BotActionType.WaitForText:
                await WaitForTextAsync(a, ct).ConfigureAwait(false);
                break;
            case BotActionType.TapIfText:
                await TapIfTextAsync(a, ct).ConfigureAwait(false);
                break;
            case BotActionType.PressUntilText:
                await PressUntilTextAsync(a, ct).ConfigureAwait(false);
                break;
            case BotActionType.WaitForGameplayBand:
                await WaitForGameplayBandAsync(a, ct).ConfigureAwait(false);
                break;
            case BotActionType.MarkStart:
                if (Inject) EnsureGameForeground();   // open the measured window with the GAME foreground — a backgrounded borderless game (focus stolen by e.g. the agent's own GUI on the bench display) PAUSES -> GPU idle -> 'frozen capture' (Ratchet 2026-07-01). The PadLeftStick holds below re-acquire periodically.
                result.StartOffsetSec = sw.Elapsed.TotalSeconds;
                MeasuredGate?.Open();   // arm runtime render-health checks — gameplay is rendering from here
                _log.Info("Bot", $"MARK START at +{result.StartOffsetSec:0.0}s{label} — benchmark scene begins (measured window starts here).");
                break;
            case BotActionType.MarkEnd:
                result.EndOffsetSec = sw.Elapsed.TotalSeconds;
                MeasuredGate?.Close();  // measured window done — disarm render-health (trailing nav/quit is idle-OK)
                _log.Info("Bot", $"MARK END at +{result.EndOffsetSec:0.0}s{label} — benchmark scene ends.");
                break;

            // ---- virtual gamepad (ViGEm) ----
            case BotActionType.PadButtonDown:
                _log.Trace("Bot", $"PadButtonDown {a.Key}{label}");
                if (PadInject && a.Key is not null) { EnsureGameForeground(); _pad!.Button(a.Key, true); _padHeld.Add(a.Key); }
                break;
            case BotActionType.PadButtonUp:
                _log.Trace("Bot", $"PadButtonUp {a.Key}{label}");
                if (PadInject && a.Key is not null) { _pad!.Button(a.Key, false); _padHeld.Remove(a.Key); }
                break;
            case BotActionType.PadButtonTap:
                // see KeyDown: the game must be foreground to POLL the pad, and the press must stay delivered for the
                // whole hold — hold the game foreground THROUGH the press, not just at the edge (280ms tap ↔ one focus
                // steal = eaten press).
                _log.Trace("Bot", $"PadButtonTap {a.Key} {a.DurationMs}ms{label}");
                if (PadInject && a.Key is not null) { EnsureGameForeground(); _pad!.Button(a.Key, true); await DelayHoldingForeground(a.DurationMs, ct).ConfigureAwait(false); _pad!.Button(a.Key, false); }
                else await Delay(a.DurationMs, ct).ConfigureAwait(false);
                break;
            case BotActionType.PadLeftStick:
                _log.Trace("Bot", $"PadLeftStick x={a.X:0.00} y={a.Y:0.00} {a.DurationMs}ms{label}");
                if (PadInject) _pad!.LeftStick(a.X, a.Y);
                await DelayHoldingForeground(a.DurationMs, ct).ConfigureAwait(false);   // keep the (borderless) game foreground through the hold so a focus steal can't pause it -> frozen capture
                break;
            case BotActionType.PadRightStick:
                _log.Trace("Bot", $"PadRightStick x={a.X:0.00} y={a.Y:0.00} {a.DurationMs}ms{label}");
                if (PadInject) _pad!.RightStick(a.X, a.Y);
                await DelayHoldingForeground(a.DurationMs, ct).ConfigureAwait(false);   // see PadLeftStick
                break;
            case BotActionType.PadTrigger:
                _log.Trace("Bot", $"PadTrigger {a.Button ?? "left"}={a.X:0.00} {a.DurationMs}ms{label}");
                if (PadInject) { EnsureGameForeground(); _pad!.Trigger(a.Button ?? "left", a.X); }
                await Delay(a.DurationMs, ct).ConfigureAwait(false);
                break;

            case BotActionType.SmartTraverse:
                await SmartTraverseAsync(a, result, ct).ConfigureAwait(false);
                break;

            case BotActionType.AssertWorldMotion:
                await AssertWorldMotionAsync(a, ct).ConfigureAwait(false);
                break;

            case BotActionType.ReplayRoute:
                await ReplayRouteAsync(a, ct).ConfigureAwait(false);
                break;

            case BotActionType.Gx10Traverse:
                await Gx10TraverseAsync(a, result, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Tier-0 SMART movement: run the character FORWARD continuously for DurationMs, but SENSE motion off the
    /// capture card every ~chunk (<see cref="CaptureCardGrabber.SampleMotionAsync"/> — ffmpeg scene-change, no
    /// benchmark-GPU cost) and TURN to unstick whenever it stops translating (wall-jam). Beats a blind fixed
    /// route: the character explores real open space instead of grinding a wall (the [[measured-route-must-move]]
    /// failure). Falls back to a blind forward run when no motion sensor is wired (dry-run / no capture). Device
    /// follows the connected pad / keyboard. Adaptive ⇒ net movement, not frame-reproducible — best in the
    /// (trimmed) warm-up, or as the live measured route when realism is wanted over strict determinism.
    /// </summary>
    private async Task SmartTraverseAsync(BotAction a, BotRunResult result, CancellationToken ct)
    {
        int totalMs = a.DurationMs > 0 ? a.DurationMs : 40000;
        if (FastForward && !Inject) totalMs = Math.Min(totalMs, 250);
        double thresh = a.StuckThreshold > 0 ? a.StuckThreshold : 1.0;
        int triggerCount = Math.Max(1, a.StuckTriggerCount);
        int maxRecoveryTurns = Math.Max(0, a.MaxRecoveryTurns);
        var motion = Vision?.Grabber;
        string dev = PadInject ? "pad" : (Inject ? "keyboard" : "dry");
        var rec = a.RecordRoutePath is not null && !(FastForward && !Inject)
            ? new RecordedRoute { Note = string.IsNullOrWhiteSpace(a.Note) ? "smart-traverse discovery" : a.Note!, RecordedDevice = dev }
            : null;   // discover-then-replay: record the emitted forward-holds + unstick-turns for a later deterministic ReplayRoute
        _log.Info("Bot", $"SmartTraverse {totalMs}ms ({dev}) — adaptive forward run; sensor {(motion is null ? "OFF (blind forward)" : "ON")}, {triggerCount} consecutive probe(s) stuck<{thresh:0.00} ⇒ turn{(rec is null ? "" : " [recording → " + a.RecordRoutePath + "]")}.{(a.Note is null ? "" : " — " + a.Note)}");
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(totalMs);
        var segStart = DateTime.UtcNow;   // start of the current continuous forward segment (for route recording)
        int turn = 1, lowStreak = 0, recoveryStreak = 0, probes = 0, parsed = 0, healthy = 0, turns = 0;
        double motionSum = 0, motionMin = double.MaxValue, motionMax = double.MinValue;
        var started = DateTime.UtcNow;
        try
        {
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                Forward(true);   // run forward (held; stick deflection / key-down persists across the probe)
                double m = motion is not null ? await motion.SampleMotionAsync(8, ct: ct).ConfigureAwait(false) : -1;
                probes++;
                GpuSuite.Core.RunHeartbeat.Ping();   // perception + movement = real progress for the hang-watchdog
                if (m < 0) { await Delay(900, ct).ConfigureAwait(false); continue; }   // no sensor / unparsed → keep running blind
                parsed++; motionSum += m; motionMin = Math.Min(motionMin, m); motionMax = Math.Max(motionMax, m);
                if (m < thresh)
                {
                    lowStreak++;
                    if (lowStreak < triggerCount)
                    {
                        _log.Trace("Bot", $"SmartTraverse: low motion {m:0.00} < {thresh:0.00} ({lowStreak}/{triggerCount}) — keep moving pending confirmation.");
                        continue;
                    }
                    Forward(false);   // stop before turning
                    recoveryStreak++;
                    if (maxRecoveryTurns > 0 && turns >= maxRecoveryTurns)
                    {
                        string reason = $"SmartTraverse abandoned after {maxRecoveryTurns} total recovery turns; route kept returning to low motion below {thresh:0.00}.";
                        _log.Error("Bot", reason + " Run rejected rather than measuring a wall, menu, or frozen scene.");
                        throw new BotGateAbortException(reason);
                    }
                    int deg = 45 + 20 * Math.Min(Math.Max(recoveryStreak, 1 + turns / 3), 4); // also escalate repeated intermittent collisions
                    if (rec is not null)
                    {
                        int segMs = (int)(DateTime.UtcNow - segStart).TotalMilliseconds;
                        if (segMs > 0) rec.Steps.Add(RouteStep.Fwd(segMs));   // the forward segment that just ran
                    }
                    // Every third collision backs out before pivoting. Alternating turns alone can oscillate
                    // against furniture/corners when a single noisy healthy probe keeps resetting the streak.
                    if ((turns + 1) % 3 == 0)
                    {
                        SetTranslation(0, -1);
                        try { await DelayHoldingForeground(650, ct).ConfigureAwait(false); }
                        finally { SetTranslation(0, 0); }
                        rec?.Steps.Add(RouteStep.BackStep(650));
                    }
                    rec?.Steps.Add(RouteStep.TurnBy(turn * deg));
                    await TurnAsync(turn * deg, ct).ConfigureAwait(false);
                    turn = -turn;     // alternate turn direction so it can't loop a single corner
                    turns++;
                    lowStreak = 0;
                    segStart = DateTime.UtcNow;   // the next forward segment begins after the turn
                    _log.Trace("Bot", $"SmartTraverse: stuck confirmed ({triggerCount} low probes, latest {m:0.00} < {thresh:0.00}) — turned {deg}° (recovery {recoveryStreak}).");
                }
                else
                {
                    healthy++;
                    lowStreak = 0;
                    recoveryStreak = 0;   // translating fine — a future obstacle starts with the smallest escape turn
                }
            }
            if (motion is not null && a.MinHealthyMotionRatio > 0)
            {
                double ratio = parsed > 0 ? (double)healthy / parsed : 0;
                if (parsed < 2)
                    throw new BotGateAbortException($"SmartTraverse motion proof was incomplete: {parsed}/2 required probe(s) completed ({healthy} healthy). The route is not accepted without enough independent motion samples.");
                if (ratio < a.MinHealthyMotionRatio)
                    throw new BotGateAbortException($"SmartTraverse motion quality {healthy}/{parsed} healthy probes ({ratio:P0}) was below the required {a.MinHealthyMotionRatio:P0}.");
            }
        }
        finally
        {
            Forward(false);
            if (_pad is { Connected: true }) { try { _pad.RightStick(0, 0); _pad.LeftStick(0, 0); } catch { } }
            if (rec is not null)
            {
                int segMs = (int)(DateTime.UtcNow - segStart).TotalMilliseconds;
                if (segMs > 0) rec.Steps.Add(RouteStep.Fwd(segMs));   // the trailing forward segment
                try { rec.Save(a.RecordRoutePath!); _log.Info("Bot", $"SmartTraverse: recorded {rec.Steps.Count} route step(s) → {a.RecordRoutePath} (replay deterministically with BotAction.Replay)."); }
                catch (Exception ex) { _log.Warn("Bot", $"SmartTraverse: route record write failed: {ex.Message}"); }
            }
            result.Traversals.Add(new BotTraversalStats
            {
                Type = nameof(BotActionType.SmartTraverse), DurationSec = (DateTime.UtcNow - started).TotalSeconds,
                MotionSensorActive = motion is not null, MotionProbes = parsed, HealthyMotionProbes = healthy,
                HealthyMotionRatio = parsed > 0 ? (double)healthy / parsed : null,
                MeanMotionScore = parsed > 0 ? motionSum / parsed : null,
                MinMotionScore = parsed > 0 ? motionMin : null, MaxMotionScore = parsed > 0 ? motionMax : null,
                RecoveryTurns = turns
            });
            _log.Info("Bot", $"SmartTraverse done — {probes} probe attempt(s), {parsed} parsed, {healthy} healthy ({(parsed > 0 ? (double)healthy / parsed : 0):P0}), {turns} unstick-turn(s).");
        }
    }

    /// <summary>
    /// IN-WORLD PROOF gate (frame-rate-independent menu rejection): hold the character's FORWARD movement and
    /// sample capture-card motion (ffmpeg scene-change, CPU-side — same sensor as <see cref="SmartTraverseAsync"/>)
    /// until one probe reaches <see cref="BotAction.StuckThreshold"/> or DurationMs elapses. Genuinely running
    /// in-world translates the ENTIRE camera view (motion well above the SmartTraverse stuck floor of ~1.0), while
    /// the same stick input on a MENU leaves the background static (motion ~0) — so this discriminates in-world
    /// from menu regardless of fps, which the WaitForGameplayBand ceiling cannot once frame generation lifts
    /// in-world presents past the menu's rate (BMW MFG 3X/4X in-world ~270-360 fps vs menu ~250 — the ceiling
    /// that caught the eaten-triple-A menu false-pass stops working there). Required ⇒ ABORT (run rejected):
    /// the supposed in-world state was actually a menu/static screen. The forward hold doubles as the start of
    /// movement (place it as a route leg — e.g. the first warm-up forward — so it costs no extra position drift).
    /// Degrades to a bounded blind forward hold when no motion sensor is wired (dry-run) so it never wedges.
    /// </summary>
    private async Task AssertWorldMotionAsync(BotAction a, CancellationToken ct)
    {
        int totalMs = a.DurationMs > 0 ? a.DurationMs : 6000;
        if (FastForward && !Inject) totalMs = Math.Min(totalMs, 250);
        double thresh = a.StuckThreshold > 0 ? a.StuckThreshold : 1.0;
        var motion = Vision?.Grabber;
        if (motion is null)
        {
            _log.Trace("Bot", $"AssertWorldMotion: no motion sensor (dry-run) — blind forward {Math.Min(totalMs, 1500)}ms.");
            Forward(true);
            try { await Delay(Math.Min(totalMs, 1500), ct).ConfigureAwait(false); } finally { Forward(false); }
            return;
        }
        _log.Info("Bot", $"AssertWorldMotion: forward-hold + motion probes for up to {totalMs}ms — require one probe ≥ {thresh:0.00} (in-world camera translation; a menu stays static).{(a.Note is null ? "" : " — " + a.Note)}");
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(totalMs);
        double best = 0; int probes = 0;
        Forward(true);
        try
        {
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                double m = await motion.SampleMotionAsync(8, ct: ct).ConfigureAwait(false);
                probes++;
                GpuSuite.Core.RunHeartbeat.Ping();   // perception + movement = real progress for the hang-watchdog
                if (m > best) best = m;
                if (m >= thresh)
                {
                    _log.Info("Bot", $"AssertWorldMotion PASSED — motion {m:0.00} ≥ {thresh:0.00} on probe {probes} (genuinely in-world; camera is translating).");
                    return;
                }
                _log.Trace("Bot", $"AssertWorldMotion: probe {probes} motion {m:0.00} < {thresh:0.00} — still static (menu?), continuing.");
            }
        }
        finally
        {
            Forward(false);
            if (_pad is { Connected: true }) { try { _pad.LeftStick(0, 0); } catch { } }
        }
        if (a.Required)
        {
            _log.Error("Bot", $"AssertWorldMotion FAILED after {probes} probe(s) — best motion {best:0.00} < {thresh:0.00}: forward input produced NO camera translation, so this is a MENU/static screen, not in-world gameplay — ABORTING (run rejected rather than measure the menu).");
            throw new BotGateAbortException($"world-motion assert failed (best {best:0.00} < {thresh:0.00} — menu/static screen)");
        }
        _log.Warn("Bot", $"AssertWorldMotion best motion {best:0.00} < {thresh:0.00} after {probes} probe(s) — proceeding best-effort (not marked required).");
    }

    /// <summary>
    /// Deterministically REPLAY a recorded smart route (forward-holds + unstick-turns) — the REPRODUCIBLE half of
    /// discover-then-replay. No motion sensor, no GX10: the movement is FIXED, so the measured window is repeatable
    /// run-to-run while still tracing the smart (open-space, non-wall-stuck) path a SmartTraverse discovery pass found.
    /// Degrades to a warning + no-op when the route file is missing/empty (a dry-run, or a not-yet-recorded route) so
    /// it can never wedge a run. Device follows the live script (pad left-stick / W key + right-stick / mouse).
    /// </summary>
    private async Task ReplayRouteAsync(BotAction a, CancellationToken ct)
    {
        var route = a.RoutePath is not null ? RecordedRoute.Load(a.RoutePath) : null;
        if (route is null || route.Steps.Count == 0)
        {
            string message = $"ReplayRoute: no usable route at '{a.RoutePath}' (missing/empty)";
            if (a.Required)
            {
                _log.Error("Bot", message + " — required measured route cannot run; aborting to prevent a static false pass.");
                throw new BotGateAbortException($"required replay route missing or empty ('{a.RoutePath}')");
            }
            _log.Warn("Bot", message + " — skipping. Record one with a SmartTraverse discovery pass (recordRoutePath).");
            return;
        }
        await ReplayRouteAsync(route, ct).ConfigureAwait(false);
    }

    /// <summary>Replay an in-memory <see cref="RecordedRoute"/> — shared by the file-path action handler and the
    /// offline <c>selftest-route</c> (which drives a dry, non-injecting engine to assert the sequence executes).</summary>
    public async Task ReplayRouteAsync(RecordedRoute route, CancellationToken ct)
    {
        string dev = PadInject ? "pad" : (Inject ? "keyboard" : "dry");
        _log.Info("Bot", $"ReplayRoute ({dev}) — {route.Steps.Count} step(s), ~{route.TotalMs}ms deterministic{(string.IsNullOrWhiteSpace(route.Note) ? "" : " — " + route.Note)}.");
        int moves = 0, trn = 0;
        try
        {
            foreach (var s in route.Steps)
            {
                if (ct.IsCancellationRequested) break;
                switch (s.Kind)
                {
                    case RouteStepKind.Forward:     await HoldMoveAsync(0, 1, s.DurationMs, ct).ConfigureAwait(false); moves++; break;
                    case RouteStepKind.Back:        await HoldMoveAsync(0, -1, s.DurationMs, ct).ConfigureAwait(false); moves++; break;
                    case RouteStepKind.StrafeLeft:  await HoldMoveAsync(-1, 0, s.DurationMs, ct).ConfigureAwait(false); moves++; break;
                    case RouteStepKind.StrafeRight: await HoldMoveAsync(1, 0, s.DurationMs, ct).ConfigureAwait(false); moves++; break;
                    case RouteStepKind.Turn:        await TurnAsync(s.Deg, ct).ConfigureAwait(false); trn++; break;
                    case RouteStepKind.Jump:        await JumpAsync(ct).ConfigureAwait(false); moves++; break;
                    case RouteStepKind.Wait:        await Delay(s.DurationMs, ct).ConfigureAwait(false); break;
                }
                GpuSuite.Core.RunHeartbeat.Ping();   // real movement = progress for the hang-watchdog
            }
        }
        finally
        {
            StopMovement();
            _log.Info("Bot", $"ReplayRoute done — {moves} move(s), {trn} turn(s).");
        }
    }

    /// <summary>Hold a directional MOVE (x = strafe ±1, y = forward/back ±1) for <paramref name="ms"/> then release —
    /// pad left-stick, or W/A/S/D for keyboard, or just a dwell in dry-run. Shared by ReplayRoute + the GX10 driver.</summary>
    private async Task HoldMoveAsync(double x, double y, int ms, CancellationToken ct)
    {
        if (PadInject)
        {
            _pad!.LeftStick(x, y);
            try { await Delay(ms, ct).ConfigureAwait(false); } finally { _pad!.LeftStick(0, 0); }
        }
        else if (Inject)
        {
            var keys = MoveKeys(x, y);
            foreach (var k in keys) { KeyDown(k, false); _held.Add(k); }
            try { await Delay(ms, ct).ConfigureAwait(false); }
            finally { foreach (var k in keys) { KeyUp(k, false); _held.Remove(k); } }
        }
        else await Delay(ms, ct).ConfigureAwait(false);
    }

    /// <summary>Map a strafe/forward vector to the held WASD keys.</summary>
    private static List<string> MoveKeys(double x, double y)
    {
        var keys = new List<string>();
        if (y > 0.5) keys.Add("W"); else if (y < -0.5) keys.Add("S");
        if (x > 0.5) keys.Add("D"); else if (x < -0.5) keys.Add("A");
        return keys;
    }

    /// <summary>A brief jump (pad A / Space tap).</summary>
    private async Task JumpAsync(CancellationToken ct)
    {
        if (PadInject) { _pad!.Button("A", true); await Delay(80, ct).ConfigureAwait(false); _pad!.Button("A", false); }
        else if (Inject) { KeyDown("Space", false); await Delay(80, ct).ConfigureAwait(false); KeyUp("Space", false); }
        else await Delay(80, ct).ConfigureAwait(false);
    }

    /// <summary>Release ALL movement inputs (forward/strafe keys + both sticks) — the teardown for the move drivers.</summary>
    private void StopMovement()
    {
        Forward(false);
        foreach (var k in _translationKeys.Concat(new[] { "W", "A", "S", "D" }).Distinct().ToList())
            if (_held.Contains(k)) { try { KeyUp(k, false); } catch { } _held.Remove(k); }
        _translationKeys.Clear();
        if (_pad is { Connected: true }) { try { _pad.LeftStick(0, 0); _pad.RightStick(0, 0); } catch { } }
    }

    /// <summary>
    /// TIER-1 GX10-driven in-world traversal: for DurationMs, ask the <see cref="Gx10Navigator"/> (capture-card frame →
    /// GX10 vision, OFF-BENCH) for the next MOVEMENT and execute it (forward/back/strafe/turn/jump), re-deciding every
    /// chunk. Optionally RECORDS the driven route (<see cref="BotAction.RecordRoutePath"/>) so a later ReplayRoute can
    /// reproduce it deterministically — the DISCOVERY half of discover-then-replay. When no safe remote navigator is
    /// wired, execution falls back to SmartTraverse so capture-card motion sensing still prevents a blind wall-grind.
    /// </summary>
    private async Task Gx10TraverseAsync(BotAction a, BotRunResult result, CancellationToken ct)
    {
        int totalMs = a.DurationMs > 0 ? a.DurationMs : 40_000;
        if (FastForward && !Inject) totalMs = Math.Min(totalMs, 250);
        string goal = !string.IsNullOrWhiteSpace(a.Goal) ? a.Goal! : "explore the open area ahead; keep moving through traversable space; do not grind against walls";
        var nav = Gx10Navigator;
        // A missing/unreachable remote vision backend must retain the Tier-0 motion sensor. The old path claimed it
        // was falling back to SmartTraverse but actually held FORWARD blindly, recreating the 1-2 metre wall-grind.
        if (nav is null)
        {
            _log.Warn("Bot", "Gx10Traverse: remote/off-bench in-world vision unavailable — using bounded SmartTraverse motion fallback.");
            await SmartTraverseAsync(a, result, ct).ConfigureAwait(false);
            return;
        }
        if (a.StuckThreshold > 0) nav.StuckThreshold = a.StuckThreshold;
        if (a.StuckTriggerCount > 0) nav.StuckTriggerCount = a.StuckTriggerCount;
        string dev = PadInject ? "pad" : (Inject ? "keyboard" : "dry");
        var rec = a.RecordRoutePath is not null && !(FastForward && !Inject)
            ? new RecordedRoute { Note = string.IsNullOrWhiteSpace(a.Note) ? "gx10 in-world discovery" : a.Note!, RecordedDevice = dev }
            : null;
        _log.Info("Bot", $"Gx10Traverse {totalMs}ms ({dev}) — navigator ON ({nav.Model} @ {nav.Endpoint}){(rec is null ? "" : " [recording → " + a.RecordRoutePath + "]")}; CONTINUOUS hold (translation persists while re-deciding; turns overlay). goal: {goal}");
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(totalMs);
        using var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        phaseCts.CancelAfter(totalMs);
        var phaseCt = phaseCts.Token; // hard action deadline: a slow remote decision may not extend MarkEnd
        int decided = 0, blind = 0, turns = 0, jumps = 0, motionProbes = 0, healthyMotion = 0;
        double motionSum = 0, motionMin = double.MaxValue, motionMax = double.MinValue;
        var started = DateTime.UtcNow;
        void RecordMotion(double score)
        {
            if (score < 0) return;
            motionProbes++; motionSum += score; motionMin = Math.Min(motionMin, score); motionMax = Math.Max(motionMax, score);
            if (score >= nav.StuckThreshold) healthyMotion++;
        }

        static bool IsTranslation(MoveAction m) => m is MoveAction.Forward or MoveAction.Back or MoveAction.StrafeLeft or MoveAction.StrafeRight;
        static (double x, double y) Vec(MoveAction m) => m switch
        {
            MoveAction.Forward => (0, 1), MoveAction.Back => (0, -1),
            MoveAction.StrafeLeft => (-1, 0), MoveAction.StrafeRight => (1, 0), _ => (0, 0)
        };
        static RouteStep TransStep(MoveAction m, int ms) => m switch
        {
            MoveAction.Back => RouteStep.BackStep(ms), MoveAction.StrafeLeft => RouteStep.StrafeL(ms),
            MoveAction.StrafeRight => RouteStep.StrafeR(ms), MoveAction.Forward => RouteStep.Fwd(ms), _ => RouteStep.Dwell(ms)
        };

        // Start moving forward immediately so the character is NEVER idle while the (slow) box decides.
        MoveAction held = MoveAction.Forward;
        var heldStart = DateTime.UtcNow;
        SetTranslation(0, 1);
        void CommitHeld()
        {
            int ms = (int)(DateTime.UtcNow - heldStart).TotalMilliseconds;
            if (rec is not null && ms > 0) rec.Steps.Add(TransStep(held, ms));
            heldStart = DateTime.UtcNow;
        }

        // CPU frame-diff STUCK-REFLEX: while a translation is held, periodically sample capture-card motion
        // (ffmpeg scdet, OFF-BENCH) — a run of low scores means the character is wall-grinding (the "ran into
        // debris and kept pressing FORWARD" failure), so FORCE a committed escape-turn instead of trusting the
        // single-frame model, and tell the model it was stuck on the next look. Disabled when threshold ≤ 0.
        var reflex = (nav is not null && nav.StuckThreshold > 0) ? new StuckReflex(nav.StuckThreshold, nav.StuckTriggerCount) : null;
        int checkMs = nav?.StuckCheckMs ?? 3500;
        var lastMotionCheck = DateTime.UtcNow;
        string? pendingHint = null;
        int forced = 0;
        if (reflex is not null) _log.Info("Bot", $"Gx10Traverse stuck-reflex ARMED — motion thr {nav!.StuckThreshold:0.00}, {nav!.StuckTriggerCount} low sample(s) → forced turn; sampled ≤ every {checkMs}ms (off-bench).");

        async Task EscapeAsync(StuckVerdict escape)
        {
            // The remote model can take 12-20s per decision. Once the independent motion probe sees a jam,
            // do not wait for that response: cancel/discard it, escape locally, and re-look from the new heading.
            for (int escapeAttempt = 0; escape.ForceTurn && escapeAttempt < 3 && DateTime.UtcNow < deadline; escapeAttempt++)
            {
                if (a.MaxRecoveryTurns > 0 && turns >= a.MaxRecoveryTurns)
                    throw new BotGateAbortException($"Gx10Traverse abandoned after {a.MaxRecoveryTurns} total turns; the route kept returning to low motion.");
                CommitHeld();
                SetTranslation(0, 0);
                int escapeMs = escapeAttempt == 2 ? 1100 : 800;
                if (escapeAttempt == 1)
                {
                    // A side-step breaks narrow furniture/door-frame traps where reversing alone remains
                    // aligned with the obstacle. Strafe opposite the planned pivot.
                    double strafe = escape.TurnDeg >= 0 ? -1 : 1;
                    SetTranslation(strafe, 0);
                    rec?.Steps.Add(strafe < 0 ? RouteStep.StrafeL(escapeMs) : RouteStep.StrafeR(escapeMs));
                }
                else
                {
                    SetTranslation(0, -1); // back away from the contacted wall before rotating
                    rec?.Steps.Add(RouteStep.BackStep(escapeMs));
                }
                try { await DelayHoldingForeground(escapeMs, phaseCt).ConfigureAwait(false); }
                finally { SetTranslation(0, 0); }
                await TurnAsync(escape.TurnDeg, phaseCt).ConfigureAwait(false);
                rec?.Steps.Add(RouteStep.TurnBy(escape.TurnDeg));
                held = MoveAction.Forward; SetTranslation(0, 1); heldStart = DateTime.UtcNow;
                forced++; turns++;
                pendingHint = escape.Hint;
                _log.Info("Bot", $"Gx10Traverse: STUCK (wall-grind) → forced {escape.TurnDeg}° escape turn {escapeAttempt + 1}/3; re-probing the new heading.");

                double postTurnMotion = await nav.SampleMotionAsync(phaseCt).ConfigureAwait(false);
                RecordMotion(postTurnMotion);
                escape = reflex!.Observe(true, postTurnMotion);
                _log.Trace("Bot", $"Gx10Traverse post-turn sensor: motion {postTurnMotion:0.00} (thr {nav.StuckThreshold:0.00}){(escape.ForceTurn ? " → still stuck" : " → translating") }.");
                if (!escape.ForceTurn) pendingHint = null;
            }
            lastMotionCheck = DateTime.UtcNow;
        }
        try
        {
            while (DateTime.UtcNow < deadline && !phaseCt.IsCancellationRequested)
            {
                // Keep the collision sensor running WHILE the remote model thinks. The former sequential loop
                // blocked here for 12-20s per AI decision, so a 35s action could finish with 0/0 or 1/1 probes
                // and falsely fail despite continuous movement. Sampling concurrently also reacts to a wall
                // within checkMs instead of letting the bot grind forward until the next model response.
                using var decisionCts = CancellationTokenSource.CreateLinkedTokenSource(phaseCt);
                Task<MoveStep?> decisionTask = nav!.NextMoveAsync(goal, pendingHint, decisionCts.Token);
                pendingHint = null;
                StuckVerdict pendingEscape = StuckVerdict.None;
                while (!decisionTask.IsCompleted && DateTime.UtcNow < deadline && !phaseCt.IsCancellationRequested)
                {
                    int dueMs = Math.Max(0, checkMs - (int)(DateTime.UtcNow - lastMotionCheck).TotalMilliseconds);
                    if (dueMs > 0)
                    {
                        Task completed = await Task.WhenAny(decisionTask, Task.Delay(dueMs, phaseCt)).ConfigureAwait(false);
                        if (completed == decisionTask) break;
                    }
                    if (reflex is null || !IsTranslation(held))
                    {
                        lastMotionCheck = DateTime.UtcNow;
                        continue;
                    }
                    lastMotionCheck = DateTime.UtcNow;
                    double motion = await nav.SampleMotionAsync(phaseCt).ConfigureAwait(false);
                    RecordMotion(motion);
                    var v = reflex.Observe(true, motion);
                    _log.Trace("Bot", $"Gx10Traverse stuck-sensor: motion {motion:0.00} (thr {nav.StuckThreshold:0.00}), lowStreak {reflex.LowStreak}{(v.ForceTurn ? $" → FORCE TURN {v.TurnDeg}°" : "")}");
                    GpuSuite.Core.RunHeartbeat.Ping();
                    if (v.ForceTurn)
                    {
                        pendingEscape = v;
                        decisionCts.Cancel();
                        try { await decisionTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
                        break;
                    }
                }
                if (pendingEscape.ForceTurn)
                {
                    await EscapeAsync(pendingEscape).ConfigureAwait(false);
                    continue; // discard the stale AI answer and re-look after the escape turn
                }
                MoveStep? step = await decisionTask.ConfigureAwait(false);
                GpuSuite.Core.RunHeartbeat.Ping();   // perception + (continuing) movement = real progress for the watchdog
                if (step is null) { blind++; continue; }   // keep HOLDING the current move while the remote response is unavailable
                if (step.Done) { _log.Info("Bot", "Gx10Traverse: navigator reported DONE — goal reached."); break; }
                decided++;
                var act = step.Action ?? MoveAction.Forward;
                if (act is MoveAction.TurnLeft or MoveAction.TurnRight)
                {
                    if (a.MaxRecoveryTurns > 0 && turns >= a.MaxRecoveryTurns)
                        throw new BotGateAbortException($"Gx10Traverse abandoned after {a.MaxRecoveryTurns} total turns; useful forward traversal was not sustained.");
                    int deg = act == MoveAction.TurnRight ? 60 : -60;   // brief RIGHT-stick sweep; LEFT-stick translation keeps moving
                    await TurnAsync(deg, phaseCt).ConfigureAwait(false); turns++;
                    rec?.Steps.Add(RouteStep.TurnBy(deg));
                    reflex?.NotePivot();                                 // the model's own turn already breaks a jam — re-arm
                    lastMotionCheck = DateTime.UtcNow;                   // let the new heading settle before re-sampling
                }
                else if (act == MoveAction.Jump)
                {
                    await JumpAsync(phaseCt).ConfigureAwait(false); jumps++;
                    rec?.Steps.Add(RouteStep.JumpStep());
                }
                else if (act != held)   // a new translation (or Stop) — commit the held segment, switch, keep holding
                {
                    CommitHeld();
                    held = act;
                    if (IsTranslation(act)) { var (x, y) = Vec(act); SetTranslation(x, y); lastMotionCheck = DateTime.UtcNow; }
                    else SetTranslation(0, 0);   // Stop / Wait → idle until the next decision
                }
                // else: same translation already held → do nothing (continuous movement, no re-issue)
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && phaseCts.IsCancellationRequested)
        {
            _log.Trace("Bot", $"Gx10Traverse action deadline reached at {totalMs}ms — cancelling the in-flight remote decision and closing the measured window on time.");
        }
        finally
        {
            CommitHeld();
            StopMovement();
            if (rec is not null)
            {
                try { rec.Save(a.RecordRoutePath!); _log.Info("Bot", $"Gx10Traverse: recorded {rec.Steps.Count} route step(s) → {a.RecordRoutePath} (replay deterministically with BotAction.Replay)."); }
                catch (Exception ex) { _log.Warn("Bot", $"Gx10Traverse: route record write failed: {ex.Message}"); }
            }
            result.Traversals.Add(new BotTraversalStats
            {
                Type = nameof(BotActionType.Gx10Traverse), DurationSec = (DateTime.UtcNow - started).TotalSeconds,
                MotionSensorActive = reflex is not null, MotionProbes = motionProbes, HealthyMotionProbes = healthyMotion,
                HealthyMotionRatio = motionProbes > 0 ? (double)healthyMotion / motionProbes : null,
                MeanMotionScore = motionProbes > 0 ? motionSum / motionProbes : null,
                MinMotionScore = motionProbes > 0 ? motionMin : null, MaxMotionScore = motionProbes > 0 ? motionMax : null,
                RecoveryTurns = turns, ForcedRecoveryTurns = forced, Decisions = decided, BlindHolds = blind
            });
            _log.Info("Bot", $"Gx10Traverse done — {decided} decision(s), {turns} turn(s) ({forced} forced by stuck-reflex), {jumps} jump(s), {blind} blind hold(s), motion {healthyMotion}/{motionProbes} healthy.");
        }
        // This must run AFTER the deadline catch/finally. A normal 35s action usually ends by cancelling an
        // in-flight remote decision; checking inside the try let that normal OCE skip the quality gate and
        // falsely accept 0/N-motion routes (live AW2 2026-07-15).
        if (reflex is not null && a.MinHealthyMotionRatio > 0)
        {
            double ratio = motionProbes > 0 ? (double)healthyMotion / motionProbes : 0;
            if (motionProbes < 2)
                throw new BotGateAbortException($"Gx10Traverse motion proof was incomplete: {motionProbes}/2 required probe(s) completed ({healthyMotion} healthy). The route is not accepted without enough independent motion samples.");
            if (ratio < a.MinHealthyMotionRatio)
                throw new BotGateAbortException($"Gx10Traverse motion quality {healthyMotion}/{motionProbes} healthy probes ({ratio:P0}) was below the required {a.MinHealthyMotionRatio:P0}.");
        }
    }

    /// <summary>Set a PERSISTENT translation (held until changed): pad left-stick, or held W/A/S/D for keyboard. Unlike
    /// <see cref="HoldMoveAsync"/> (which auto-releases) this keeps the character moving across the navigator's decision
    /// latency, so smart-live driving translates CONTINUOUSLY instead of in idle-separated bursts. (0,0) = idle.</summary>
    private void SetTranslation(double x, double y)
    {
        if (PadInject) { _pad!.LeftStick(x, y); return; }
        if (!Inject) return;
        var want = MoveKeys(x, y);
        foreach (var k in _translationKeys.Where(k => !want.Contains(k)).ToList()) { try { KeyUp(k, false); } catch { } _held.Remove(k); }
        foreach (var k in want.Where(k => !_translationKeys.Contains(k))) { KeyDown(k, false); _held.Add(k); }
        _translationKeys = want;
    }

    /// <summary>Hold/release "run forward" on whichever device is live (pad left-stick up, or the W key).</summary>
    private void Forward(bool on)
    {
        if (PadInject) { _pad!.LeftStick(0, on ? 1.0 : 0.0); }
        else if (Inject)
        {
            if (on) { KeyDown("W", false); _held.Add("W"); }
            else { KeyUp("W", false); _held.Remove("W"); }
        }
    }

    /// <summary>Turn the camera/character ~<paramref name="deg"/>° (sign = direction): pad right-stick sweep, or a
    /// relative mouse nudge for keyboard games. Rough by design (calibratable) — it only needs to break a wall-jam.</summary>
    private async Task TurnAsync(int deg, CancellationToken ct)
    {
        int ms = Math.Clamp(Math.Abs(deg) * 7, 150, 900);
        double sign = deg >= 0 ? 1.0 : -1.0;
        if (PadInject) { _pad!.RightStick(sign * 0.8, 0); try { await Delay(ms, ct).ConfigureAwait(false); } finally { _pad!.RightStick(0, 0); } }
        else if (Inject) { MouseMoveRelative((int)(sign * Math.Abs(deg) * 12), 0); await Delay(Math.Min(ms, 300), ct).ConfigureAwait(false); }
        else await Delay(ms, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Vision-gated wait: poll the capture-card OCR until <paramref name="a"/>.Text appears (or, with
    /// WaitForAbsent, disappears) or the timeout (DurationMs) elapses. This replaces a fragile blind
    /// fixed wait for transitions whose duration we can't predict — chiefly a cold-launch title screen
    /// behind a multi-minute shader pre-compile. Best-effort: on timeout it logs a warning and PROCEEDS
    /// (a single missed OCR read shouldn't kill a run whose screen is actually correct; a genuinely wrong
    /// screen then fails validation and the orchestrator auto-repeats). Degrades to a bounded blind wait
    /// when no OCR is wired (dry-run, or no capture device).
    /// </summary>
    private async Task WaitForTextAsync(BotAction a, CancellationToken ct)
    {
        int timeoutMs = a.DurationMs > 0 ? a.DurationMs : 60_000;
        int pollMs = a.PollMs > 0 ? a.PollMs : 1200;
        string? text = a.Text;
        string[] anchors = new[] { text }.Concat(a.TextAlternatives.Cast<string?>())
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => candidate!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string target = string.Join(" | ", anchors);
        string label = a.Note is null ? "" : $" — {a.Note}";

        // No OCR available (dry-run, or capture device not wired) ⇒ fall back to a blind wait. In dry-run
        // FastForward caps this at 25ms; on a real run with no vision it preserves "wait long enough".
        if (!Inject || Vision is null || anchors.Length == 0)
        {
            if (Inject && Vision is null && anchors.Length > 0)
                _log.Warn("Bot", $"WaitForText '{target}' but no capture-card OCR wired — blind-waiting {timeoutMs}ms{label}.");
            await Delay(timeoutMs, ct).ConfigureAwait(false);
            return;
        }

        string mode = a.WaitForAbsent ? "absent" : "present";
        int needConsec = Math.Max(1, a.MinConsecutive);
        string consecNote = needConsec > 1 ? $", x{needConsec} consecutive" : "";
        _log.Info("Bot", $"WaitForText: '{target}' ({mode}, timeout {timeoutMs / 1000.0:0.#}s, poll {pollMs}ms{consecNote}){label}");
        var start = DateTime.UtcNow;
        var deadline = start + TimeSpan.FromMilliseconds(timeoutMs);
        int reads = 0;
        int consec = 0;   // consecutive satisfying reads (for MinConsecutive-hardened gates)
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            GpuSuite.Core.RunHeartbeat.Ping();   // an actively-polling gate IS progress (2026-07-07: with PresentMon
                                                 // 0-framing there are no frame pings, and a CPU-bound load has no
                                                 // GPU-busy pings — the 60s watchdog killed HEALTHY loading games
                                                 // mid-gate; the gate's own bounded timeout still catches real hangs).
            ThrowIfTargetDead("WaitForText");    // ...but a DEAD game must not be pinged at until the timeout
            EnsureGameForeground();   // CRITICAL: bring the game to the captured display BEFORE each OCR grab.
                                      // If the game is launched unfocused (its window doesn't exist at bot start,
                                      // so the one-shot focus at RunAsync is a no-op), the capture card shows the
                                      // DESKTOP and the OCR FALSE-MATCHES unrelated on-screen text — diagnosed
                                      // 2026-06-25 on F1 25 (the OCR matched menu-anchor words off other windows).
            OcrFrame? frame = null;
            if (IsTargetForeground())   // only trust the capture card when the game is actually showing
            {
                try { frame = GuardOcr(await Vision.ReadAsync(ct, warmupFrames: 6).ConfigureAwait(false)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.Trace("Bot", $"WaitForText OCR read error ({ex.GetType().Name}: {ex.Message})."); }
            }
            else
            {
                if (reads % 10 == 0) _log.Trace("Bot", "WaitForText: game not yet foreground on the captured display — skipping OCR (capture shows the desktop), still waiting.");
                GpuSuite.Core.RunHeartbeat.Ping();   // actively EnsureGameForeground-polling for a slow/cold launch IS progress — let THIS loop's own deadline bound it, not the 60s hang-watchdog (fixed F1's ~90s EA-AntiCheat cold launch being false-killed as a 'crash' 2026-06-30).
            }
            reads++;
            if (frame is not null)
            {
                GpuSuite.Core.RunHeartbeat.Ping();   // game is foreground and rendering on the captured display = real nav progress
                bool present = anchors.Any(anchor =>
                    (a.WholeWord ? frame.FindWord(anchor) : frame.Find(anchor)) is not null);
                bool satisfied = a.WaitForAbsent ? !present : present;
                // Require needConsec CONSECUTIVE satisfying reads so a single transient OCR miss can't satisfy an
                // absence gate prematurely (the Ratchet RESUME-absent 1-read false-pass). A non-satisfying read
                // breaks the streak; a null/unreadable frame (below) also resets it.
                consec = satisfied ? consec + 1 : 0;
                if (consec >= needConsec)
                {
                    _log.Info("Bot", $"WaitForText satisfied — '{target}' {mode} after {(DateTime.UtcNow - start).TotalSeconds:0.0}s ({reads} read(s){(needConsec > 1 ? $", {needConsec} consecutive" : "")}).");
                    return;
                }
            }
            else consec = 0;   // unreadable frame — can't confirm the condition, restart the streak
            await Delay(pollMs, ct).ConfigureAwait(false);
        }
        if (a.Required)
        {
            _log.Error("Bot", $"WaitForText TIMEOUT after {(DateTime.UtcNow - start).TotalSeconds:0.0}s ({reads} read(s)) waiting for REQUIRED '{target}' {mode}{label} — ABORTING (the bot never reached this screen; run rejected to avoid a false menu-measured pass).");
            throw new BotGateAbortException($"required gate '{target}' ({mode}) not reached");
        }
        _log.Warn("Bot", $"WaitForText TIMEOUT after {(DateTime.UtcNow - start).TotalSeconds:0.0}s ({reads} read(s)) waiting for '{target}' {mode}{label} — proceeding best-effort.");
    }

    /// <summary>
    /// Tap <paramref name="a"/>.Key ONCE, but ONLY if <paramref name="a"/>.Text is currently on the capture
    /// card — the CONDITIONAL dismiss for a screen that exists in one run shape but not another. Born
    /// 2026-07-05 (DOOM runs 21/23/24): the splash-dismiss Space was UNCONDITIONAL, and on a menu-apply run
    /// (no splash — the applier's open already dismissed it) that Space SELECTED the focused main-menu row
    /// (runs 21/23: opened Settings; run 24, post focus-restore: opened Campaign's difficulty screen), so the
    /// whole calibrated walk fired into the wrong menu. On a cold boot the splash IS up, the text matches and
    /// the tap fires exactly as before. Null/unreadable frames NEVER tap (the PressUntilText rule: an unknown
    /// screen must not be pressed at) — retried a few times, then skipped with a warning; a stuck splash then
    /// fails visibly at the next gate instead of silently desyncing. Dry-run (no OCR): logs and skips.
    /// </summary>
    private async Task TapIfTextAsync(BotAction a, CancellationToken ct)
    {
        string? key = a.Key, text = a.Text;
        string label = a.Note is null ? "" : $" — {a.Note}";
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(text))
        {
            _log.Warn("Bot", "TapIfText: missing Key or Text — skipping.");
            return;
        }
        if (!Inject || Vision is null)
        {
            _log.Info("Bot", $"TapIfText '{key}' if '{text}': no OCR wired (dry-run or no capture) — skipping (conditional taps never fire blind).");
            return;
        }

        const int tries = 4;
        for (int i = 0; i < tries && !ct.IsCancellationRequested; i++)
        {
            EnsureGameForeground();   // bring the game to the captured display before the OCR grab (see WaitForText).
            OcrFrame? frame = null;
            if (IsTargetForeground())
            {
                try { frame = GuardOcr(await Vision.ReadAsync(ct, warmupFrames: 6).ConfigureAwait(false)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.Trace("Bot", $"TapIfText OCR read error ({ex.GetType().Name}: {ex.Message})."); }
            }
            GpuSuite.Core.RunHeartbeat.Ping();
            if (frame is not null)
            {
                bool present = (a.WholeWord ? frame.FindWord(text) : frame.Find(text)) is not null;
                if (present)
                {
                    _log.Info("Bot", $"TapIfText: '{text}' visible → tapping '{key}'{label}.");
                    await PressTapAsync(key, a.DurationMs > 0 ? a.DurationMs : 130, ct, a.Vk).ConfigureAwait(false);
                }
                else
                {
                    _log.Info("Bot", $"TapIfText: '{text}' NOT on screen → skipping '{key}'{label}.");
                }
                return;
            }
            await Delay(1000, ct).ConfigureAwait(false);
        }
        _log.Warn("Bot", $"TapIfText: no readable frame after {tries} read(s) — NOT tapping '{key}' (unknown screen must not be pressed at){label}.");
    }

    /// <summary>
    /// Press <paramref name="a"/>.Key repeatedly until <paramref name="a"/>.Text appears on the capture card
    /// (or the timeout elapses). The robust "advance past a screen whose input-readiness we can't predict"
    /// primitive: a freshly-arrived title still streaming/loading renders its prompt seconds before it accepts
    /// input, and a controller-detection tap can be silently eaten — a single blind press then misses and the
    /// rest of the macro runs on the wrong screen. CHECKS BEFORE each press (so it won't over-press once the
    /// target screen is up), then taps Key (pad or keyboard) and waits PollMs for the transition. Best-effort:
    /// proceeds on timeout. Degrades to a single press + bounded blind wait when no OCR is wired (dry-run).
    /// </summary>
    private async Task PressUntilTextAsync(BotAction a, CancellationToken ct)
    {
        int timeoutMs = a.DurationMs > 0 ? a.DurationMs : 90_000;
        int pollMs = a.PollMs > 0 ? a.PollMs : 2500;
        string? key = a.Key, text = a.Text;
        string label = a.Note is null ? "" : $" — {a.Note}";

        if (!Inject || Vision is null || string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(key))
        {
            if (Inject && !string.IsNullOrWhiteSpace(key)) await PressTapAsync(key, 130, ct, a.Vk).ConfigureAwait(false);
            await Delay(Math.Min(timeoutMs, 3000), ct).ConfigureAwait(false);
            return;
        }

        _log.Info("Bot", $"PressUntilText: press '{key}' until '{text}' (timeout {timeoutMs / 1000.0:0.#}s, poll {pollMs}ms){label}");
        var start = DateTime.UtcNow;
        var deadline = start + TimeSpan.FromMilliseconds(timeoutMs);
        int presses = 0, reads = 0, satisfiedStreak = 0;
        int requiredStreak = Math.Max(1, a.MinConsecutive);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            GpuSuite.Core.RunHeartbeat.Ping();   // gate-poll = progress; see WaitForText (watchdog false-kill fix 2026-07-07).
            ThrowIfTargetDead("PressUntilText"); // a dead game fails the gate NOW, not at the timeout
            EnsureGameForeground();   // bring the game to the captured display before each OCR grab (see WaitForText).
            OcrFrame? frame = null;
            if (IsTargetForeground())   // only trust the capture card when the game is actually showing; else the
            {                           // capture shows the desktop -> a null frame here means "don't press, re-poll".
                try { frame = GuardOcr(await Vision.ReadAsync(ct, warmupFrames: 6).ConfigureAwait(false)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.Trace("Bot", $"PressUntilText OCR error ({ex.GetType().Name}: {ex.Message})."); }
            }
            else
            {
                if (reads % 10 == 0) _log.Trace("Bot", "PressUntilText: game not yet foreground — skipping OCR (capture shows the desktop), not pressing.");
                GpuSuite.Core.RunHeartbeat.Ping();   // waiting for a slow/cold launch to foreground IS progress (see WaitForText) — don't let the 60s watchdog false-kill it.
            }
            reads++;
            // A NULL read means the OCR genuinely failed (capture-card hiccup / timeout) — the on-screen state
            // is UNKNOWN, so do NOT press (an extra confirm tap on an already-correct screen would select the
            // highlighted item and desync the macro). Re-poll instead. Only act on a real frame. (cf. WaitForText.)
            if (frame is null)
            {
                await Delay(pollMs, ct).ConfigureAwait(false);
                continue;
            }
            GpuSuite.Core.RunHeartbeat.Ping();   // foreground OCR grab succeeded = real nav progress (see WaitForText)
            string? terminalText = FindTerminalScreenText(a, frame);
            if (terminalText is not null)
            {
                string reason = $"fatal screen text '{terminalText}' detected while waiting for '{text}'";
                _log.Error("Bot", $"PressUntilText ABORT — {reason}{label}. This is an environment/session block, not a benchmark scene; no further '{key}' input will be injected.");
                throw new BotGateAbortException(reason);
            }
            bool present = (a.WholeWord ? frame.FindWord(text) : frame.Find(text)) is not null;
            bool satisfied = a.WaitForAbsent ? !present : present;
            if (satisfied)
            {
                if (AdvanceConsecutiveGate(true, requiredStreak, ref satisfiedStreak))
                {
                    _log.Info("Bot", $"PressUntilText satisfied — '{text}' {(a.WaitForAbsent ? "absent" : "present")} after {presses} press(es), {(DateTime.UtcNow - start).TotalSeconds:0.0}s ({reads} read(s), {satisfiedStreak} consecutive).");
                    return;
                }
                _log.Trace("Bot", $"PressUntilText: '{text}' {(a.WaitForAbsent ? "absent" : "present")} ({satisfiedStreak}/{requiredStreak} consecutive) — confirming without another press.");
                await Delay(pollMs, ct).ConfigureAwait(false);
                continue;
            }
            AdvanceConsecutiveGate(false, requiredStreak, ref satisfiedStreak);
            if (a.MaxPresses > 0 && presses >= a.MaxPresses)
            {
                string reason = $"press safety limit {a.MaxPresses} reached before '{text}' was {(a.WaitForAbsent ? "absent" : "present")}";
                if (a.Required)
                {
                    _log.Error("Bot", $"PressUntilText ABORT — {reason}{label}. No further '{key}' input will be injected on this unknown screen.");
                    throw new BotGateAbortException(reason);
                }
                _log.Warn("Bot", $"PressUntilText stopped — {reason}{label}. No further '{key}' input will be injected on this unknown screen.");
                return;
            }
            await PressTapAsync(key, 130, ct, a.Vk).ConfigureAwait(false);
            presses++;
            await Delay(pollMs, ct).ConfigureAwait(false);
        }
        if (a.Required)
        {
            _log.Error("Bot", $"PressUntilText TIMEOUT after {(DateTime.UtcNow - start).TotalSeconds:0.0}s ({presses} press(es)) waiting for REQUIRED '{text}'{label} — ABORTING (the bot never reached this screen; run rejected to avoid a false menu-measured pass).");
            throw new BotGateAbortException($"required gate '{text}' not reached after {presses} press(es)");
        }
        _log.Warn("Bot", $"PressUntilText TIMEOUT after {(DateTime.UtcNow - start).TotalSeconds:0.0}s ({presses} press(es)) waiting for '{text}'{label} — proceeding best-effort.");
    }

    private static string? FindTerminalScreenText(BotAction action, OcrFrame frame)
    {
        if (!string.IsNullOrWhiteSpace(action.AbortIfText) && frame.Find(action.AbortIfText) is not null)
            return action.AbortIfText;
        foreach (string text in action.AbortIfTexts.Where(text => !string.IsNullOrWhiteSpace(text)))
            if (frame.Find(text) is not null) return text;
        return null;
    }

    internal static bool AdvanceConsecutiveGate(bool satisfied, int required, ref int streak)
    {
        streak = satisfied ? streak + 1 : 0;
        return streak >= Math.Max(1, required);
    }

    /// <summary>
    /// Gameplay-band gate: sample the LIVE capture's running frame count over PollMs windows to derive the
    /// frame-rate, and return once it has settled inside the gameplay band for <see cref="BotAction.SustainMs"/>
    /// — AT/BELOW <see cref="BotAction.CeilingFps"/> and, when <see cref="BotAction.FloorFps"/> &gt; 0, AT/ABOVE
    /// it too. The one-sided ceiling proves a 60fps-capped LOAD/menu has given way to sub-ceiling gameplay
    /// (Ratchet); the optional floor additionally rejects a low-fps stalled/black LOAD screen that renders BELOW
    /// gameplay (Black Myth, whose ~450fps menu sits above the ceiling but whose load tip-screen fps is unknown).
    /// The following MarkStart then opens the measured window on actual gameplay no matter how long the load
    /// took, fixing the Ratchet "fixed 41s load-wait opened the window mid-load on slow loads → 49 band frames →
    /// Invalid" flake AND the Black Myth "OCR MarkStart-gate defeated by the RTSS OSD → window opened on the
    /// 450fps menu" false-pass. Immune to OCR misses and per-frame noise (it averages over each PollMs).
    /// Required ⇒ ABORT if the band is never reached (stuck on the load/menu) rather than measure the wrong
    /// state. Degrades to a bounded blind wait when no live frame source is wired (dry-run).
    /// </summary>
    private async Task WaitForGameplayBandAsync(BotAction a, CancellationToken ct)
    {
        int timeoutMs = a.DurationMs > 0 ? a.DurationMs : 120_000;
        int pollMs = a.PollMs > 0 ? a.PollMs : 1000;
        double ceiling = a.CeilingFps > 0 ? a.CeilingFps : 40;
        double floor = a.FloorFps > 0 ? a.FloorFps : 0;   // 0 = one-sided ceiling (no floor)
        int sustainMs = a.SustainMs > 0 ? a.SustainMs : 3000;
        string label = a.Note is null ? "" : $" — {a.Note}";
        string bandDesc = floor > 0 ? $"{floor:0}–{ceiling:0} fps" : $"≤ {ceiling:0} fps";

        if (LiveSampleCount is null)
        {
            _log.Trace("Bot", "WaitForGameplayBand: no live frame source (dry-run) — bounded blind wait.");
            await Delay(Math.Min(timeoutMs, 3000), ct).ConfigureAwait(false);
            return;
        }

        double bandFraction = a.BandFraction is > 0 and <= 1 ? a.BandFraction : 0.7;
        _log.Info("Bot", $"WaitForGameplayBand: wait until ≥{bandFraction * 100:0}% of a {sustainMs / 1000.0:0.#}s window is {bandDesc} (hitch-tolerant; timeout {timeoutMs / 1000.0:0.#}s){label}");
        var start = DateTime.UtcNow;
        var deadline = start + TimeSpan.FromMilliseconds(timeoutMs);
        // Hitch-tolerant settle: a severe-hitch game (Ratchet) injects 0-frame stalls + brief catch-up spikes into
        // otherwise-in-band gameplay; a strict continuous accumulator (reset on every hitch) never reaches the sustain
        // window — the 0/5 "band never settles". The tracker instead asks "was ≥bandFraction of the trailing window
        // in-band?": 0-frame polls are neutral (a hitch is still gameplay), a transient spike is outvoted, but a
        // SUSTAINED out-of-band load keeps the fraction low so the window still can't open mid-load. See the tracker.
        var tracker = new GameplayBandTracker(ceiling, floor, sustainMs, bandFraction);
        int lastCount = LiveSampleCount();
        var lastAt = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            GpuSuite.Core.RunHeartbeat.Ping();   // gate-poll = progress: a CPU-bound save-load/PSO-compile has no frame
                                                 // or GPU-busy pings — the 60s watchdog killed a healthy loading BMW
                                                 // mid-gate (2026-07-07 02:19). This gate's own required-timeout still
                                                 // aborts a truly dead game; the watchdog stays the orchestrator backstop.
            ThrowIfTargetDead("WaitForGameplayBand");   // a dead game fails the gate NOW, not at the timeout
            await Delay(pollMs, ct).ConfigureAwait(false);
            int nowCount = LiveSampleCount();
            int delta = nowCount - lastCount;
            double elapsedMs = (DateTime.UtcNow - lastAt).TotalMilliseconds;
            lastCount = nowCount;
            lastAt = DateTime.UtcNow;
            if (tracker.Feed(delta, elapsedMs))
            {
                _log.Info("Bot", $"WaitForGameplayBand satisfied — {tracker.InBandFraction * 100:0}% of the last {sustainMs / 1000.0:0.#}s in {bandDesc} (live fps {tracker.LastFps:0.0}) after {(DateTime.UtcNow - start).TotalSeconds:0.0}s (load→gameplay; window opens on gameplay).");
                return;
            }
        }
        if (a.Required)
        {
            _log.Error("Bot", $"WaitForGameplayBand TIMEOUT after {(DateTime.UtcNow - start).TotalSeconds:0.0}s — live fps never settled within {bandDesc} (stuck on the load/menu, or the world never loaded){label} — ABORTING (run rejected rather than measure a load screen).");
            throw new BotGateAbortException($"gameplay band ({bandDesc}) never reached");
        }
        _log.Warn("Bot", $"WaitForGameplayBand TIMEOUT after {(DateTime.UtcNow - start).TotalSeconds:0.0}s — proceeding best-effort.");
    }

    /// <summary>
    /// REACTIVE graph navigator (the "smart" menu driver). Each tick: bring the game foreground, OCR the captured
    /// display, find the FIRST <see cref="NavScreen"/> whose signature matches, run that screen's steps, then
    /// re-observe — steering toward the GOAL screen instead of replaying a fixed sequence. Self-corrects across
    /// branching/stalled front-ends and waits out sign-in/loading screens. If no known screen matches for
    /// <see cref="NavGraph.LostMs"/>, it consults the <see cref="Supervisor"/> (LLM), else presses RecoverKey,
    /// else aborts (run rejected, never a false pass). Deterministic on the happy path ⇒ reproducible measurement.
    /// In dry-run (no Vision), it just validates + prints the graph and returns.
    /// </summary>
    private async Task RunGraphAsync(NavGraph g, Stopwatch sw, BotRunResult result, DateTime hardDeadline, CancellationToken ct)
    {
        int pollMs = g.PollMs > 0 ? g.PollMs : 2500;
        int lostMs = g.LostMs > 0 ? g.LostMs : 25000;
        int maxRecover = Math.Max(0, g.MaxRecoveries);

        if (Vision is null)
        {
            _log.Info("Bot", $"Graph nav DRY-RUN (no OCR wired): {g.Screens.Count} screen(s), goal='{g.GoalDescription ?? "(unset)"}'. Validating structure:");
            foreach (var s in g.Screens)
                _log.Info("Bot", $"  • {s.Id}{(s.Goal ? " [GOAL]" : "")}{(s.MarkStartHere ? " [MarkStart]" : "")} — sig(any:[{string.Join(",", s.AnyOf)}] all:[{string.Join(",", s.AllOf)}] none:[{string.Join(",", s.NoneOf)}]) ⇒ {DescribeSteps(s)}");
            if (!g.Screens.Any(s => s.Goal)) _log.Warn("Bot", "Graph has NO goal screen — the runner could never complete.");
            return;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(g.TimeoutSeconds > 0 ? g.TimeoutSeconds : 300);
        if (hardDeadline < deadline) deadline = hardDeadline;
        _log.Info("Bot", $"Graph nav: {g.Screens.Count} screen(s), goal-seeking '{g.GoalDescription ?? "goal screen"}' (timeout {(deadline - DateTime.UtcNow).TotalSeconds:0}s, poll {pollMs}ms).");

        var lastKnown = DateTime.UtcNow;
        string? lastScreenId = null;
        int recoveries = 0, reads = 0;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            EnsureGameForeground();
            OcrFrame? frame = null;
            if (IsTargetForeground())
            {
                try { frame = GuardOcr(await Vision.ReadAsync(ct, warmupFrames: 6).ConfigureAwait(false)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.Trace("Bot", $"Graph OCR error ({ex.GetType().Name}: {ex.Message})."); }
            }
            else
            {
                if (reads % 8 == 0) _log.Trace("Bot", "Graph: game not foreground on the captured display — skipping OCR.");
                GpuSuite.Core.RunHeartbeat.Ping();   // EnsureGameForeground-polling for a slow/cold launch IS progress — let the graph's own timeout bound it, not the 60s watchdog (see WaitForText).
            }
            reads++;
            if (frame is null) { await Delay(pollMs, ct).ConfigureAwait(false); continue; }
            GpuSuite.Core.RunHeartbeat.Ping();

            var screen = g.Screens.FirstOrDefault(s => s.Matches(frame));
            if (screen is null)
            {
                if ((DateTime.UtcNow - lastKnown).TotalMilliseconds < lostMs) { await Delay(pollMs, ct).ConfigureAwait(false); continue; }
                // LOST: try the LLM supervisor first, then a blind recovery press, then abort.
                string? key = null;
                bool recVk = false;   // VK routing: supervisor CONFIRM/BACK keys via g.VkConfirm; blind recovery via g.RecoverVk
                if (Supervisor is not null)
                {
                    try { key = await Supervisor.SuggestKeyAsync(frame.Lines.Select(l => l.Text).ToList(), g.GoalDescription ?? "reach the benchmark / in-world scene", ct).ConfigureAwait(false); }
                    catch (Exception ex) { _log.Trace("Bot", $"Supervisor error ({ex.Message})."); }
                    if (string.Equals(key, NavSupervisorDecision.Wait, StringComparison.Ordinal))
                    {
                        _log.Info("Bot", "Graph LOST — supervisor identifies a legitimate wait/loading state; continuing observation without consuming a recovery.");
                        lastKnown = DateTime.UtcNow;
                        await Delay(pollMs, ct).ConfigureAwait(false);
                        continue;
                    }
                    if (key is not null)
                    {
                        // A supervisor-suggested CONFIRM/BACK on a mouse-first (VK-only) menu must go out as a
                        // VIRTUAL KEY or it silently no-ops (Cyberpunk rejects scancode Enter — live-proven
                        // 2026-06-28). Arrows/tabs stay scancode (those the game accepts, and VK arrows can
                        // double-step some menus).
                        recVk = g.VkConfirm && key is "Enter" or "Escape" or "Space";
                        _log.Info("Bot", $"Graph LOST — LLM supervisor suggests '{key}'{(recVk ? " [vk]" : "")}.");
                    }
                }
                if (key is null && g.RecoverKey is not null && recoveries < maxRecover)
                {
                    recoveries++;
                    key = g.RecoverKey;
                    recVk = g.RecoverVk;
                    _log.Warn("Bot", $"Graph LOST (~{lostMs}ms, no known screen) — blind recovery press '{key}'{(recVk ? " [vk]" : "")} ({recoveries}/{maxRecover}). Screen text: {Preview(frame)}");
                }
                if (key is null)
                    throw new BotGateAbortException($"graph nav LOST — no known screen / no recovery reached the goal (last seen: {lastScreenId ?? "none"})");
                await PressTapAsync(key, 130, ct, recVk).ConfigureAwait(false);
                lastKnown = DateTime.UtcNow;
                await Delay(pollMs, ct).ConfigureAwait(false);
                continue;
            }

            // Matched a known screen.
            lastKnown = DateTime.UtcNow;
            recoveries = 0;
            if (screen.Id != lastScreenId) { _log.Info("Bot", $"Graph: at '{screen.Id}'{(screen.Goal ? " (GOAL)" : "")} ⇒ {DescribeSteps(screen)}"); lastScreenId = screen.Id; }

            foreach (var step in screen.Do)
            {
                int n = Math.Max(1, step.Repeat);
                for (int i = 0; i < n && !ct.IsCancellationRequested; i++)
                {
                    await PressTapAsync(step.Key, step.HoldMs > 0 ? step.HoldMs : 120, ct, step.Vk).ConfigureAwait(false);
                    await Delay(step.GapMs > 0 ? step.GapMs : 350, ct).ConfigureAwait(false);
                }
            }

            if (screen.MarkStartHere)
            {
                result.StartOffsetSec = sw.Elapsed.TotalSeconds;
                MeasuredGate?.Open();   // arm runtime render-health checks at the graph-driven MarkStart
                _log.Info("Bot", $"MARK START at +{result.StartOffsetSec:0.0}s (graph screen '{screen.Id}') — measured window starts here.");
            }
            if (screen.Goal)
            {
                _log.Info("Bot", $"Graph reached GOAL '{screen.Id}' after {(sw.Elapsed.TotalSeconds):0.0}s — nav complete.");
                return;
            }
            await Delay(pollMs, ct).ConfigureAwait(false);
        }
        throw new BotGateAbortException($"graph nav did not reach the goal within the window (last seen: {lastScreenId ?? "none"})");
    }

    /// <summary>
    /// VISION nav: drive the menu / desktop to an in-world (or target) state by LOOKING at the screen on the GPU.
    /// Each tick: bring the game foreground, ask the <see cref="IVisionNavigator"/> for the next device-independent
    /// <see cref="NavAction"/> (or DONE / WAIT), map it to THIS bot's device key, press it, re-observe. Completes on
    /// the script's configured number of consecutive DONE observations (two by default, so a single misread can't
    /// stop early) or aborts (run rejected — never a false pass). The
    /// fixed measured route in Actions runs AFTER this, with the vision model unloaded so the GPU is all the game's.
    /// </summary>
    private async Task RunVisionNavAsync(string goal, IVisionNavigator nav, int holdMs, int timeoutSeconds, int doneConfirmations, bool vkConfirm, Stopwatch sw, BotRunResult result, DateTime hardDeadline, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds > 0 ? timeoutSeconds : 300);
        if (hardDeadline < deadline) deadline = hardDeadline;
        _log.Info("Bot", $"Vision nav: goal-seeking \"{goal}\" (timeout {(deadline - DateTime.UtcNow).TotalSeconds:0}s, device={(PadInject ? "pad" : "keyboard")}, hold {(holdMs > 0 ? holdMs : 130)}ms).");

        int steps = 0, doneStreak = 0, idle = 0;
        int requiredDone = Math.Max(1, doneConfirmations);
        const int maxSteps = 80;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested && steps < maxSteps)
        {
            EnsureGameForeground();                    // the model must read the GAME, not a stray desktop window
            if (!IsTargetForeground())
            {
                if (idle++ % 8 == 0) _log.Trace("Bot", "Vision nav: game not foreground on the captured display — waiting.");
                await Delay(1500, ct).ConfigureAwait(false);
                continue;
            }
            // Desktop guard (#5): even when the game window is foreground a just-launched game can still be SHOWING
            // the desktop behind it before it paints; the vision model would then read desktop / chat words and
            // false-DONE (contamination diagnosed 2026-06-27). A quick OCR veto makes us WAIT instead of asking the
            // model. OCR hiccup (null) ⇒ not vetoed (the model grab handles it).
            if (Vision is not null && DesktopGuardAnchors is { Count: > 0 })
            {
                OcrFrame? guardFrame = null;
                try { guardFrame = await Vision.ReadAsync(ct, warmupFrames: 4).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { }
                if (IsDesktopFrame(guardFrame))
                {
                    if (idle++ % 8 == 0) _log.Trace("Bot", "Vision nav: capture shows the Windows desktop — waiting (not asking the model).");
                    await Delay(1500, ct).ConfigureAwait(false);
                    continue;
                }
            }
            VisionStep? step;
            try { step = await nav.NextStepAsync(goal, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.Trace("Bot", $"Vision nav step error ({ex.Message})."); step = null; }

            if (step is null) { await Delay(1200, ct).ConfigureAwait(false); continue; }   // grab/infer hiccup — retry
            GpuSuite.Core.RunHeartbeat.Ping();                                            // real nav progress (heartbeat fresh)

            if (step.Done)
            {
                if (++doneStreak >= requiredDone)
                {
                    _log.Info("Bot", $"Vision nav: goal reached after {sw.Elapsed.TotalSeconds:0.0}s ({steps} action(s)) — handing off to the measured route.");
                    return;
                }
                await Delay(1200, ct).ConfigureAwait(false);   // confirm DONE on a second look before trusting it
                continue;
            }
            doneStreak = 0;

            if (step.Action is null or NavAction.Wait)
            {
                await Delay(1800, ct).ConfigureAwait(false);   // loading screen / intro video — wait it out, look again
                continue;
            }

            var navAct = step.Action.Value;
            // Mouse-first menus (VkConfirm) navigate on scancode arrows but only CONFIRM/BACK on a virtual key.
            bool vk = vkConfirm && navAct is NavAction.Confirm or NavAction.Back or NavAction.Start;
            await PressTapAsync(MapNavAction(navAct), holdMs > 0 ? holdMs : 130, ct, vk).ConfigureAwait(false);
            steps++;
            await Delay(900, ct).ConfigureAwait(false);        // let the menu settle before the next observe
        }
        throw new BotGateAbortException($"vision nav did not reach the goal within the window (\"{goal}\", {steps} action(s))");
    }

    /// <summary>Map a device-independent <see cref="NavAction"/> to the concrete key for THIS bot's device — the
    /// ViGEm pad's dpad/A/B/LB/RB/Start, or the keyboard's arrows/Enter/Esc (PC menus; tabs → Q/E by convention).</summary>
    private string MapNavAction(NavAction a) => PadInject
        ? a switch
        {
            NavAction.Up => "Up", NavAction.Down => "Down", NavAction.Left => "Left", NavAction.Right => "Right",
            NavAction.Confirm => "A", NavAction.Back => "B", NavAction.TabLeft => "LB", NavAction.TabRight => "RB",
            NavAction.Start => "Start", _ => "A"
        }
        : a switch
        {
            NavAction.Up => "Up", NavAction.Down => "Down", NavAction.Left => "Left", NavAction.Right => "Right",
            NavAction.Confirm => "Enter", NavAction.Back => "Escape", NavAction.TabLeft => "Q", NavAction.TabRight => "E",
            NavAction.Start => "Enter", _ => "Enter"
        };

    private static string DescribeSteps(NavScreen s) =>
        s.Do.Count == 0 ? "wait (no action)" : string.Join(", ", s.Do.Select(d => $"{d.Key}×{Math.Max(1, d.Repeat)}"));
    private static string Preview(OcrFrame f) =>
        string.Join(" | ", f.Lines.Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)).Take(6));

    /// <summary>Tap a button via the active device: the virtual pad when one is connected, else keyboard.
    /// No-op (just a delay) in dry-run. Used by PressUntilText so it works for pad and keyboard bots alike.</summary>
    private async Task PressTapAsync(string key, int ms, CancellationToken ct, bool vk = false)
    {
        // Re-assert foreground at the press edge, not just at the preceding OCR read — the read (warm-up frames +
        // OCR) takes seconds, plenty for an operator/remote window to steal focus in between (see KeyDown).
        if (PadInject) { EnsureGameForeground(); _pad!.Button(key, true); try { await Delay(ms, ct).ConfigureAwait(false); } finally { _pad?.Button(key, false); } }
        else if (Inject) { EnsureGameForeground(); KeyDown(key, vk); try { await Delay(ms, ct).ConfigureAwait(false); } finally { KeyUp(key, vk); } }
        else await Delay(ms, ct).ConfigureAwait(false);
    }

    /// <summary>Pad buttons currently held down (released defensively on teardown; the pad's own
    /// Neutral() on Dispose also zeroes everything).</summary>
    private readonly HashSet<string> _padHeld = new();

    private void ReleaseAllHeldKeys()
    {
        if (!Inject) { _held.Clear(); return; }
        foreach (var k in _held.ToArray()) { try { KeyUp(k); } catch { } }
        _held.Clear();
    }

    // ---------------- Win32 SendInput (DirectInput-style scancodes) ----------------
    // Mirrors the approach of the existing Python directkeys.py (scan-code key events). Scancode input
    // is what most fullscreen games (which read DirectInput/raw input) actually respond to.

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    private const uint INPUT_KEYBOARD = 1, INPUT_MOUSE = 0;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_SCANCODE = 0x0008;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // ---------------- foreground restore (so a minimized fullscreen game actually receives input) ----------------
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private const int SW_RESTORE = 9;
    // Foreground-lock bypass: from a BACKGROUND process (the suite), Windows refuses SetForegroundWindow once a
    // foreground lock is active — and the lock is held by WHOEVER last took focus (the user typing, the launcher
    // console, another app). That is exactly what aborts a vision-nav game's REPEAT passes: the relaunched game
    // (e.g. FH6 on its Xbox relaunch) comes up behind the desktop, the OCR reads the desktop, and the required
    // benchmark gate never matches. Two defeats applied together: (1) zero SPI_SETFOREGROUNDLOCKTIMEOUT so the
    // lock never blocks us, and (2) a synthetic ALT tap, which the shell counts as user input that lifts the lock
    // for the immediately-following SetForegroundWindow.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    private const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;
    private const uint SPIF_SENDCHANGE = 0x0002;
    private const byte VK_MENU = 0x12;          // ALT
    private const uint KEYEVENTF_KEYUP_FLAG = 0x0002;

    /// <summary>Restore + foreground <see cref="TargetPid"/>'s main window so injected keyboard/mouse
    /// reaches the game. Exclusive-fullscreen titles minimize on focus loss (e.g. when the suite's console
    /// launches); once minimized, SendInput goes nowhere and the bot appears to run but does nothing. Uses
    /// the AttachThreadInput trick to defeat the Win32 foreground lock. Best-effort: logs and continues on
    /// any failure (so a windowed game or a missing window never aborts the run).</summary>
    /// <summary>Delay for <paramref name="ms"/> while KEEPING the target game foreground — re-acquires focus every
    /// ~1s during the hold so a window that steals foreground mid-hold (e.g. the agent's own GUI on the bench display)
    /// can't background a borderless game into a pause → GPU-idle "frozen capture" (Ratchet 2026-07-01: the measured
    /// PadLeftStick holds never re-acquired focus, so the desktop coming forward paused the game for the whole window).
    /// EnsureGameForeground is a no-op when the game is already foreground, so this is cheap in the normal case.</summary>
    private async Task DelayHoldingForeground(int ms, CancellationToken ct)
    {
        if (!Inject || ms <= 0) { await Delay(ms, ct).ConfigureAwait(false); return; }
        const int chunk = 1000;
        int elapsed = 0;
        while (elapsed < ms && !ct.IsCancellationRequested)
        {
            EnsureGameForeground();
            int step = Math.Min(chunk, ms - elapsed);
            await Delay(step, ct).ConfigureAwait(false);
            elapsed += step;
        }
    }

    /// <summary>PUBLIC read-guard for callers that OCR the capture card OUTSIDE the script loop (the vision menu
    /// applier): foreground the target game, then report whether it is actually the foreground window. The
    /// applier calls this BEFORE each OCR read so the read never captures a window that stole focus over a
    /// WINDOWED game (live 2026-07-05: DOOM windowed lost focus to the agent's own chat window; the applier
    /// read the chat's menu words — 'VIDEO', 'Ripatorium' — and false-matched them, failing every row read
    /// while "confirming" a back anchor off the chat). Returns false ⇒ the caller should re-poll, not act.</summary>
    public bool TryForegroundTargetForRead()
    {
        if (!Inject || TargetPid is null) return true;
        try { EnsureGameForeground(); } catch { }
        return IsTargetForeground();
    }

    private void EnsureGameForeground()
    {
        if (TargetPid is not int pid) return;
        try
        {
            using var proc = Process.GetProcessById(pid);
            IntPtr h = proc.MainWindowHandle;
            // MainWindowHandle can be 0 right after launch (or point at a launcher splash) even though the real
            // game window exists — enumerate the pid's top-level visible windows and take the largest.
            if (h == IntPtr.Zero) h = FindMainWindowForPid(pid);
            // Strict mode: an intermittent resolve miss must NOT fall through to shell injection — re-resolve on
            // a short bound first (the miss is transient: alt-tab/present-mode flips leave the window briefly
            // unenumerable), then abort the script loudly if the window genuinely can't be found.
            if (h == IntPtr.Zero && StrictForeground)
            {
                for (int i = 0; i < 6 && h == IntPtr.Zero; i++)
                {
                    System.Threading.Thread.Sleep(500);
                    h = proc.MainWindowHandle;
                    if (h == IntPtr.Zero) h = FindMainWindowForPid(pid);
                }
                if (h == IntPtr.Zero)
                    throw new BotGateAbortException($"strict foreground: pid {pid}'s game window could not be resolved after 3s — refusing to inject into '{ForegroundHolderName()}'.");
            }
            if (h == IntPtr.Zero) { _log.Trace("Bot", $"Foreground: pid {pid} has no main window yet; injecting to current foreground."); return; }
            if (IsTargetForegroundHandle(h, pid) && !IsIconic(h)) return;   // already foreground and not minimized

            // Lift the Win32 foreground lock so SetForegroundWindow actually takes from this background process,
            // then drive focus and VERIFY it landed — retrying, because a single SetForegroundWindow loses an
            // intermittent race at (re)launch (observed live on ACM: the game window stayed behind the desktop
            // for the whole nav and EVERY OCR poll read the desktop -> required gate never matched -> abort).
            try { SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, SPIF_SENDCHANGE); } catch { }
            for (int round = 0; round < 2; round++)
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);                 // synthetic ALT = "user input" that lifts the lock
                    keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP_FLAG, UIntPtr.Zero);
                    uint cur = GetCurrentThreadId();
                    uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
                    AttachThreadInput(cur, fgThread, true);
                    try
                    {
                        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                        BringWindowToTop(h);
                        SetForegroundWindow(h);
                        SwitchToThisWindow(h, true);                          // the Alt+Tab focus API — succeeds where SetForegroundWindow alone is refused
                    }
                    finally { AttachThreadInput(cur, fgThread, false); }
                    if (IsTargetForegroundHandle(h, pid)) { _log.Info("Bot", $"Foreground: focused the game window (pid {pid}) on try {round * 5 + attempt + 1}."); return; }
                    System.Threading.Thread.Sleep(150);                       // let the switch land before re-checking / retrying
                }
                // 5 refusals in a row = something is genuinely camped on the foreground. If it's a stateless
                // Windows SHELL OVERLAY (Search flyout / touch keyboard / Start), kill it and take one more
                // round — a game or user app is never touched here.
                if (round == 0 && TryKillShellOverlayForeground()) continue;
                break;
            }
            if (StrictForeground)
                throw new BotGateAbortException($"strict foreground: could not focus the game (pid {pid}); {ForegroundHolderName()} holds the foreground — refusing to inject into it.");
            _log.Trace("Bot", $"Foreground: could NOT focus pid {pid} ({ForegroundHolderName()} holds the foreground); OCR will keep skipping until it yields.");
        }
        catch (BotGateAbortException) { throw; }   // strict-mode refusal — must reach RunAsync's Aborted path, not the swallow below
        catch (Exception ex)
        {
            if (StrictForeground)
                throw new BotGateAbortException($"strict foreground: game window unavailable ({ex.GetType().Name}: {ex.Message}) — refusing to inject into '{ForegroundHolderName()}'.");
            _log.Trace("Bot", $"Foreground restore skipped ({ex.GetType().Name}: {ex.Message}); injecting to current foreground.");
        }
    }

    private bool IsTargetForegroundHandle(IntPtr h, int pid)
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint fgPid);
        return fgPid == (uint)pid;
    }

    /// <summary>Process name of whatever currently owns the foreground window — names the focus thief in logs.</summary>
    private static string ForegroundHolderName()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint fgPid);
            if (fgPid == 0) return "an unknown window";
            using var p = Process.GetProcessById((int)fgPid);
            return $"'{p.ProcessName}' (pid {fgPid})";
        }
        catch { return "an unknown window"; }
    }

    /// <summary>
    /// Kills the foreground holder ONLY when it is a stateless Windows shell overlay (Search flyout, touch
    /// keyboard, Start menu) — these open OVER the bench display when injected input leaks to the desktop
    /// (live 2026-07-03: an F1 controller-disconnect modal leaked the bot's keys; the Search flyout + touch
    /// keyboard then refused the foreground for three straight runs until killed by hand). All of them
    /// auto-restart on demand, so the kill is side-effect-free; anything else is left alone and only named.
    /// </summary>
    private bool TryKillShellOverlayForeground()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint fgPid);
            if (fgPid == 0) return false;
            using var fg = Process.GetProcessById((int)fgPid);
            string name = fg.ProcessName;
            string[] overlays = { "SearchHost", "SearchApp", "TextInputHost", "TabTip", "StartMenuExperienceHost" };
            if (!overlays.Any(o => o.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                _log.Trace("Bot", $"Foreground held by '{name}' (pid {fgPid}) — not a shell overlay, leaving it alone.");
                return false;
            }
            _log.Warn("Bot", $"Foreground: shell overlay '{name}' (pid {fgPid}) is camped over the bench display — killing it (auto-restarts on demand) and retrying focus.");
            fg.Kill();
            System.Threading.Thread.Sleep(400);   // let the shell settle before re-asserting the game window
            return true;
        }
        catch (Exception ex) { _log.Trace("Bot", $"Shell-overlay check skipped ({ex.GetType().Name}: {ex.Message})."); return false; }
    }

    /// <summary>Largest visible top-level window owned by <paramref name="pid"/> — the real game window when
    /// Process.MainWindowHandle isn't populated yet (or points at a launcher splash). FALLBACK (2026-07-04,
    /// the DOOM menu-apply miss): an exclusive-fullscreen window mid alt-tab / present-mode flip can be
    /// transiently NOT IsWindowVisible, making the visible-only pass return 0 and the caller inject into the
    /// shell — so when no visible window matches, accept the pid's largest TITLED window (even hidden or
    /// minimized; the caller's SW_RESTORE + focus drive brings it back).</summary>
    private static IntPtr FindMainWindowForPid(int pid)
    {
        IntPtr best = IntPtr.Zero; long bestArea = 0;
        IntPtr titled = IntPtr.Zero; long titledArea = -1;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint wp);
            if (wp != (uint)pid) return true;
            long area = 0;
            if (GetWindowRect(hWnd, out RECT r))
                area = (long)Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);
            if (IsWindowVisible(hWnd) && area > bestArea) { bestArea = area; best = hWnd; }
            if (GetWindowTextLength(hWnd) > 0 && area > titledArea) { titledArea = area; titled = hWnd; }
            return true;
        }, IntPtr.Zero);
        return best != IntPtr.Zero ? best : titled;
    }

    /// <summary>True when the capture card is showing the GAME (TargetPid's window is the foreground window),
    /// so an OCR read can be trusted. False while the game is still loading (no window yet) or otherwise not
    /// focused — in that state the capture card shows the DESKTOP, and OCR-matching it can FALSE-MATCH the
    /// menu-anchor words off unrelated windows (diagnosed on F1 25, 2026-06-25). No TargetPid ⇒ not gated.</summary>
    private bool IsTargetForeground()
    {
        if (TargetPid is not int pid) return true;
        try
        {
            IntPtr fg = GetForegroundWindow();
            GetWindowThreadProcessId(fg, out uint fgPid);
            return fgPid == (uint)pid;
        }
        catch { return true; }   // can't tell ⇒ don't block the read
    }

    /// <summary>Fail a polling gate FAST when the tracked game process is DEAD. The gate-poll heartbeat pings
    /// (2026-07-07) keep the hang-watchdog calm while a gate waits — correct for a live game, but a dead one
    /// (the ACM 1080p launch crash exits ~1s after detection) would otherwise ping at a corpse for the gate's
    /// FULL timeout. Throws the same abort a required-gate timeout does, so the scene runner rejects the run
    /// and the orchestrator relaunches/classifies. Uncertain process queries never abort; no TargetPid ⇒ not gated.</summary>
    private void ThrowIfTargetDead(string gate)
    {
        if (!Inject || TargetPid is not int pid) return;
        bool dead;
        try { using var p = Process.GetProcessById(pid); dead = p.HasExited; }
        catch (ArgumentException) { dead = true; }   // no such pid — the process exited and was reaped
        catch { return; }
        if (!dead) return;
        _log.Error("Bot", $"{gate}: tracked game process (pid {pid}) is DEAD — aborting the gate now instead of polling out its timeout (crash, not a slow load).");
        throw new BotGateAbortException($"game process (pid {pid}) died during {gate}");
    }

    // Instance (not static): the injection mode (scancode vs virtual-key) is per-engine (UseVirtualKeys) OR
    // per-action (forceVk, from BotAction.Vk / the "vk:" token) — so one script can scancode-navigate yet VK-confirm.
    private void KeyDown(string key, bool forceVk = false)
    {
        if (forceVk || UseVirtualKeys) { SendKeyVk(VirtualKey(key), false); return; }
        var (s, e) = ScanCode(key); SendKey(s, e, false);
    }
    private void KeyUp(string key, bool forceVk = false)
    {
        if (forceVk || UseVirtualKeys) { SendKeyVk(VirtualKey(key), true); return; }
        var (s, e) = ScanCode(key); SendKey(s, e, true);
    }

    private static void SendKey(ushort scan, bool extended, bool up)
    {
        if (scan == 0) return;
        uint flags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0) | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
        var inp = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = scan, dwFlags = flags } } };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Virtual-key injection (wVk path, no SCANCODE flag): the OS posts WM_KEYDOWN/WM_CHAR by virtual key,
    /// which is what a window-message / VK-reading menu "accept" consumes. Complements <see cref="SendKey"/> for
    /// mouse-first UIs that ignore a scancode confirm (see <see cref="UseVirtualKeys"/>).</summary>
    private static void SendKeyVk(ushort vk, bool up)
    {
        if (vk == 0) return;
        uint flags = up ? KEYEVENTF_KEYUP : 0;   // NO KEYEVENTF_SCANCODE ⇒ Windows delivers this as the virtual key
        var inp = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = flags } } };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    private static void MouseMoveRelative(int dx, int dy)
    {
        var inp = new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE } } };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Move the cursor to a fraction (0..1) of the virtual desktop using absolute coordinates.</summary>
    private static void MouseMoveAbsolute(double xNorm, double yNorm)
    {
        int ax = (int)Math.Round(Math.Clamp(xNorm, 0, 1) * 65535);
        int ay = (int)Math.Round(Math.Clamp(yNorm, 0, 1) * 65535);
        var inp = new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dx = ax, dy = ay, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK } } };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    private static void MouseButton(string? button, bool down)
    {
        uint flag = (button?.ToLowerInvariant()) switch
        {
            "right" => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            "middle" => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP
        };
        var inp = new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flag } } };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// DirectInput scan codes (set 1). Returns the make-code plus whether it is an E0-extended key
    /// (dedicated arrows / nav cluster), which must carry KEYEVENTF_EXTENDEDKEY to be distinguished
    /// from the numpad. Unknown names map to 0 (ignored).
    /// </summary>
    private static (ushort scan, bool extended) ScanCode(string key) => key.Trim().ToUpperInvariant() switch
    {
        // letters
        "A" => (0x1E, false), "B" => (0x30, false), "C" => (0x2E, false), "D" => (0x20, false),
        "E" => (0x12, false), "F" => (0x21, false), "G" => (0x22, false), "H" => (0x23, false),
        "I" => (0x17, false), "J" => (0x24, false), "K" => (0x25, false), "L" => (0x26, false),
        "M" => (0x32, false), "N" => (0x31, false), "O" => (0x18, false), "P" => (0x19, false),
        "Q" => (0x10, false), "R" => (0x13, false), "S" => (0x1F, false), "T" => (0x14, false),
        "U" => (0x16, false), "V" => (0x2F, false), "W" => (0x11, false), "X" => (0x2D, false),
        "Y" => (0x15, false), "Z" => (0x2C, false),
        // digits (top row)
        "1" => (0x02, false), "2" => (0x03, false), "3" => (0x04, false), "4" => (0x05, false),
        "5" => (0x06, false), "6" => (0x07, false), "7" => (0x08, false), "8" => (0x09, false),
        "9" => (0x0A, false), "0" => (0x0B, false),
        // control / whitespace
        "ESC" or "ESCAPE" => (0x01, false), "BACKSPACE" or "BACK" => (0x0E, false), "TAB" => (0x0F, false),
        "ENTER" or "RETURN" => (0x1C, false), "SPACE" => (0x39, false),
        "CTRL" or "LCTRL" => (0x1D, false), "SHIFT" or "LSHIFT" => (0x2A, false), "ALT" or "LALT" => (0x38, false),
        "MINUS" => (0x0C, false), "EQUALS" => (0x0D, false),
        // bracket keys — some games bind menu TAB-CYCLE to [ / ] (e.g. Forza Motorsport's settings tabs)
        "LBRACKET" or "[" => (0x1A, false), "RBRACKET" or "]" => (0x1B, false),
        // function keys
        "F1" => (0x3B, false), "F2" => (0x3C, false), "F3" => (0x3D, false), "F4" => (0x3E, false),
        "F5" => (0x3F, false), "F6" => (0x40, false), "F7" => (0x41, false), "F8" => (0x42, false),
        "F9" => (0x43, false), "F10" => (0x44, false), "F11" => (0x57, false), "F12" => (0x58, false),
        // navigation cluster (E0-extended)
        "UP" => (0x48, true), "DOWN" => (0x50, true), "LEFT" => (0x4B, true), "RIGHT" => (0x4D, true),
        "HOME" => (0x47, true), "END" => (0x4F, true), "PAGEUP" or "PGUP" => (0x49, true), "PAGEDOWN" or "PGDN" => (0x51, true),
        "INSERT" or "INS" => (0x52, true), "DELETE" or "DEL" => (0x53, true),
        _ => (0x00, false)
    };

    /// <summary>
    /// Windows VIRTUAL-KEY codes (the same key names as <see cref="ScanCode"/>), for the <see cref="UseVirtualKeys"/>
    /// injection path. Used when a mouse-first menu reads its confirm via the message queue (VK) rather than the
    /// scancode stream. Unknown names map to 0 (ignored).
    /// </summary>
    private static ushort VirtualKey(string key)
    {
        string k = key.Trim().ToUpperInvariant();
        // letters A..Z and digits 0..9 are their ASCII codes as virtual keys
        if (k.Length == 1)
        {
            char c = k[0];
            if (c is >= 'A' and <= 'Z') return (ushort)c;
            if (c is >= '0' and <= '9') return (ushort)c;
        }
        return k switch
        {
            "ESC" or "ESCAPE" => 0x1B, "BACKSPACE" or "BACK" => 0x08, "TAB" => 0x09,
            "ENTER" or "RETURN" => 0x0D, "SPACE" => 0x20,
            "CTRL" or "LCTRL" => 0x11, "SHIFT" or "LSHIFT" => 0x10, "ALT" or "LALT" => 0x12,
            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73, "F5" => 0x74, "F6" => 0x75,
            "F7" => 0x76, "F8" => 0x77, "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
            "UP" => 0x26, "DOWN" => 0x28, "LEFT" => 0x25, "RIGHT" => 0x27,
            "HOME" => 0x24, "END" => 0x23, "PAGEUP" or "PGUP" => 0x21, "PAGEDOWN" or "PGDN" => 0x22,
            "INSERT" or "INS" => 0x2D, "DELETE" or "DEL" => 0x2E,
            "LBRACKET" or "[" => 0xDB, "RBRACKET" or "]" => 0xDD,   // VK_OEM_4 / VK_OEM_6
            _ => 0x00
        };
    }
}
