namespace GpuSuite.Engine.Automation;

public enum BotActionType
{
    KeyDown, KeyUp, KeyTap,
    MouseMove,      // relative move (Dx,Dy)
    MouseMoveAbs,   // absolute move to normalized screen coords (X,Y in 0..1)
    MouseClick,     // press+release a button (Button) at the current cursor position
    Wait,
    WaitForText,    // poll the capture-card OCR until expected on-screen TEXT appears (or, with WaitForAbsent, disappears) — robust to variable load / shader-compile timing that fixed waits can't bound
    PressUntilText, // press Key repeatedly until expected OCR Text appears (or timeout) — robust "advance past a screen" when a freshly-arrived menu renders its prompt seconds before it accepts input, or a controller-detection tap is silently eaten. Checks BEFORE each press so it never over-presses once the target screen is up.
    TapIfText,      // tap Key ONCE, but ONLY if OCR Text is currently on screen — the CONDITIONAL dismiss for a screen that exists in one run shape but not another. Born 2026-07-05 (DOOM runs 21/23/24): the splash-dismiss Space was unconditional, and on a menu-apply run (no splash — the applier already dismissed it) it SELECTED the focused main-menu row (Campaign difficulty / Settings), desyncing the whole walk. Skips (never taps) on a null/unreadable frame — an unknown screen must not be pressed at (the PressUntilText rule).
    WaitForGameplayBand, // poll the LIVE capture frame-rate (frames/s over PollMs windows) until it settles AT/BELOW CeilingFps sustained for SustainMs — proves a 60fps-capped LOAD/menu has ended and real (sub-ceiling) gameplay has begun, so the following MarkStart opens the window on gameplay regardless of how long the load took. Immune to OCR misses + load-time variance (the Ratchet fixed-load-wait failure mode).
    MarkStart,      // bot signals "the benchmark scene is now beginning" (bounds the measured window)
    MarkEnd,        // bot signals "the benchmark scene has ended"

    // ---- virtual gamepad (ViGEm) — for engines that ignore injected keyboard/mouse (RE Engine) ----
    PadButtonDown,  // press a pad button by name in Key (A,B,X,Y,LB,RB,Start,Back,Up,Down,…); held until PadButtonUp
    PadButtonUp,    // release a pad button by name in Key
    PadButtonTap,   // press+release a pad button (Key) held for DurationMs
    PadLeftStick,   // set the left stick to (X,Y) each -1..1 and dwell DurationMs (deflection persists until changed)
    PadRightStick,  // set the right stick to (X,Y) each -1..1 and dwell DurationMs
    PadTrigger,     // set a trigger (Button "left"/"right") to X in 0..1 and dwell DurationMs

    // ---- smart movement (Tier-0 reflex) ----
    SmartTraverse   // run the character forward for DurationMs, but SENSE motion off the capture card (ffmpeg
                    // scene-change, no benchmark-GPU cost) and TURN to unstick whenever it stops translating
                    // (wall-jam) — adaptive, anti-stuck movement vs a blind fixed route. StuckThreshold tunes
                    // the "stuck" cutoff (calibrate via `gpusuite motion-probe`). Device follows the script.
    ,
    ReplayRoute     // deterministically REPLAY a previously-RECORDED RecordedRoute (forward-holds + unstick-turns at
                    // RoutePath) — the REPRODUCIBLE half of discover-then-replay: a SmartTraverse "discovery" pass
                    // records the smart route ONCE; this replays the SAME route bit-for-bit with NO motion sensor or
                    // GX10 in the measured window, so the timed run traces the open-space path yet stays repeatable.
    ,
    AssertWorldMotion // IN-WORLD PROOF gate: hold the character's FORWARD movement and sample capture-card
                      // motion (ffmpeg scene-change, CPU-side) until one probe reaches StuckThreshold or
                      // DurationMs elapses. Running in-world translates the whole camera (motion >> 1);
                      // the same stick input on a MENU leaves the background static (motion ~0) — so this
                      // discriminates in-world from menu INDEPENDENT of frame rate, which a WaitForGameplayBand
                      // fps ceiling cannot do once frame generation lifts in-world fps past the menu's
                      // (BMW MFG 3X/4X in-world ~270-360 vs menu ~250). Required ⇒ ABORT (run rejected)
                      // when motion never reaches the threshold: the "in-world" state was actually a menu.
    ,
    Gx10Traverse    // hand control to the TIER-1 Gx10InWorldNavigator: it LOOKS at the capture-card frame ON THE GX10
                    // and drives in-world MOVEMENT (forward/back/strafe/turn/jump) toward Goal for DurationMs, perception
                    // OFF-BENCH (box GPU). Optionally RECORDS the driven route (RecordRoutePath) for a later
                    // deterministic ReplayRoute — the DISCOVERY half of discover-then-replay. Falls back to blind-forward
                    // chunks when the navigator is absent/unreachable (never wedges, never false-passes).
}

/// <summary>Which input device the engine drives the script with. Gamepad routes through a ViGEm virtual
/// Xbox 360 controller (required for RE Engine titles, which filter injected keyboard/mouse).</summary>
public enum BotInputDevice { Keyboard, Gamepad }

/// <summary>One timed step in a scripted scene (fixed camera path / menu macro).</summary>
public sealed class BotAction
{
    public BotActionType Type { get; set; }
    /// <summary>Virtual-key name, e.g. "W","A","S","D","Space","Enter","Down","B". For Key* actions.</summary>
    public string? Key { get; set; }
    /// <summary>When true, this Key* action injects a Windows VIRTUAL KEY (wVk) instead of the default DirectInput
    /// SCANCODE. Most games want scancodes (the default), but some MOUSE-FIRST menus navigate on scancode arrows
    /// yet read their CONFIRM/BACK off the message queue (a virtual key) — live-validated 2026-06-28 on Cyberpunk:
    /// scancode arrows move the menu, scancode Enter does NOTHING, but VK Enter opens Settings and VK Esc closes it.
    /// So a Drive can mix scancode nav with a VK confirm. Authored in a key sequence as the "vk:" prefix
    /// (e.g. "Down,Down,vk:Enter"). Default false (scancode), so every existing bot is unchanged.</summary>
    public bool Vk { get; set; }
    public int Dx { get; set; }
    public int Dy { get; set; }
    /// <summary>Absolute target as a fraction of the virtual screen (0..1). For MouseMoveAbs.</summary>
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>"left" | "right" | "middle". For MouseClick (default "left").</summary>
    public string? Button { get; set; }
    /// <summary>Duration to hold (KeyTap/MouseClick) or wait (Wait) or spread a MouseMove over (ms).</summary>
    public int DurationMs { get; set; }
    /// <summary>Optional human label for the timeline log (e.g. "open display settings").</summary>
    public string? Note { get; set; }
    /// <summary>For WaitForText: the on-screen text to gate on (alphanumeric-insensitive substring, matched via
    /// the same OCR <c>Find</c> the menu applier uses, so spacing/punctuation/case are ignored).</summary>
    public string? Text { get; set; }
    /// <summary>For WaitForText: optional additional OCR anchors. A presence gate succeeds when any anchor is
    /// visible; an absence gate succeeds only when all anchors are absent. This lets one bounded startup gate
    /// handle alternate valid states such as a cold "PRESS TO START" screen or an already-open main menu.</summary>
    public List<string> TextAlternatives { get; set; } = [];
    /// <summary>For WaitForText: poll interval (ms) between OCR reads while waiting. Default 1200 when 0.</summary>
    public int PollMs { get; set; }
    /// <summary>For WaitForText: when true, wait until <see cref="Text"/> is ABSENT (e.g. a "Loading…" banner
    /// clears) instead of present. Default false (wait until present).</summary>
    public bool WaitForAbsent { get; set; }
    /// <summary>For WaitForText / PressUntilText: when true, a TIMEOUT is FATAL — the bot ABORTS the run and the
    /// scene runner marks it Invalid, instead of logging a warning and "proceeding best-effort". Use on the one
    /// or two gates that PROVE the bot reached the benchmark / in-world scene (e.g. Forza's "Run Benchmark" row,
    /// Ratchet's menu-gone gate). Without this, a desynced nav silently measures whatever is on screen (a menu /
    /// loading title) and reports a FALSE pass — exactly the Ratchet/Forza false-passes the recorded videos
    /// caught. Default false (legacy best-effort behaviour, correct for non-critical intermediate gates).</summary>
    public bool Required { get; set; }
    /// <summary>For WaitForText / PressUntilText: match <see cref="Text"/> as a WHOLE WORD/phrase token rather
    /// than a substring, so e.g. gating on "PLAY" does NOT trip on "gameplay" and "RACE" does not trip on
    /// "terrace". Maps to <c>OcrFrame.FindWord</c>. Use on short anchors that are substrings of unrelated words
    /// the game might show first — the Forza legal-warning splash satisfied a substring "PLAY" gate via
    /// "Driving Depicted in Gameplay" and desynced the whole nav 2-3 minutes early. Default false (substring).</summary>
    public bool WholeWord { get; set; }
    /// <summary>For WaitForText: require the present/absent condition to hold for this many CONSECUTIVE OCR reads
    /// before the gate is satisfied (default 1). Hardens fragile ABSENCE gates — a single transient OCR miss
    /// instantly satisfied <c>WaitForAbsent</c> and let the bot proceed off a menu it hadn't actually left
    /// (Ratchet's RESUME-absent gate fired after one read on a menu still showing, → the measured window opened
    /// on the wrong state). 3 consecutive confirmations defeat a one-frame miss; a null/unreadable frame resets
    /// the streak. Default 1 = legacy single-read behaviour, correct for robust PRESENCE gates.</summary>
    public int MinConsecutive { get; set; }
    /// <summary>For PressUntilText: optional safety ceiling on injected key taps. Once this many taps have
    /// failed to satisfy the OCR condition, stop immediately (throwing when <see cref="Required"/> is true)
    /// instead of continuing to press through an unexpected menu. Zero preserves the legacy timeout-only
    /// behaviour. Use for confirm buttons whose repeated activation could select an unintended menu item.</summary>
    public int MaxPresses { get; set; }
    /// <summary>For OCR-gated actions: an optional terminal on-screen phrase. When it appears, the action
    /// aborts immediately instead of continuing to inject input through an account, update, or other blocking
    /// screen. This is deliberately fail-closed: the text identifies an environment condition, not a benchmark
    /// scene, so no captured frames can be mistaken for a valid run.</summary>
    public string? AbortIfText { get; set; }
    /// <summary>Additional terminal OCR phrases for the same action. This keeps older profiles using
    /// <see cref="AbortIfText"/> compatible while allowing a route to identify several mutually-exclusive
    /// account/cloud-service screens without choosing or overwriting user data.</summary>
    public List<string> AbortIfTexts { get; set; } = new();
    /// <summary>For WaitForGameplayBand: the frames-per-second ceiling of the gameplay band — the gate returns once
    /// the live capture rate is AT/BELOW this, sustained for <see cref="SustainMs"/>. Set to the scene's
    /// gameplayBandCeilingFps (e.g. 40 for Ratchet's ~23fps gameplay vs its 60fps load). Default 40 when 0.</summary>
    public double CeilingFps { get; set; }
    /// <summary>For WaitForGameplayBand: OPTIONAL lower bound of the gameplay band. When &gt; 0 the gate requires the
    /// live rate to sit AT/ABOVE this as well, i.e. within [FloorFps, CeilingFps] — a true band. This excludes a
    /// stalled/near-black LOAD screen that renders BELOW gameplay fps (e.g. Black Myth's ~450fps menu sits above the
    /// ceiling, real gameplay ~45-90 sits in-band, and a black load screen at single-digit fps sits below the floor
    /// → won't trip the gate). Default 0 = no floor = the one-sided ceiling behaviour (correct for Ratchet, whose
    /// load/menu is a clean 60fps ABOVE the ceiling so no floor is needed).</summary>
    public double FloorFps { get; set; }
    /// <summary>For WaitForGameplayBand: how long (ms) the live rate must stay in-band before the gate is satisfied,
    /// so a brief mid-load fps dip can't trip it early. Default 3000 when 0.</summary>
    public int SustainMs { get; set; }
    /// <summary>For WaitForGameplayBand: fraction (0..1) of the trailing <see cref="SustainMs"/> window that must be
    /// IN-BAND for the gate to settle. Default 0.7 (when 0). Below 1.0 it TOLERATES a severe-hitch game whose
    /// gameplay is in-band on average but injects 0-frame stalls + brief catch-up spikes (the Ratchet 0/5 "band
    /// never settles" — a strict continuous accumulator reset on every hitch). A sustained out-of-band load still
    /// keeps the fraction low so the window can't open mid-load. 1.0 = the old continuous-in-band behaviour.</summary>
    public double BandFraction { get; set; }
    /// <summary>For SmartTraverse: the capture-card MOTION (mean ffmpeg scene-change) BELOW which the character is
    /// judged stuck (wall-jam / not translating) → the bot turns to unstick. Calibrate live with
    /// <c>gpusuite motion-probe</c> (set a bit above the wall/idle reading). Default 1.0 when 0.</summary>
    public double StuckThreshold { get; set; }
    /// <summary>For SmartTraverse: consecutive below-threshold motion probes required before an escape turn.
    /// Default 1 preserves legacy behavior. Use 2-3 for visually noisy scenes so one low-motion frame does not
    /// cause a twitchy false recovery while the character is still translating normally.</summary>
    public int StuckTriggerCount { get; set; } = 1;
    /// <summary>For SmartTraverse / Gx10Traverse: maximum TOTAL collision-recovery turns during this action before
    /// the route is rejected. Zero preserves unlimited behavior. This counter deliberately does not reset after
    /// a briefly healthy probe: repeated wall contact across the full window is still a poor benchmark route.</summary>
    public int MaxRecoveryTurns { get; set; }
    /// <summary>Optional minimum fraction (0..1) of parsed motion probes that must meet StuckThreshold. Zero
    /// disables the quality gate. When enabled, too few parsed samples or a lower ratio aborts the route instead
    /// of saving a nominally valid result from a mostly stationary/wall-grinding scene.</summary>
    public double MinHealthyMotionRatio { get; set; }
    /// <summary>For ReplayRoute: path to the <see cref="RecordedRoute"/> JSON to replay (absolute, or relative to the
    /// working directory). For SmartTraverse: see <see cref="RecordRoutePath"/> to RECORD one in a discovery pass.</summary>
    public string? RoutePath { get; set; }
    /// <summary>For SmartTraverse / Gx10Traverse: when set, RECORD the emitted movement primitives to this
    /// <see cref="RecordedRoute"/> JSON path as a one-time "discovery" pass, so a later
    /// <see cref="BotActionType.ReplayRoute"/> can replay the SAME route deterministically. Null = don't record.</summary>
    public string? RecordRoutePath { get; set; }
    /// <summary>For Gx10Traverse: the plain-language movement goal handed to the in-world navigator (e.g. "follow the
    /// corridor and reach the open plaza"). Empty ⇒ a generic "explore open space, don't grind walls" default.</summary>
    public string? Goal { get; set; }

    public static BotAction Tap(string key, int ms = 40) => new() { Type = BotActionType.KeyTap, Key = key, DurationMs = ms };
    /// <summary>Tap <paramref name="key"/> as a VIRTUAL KEY (wVk) rather than a scancode — for a mouse-first menu's
    /// confirm/back that ignores a scancode key (see <see cref="Vk"/>). Authored as the "vk:" prefix in a sequence.</summary>
    public static BotAction TapVk(string key, int ms = 40) => new() { Type = BotActionType.KeyTap, Key = key, DurationMs = ms, Vk = true };
    public static BotAction Down(string key) => new() { Type = BotActionType.KeyDown, Key = key };
    public static BotAction Up(string key) => new() { Type = BotActionType.KeyUp, Key = key };
    public static BotAction Move(int dx, int dy, int ms = 50) => new() { Type = BotActionType.MouseMove, Dx = dx, Dy = dy, DurationMs = ms };
    public static BotAction MoveAbs(double x, double y, int ms = 60) => new() { Type = BotActionType.MouseMoveAbs, X = x, Y = y, DurationMs = ms };
    public static BotAction Click(string button = "left", int ms = 40) => new() { Type = BotActionType.MouseClick, Button = button, DurationMs = ms };
    public static BotAction Pause(int ms) => new() { Type = BotActionType.Wait, DurationMs = ms };
    /// <summary>Vision-gated wait: poll the capture-card OCR until <paramref name="text"/> appears (or, with
    /// <paramref name="absent"/>, disappears) or <paramref name="timeoutMs"/> elapses. Falls back to a blind
    /// wait of <paramref name="timeoutMs"/> when no capture/OCR is wired (and is fast in dry-run).</summary>
    public static BotAction WaitText(string text, int timeoutMs = 60000, int pollMs = 1200, bool absent = false)
        => new() { Type = BotActionType.WaitForText, Text = text, DurationMs = timeoutMs, PollMs = pollMs, WaitForAbsent = absent };
    /// <summary>Press <paramref name="key"/> repeatedly until <paramref name="text"/> appears on the capture
    /// card (or <paramref name="timeoutMs"/> elapses), waiting <paramref name="pollMs"/> after each press.
    /// Robust title→menu advance: tolerates a screen that renders its prompt before accepting input, and a
    /// first press eaten by controller-detection. Checks before each press so it won't over-press.</summary>
    public static BotAction PressUntil(string key, string text, int timeoutMs = 90000, int pollMs = 2500)
        => new() { Type = BotActionType.PressUntilText, Key = key, Text = text, DurationMs = timeoutMs, PollMs = pollMs };
    /// <summary>Wait until the LIVE capture frame-rate settles within the gameplay band sustained for
    /// <paramref name="sustainMs"/> (proving a load/menu has given way to gameplay), then return. The band is
    /// at/below <paramref name="ceilingFps"/>, and — when <paramref name="floorFps"/> &gt; 0 — also at/above it
    /// (a two-sided band that excludes a low-fps stalled/black load screen, not just the high-fps menu).
    /// <paramref name="required"/> aborts the run if the band is never reached within <paramref name="timeoutMs"/>.</summary>
    public static BotAction WaitGameplay(double ceilingFps = 40, int sustainMs = 3000, int timeoutMs = 120000, int pollMs = 1000, bool required = true, double floorFps = 0)
        => new() { Type = BotActionType.WaitForGameplayBand, CeilingFps = ceilingFps, FloorFps = floorFps, SustainMs = sustainMs, DurationMs = timeoutMs, PollMs = pollMs, Required = required };
    public static BotAction Begin() => new() { Type = BotActionType.MarkStart };
    public static BotAction End() => new() { Type = BotActionType.MarkEnd };

    // ---- gamepad factory helpers ----
    public static BotAction PadDown(string button) => new() { Type = BotActionType.PadButtonDown, Key = button };
    public static BotAction PadUp(string button) => new() { Type = BotActionType.PadButtonUp, Key = button };
    public static BotAction PadTap(string button, int ms = 80) => new() { Type = BotActionType.PadButtonTap, Key = button, DurationMs = ms };
    public static BotAction LStick(double x, double y, int ms) => new() { Type = BotActionType.PadLeftStick, X = x, Y = y, DurationMs = ms };
    public static BotAction RStick(double x, double y, int ms) => new() { Type = BotActionType.PadRightStick, X = x, Y = y, DurationMs = ms };
    public static BotAction PadTrig(string side, double value, int ms) => new() { Type = BotActionType.PadTrigger, Button = side, X = value, DurationMs = ms };
    /// <summary>Adaptive forward run for <paramref name="ms"/> with the Tier-0 stuck-reflex: senses capture-card
    /// motion and turns to unstick on a wall-jam. <paramref name="threshold"/> is the stuck cutoff (motion-probe).</summary>
    public static BotAction Smart(int ms, double threshold = 1.0, string? recordRoutePath = null)
        => new() { Type = BotActionType.SmartTraverse, DurationMs = ms, StuckThreshold = threshold, RecordRoutePath = recordRoutePath };
    /// <summary>Deterministically REPLAY a previously-recorded smart route (the reproducible half of
    /// discover-then-replay — see <see cref="BotActionType.ReplayRoute"/>). No motion sensor / GX10 in the loop.</summary>
    public static BotAction Replay(string routePath) => new() { Type = BotActionType.ReplayRoute, RoutePath = routePath };
    /// <summary>Drive in-world movement via the Tier-1 GX10 navigator toward <paramref name="goal"/> for
    /// <paramref name="ms"/>; optionally record the driven route for a later deterministic <see cref="Replay"/>.
    /// <paramref name="chunkMs"/> is how long each chosen move is held before re-deciding.</summary>
    public static BotAction Gx10Smart(int ms, string goal, string? recordRoutePath = null, int chunkMs = 700)
        => new() { Type = BotActionType.Gx10Traverse, DurationMs = ms, Goal = goal, RecordRoutePath = recordRoutePath, PollMs = chunkMs };
}

/// <summary>A named, replayable scripted scene path (camera macro) or menu-navigation sequence.</summary>
public sealed class BotScript
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Input device the engine drives this script with. Keyboard (default) = SendInput scancodes +
    /// mouse; Gamepad = a ViGEm virtual Xbox 360 pad (required for RE Engine titles that ignore injected
    /// keyboard/mouse). A gamepad script should use the Pad* action types.</summary>
    public BotInputDevice InputDevice { get; set; } = BotInputDevice.Keyboard;
    /// <summary>If true, the engine loops the action list until the scene capture window ends.
    /// Menu-navigation scripts (that trigger a built-in benchmark once) MUST be non-looping.</summary>
    public bool Loop { get; set; } = true;
    public List<BotAction> Actions { get; set; } = new();
    /// <summary>Optional REACTIVE nav graph (the "smart" menu navigator). When present, the engine runs the graph
    /// (observe→identify-screen→act→re-observe toward the goal) INSTEAD of the linear <see cref="Actions"/> list —
    /// robust to branching/stalled front-ends. A bot uses one or the other.</summary>
    public NavGraph? Graph { get; set; }

    /// <summary>
    /// Optional plain-language GOAL for the VISION navigator. When set AND a vision navigator is wired
    /// (settings.navSupervisor = "vision"), the engine runs a multimodal model that SEES the captured screen and
    /// goal-seeks the next menu/desktop action until this goal state is reached — the robust, self-adapting
    /// replacement for the brittle OCR-signature <see cref="Graph"/> on the fragile cold-launch→in-world path.
    /// It runs ON THE GPU (no benchmark during menus) and is unloaded before the <see cref="Actions"/> measured
    /// route runs, so the in-game motion stays a FIXED deterministic window (reproducible) with the GPU all to the
    /// game. Describe a clearly-detectable end state, e.g. "the player character is standing in the open courtyard
    /// in normal gameplay, with NO menu, loading screen or dialog on screen". Falls back to <see cref="Graph"/>
    /// when no vision navigator is available (e.g. dry-run).
    /// </summary>
    public string? VisionGoal { get; set; }

    /// <summary>When true, the CLOSED-LOOP navigators inject the CONFIRM / BACK / START action as a VIRTUAL KEY
    /// (wVk) instead of a DirectInput scancode — for a MOUSE-FIRST menu (e.g. Cyberpunk) that navigates on scancode
    /// arrows but ignores a scancode confirm (live-proven 2026-06-28: VK Enter opens its Settings, scancode does not).
    /// Applies to vision-nav (Confirm/Back/Start NavActions) and to graph steps marked <see cref="NavStep.Vk"/>.
    /// Navigation (arrows/tabs) stays scancode. Keyboard scripts only (a Gamepad script uses pad buttons). Default
    /// false, so every existing bot is unchanged.</summary>
    public bool VkConfirm { get; set; }

    /// <summary>Vision-nav press hold (ms) for each CONFIRM / dpad action. Some games ignore short taps (Black Myth
    /// ignores &lt; 200 ms). Default 130.</summary>
    public int VisionHoldMs { get; set; } = 130;
    /// <summary>Vision-nav goal-seek budget (s). Raise for a long first-launch shader compile (Black Myth ~9 min,
    /// TLOU multi-min) so the nav doesn't time out before the menu renders. Capped by the scene's capture window.
    /// Default 300.</summary>
    public int VisionTimeoutSeconds { get; set; } = 300;

    /// <summary>Number of consecutive VISION DONE observations required before handing off to Actions.
    /// The default of two protects against a single false-DONE. A script may lower this only when it has an
    /// explicit required post-navigation gate (for example TLOU's OCR CONTINUE gate), which remains the
    /// authoritative proof that the target menu is actually present.</summary>
    public int VisionDoneConfirmations { get; set; } = 2;

    /// <summary>
    /// Built-in fixed camera path: a deterministic "walk a loop while panning" pattern,
    /// the kind used to benchmark a repeatable playable scene (à la the cyberpython bots).
    /// </summary>
    public static BotScript CameraPathBasic() => new()
    {
        Id = "camera_path_basic",
        Description = "Forward-strafe loop with periodic camera pan; deterministic and repeatable.",
        Loop = true,
        Actions = new()
        {
            BotAction.Down("W"),
            BotAction.Move(180, 0, 600), BotAction.Pause(1200),
            BotAction.Tap("A", 500),    BotAction.Move(-180, 0, 600), BotAction.Pause(1200),
            BotAction.Tap("D", 500),    BotAction.Move(0, 90, 400),   BotAction.Pause(1000),
            BotAction.Move(0, -90, 400),BotAction.Pause(1000),
            BotAction.Up("W")
        }
    };
}
