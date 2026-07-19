using GpuSuite.Core.Models;

namespace GpuSuite.Core.Config;

/// <summary>Global suite configuration + validation thresholds. Serialized to settings.json.</summary>
public sealed class SuiteConfig
{
    public string ResultsRoot { get; set; } = "Results";
    public string ProfilesDir { get; set; } = "profiles";

    /// <summary>Resolutions to test, by name. Order preserved.</summary>
    public List<string> Resolutions { get; set; } = new() { "1080p", "1440p", "4K" };

    /// <summary>
    /// Optional explicit selection of graphics variants ("extra models") to run, by variant id. Empty =
    /// run each game's ENABLED variants (its default model set). When non-empty, only variants whose id
    /// is listed run, and the selection OVERRIDES each variant's Enabled toggle — so the operator can
    /// say "just the rt-dlss-fg model across every game". Set via the CLI --variants flag; not normally
    /// persisted. Games that define no variants always run their single implicit default.
    /// </summary>
    public List<string> SelectedVariantIds { get; set; } = new();

    /// <summary>
    /// Per-GAME variant selections (gameId → variant ids) — the runtime resolution of a RUN PLAN
    /// (CLI --plan / the App's Run tab). A game with an entry here runs EXACTLY those variants
    /// (overriding each variant's Enabled toggle, same rule as <see cref="SelectedVariantIds"/>);
    /// a game with no entry (or an empty list) falls back to SelectedVariantIds, then its enabled
    /// set. Set per-invocation from plans.json / the UI; not normally persisted in settings.json.
    /// </summary>
    public Dictionary<string, List<string>> GameVariantSelections { get; set; } = new();

    /// <summary>Minimum repeats per scene/resolution (TPU-style = at least 3).</summary>
    public int RepeatsPerScene { get; set; } = 3;

    /// <summary>
    /// Runtime-only EXPLICIT repeats override from the CLI <c>--repeats N</c> flag. When set (≥1) it is honored
    /// EXACTLY — including 1 or 2 — bypassing the production ≥3 floor that the default <see cref="RepeatsPerScene"/>
    /// and per-profile <c>repeats</c> are clamped to, so an operator can do a fast single-run validation. Null
    /// (default) = no override → the normal floored behaviour. Never persisted (set per-invocation by the CLI).
    /// </summary>
    public int? RepeatsOverride { get; set; }

    /// <summary>Telemetry/power sampling interval in milliseconds.</summary>
    public int TelemetryIntervalMs { get; set; } = 100;

    /// <summary>
    /// Power-log sampling RATE in readings/second (Hz). The Powenetics V2 PMD streams at ~1 kHz; logging every
    /// frame produces a HUGE file over a multi-hour sweep (1000/s × 3 h ≈ 11M rows) for no benefit — the power
    /// AVERAGE converges from far fewer. So the suite DECIMATES the stream to this rate (keeps one sample per
    /// 1000/Hz ms; the average is unbiased, only sub-ms transients are lost). Also caps the LHM-fallback poll.
    /// Common values: 1000, 500, 250, 125, 50, 10. Default 50 (one reading / 20 ms — plenty for benchmark power).
    /// Clamped to [1, 1000].
    /// </summary>
    public int PowerSampleHz { get; set; } = 50;

    /// <summary>Effective per-sample interval (ms) derived from <see cref="PowerSampleHz"/> — the decimation floor
    /// for Powenetics and the poll cadence for the LHM fallback.</summary>
    public int PowerLogIntervalMs => Math.Max(1, 1000 / Math.Clamp(PowerSampleHz, 1, 1000));

    /// <summary>Seconds of inter-run cooldown to let the GPU return toward idle.</summary>
    public int CooldownSeconds { get; set; } = 20;

    /// <summary>Path to the PresentMon executable; resolved relative to app dir if not absolute.</summary>
    public string PresentMonPath { get; set; } = "tools/PresentMon/PresentMon.exe";

    /// <summary>Preferred Powenetics COM port (e.g. "COM9"). Probed directly when set.</summary>
    public string PoweneticsComPort { get; set; } = "";

    /// <summary>
    /// Which power source to use, GPU-agnostically:
    /// "auto" (default) — Powenetics PMD if it streams, else LHM's GPU board-power sensor, else synthetic;
    /// "powenetics" — only the PMD (else synthetic); "lhm" — only LHM's GPU board power, skipping the PMD
    /// entirely (use while the PMD is disconnected/flaky so runs log real watts instead of synthetic);
    /// "synthetic" — force synthetic. LHM board power is a single board figure (no per-rail breakdown) and
    /// updates ~1 Hz, but it works on ANY GPU LHM can read with no external hardware.
    /// </summary>
    public string PowerSource { get; set; } = "auto";

    /// <summary>
    /// Substring of the GPU-under-test's name (e.g. "NVIDIA GeForce RTX 5060", "RTX 5060", or just
    /// "NVIDIA"). On a hybrid system (discrete dGPU + integrated iGPU) this pins telemetry and the
    /// reported GpuName to the card actually being reviewed, instead of guessing by load — which can
    /// lock onto the idle-but-desktop-driving iGPU when GPU selection happens at pre-flight, before
    /// the game is rendering. Empty => auto: pick the highest-load GPU (correct on single-GPU boxes).
    /// </summary>
    public string PreferredGpu { get; set; } = "";

    /// <summary>
    /// Frame-capture backend: "presentmon" (default — PresentMon 2.x ETW capture), "rtss" (RivaTuner
    /// Statistics Server shared memory — reliable for SHORT capture windows where PresentMon's realtime
    /// session warm-up can lose the first seconds of a brief scene), or "auto" (PresentMon, falling
    /// back to RTSS when PresentMon is unavailable). RTSS must be running (see <see cref="RtssExePath"/>).
    /// </summary>
    public string FrameProvider { get; set; } = "presentmon";

    /// <summary>Optional path to RTSS.exe (e.g. the one bundled with Powenetics V2). When set and RTSS
    /// is not already running at probe time, it is launched so RTSS capture works out of the box.
    /// Empty = assume the operator starts RTSS externally.</summary>
    public string RtssExePath { get; set; } = "";

    /// <summary>
    /// Live On-Screen Display during benchmark runs. When true (default), the suite pushes a compact
    /// overlay to the RTSS OSD showing the current test pass + resolution + phase and live FPS / power /
    /// temperatures, rendered over the running game. Requires RTSS (see <see cref="RtssExePath"/>); when
    /// RTSS isn't available the OSD is silently skipped. Independent of the frame-capture backend — you
    /// can capture with PresentMon and still show the OSD via RTSS.
    /// </summary>
    public bool Osd { get; set; } = true;

    /// <summary>OSD refresh cadence in milliseconds (clamped to ≥100). 300ms ≈ 3 Hz reads calmly.</summary>
    public int OsdIntervalMs { get; set; } = 300;

    /// <summary>
    /// When true AND no specific port is set, scan every COM port for a Powenetics device.
    /// Off by default: on a test bench other serial equipment may be present, and probing
    /// writes PMD init bytes — we don't poke unknown devices unless explicitly allowed.
    /// </summary>
    public bool PoweneticsAutoDetect { get; set; }

    /// <summary>Force synthetic data even if hardware is present (for pipeline testing).</summary>
    public bool ForceSyntheticPower { get; set; }
    public bool ForceSyntheticFrames { get; set; }
    public bool ForceSyntheticTelemetry { get; set; }

    /// <summary>
    /// When true, the launcher never actually starts a game (every launch is reported as SIMULATED).
    /// Lets a real-game profile be validated end-to-end (settings → completion → capture → validation
    /// → report) without launching the game. Set via the CLI --no-launch flag; not normally persisted.
    /// </summary>
    public bool SimulateLaunch { get; set; }

    /// <summary>
    /// What happens when a REAL launch fails (the game process never appears within the grace period, or
    /// the store/launcher request errors). Honest failure is the safe default. Development callers may opt in
    /// explicitly, but ordinary CLI and WPF runs must never turn a missing game into a synthetic benchmark.
    /// </summary>
    public bool SimulateOnLaunchFailure { get; set; }

    /// <summary>Runtime-only marker set by the CLI for autonomous campaigns.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Unattended { get; set; }

    /// <summary>
    /// Attach to an ALREADY-RUNNING game instead of launching it. The operator starts the game,
    /// handles any login/EULA/save-loading, and gets it into a representative scene; the suite then
    /// finds the process by CaptureProcessName, measures it (capture + scene bot + OSD), and—crucially—
    /// does NOT kill it on cleanup. This is the practical path for scripted-scene games (whose
    /// BotDriven flow never loads a save) and for friction-heavy ports (Denuvo/Ubisoft/Xbox login).
    /// Set via the CLI --attach flag; overrides SimulateLaunch. Pair with a single --res matching what
    /// the operator set in-game (the suite cannot change a running game's resolution).
    /// </summary>
    public bool AttachToRunning { get; set; }

    /// <summary>
    /// Path to ffmpeg.exe used for capture-card frame grabs — the operator-prep VISION path. A clean,
    /// resolution-independent still of exactly what the bench display renders (via the Elgato/HDMI capture
    /// card), which also sees exclusive-fullscreen that GDI screenshots return black for. Empty => resolve
    /// "ffmpeg" from PATH. NOT a frame-timing source — PresentMon/RTSS remain the FPS path.
    /// </summary>
    public string FfmpegPath { get; set; } = "";

    /// <summary>DirectShow VIDEO device name of the capture card (e.g. "Elgato 4K Pro"), exactly as listed
    /// by `gpusuite grab --list`. Required for capture-card grabs; empty disables them.</summary>
    public string CaptureCardDevice { get; set; } = "";

    /// <summary>
    /// The hard maximum display refresh (Hz) this bench may ever drive. The display feeds THROUGH an
    /// Elgato 4K Pro capture card capped at 60 Hz; above it the card loses sync and the monitor blanks.
    /// <see cref="GpuSuite.Engine.Display.DisplayController"/> refuses to set any mode above this, and the
    /// refresh guard (below) kills a game that drives the panel past it. Keep at 60 on this bench.
    /// </summary>
    public int MaxRefreshHz { get; set; } = 60;

    /// <summary>
    /// When true (default), a background <see cref="GpuSuite.Engine.Display.RefreshGuard"/> watches the
    /// live desktop refresh during every launched run and, if it exceeds <see cref="MaxRefreshHz"/>,
    /// protects the capture card by killing the game (see <see cref="KillGameOnRefreshViolation"/>).
    /// This is the safety net for exclusive-fullscreen titles that switch the scanout to the panel's
    /// native high refresh on their own (e.g. DOOM at its menu). No effect on borderless games that
    /// inherit the 60 Hz desktop.
    /// </summary>
    public bool EnforceRefreshCap { get; set; } = true;

    /// <summary>
    /// When a refresh-cap violation is detected, kill the offending game so the panel re-syncs at the
    /// safe desktop refresh. True (default) protects the hardware at the cost of an Invalid run; set
    /// false to only LOG the breach (e.g. while diagnosing which games trip it).
    /// </summary>
    public bool KillGameOnRefreshViolation { get; set; } = true;

    /// <summary>Refresh-guard poll cadence in milliseconds (clamped ≥250). Lower = the panel un-blanks /
    /// recovers faster after a violation, at a little more polling overhead. 400ms keeps any over-cap blank
    /// under half a second so a borderless game (Ratchet) is pulled back to 60Hz before the Elgato faults
    /// (EnumDisplaySettings is cheap, so the extra polls cost nothing meaningful).</summary>
    public int RefreshGuardPollMs { get; set; } = 400;

    /// <summary>
    /// Input-lock crash/hang escape hatch. While the physical input lock is armed, the suite keeps a
    /// progress beacon (<see cref="GpuSuite.Core.RunHeartbeat"/>) that the engine pings on real progress —
    /// frames presented, or the game read in the foreground during nav. If the game under test crashes
    /// (its render/present stops) or hangs (its window never reaches the captured display) and nothing
    /// pings for this many seconds, the lock self-releases so the operator regains the keyboard and mouse
    /// WITHOUT having to wait for the whole suite to die. 60s is comfortably above the longest legitimate
    /// no-progress gap (a cold launch's pre-window load) so it won't false-trip a healthy run; physical
    /// ESC (graceful) and ESC×3 (force-unlock) remain the instant manual escapes. ≤0 disables the net.</summary>
    public int InputHangReleaseSeconds { get; set; } = 60;

    /// <summary>
    /// When true (default), the suite tidies the bench screen when a run finishes (or aborts): it closes the
    /// store-launcher windows it caused to open (Steam, Epic, EA, GOG, Xbox app) TO THE TRAY — the launchers
    /// keep running and stay logged in, so the next run and any login-walled store are unaffected; only the
    /// on-screen window goes away — and dismisses leftover game-crash dialogs (e.g. Forza's FHC00 box). Set
    /// false to leave every launcher window on screen (e.g. while debugging a launch issue).
    /// </summary>
    public bool CloseLaunchersWhenDone { get; set; } = true;

    /// <summary>
    /// LLM driver for the reactive smart-bot MENU/DESKTOP nav (no benchmark is running there, so the GPU is free).
    /// "none" (default) = deterministic-only (recover/abort). "ollama" = a LOCAL text model reads the screen's OCR
    /// text + goal and suggests the next button when the graph is LOST (CPU; LOST-fallback only). "vision" = a LOCAL
    /// multimodal model (e.g. Qwen2.5-VL) that actually SEES the captured screen and goal-seeks the next action — the
    /// primary navigator for a bot that declares a <c>visionGoal</c>; robust to our own OSD, layout changes and new
    /// scenarios. The vision model runs ON THE GPU (menus only) and is UNLOADED before the measured window so the
    /// benchmark owns the GPU 100% — and the in-game measured route stays a fixed deterministic motion (no LLM),
    /// keeping the benchmark reproducible.
    /// </summary>
    public string NavSupervisor { get; set; } = "none";
    /// <summary>Ollama TEXT model id for the "ollama" LOST-fallback supervisor (e.g. "qwen2.5:7b").</summary>
    public string NavSupervisorModel { get; set; } = "qwen2.5:7b";
    /// <summary>Ollama VISION (multimodal) model id for the "vision" navigator (e.g. "qwen2.5vl:7b"). Sees the
    /// captured frame on the GPU and decides the next menu/desktop action; unloaded before the benchmark window.</summary>
    public string NavSupervisorVisionModel { get; set; } = "qwen2.5vl:7b";
    /// <summary>Frame width (px) the captured 4K screen is downscaled to before sending to the vision model — small
    /// enough for fast GPU inference and to stay under the request-size limit, large enough to read menu text.</summary>
    public int NavSupervisorVisionWidth { get; set; } = 1344;
    /// <summary>Frame width (px) for the TIER-1 IN-WORLD navigator specifically. Driving only needs "which way is
    /// open", not legible menu text, so this is much smaller than <see cref="NavSupervisorVisionWidth"/> — measured
    /// on the GX10 (qwen2.5vl:7b), a 1344px frame decides in ~16s but ~768px hits the model's ~12s latency floor, so
    /// shrinking the in-world frame buys ~25% faster decisions with no loss for traversal. Below ~768 gives no
    /// further speed-up (the model tiles to a token minimum) and loses detail, so 768 is the sweet spot.</summary>
    public int NavInWorldVisionWidth { get; set; } = 768;
    /// <summary>Where the vision model runs: "auto" (default), "on" (force GPU), "off" (force CPU / num_gpu=0).
    /// auto = a REMOTE navSupervisorEndpoint (e.g. the lab GX10) → GPU on that box (bench untouched); a LOCAL
    /// endpoint → GPU only if the device GPU has >= navSupervisorVisionMinGpuGb VRAM, else CPU so a small (6 GB)
    /// card never thrashes against the game menu. The model is unloaded before the measured window regardless.</summary>
    public string NavSupervisorVisionGpu { get; set; } = "auto";
    /// <summary>Min LOCAL GPU VRAM (GB) for "auto" to place the vision model on the GPU; below this it runs on CPU.
    /// ~10 leaves room for the 7B model (~6 GB) + a game menu. Lower it if you pin a small (3B) vision model.</summary>
    public int NavSupervisorVisionMinGpuGb { get; set; } = 10;
    /// <summary>Ollama HTTP endpoint for the nav supervisor — point it at a REMOTE box (e.g. the GX10) to run the
    /// vision LLM entirely off the bench GPU, which makes vision-nav work on ANY card including 6 GB.</summary>
    public string NavSupervisorEndpoint { get; set; } = "http://localhost:11434";
    /// <summary>Optional REMOTE fallback endpoint (e.g. the GX10) used by "auto" ONLY when the primary endpoint is
    /// LOCAL and the device GPU is below <see cref="NavSupervisorVisionMinGpuGb"/> — a 6 GB card then runs nav
    /// off-bench on this box instead of slow local CPU. Big cards keep using the fast LOCAL primary; empty disables
    /// the fallback (sub-threshold cards fall back to CPU as before). This is the "no issues even on 6 GB" path.</summary>
    public string NavSupervisorRemoteFallbackEndpoint { get; set; } = "";
    /// <summary>Stuck-reflex threshold for the Tier-1 in-world navigator: the mean capture-card scene-change
    /// (ffmpeg scdet, CPU/off-bench) BELOW which a HELD translation counts as "wall-grinding". After a couple of
    /// consecutive low samples the navigator FORCES a committed turn to break free, instead of trusting yet another
    /// FORWARD from the single-frame vision model — the fix for "ran straight into debris" driving. Calibrate with
    /// <c>gpusuite motion-probe</c> (drive a character = high score, face a wall = low) and set this a bit above the
    /// wall/idle low end. 0 disables the reflex (pure vision driving).</summary>
    public double NavStuckThreshold { get; set; } = 0.5;

    /// <summary>
    /// Comma-separated Windows-shell anchor phrases that mark a captured nav frame as the DESKTOP (not the game) so
    /// vision/graph nav WAITs instead of false-matching menu words off desktop icons / the operator's chat window
    /// (diagnosed 2026-06-27). Empty = the built-in default set (<c>DesktopDetector.DefaultAnchors</c>);
    /// "none"/"off" disables the guard. See the input-engine DesktopGuardAnchors.
    /// </summary>
    public string DesktopGuardAnchors { get; set; } = "";
    /// <summary>How many desktop anchors must appear before a nav frame is treated as the desktop (default 2).</summary>
    public int DesktopGuardMinHits { get; set; } = 2;
    /// <summary>Strip RTSS / benchmark OSD lines (the top-left fps/frametime/pass overlay) from nav OCR before
    /// menu-anchor matching, so the overlay can't clutter or false-match menu text (complements the OSD-blank-during-
    /// nav). Default true.</summary>
    public bool StripOsdFromOcr { get; set; } = true;

    /// <summary>
    /// High-level choice of WHERE the vision model that drives menu/desktop nav runs: "auto" (default), "gpu",
    /// "gx10", "custom", "openai", or "anthropic". "gpu" = the LOCAL bench GPU (unloaded before the measured window). "gx10" = the lab GX10 /
    /// Custom AI endpoint (<see cref="Gx10Endpoint"/>) — preferably off-bench, so the bench GPU is never touched; usable on any local
    /// card (incl. 6 GB). "auto" = use the GX10 when it is REACHABLE, else the local GPU. The GX10 is selected
    /// only when a live probe confirms it exists; if it does not, the local GPU handles the vision model. This is
    /// the single selector the App's "Vision compute" toggle binds to (it greys out GX10 until the probe succeeds).
    /// It composes with the lower-level <see cref="NavSupervisorVisionGpu"/> on-card placement.
    /// </summary>
    public string VisionCompute { get; set; } = "auto";

    /// <summary>Vision-capable OpenAI model used when <see cref="VisionCompute"/> is "openai", or when
    /// "auto" has no GX10 but an OPENAI_API_KEY user environment variable is available. The key is deliberately
    /// never written to settings.json.</summary>
    public string OpenAiVisionModel { get; set; } = "gpt-5-mini";
    /// <summary>Optional OpenAI reasoning effort: none, low, medium, or high. Applied only to compatible reasoning models.</summary>
    public string OpenAiVisionEffort { get; set; } = "low";

    /// <summary>Vision-capable Anthropic model used when <see cref="VisionCompute"/> is "anthropic", or when
    /// "auto" has neither GX10 nor OpenAI configured but ANTHROPIC_API_KEY is available. The key is deliberately
    /// never written to settings.json.</summary>
    public string AnthropicVisionModel { get; set; } = "claude-sonnet-4-20250514";
    /// <summary>Optional Anthropic extended-thinking effort: none, low, medium, or high.</summary>
    public string AnthropicVisionEffort { get; set; } = "low";

    /// <summary>Any other reachable local Ollama server, including another GX10. Select visionCompute=custom to
    /// use it. It is intentionally generic: the hostname and model are supplied by the evaluator.</summary>
    public string CustomVisionEndpoint { get; set; } = "";
    public string CustomVisionModel { get; set; } = "qwen2.5vl:7b";

    /// <summary>When enabled, persist the complete trace-level run timeline under Results\Logs. Enabled by default
    /// for reviewer/beta builds so a failure report has enough launch, bot, vision, capture, and validation evidence.</summary>
    public bool DebugFullRunLog { get; set; } = true;

    /// <summary>
    /// Ollama HTTP endpoint of the lab GX10 / GB10 box. Used when <see cref="VisionCompute"/> selects "gx10"
    /// (or "auto" and the box is reachable), by the AI Calibration Engineer's GX10 reasoner, and by the
    /// `gpusuite gx10` status check / doctor line. LAN-only; any non-empty Ollama key works.
    /// </summary>
    public string Gx10Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Model the advisory post-run FAILURE-TRIAGE uses on the GX10. Triage reasons over TEXT only (the failure
    /// class, reason, run counts — no screenshot), so it runs on a fast TEXT model rather than the slow 4K vision
    /// model: the original triage spent ~4 min/failure doing 3 vision samples. Default <c>gpt-oss:20b</c> (a
    /// reasoning model that's already on the box) drops that to seconds. Falls back to
    /// <see cref="NavSupervisorVisionModel"/> if this model isn't installed on the box.
    /// </summary>
    public string Gx10TriageModel { get; set; } = "gpt-oss:20b";
    /// <summary>Consensus samples for the advisory failure-triage. 1 (default) = single fast pass — triage is an
    /// advisory note a human reviews, so a single sample is fine; raise to 2-3 to trade latency for agreement.</summary>
    public int Gx10TriageSamples { get; set; } = 1;

    public ValidationThresholds Validation { get; set; } = new();

    /// <summary>
    /// Pre-game hardware-validation gate (Milestone 2): verify GPU/VRAM/PCIe/monitor/PresentMon/Powenetics/
    /// telemetry/disk/temperature before each game. A failed FATAL check aborts only that benchmark (the game
    /// is classified HardwarePrecheck and skipped); the roster continues.
    /// </summary>
    public HardwareValidationConfig HardwareValidation { get; set; } = new();

    /// <summary>
    /// Runtime health watchdog (Milestone 3): during each measured run, detect a GPU stuck near 0%, frozen
    /// render / 0 fps, stopped telemetry, flatlined power, an exited process, or a benchmark timeout. A
    /// sustained incident invalidates the run (with a screenshot + log) so the deterministic auto-repeat
    /// retries; a persistent failure classifies the game RuntimeHealth and skips it. The roster never stops.
    /// </summary>
    public RuntimeHealthConfig RuntimeHealth { get; set; } = new();

    /// <summary>
    /// Warm-up engine (Milestone 4): before the first measured run of a cell, render + discard for a
    /// configurable duration (optionally waiting for shader compilation), and start the official measurement
    /// only after frame times stabilize. Opt-in (disabled by default).
    /// </summary>
    public WarmupConfig Warmup { get; set; } = new();
}

/// <summary>Rules the Run Validator enforces. Tuned conservatively; all configurable.</summary>
public sealed class ValidationThresholds
{
    /// <summary>A capture shorter than this (seconds) is rejected as too short.</summary>
    public double MinCaptureSeconds { get; set; } = 10.0;

    /// <summary>Reject if fewer than this many frames were captured.</summary>
    public int MinFrameCount { get; set; } = 200;

    /// <summary>Reject if avg FPS is below this (capture likely failed / wrong process).</summary>
    public double MinAvgFps { get; set; } = 5.0;

    /// <summary>Reject a built-in benchmark capture when its measured average FPS differs from the game's
    /// authoritative result-file average by more than this percentage. A large mismatch means the captured
    /// window contains menu/load frames or a trace seam even when its other frame-health checks look valid.
    /// 0 disables the cross-check gate.</summary>
    public double MaxGameReportedFpsDeviationPct { get; set; } = 10.0;

    /// <summary>Reject if any single frame time exceeds this (ms) — a hard hitch / capture gap.</summary>
    public double MaxSingleFrameTimeMs { get; set; } = 2000.0;

    /// <summary>Reject if the largest gap between consecutive frame timestamps exceeds this (s).</summary>
    public double MaxCaptureGapSeconds { get; set; } = 3.0;

    /// <summary>Reject if stutter percentage exceeds this (frame-time behavior clearly broken).</summary>
    public double MaxStutterPct { get; set; } = 25.0;

    /// <summary>Reject power data if fewer than this many samples were logged.</summary>
    public int MinPowerSamples { get; set; } = 50;

    /// <summary>Reject telemetry if fewer than this many samples were logged.</summary>
    public int MinTelemetrySamples { get; set; } = 20;

    /// <summary>Reject a measured window whose AVERAGE GPU load (LHM GpuLoadPct) sits below this — the GPU was
    /// essentially idle, so the capture is a paused/frozen or non-gameplay screen (e.g. a borderless game that
    /// lost focus: the desktop compositor re-presents the frozen frame at the refresh rate → a fake ~60fps at
    /// ~13% load). A genuine GPU-bound benchmark window normally runs ≥85% load, but a low-resolution,
    /// VSync-limited real render can fall below this floor. 40 catches the AW2 frozen captures (13-29% load —
    /// a 29% one slipped a 25% floor).
    /// A below-floor run is accepted only when independent capture-card probes show sustained, strong scene
    /// motion; this preserves legitimate low-resolution/VSync-limited rendering while frozen frames still fail.
    /// 0 disables the check. Only applied when GPU-load telemetry is present.</summary>
    public double MinGpuLoadPct { get; set; } = 40.0;

    /// <summary>Flag a run as outlier (excluded from the aggregate) if its avg FPS deviates from the median of
    /// the repeat set by more than this percentage AND by more than <see cref="OutlierFpsMinAbsDeviation"/>
    /// fps. Both conditions are required so the test is SCALE-AWARE: at a low absolute frame rate a perfectly
    /// normal ~1-2 fps run-to-run spread is a large PERCENTAGE (e.g. 1.4 fps at 19 fps ≈ 7%), which a
    /// percentage-only test wrongly rejected — it demoted 2 of 3 valid Cyberpunk rt-native runs and reported
    /// "1/4 valid" for a benchmark that actually passed. A real glitch run is always many fps off, so it still
    /// trips both gates.</summary>
    public double OutlierFpsDeviationPct { get; set; } = 20.0;

    /// <summary>Minimum ABSOLUTE avg-FPS deviation (fps) from the set median for a run to count as an outlier;
    /// paired with <see cref="OutlierFpsDeviationPct"/> (both must be exceeded). Protects low-fps benchmarks
    /// whose normal variance is a high percentage but a tiny absolute spread.</summary>
    public double OutlierFpsMinAbsDeviation { get; set; } = 4.0;

    /// <summary>Automatically repeat outlier/invalid runs up to this many extra attempts.</summary>
    public int MaxAutoRepeats { get; set; } = 2;
}
