using GpuSuite.Engine.Vision;

namespace GpuSuite.Engine.Automation;

/// <summary>
/// A REACTIVE screen→action navigation graph — the "smart", deterministic menu navigator.
///
/// Instead of a fixed action SEQUENCE (brittle when a game's front-end branches, reorders, stalls on sign-in, or
/// pops an unexpected prompt), the runner loops <b>observe → identify the current screen by OCR → take that
/// screen's action → re-observe</b> until it reaches the GOAL screen. Because it reacts to what is actually on
/// the captured display rather than replaying a script, it self-corrects across branches (e.g. Forza's classic
/// PLAY-title vs the stalled attract→WELCOME path), waits out sign-in/update screens, and can back out and retry
/// when it lands somewhere unexpected. It is still fully DETERMINISTIC (no per-run choices) so the measured scene
/// it navigates to is reproducible — the requirement for a benchmark.
///
/// When the runner is LOST (no known screen for a while) it can hand off to an <see cref="INavSupervisor"/>
/// (an LLM that reads the screen + goal and suggests the next key) before falling back to a recovery press or
/// aborting — so the deterministic graph drives the happy path and the model only engages on genuine surprises.
/// </summary>
public sealed class NavGraph
{
    /// <summary>The known screens, checked in ORDER each tick — the FIRST whose signature matches is acted on.
    /// Author goal-ward / most-specific screens first (e.g. the DISPLAY tab before HOME) so a deeper screen wins
    /// when several could nominally match.</summary>
    public List<NavScreen> Screens { get; set; } = new();
    /// <summary>Overall budget (s) to reach the goal before aborting (the run is then rejected, not a false pass).</summary>
    public int TimeoutSeconds { get; set; } = 300;
    /// <summary>OCR poll cadence (ms) between observe ticks.</summary>
    public int PollMs { get; set; } = 2500;
    /// <summary>If NO known screen matches for this long (ms), the runner is LOST → supervisor (if wired) → a
    /// <see cref="RecoverKey"/> press → eventually abort.</summary>
    public int LostMs { get; set; } = 25000;
    /// <summary>Key pressed to nudge out of an unknown screen when lost (a game's front-end usually advances on
    /// "A"; a wrong submenu backs out on "B"). Null disables blind recovery (lost ⇒ supervisor or abort).</summary>
    public string? RecoverKey { get; set; } = "A";
    /// <summary>Inject <see cref="RecoverKey"/> as a VIRTUAL KEY (wVk) rather than a DirectInput scancode — for a
    /// mouse-first menu (e.g. Cyberpunk) whose confirm/back the scancode path can't trigger. Keyboard graphs only
    /// (a Gamepad graph ignores it). Default false (scancode).</summary>
    public bool RecoverVk { get; set; }
    /// <summary>Send LLM-SUPERVISOR-suggested CONFIRM/BACK keys (Enter / Escape / Space) as VIRTUAL KEYS — the
    /// third press source in a mouse-first (VK-only) menu, completing the VK story: authored steps opt in via
    /// <see cref="NavStep.Vk"/>, blind recovery via <see cref="RecoverVk"/>, and this covers the keys the
    /// supervisor proposes when the graph is LOST (a scancode Enter silently no-ops on Cyberpunk, so the
    /// closed-loop recovery could never actually confirm). Navigation suggestions (arrows) stay scancode.
    /// Keyboard graphs only. Default false.</summary>
    public bool VkConfirm { get; set; }
    /// <summary>Max blind recovery presses before aborting (so a truly stuck screen can't loop forever).</summary>
    public int MaxRecoveries { get; set; } = 3;
    /// <summary>Plain-language goal handed to the LLM supervisor when lost (e.g. "reach the DISPLAY settings tab
    /// and start Run Benchmark").</summary>
    public string? GoalDescription { get; set; }
}

/// <summary>One on-screen state: a SIGNATURE (which OCR text is present/absent) plus the action(s) that advance it
/// toward the goal. A screen with no <see cref="Do"/> steps is a "wait it out" state (sign-in / update / loading)
/// — the runner just re-observes until it changes.</summary>
public sealed class NavScreen
{
    public string Id { get; set; } = "";
    /// <summary>Screen matches if ANY of these OCR texts is present (its primary signature).</summary>
    public List<string> AnyOf { get; set; } = new();
    /// <summary>…AND ALL of these are present (tighter match; optional).</summary>
    public List<string> AllOf { get; set; } = new();
    /// <summary>…AND NONE of these are present (disambiguates similar screens; optional).</summary>
    public List<string> NoneOf { get; set; } = new();
    /// <summary>Match signatures as WHOLE WORDS (vs substring) — default true, since menu anchors are short tokens
    /// that a substring match would false-trip on (e.g. "PLAY" ⊂ "gameplay").</summary>
    public bool WholeWord { get; set; } = true;
    /// <summary>The input step(s) to take while on this screen to advance toward the goal. Empty = wait.</summary>
    public List<NavStep> Do { get; set; } = new();
    /// <summary>Reaching this screen (and running its steps) is the GOAL — the runner completes here.</summary>
    public bool Goal { get; set; }
    /// <summary>Fire MarkStart after this screen's steps run (bounds the measured window at the benchmark start).</summary>
    public bool MarkStartHere { get; set; }

    /// <summary>True if <paramref name="f"/> matches this screen's signature. Requires at least one positive
    /// (AnyOf/AllOf) clause so an all-negative screen can't match everything.</summary>
    public bool Matches(OcrFrame f)
    {
        bool Has(string t) => WholeWord ? f.FindWord(t) is not null : f.Find(t) is not null;
        if (AnyOf.Count == 0 && AllOf.Count == 0) return false;
        if (AnyOf.Count > 0 && !AnyOf.Any(Has)) return false;
        if (AllOf.Count > 0 && !AllOf.All(Has)) return false;
        if (NoneOf.Count > 0 && NoneOf.Any(Has)) return false;
        return true;
    }
}

/// <summary>One input step inside a screen's action: a pad button / key, optionally repeated (e.g. 7× Down).</summary>
public sealed class NavStep
{
    /// <summary>Button/key name routed via the script's device — pad ("A","B","Down","RB","Start"…) for a Gamepad
    /// graph, keyboard scancode name otherwise.</summary>
    public string Key { get; set; } = "";
    public int Repeat { get; set; } = 1;
    public int HoldMs { get; set; } = 120;
    public int GapMs { get; set; } = 350;   // wait after each press (let the menu settle before the next press / re-observe)
    public string? Note { get; set; }
    /// <summary>Inject this step's key as a VIRTUAL KEY (wVk) rather than a DirectInput scancode — for a mouse-first
    /// menu (e.g. Cyberpunk) whose CONFIRM/BACK ignores a scancode but accepts a VK (live-proven 2026-06-28).
    /// Set it ONLY on the confirm/back steps; navigation keys (arrows/tabs) stay scancode. Keyboard graphs only.</summary>
    public bool Vk { get; set; }
}

/// <summary>
/// Optional LLM fallback for the graph runner. When the deterministic graph is LOST (no known screen), the runner
/// calls this with the current screen's OCR lines + the goal; the implementation (an LLM) returns the next key to
/// press, or null to give up. Keeps the happy path deterministic while letting a model handle genuine surprises.
/// The default <see cref="NullNavSupervisor"/> always returns null (deterministic-only).
/// </summary>
public interface INavSupervisor
{
    Task<string?> SuggestKeyAsync(IReadOnlyList<string> screenText, string goal, CancellationToken ct);

    /// <summary>Free any GPU/VRAM the supervisor holds (a loaded vision model) BEFORE the measured benchmark
    /// window opens, so the game owns the GPU 100%. No-op for CPU/text supervisors. Called by the engine at the
    /// nav→measured-route hand-off; must never throw.</summary>
    Task UnloadAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Out-of-band supervisor decisions that are not physical input keys.</summary>
internal static class NavSupervisorDecision
{
    /// <summary>The current screen is a legitimate loading/wait state. Re-observe without consuming a blind
    /// recovery or aborting the graph.</summary>
    internal const string Wait = "__WAIT__";
}

/// <summary>
/// A multimodal "see the screen and decide" navigator. Unlike <see cref="INavSupervisor"/> (which reasons over
/// OCR TEXT and only on LOST), this drives the WHOLE menu/desktop nav by looking at the captured frame: each call
/// grabs a fresh screen, asks the vision model for the single next action toward <paramref name="goal"/>, and
/// reports DONE when the goal STATE is visible. Used for the no-built-in-benchmark games whose fragile cold-launch
/// nav an OCR-signature graph can't survive. Runs on the GPU (menus only); the engine unloads it before measuring.
/// </summary>
public interface IVisionNavigator
{
    Task<VisionStep?> NextStepAsync(string goal, CancellationToken ct);
}

/// <summary>One vision-nav decision: a SEMANTIC action (device-independent — the engine maps it to the bot's
/// keyboard or gamepad), whether the goal is already reached (<see cref="Done"/>), and the model's short rationale
/// (logged for calibration). <see cref="Action"/> is null when the model gave no usable action (the runner waits
/// and re-observes — correct for a loading screen).</summary>
public sealed record VisionStep(NavAction? Action, bool Done, string Rationale);

/// <summary>Device-independent navigation intents the vision model chooses among; the engine maps each to the
/// concrete key for the active device (keyboard arrows+Enter/Esc, or the ViGEm pad's dpad+A/B/LB/RB/Start).</summary>
public enum NavAction { Up, Down, Left, Right, Confirm, Back, TabLeft, TabRight, Start, Wait }

/// <summary>No-op supervisor: never suggests an action (the runner falls back to recovery / abort). The default
/// when no LLM supervisor is wired (settings.navSupervisor = "none").</summary>
public sealed class NullNavSupervisor : INavSupervisor
{
    public Task<string?> SuggestKeyAsync(IReadOnlyList<string> screenText, string goal, CancellationToken ct) => Task.FromResult<string?>(null);
}
