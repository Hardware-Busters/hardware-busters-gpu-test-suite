namespace GpuSuite.Core.Models;

/// <summary>
/// A game (or synthetic workload) the suite knows how to launch and benchmark.
/// Serialized to JSON under /profiles. This is the unit the Game Profile Manager loads.
/// </summary>
public sealed class GameProfile
{
    /// <summary>Stable kebab-case id, e.g. "cyberpunk-2077". Used in folder paths.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Engine { get; set; }
    public string? Notes { get; set; }

    /// <summary>User toggle: only enabled games enter the test plan. The benchmark list is
    /// fully user-controlled — discovery never enables or adds games on its own.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Repeats per scene/resolution for this game (minimum 3 enforced at resolve time).</summary>
    public int Repeats { get; set; } = 3;

    /// <summary>Resolutions this game is tested at (names). Empty = use the suite default set.</summary>
    public List<string> SupportedResolutions { get; set; } = new();

    /// <summary>Graphics preset/settings profile the bot applies, e.g. "Ultra" or "RT-Ultra".</summary>
    public string? GraphicsPreset { get; set; }

    /// <summary>
    /// Adjustable graphics knobs this game exposes (upscaler, upscaler quality, ray tracing, frame
    /// generation, preset, texture quality, …). Each declares how it is applied (config-file regex edit
    /// or in-game menu via the vision-nav engine) and its allowed option set. A <see cref="GameVariant"/>
    /// references these by <see cref="GameSetting.Key"/>. Empty = the game runs at its profile defaults.
    /// </summary>
    public List<GameSetting> Settings { get; set; } = new();

    /// <summary>
    /// Named run configurations — the "extra models" the operator picks to benchmark a game more than
    /// once with different graphics (e.g. native vs DLSS-Quality vs RT+DLSS+Frame-Gen, mirroring the
    /// review methodology matrix). Each variant overrides a subset of <see cref="Settings"/> keys; keys
    /// it doesn't set fall back to that setting's Default. When empty the game runs once per resolution
    /// at its defaults (an implicit single variant). Toggle individual variants with <see cref="GameVariant.Enabled"/>.
    /// </summary>
    public List<GameVariant> Variants { get; set; } = new();

    /// <summary>
    /// Per-game calibration for the vision-nav menu applier: how to open the graphics settings page, how to
    /// reach each menu-method setting's row, and how its value is cycled + read off the capture card. Null
    /// until calibrated against the live menu — until then menu-method variants stay fail-safe (not launched).
    /// </summary>
    public MenuMap? MenuMap { get; set; }

    /// <summary>Bot script id that navigates the in-game menu to apply the preset + resolution.</summary>
    public string? SettingsBotScript { get; set; }

    /// <summary>How to confirm the requested settings were applied (else the run is Invalid).</summary>
    public SettingsVerification SettingsVerify { get; set; } = new();

    public LaunchSpec Launch { get; set; } = new();

    /// <summary>Process name PresentMon/Powenetics target for capture, e.g. "Cyberpunk2077.exe".</summary>
    public string CaptureProcessName { get; set; } = "";

    /// <summary>
    /// Target PresentMon by the resolved render-process PID instead of by executable name. Use this for
    /// direct-launch games that PresentMon can trace by PID but may expose as &lt;unknown&gt; to a
    /// non-elevated process-name query. Leave false for store/bootstrap games whose initial PID can hand
    /// rendering off to a new same-named process.
    /// </summary>
    public bool PresentMonByPid { get; set; }

    /// <summary>
    /// How to apply a resolution before a scene. Token {WIDTH}/{HEIGHT}/{NAME} are substituted.
    /// Empty list = handled inside the scene (e.g. an in-game config the bot edits).
    /// </summary>
    public ResolutionApplyMethod ResolutionApply { get; set; } = new();

    public List<SceneProfile> Scenes { get; set; } = new();

    /// <summary>Seconds to wait after launch before assuming the main menu is interactive.</summary>
    public int StartupGraceSeconds { get; set; } = 30;

    /// <summary>
    /// Optional per-game first-choice frame-capture backend. "rtss" forces RivaTuner for this game and
    /// "presentmon" starts RTSS-free regardless of the global --frames setting. AppContainer / Xbox titles sandbox
    /// the render process, so PresentMon's ETW session traces 0 frames unless the suite runs ELEVATED — RTSS
    /// hooks them without elevation. Setting this lets a single "hit go" sweep mix sandboxed (RTSS) and normal
    /// Win32 (PresentMon/auto) games without per-game --frames flags. Null/empty = use the global setting.
    /// (Requires RTSS running, which it is whenever the OSD is on — the default.)
    /// </summary>
    public string? FrameProvider { get; set; }

    /// <summary>
    /// Never retry this game with RTSS, even after a PresentMon capture failure. Use for titles such as
    /// Ratchet whose process is destabilized by the RTSS global render hook.
    /// </summary>
    public bool ForbidRtss { get; set; }

    /// <summary>
    /// Keep RTSS closed through launch, menu navigation, and warm-up, then start its hook only when the
    /// measured frame window opens and stop it as soon as that window closes. This is narrower than a normal
    /// RTSS backend and is intended for titles that are destabilized by a long-lived/menu-time RTSS hook.
    /// This mode is ignored whenever <see cref="ForbidRtss"/> is true; the safety prohibition always wins.
    /// </summary>
    public bool LateRtss { get; set; }

    /// <summary>
    /// Declarative render-settings fingerprint: config-file keys the engine reads (read-only) at run time
    /// and records on every <see cref="RunResult.SettingsFingerprint"/>, so each measured number carries the
    /// ACTUAL as-set render state — render resolution / scale, upscaler + mode, frame generation, RT. Born
    /// from the 2026-07-02 audit: the roster's "fps @4K" column silently mixed native (Ratchet 81), upscaled
    /// (Forza Motorsport DLSS-Performance 116) and frame-generated (AW2 263 presented ≈ 77 rendered) numbers.
    /// Empty = the game's settings store is unreadable externally (menu-only idTech8, unknown store); runs
    /// then record no fingerprint rather than silently looking native — document why in a profile note.
    /// </summary>
    public List<FingerprintKey> SettingsFingerprintKeys { get; set; } = new();
}

/// <summary>One read-only key of a game's render-settings fingerprint (see
/// <see cref="GameProfile.SettingsFingerprintKeys"/>). Extraction is best-effort and never fails a run:
/// a missing file records "(file missing)", a non-matching pattern "(not found)".</summary>
public sealed class FingerprintKey
{
    /// <summary>Short label the value is recorded under, e.g. "upscaler", "renderW", "frameGen".</summary>
    public string Label { get; set; } = "";

    /// <summary>The game's own settings file to read (environment variables expanded).</summary>
    public string ConfigFilePath { get; set; } = "";

    /// <summary>Regex with ONE capture group; the first match's group 1 is the raw value.</summary>
    public string Pattern { get; set; } = "";

    /// <summary>REGISTRY-backed alternative to ConfigFilePath/Pattern (Nixxes ports store settings in
    /// HKCU): the key path ("HKCU\..." / "HKLM\..." shorthand accepted). When set, the value is read
    /// from <see cref="RegistryValueName"/> under this key instead of a file.</summary>
    public string? RegistryKey { get; set; }

    /// <summary>For registry keys: the value name to read (recorded via ValueMap like a file capture).</summary>
    public string? RegistryValueName { get; set; }

    /// <summary>Optional raw→friendly map (e.g. "1"→"DLSS"). ONLY for semantics verified against the live
    /// game (menu screenshot / toggle-diff) — an unverified guess here would mislabel every report. Raw
    /// values not in the map pass through unchanged.</summary>
    public Dictionary<string, string> ValueMap { get; set; } = new();

    /// <summary>Marks the frame-generation key: drives <see cref="RunResult.FrameGenActive"/> and the
    /// report's "fps counts generated presents" warning.</summary>
    public bool IsFrameGen { get; set; }

    /// <summary>Raw values (pre-map, case-insensitive) meaning frame generation is OFF, e.g. ["0","Off","NONE"].
    /// Any other captured value ⇒ FG considered ON.</summary>
    public List<string> FrameGenOffValues { get; set; } = new();
}

/// <summary>
/// How to launch a modern game. Mirrors the proven launcher abstraction from the existing
/// GPU Automation tools (gameList.py / FindGameIDs.py): launch through the store's protocol
/// URI and let the launcher start the real game process, which PresentMon then attaches to by
/// name. Direct-exe launch rarely works for current titles (REDlauncher, EA anti-tamper, etc.).
/// </summary>
public sealed class LaunchSpec
{
    /// <summary>"Steam" | "Epic" | "Uplay" | "Origin" | "Gog" | "Standalone" | "Manual".</summary>
    public string Store { get; set; } = "Steam";

    /// <summary>Store-specific id: Steam appid, Epic AppName, Uplay id, Origin/EA offerId, GOG id.</summary>
    public string GameId { get; set; } = "";

    /// <summary>For Standalone: direct exe path (used only when Store == Standalone).</summary>
    public string Target { get; set; } = "";

    /// <summary>Extra launch arguments appended where the store/exe supports them.</summary>
    public string Arguments { get; set; } = "";

    public string? WorkingDirectory { get; set; }

    /// <summary>Optional post-detection stabilization window for games whose store bootstrap process exits
    /// and respawns the real renderer under the same executable name. The launcher rebinds to the newest live
    /// PID during this window before capture begins. Zero disables the wait.</summary>
    public int ProcessStabilizationSeconds { get; set; }

    /// <summary>
    /// After launch, force the game's main window to a borderless window covering the whole primary screen
    /// (strip WS_CAPTION/WS_THICKFRAME/WS_BORDER, move to 0,0, size to the desktop). For a game we must run
    /// WINDOWED to stay at the 60 Hz desktop refresh (so an exclusive-fullscreen >60 Hz mode can't blank the
    /// Elgato — e.g. DOOM: The Dark Ages), the default windowed box only fills part of the 4K capture, leaving
    /// the desktop visible. That breaks the capture-card OCR nav (WaitForText matches the suite's own on-screen
    /// log text) AND mislabels the render. Forcing the window to fill the screen keeps it DWM-composited at
    /// 60 Hz (Elgato-safe) while giving the OCR eye a clean, full-screen game and a proper full-res render.
    /// Opt-in: exclusive-fullscreen games already fill the screen and must NOT be touched. Default false.
    /// </summary>
    public bool ForceBorderlessFullscreen { get; set; }
}

public sealed class ResolutionApplyMethod
{
    /// <summary>
    /// "config-file" — regex-edit a settings file; "display" — set the DESKTOP mode to the target
    /// resolution at the safe cap refresh (the borderless-game path: the game inherits it; capped at
    /// settings.maxRefreshHz so it never blanks the Elgato); "launch-arg" — passed on the command line;
    /// "registry" — write/read-back named registry values; "bot" — set in-game by the nav bot;
    /// "none" — assume a fixed preset.
    /// </summary>
    public string Method { get; set; } = "none";
    /// <summary>
    /// For method "none", the one resolution independently verified to be persisted in-game. Requests for
    /// every other resolution fail before launch. Empty is allowed only when SupportedResolutions itself has
    /// exactly one entry.
    /// </summary>
    public string? VerifiedFixedResolution { get; set; }
    /// <summary>Optional fingerprint labels containing the post-launch render/output width and height.</summary>
    public string? FingerprintWidthLabel { get; set; }
    public string? FingerprintHeightLabel { get; set; }
    /// <summary>For config-file: absolute path to the settings file to rewrite.</summary>
    public string? ConfigFilePath { get; set; }
    /// <summary>For config-file: regex→replacement pairs applied with {WIDTH}/{HEIGHT} tokens.</summary>
    public List<ConfigEdit> Edits { get; set; } = new();
    /// <summary>For registry: existing key containing the game's persisted resolution values.</summary>
    public string? RegistryKey { get; set; }
    /// <summary>For registry: values written for each resolution. Each edit maps resolution name to the raw
    /// DWORD/QWORD/string through <see cref="RegistryEdit.ValueMap"/> and is verified by read-back.</summary>
    public List<RegistryEdit> RegistryEdits { get; set; } = new();

    /// <summary>
    /// For config-file BORDERLESS games (ACM, BMW): ALSO switch the DESKTOP mode to the target resolution
    /// (at the safe cap refresh). A borderless window is forced to fill the primary screen, so on the 4K
    /// bench its render is ALWAYS 4K no matter what WindowedWidth/Height the config says — a "1080p" cell
    /// then silently measures a 4K render (decoded 2026-07-09: ACM 1080p ≈ its 4K number, BMW rt-off
    /// inverted-laddered 88→98→106). Switching the desktop to the target makes "fill screen" = fill the
    /// target, so the borderless render is genuinely sub-native. The set is CDS_TEST-validated and capped
    /// at maxRefreshHz (never blanks the Elgato); if the target has no settable mode the apply FAILS and the
    /// run is rejected (never a mislabeled number). Both 1920x1080@60 and 2560x1440@60 proven Elgato-safe on
    /// this bench 2026-07-09 (clean captures). The desktop is restored to bench-native after the game.
    /// Default false (config-file size edits alone are correct for exclusive-fullscreen / internal-res games).
    /// </summary>
    public bool SwitchDesktopMode { get; set; }

    /// <summary>
    /// For config-file: optional path to a known-good settings file (env vars expanded). If the live
    /// <see cref="ConfigFilePath"/> is missing or empty when a run starts, it is restored from this
    /// template before the resolution edit is applied. Some games (e.g. Cyberpunk 2077) truncate their
    /// own settings file on launch and only rewrite it on a clean exit; if a prior run was killed at
    /// launch the file can be left 0-byte, which would make every subsequent resolution edit a no-op.
    /// A committed template makes config-file resolution control self-healing.
    /// </summary>
    public string? TemplateFilePath { get; set; }
}

public sealed class ConfigEdit
{
    public string Pattern { get; set; } = "";
    /// <summary>
    /// Replacement string. Tokens substituted before the regex replace:
    /// {WIDTH}/{HEIGHT}/{NAME} (resolution) and {VALUE} (the setting value being applied, after mapping —
    /// see <see cref="ValueMap"/> / <see cref="SettingApply.ValueMap"/>).
    /// </summary>
    public string Replacement { get; set; } = "";

    /// <summary>
    /// Optional per-edit map from the friendly option to the raw token THIS edit writes. Overrides the
    /// owning <see cref="SettingApply.ValueMap"/> for this edit only. Lets one logical setting touch
    /// fields of different shapes — e.g. a "Frame Generation = On" setting writing a string field ("On")
    /// and a bool field ("true") in the same pass.
    /// </summary>
    public Dictionary<string, string> ValueMap { get; set; } = new();
}

/// <summary>A predefined benchmark scene within a game.</summary>
public sealed class SceneProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public SceneKind Kind { get; set; } = SceneKind.FixedWindow;

    /// <summary>Capture length in seconds (excludes warmup). TPU-style runs are ~20-60s.</summary>
    public int CaptureSeconds { get; set; } = 30;

    /// <summary>Seconds of warmup after the scene is "ready" before capture starts (discarded).</summary>
    public int WarmupSeconds { get; set; } = 5;

    /// <summary>Max seconds to wait for scene readiness before declaring the run invalid.</summary>
    public int ReadinessTimeoutSeconds { get; set; } = 120;

    /// <summary>For BotDriven scenes: the bot script id to run (resolved by the Automation engine).</summary>
    public string? BotScript { get; set; }

    /// <summary>How the built-in benchmark is triggered: "auto" (game starts it) | "bot" (menu script) | "arg".</summary>
    public string BenchmarkStart { get; set; } = "auto";

    /// <summary>For BenchmarkStart == "bot": the bot script id that navigates the menu to start the bench.</summary>
    public string? StartBotScript { get; set; }

    /// <summary>Measure all repeats from ONE game launch instead of a fresh cold launch per repeat. For
    /// launcher-based titles (EA / Xbox / Ubisoft) the rapid kill→relaunch the per-repeat model needs churns
    /// the launcher's online session — the next cold launch then intermittently stalls (EA "PRESS ANY BUTTON"
    /// never shows; Xbox sign-in hangs), costing valid repeats. With this set the game is launched once, the
    /// first repeat runs <see cref="StartBotScript"/>/<see cref="BotScript"/> as usual, and every later repeat
    /// runs <see cref="ReRunBotScript"/> against the still-running game (no relaunch); the game is killed once
    /// after the last repeat. Default false (cold-launch per repeat).</summary>
    public bool SingleLaunchRepeats { get; set; }

    /// <summary>For <see cref="SingleLaunchRepeats"/>: the bot script id run on repeats 2+ to re-measure the
    /// SAME running game — it navigates from the post-measurement state back to the benchmark/scene (e.g. F1's
    /// results→Run Benchmark Test, or, for a static scripted scene like MSFS's parked aircraft, simply
    /// re-marks a fresh window with no navigation). Falls back to <see cref="StartBotScript"/>/<see
    /// cref="BotScript"/> when unset (correct when the start bot is already re-entrant).</summary>
    public string? ReRunBotScript { get; set; }

    /// <summary>Number of THROWAWAY warmup passes run before the measured repeats (default 0 = none). The first
    /// run after a fresh launch is systematically unrepresentative — cold shader/asset caches make it SLOWER
    /// (e.g. F1: 44 fps cold vs 52 warm) while a cold GPU boosts CLOCKS higher and makes it FASTER (e.g. FH6:
    /// 100 fps cold vs 85 steady) — so it lands as an outlier and the aggregate shows 2/3. A warmup pass runs
    /// the full launch+nav+benchmark exactly like a measured pass but its result is DISCARDED (not aggregated,
    /// not counted toward the valid target), warming the caches + GPU thermals so the measured repeats are all
    /// steady-state → true N/N. For <see cref="SingleLaunchRepeats"/> games the warmup is the in-session
    /// cold-nav pass (so it costs no extra launch — later repeats re-run warm); for cold-launch games it is one
    /// extra launch whose thermal warmup carries into the measured launches.</summary>
    public int WarmupRepeats { get; set; }

    /// <summary>How the scene's start and finish are detected (built-in benchmarks: log-file).</summary>
    public CompletionDetection Completion { get; set; } = new();

    /// <summary>Optional first-class WARM-UP phase run BEFORE the measured capture (operator spec 2026-06-29).
    /// Null / not enabled ⇒ no warm-up (legacy timing). See <see cref="SceneWarmupConfig"/>.</summary>
    public SceneWarmupConfig? Warmup { get; set; }

    /// <summary>Per-scene override of the measured-window MOTION floor for the runtime-health scene-static
    /// check (<see cref="RuntimeHealthConfig.MotionFloorScore"/>). Null = suite default; 0 disables the check
    /// for this scene (use for a legitimately near-static measured scene, e.g. MSFS's parked 'Ready to fly').
    /// Scenes whose bot senses its own motion (SmartTraverse / Gx10Traverse) are skipped automatically.</summary>
    public double? MeasuredMotionFloor { get; set; }

    /// <summary>Seconds after the measured window opens before scene-static motion sensing begins. Use this
    /// for built-in benchmarks with a legitimate stationary opening (for example an on-grid countdown).
    /// GPU/frame/process health remains active during the delay; only the scene-static probe is deferred.</summary>
    public double MeasuredMotionDelaySeconds { get; set; }
}

/// <summary>
/// First-class GAMEPLAY WARM-UP phase for a scripted scene (operator spec 2026-06-29). A cold GPU renders a
/// scripted-scene game's heavy gameplay frames BELOW steady-state because shaders/asset caches are cold, while
/// trivial load/black screens render very fast — so a cold autonomous run would otherwise measure shader
/// COMPILATION, not GPU rendering (proven live on Ratchet &amp; Clank: 117 fps warm vs &lt;75 fps cold, black
/// load screen = 247 fps, GPU 99% in both). The objective is to benchmark GPU rendering, so when this is enabled
/// the engine — AFTER navigating to in-world — drives a DETERMINISTIC warm-up traversal (<see cref="Script"/>)
/// for <see cref="DurationSeconds"/> with NO frame/power capture (those frames are never in any reported number),
/// THEN starts a fresh PresentMon + power window for the measured route. Generic: any cold game opts in via its
/// profile. Disabled by default so the proven roster's timing is unchanged until turned on.
/// </summary>
public sealed class SceneWarmupConfig
{
    /// <summary>Master switch. False ⇒ no warm-up (legacy behaviour).</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Which of the three warm-up modes to run:
    ///   "none"             — no warm-up (same as Enabled=false).
    ///   "gameplay"         — drive a moving traversal (<see cref="Script"/>) in-world to compile shaders /
    ///                        warm asset caches, then measure (the Ratchet-class need). DEFAULT.
    ///   "shaderPrecompile" — the game compiles shaders on a LAUNCH screen (some Nixxes ports); wait for that
    ///                        screen to finish (<see cref="CompletionCondition"/>) and SKIP the gameplay warm-up
    ///                        (gameplay is already warm from frame 1).
    /// </summary>
    public string Mode { get; set; } = "gameplay";

    /// <summary>Target warm-up duration in seconds for the moving traversal. Default 90. The warm-up script
    /// should run at least this long; the engine logs the actual elapsed for traceability.</summary>
    public double DurationSeconds { get; set; } = 90;

    /// <summary>Bot script id driving the warm-up: nav-to-in-world + a deterministic moving traversal (the SAME
    /// camera/movement/duration every run). Frames during it are NOT captured. If null, a fixed in-place wait of
    /// <see cref="DurationSeconds"/> is used instead (still uncaptured).</summary>
    public string? Script { get; set; }

    /// <summary>How the engine decides the warm-up is complete:
    ///   "duration"               — the warm-up script finished / <see cref="DurationSeconds"/> elapsed (default).
    ///   "shaderScreenGone:TOKEN" — (shaderPrecompile mode) wait until the OCR TOKEN (e.g. "Compiling") is no
    ///                              longer on screen, then proceed.
    /// </summary>
    public string CompletionCondition { get; set; } = "duration";
}

/// <summary>
/// How a scene's start/finish are detected. Preferred for built-in benchmarks is parsing the
/// game's own result/log file (most reliable + lets us cross-check FPS). Falls back to the
/// PresentMon frame-activity window when no markers exist. Never relies on a fixed sleep alone
/// for benchmarks.
/// </summary>
public sealed class CompletionDetection
{
    /// <summary>"duration" | "log-file" | "activity" | "result-file".</summary>
    public string Method { get; set; } = "duration";

    /// <summary>For log-file: path to the benchmark log being written live (env vars expanded).</summary>
    public string? LogFilePath { get; set; }

    /// <summary>Optional separate result file written at the end (defaults to LogFilePath).</summary>
    public string? ResultFilePath { get; set; }

    /// <summary>
    /// For result-file: directory watched for a NEW result file that marks the benchmark FINISH
    /// (env vars expanded). Used by games whose built-in benchmark writes a fresh, timestamped
    /// result file on completion (e.g. Cyberpunk 2077's benchmarkResults folder). Start is taken
    /// from frame activity; finish is the first new file matching <see cref="ResultFileGlob"/>.
    /// </summary>
    public string? ResultDirPath { get; set; }

    /// <summary>For result-file: glob the new result file must match (e.g. "*.csv"). Default "*.*".</summary>
    public string? ResultFileGlob { get; set; }

    /// <summary>
    /// For result-file: when the measured window ends via a frame-stall but the result file has not
    /// appeared yet, keep waiting up to this many seconds for it (the game often writes its result a
    /// while AFTER rendering stops, e.g. Cyberpunk ~100s later, with the process still alive). The
    /// measured window is NOT extended — this only lets us capture the game's reported result for the
    /// cross-check. 0 = don't wait.
    /// </summary>
    public double ResultGraceSeconds { get; set; }

    /// <summary>Text/regex marking benchmark START in the log.</summary>
    public string? StartPattern { get; set; }

    /// <summary>Text/regex marking benchmark FINISH / results written.</summary>
    public string? FinishPattern { get; set; }

    /// <summary>Regex with one capture group extracting the game's own reported avg FPS (cross-check).</summary>
    public string? ResultFpsPattern { get; set; }

    /// <summary>
    /// Optional regex (one capture group) extracting the game-reported render WIDTH from the result
    /// file (e.g. Cyberpunk 2077's summary.json "renderWidth"). When present, the run is VERIFIED to
    /// have actually rendered at the requested resolution — a mismatch (e.g. a 4K request that the
    /// display silently clamped to 1440p, or a resolution edit that didn't take) invalidates the run
    /// instead of silently recording mislabeled data.
    /// </summary>
    public string? ResultWidthPattern { get; set; }

    /// <summary>Optional regex (one capture group) extracting the game-reported render HEIGHT.</summary>
    public string? ResultHeightPattern { get; set; }

    /// <summary>A run shorter than this (measured window, seconds) is rejected.</summary>
    public double MinValidSeconds { get; set; } = 10;

    /// <summary>Give up waiting for start/finish after this many seconds.</summary>
    public double MaxTimeoutSeconds { get; set; } = 180;

    /// <summary>If markers aren't found, detect the window from PresentMon frame activity.</summary>
    public bool FallbackToActivity { get; set; } = true;

    /// <summary>Activity mode: frames stalled this long (s) ⇒ benchmark finished.</summary>
    public double ActivityIdleSeconds { get; set; } = 3.0;

    /// <summary>
    /// For bot-driven built-in benchmarks that have NO clean start/finish marker and render their menus
    /// and level-load screens UNCAPPED (e.g. DOOM: The Dark Ages — its benchmark-list menu and the Hebeth
    /// level-load screen render at 800–2000+ fps, then a multi-second load hitch, then the real ~50 fps
    /// flythrough, then an uncapped on-screen results screen). When &gt; 0, after the activity window is
    /// taken the measured window is further reduced to the LONGEST CONTIGUOUS band of real-gameplay frames —
    /// frames whose instantaneous FPS is at or below this ceiling AND that are not load/stall hitches. This
    /// drops the leading menu/load screen, the trailing results screen, and the load-transition hitch in one
    /// pass, leaving only the flythrough. 0 (default) disables it — marker/result-file benchmarks already
    /// carry a clean window, so only set this for the menu-driven, marker-less case.
    /// </summary>
    public double GameplayBandCeilingFps { get; set; }

    /// <summary>
    /// Warm-up to exclude from the START of the measured window AFTER it is fixed (and after any gameplay-band
    /// isolation), in seconds. Built-in benchmarks stream their scene in at the start of the flythrough, so the
    /// first few seconds carry streaming/shader-compile stutter spikes that are real frames but pollute the
    /// 1%/0.1% lows without moving the average (e.g. Cyberpunk: a cluster of 100-240 ms frames in the first ~4 s,
    /// then smooth). Dropping this lead gives steady-state lows. Only applied when enough window remains
    /// (&gt; warm-up + MinValidSeconds). 0 (default) keeps the whole post-detection window ("as-experienced").
    /// </summary>
    public double MeasuredWarmupSeconds { get; set; }

    /// <summary>
    /// Result-frametimes JSON source (e.g. DOOM: The Dark Ages writes per-frame render times to its own
    /// benchmark-*.json). When set, AFTER capture the suite reads the NEWEST matching JSON written since
    /// capture started and uses ITS per-frame times as the authoritative measured frames — overriding the
    /// captured present-rate (which a WINDOWED game DWM-caps to the desktop refresh, making RTSS read a flat
    /// ~60 fps). The JSON is already just the benchmark window, so NO trimming / band-isolation / warm-up is
    /// applied to it. Path may contain env vars (e.g. %USERPROFILE%).
    /// </summary>
    public string? FrametimesJsonDir { get; set; }
    /// <summary>Glob the result-frametimes JSON must match (default "*.json").</summary>
    public string? FrametimesJsonGlob { get; set; }
    /// <summary>The array property holding the per-frame objects (default "frames").</summary>
    public string? FrametimesJsonFramesField { get; set; }
    /// <summary>The per-frame object's frametime-in-ms field (default "frame").</summary>
    public string? FrametimesJsonFrameMsField { get; set; }
    /// <summary>Optional per-frame GPU-busy-ms field (default "gpu").</summary>
    public string? FrametimesJsonGpuMsField { get; set; }
    /// <summary>Optional top-level resolution field ("WxH" or "WxH@hz") for the authoritative resolution check (default "resolution").</summary>
    public string? FrametimesJsonResolutionField { get; set; }

    /// <summary>
    /// Directory holding a game-written per-frame CSV (the CSV counterpart of <see cref="FrametimesJsonDir"/>),
    /// e.g. a Codemasters EGO "Benchmark Mode Frame Times" file (a short banner, a "Num frames,N" line, a
    /// "Frame,Time (ms),Running time (s)" header, then one row per frame). When set on a BuiltInBenchmark, the
    /// freshest matching CSV written since capture start REPLACES the captured present samples as the
    /// authoritative measured window — so the reported fps is the game's own exact benchmark frametimes, not a
    /// menu/loading-contaminated capture. The frame-time column is auto-detected as the header cell containing
    /// "(ms)" (and not "running"), so the column order isn't hard-coded. JSON is tried first when both are set.
    /// </summary>
    public string? FrametimesCsvDir { get; set; }
    /// <summary>Glob the result-frametimes CSV must match (default "*.csv").</summary>
    public string? FrametimesCsvGlob { get; set; }
}

/// <summary>
/// How the suite confirms the requested graphics preset + resolution were actually applied.
/// If verification is configured and fails, the run is marked Invalid (never silently continued).
/// </summary>
public sealed class SettingsVerification
{
    /// <summary>"none" | "window-size" (game client area == target res) | "config-file".</summary>
    public string Method { get; set; } = "none";

    /// <summary>For config-file: file to read back after applying settings.</summary>
    public string? ConfigFilePath { get; set; }

    /// <summary>For config-file: regex (with {WIDTH}/{HEIGHT} tokens) that MUST be present afterwards.</summary>
    public string? ExpectedPattern { get; set; }
}

/// <summary>
/// One adjustable graphics knob the suite can set before a run (e.g. the upscaler, its quality level,
/// ray tracing, frame generation, the overall preset). The set of knobs a game exposes is declared on
/// <see cref="GameProfile.Settings"/>; a <see cref="GameVariant"/> selects values for them. How the value
/// is realized — a config-file edit (proven, verifiable) or an in-game menu selection (driven by the
/// capture-card vision-nav engine) — is described by <see cref="Apply"/>.
/// </summary>
public sealed class GameSetting
{
    /// <summary>Stable key referenced by variants, e.g. "upscaler", "upscalerQuality", "rt", "frameGen", "preset".</summary>
    public string Key { get; set; } = "";
    /// <summary>Human label for listings, e.g. "Upscaling Mode".</summary>
    public string Label { get; set; } = "";
    /// <summary>Allowed option values (informational + validated against). e.g. ["Off","DLSS","FSR","XeSS"].</summary>
    public List<string> Options { get; set; } = new();
    /// <summary>Value used when a selected variant does not override this key.</summary>
    public string Default { get; set; } = "";
    /// <summary>How a chosen value for this setting is applied before the run.</summary>
    public SettingApply Apply { get; set; } = new();
}

/// <summary>How a single <see cref="GameSetting"/> value is applied to the game before launch/capture.</summary>
public sealed class SettingApply
{
    /// <summary>
    /// "config-file" — regex-edit a settings file (env vars expanded), then read it back to verify;
    /// "registry" — write named values under an existing registry key (Nixxes ports store settings in
    ///              HKCU), each read back to verify;
    /// "menu" — select it in the in-game settings menu via the vision-nav engine (applied post-launch);
    /// "none" — informational only (no automatic apply).
    /// </summary>
    public string Method { get; set; } = "none";

    /// <summary>For registry: the key path, e.g. "HKCU\SOFTWARE\Nixxes\Ratchet &amp; Clank Rift Apart"
    /// (HKCU/HKLM shorthand or full hive names). The key must ALREADY exist (the game creates it on
    /// first run) — apply never creates keys, so a wrong path fails loudly instead of writing junk.</summary>
    public string? RegistryKey { get; set; }

    /// <summary>For registry: the values to write; every one is read back and must match or the variant fails.</summary>
    public List<RegistryEdit> RegistryEdits { get; set; } = new();

    /// <summary>For config-file: the settings file to rewrite (env vars expanded). May reuse the
    /// resolution config file — each knob is an independent read-modify-write pass.</summary>
    public string? ConfigFilePath { get; set; }

    /// <summary>For config-file: regex→replacement pairs. Use the {VALUE} token in the replacement.</summary>
    public List<ConfigEdit> Edits { get; set; } = new();

    /// <summary>
    /// Optional map from a friendly option (what variants/listings use, e.g. "Quality") to the raw token
    /// the config file actually stores (e.g. "2" or "DLSS_Quality"). When a value isn't in the map it is
    /// written verbatim. Applies to both the {VALUE} substitution and the read-back verification.
    /// </summary>
    public Dictionary<string, string> ValueMap { get; set; } = new();

    /// <summary>
    /// For config-file: regex (with {VALUE}) that MUST match after the edit, or the run is Invalid. When
    /// empty, the suite synthesizes a check from the edits. This is what prevents two variants (e.g. DLSS
    /// on vs off) from silently collapsing to identical settings if an edit no-ops.
    /// </summary>
    public string? VerifyPattern { get; set; }

    /// <summary>For menu: the OCR menu path to the control, e.g. "Graphics|Upscaling|Mode". The vision-nav
    /// engine walks the menu by reading labels off the capture card and selects the requested value.</summary>
    public string? MenuPath { get; set; }
}

/// <summary>
/// One registry value a "registry" apply method writes. The friendly value resolves through this edit's
/// <see cref="ValueMap"/>, then the setting-level map, then verbatim — same precedence as <see cref="ConfigEdit"/>.
/// </summary>
public sealed class RegistryEdit
{
    public string ValueName { get; set; } = "";
    /// <summary>Value kind: "dword" (default), "qword", or "string". Binary blobs are unsupported by design —
    /// their semantics can't be verified, and an unverifiable write silently mislabels runs.</summary>
    public string Kind { get; set; } = "dword";
    public Dictionary<string, string> ValueMap { get; set; } = new();
}

/// <summary>
/// A named run configuration — an "extra model" the operator benchmarks a game in (e.g. Native, DLSS
/// Quality, RT + DLSS + Frame Generation). It overrides a subset of the game's <see cref="GameProfile.Settings"/>;
/// unset keys use each setting's Default. The orchestrator runs game × scene × resolution × (enabled variant).
/// </summary>
public sealed class GameVariant
{
    /// <summary>Stable kebab-case id used in result paths, e.g. "native", "rt-dlss-q", "rt-dlss-fg".</summary>
    public string Id { get; set; } = "";
    /// <summary>Display name, e.g. "RT + DLSS Quality + Frame Gen".</summary>
    public string Name { get; set; } = "";
    /// <summary>User toggle: only enabled variants run. Lets the operator pick which models to benchmark.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Whether this variant is a real benchmark model that may appear in the app's Run picker and Full preset.
    /// Set false for calibration proofs, restore helpers, and maintenance-only variants. They remain addressable
    /// explicitly by CLI id, but can no longer contaminate a publishable sweep.
    /// </summary>
    public bool BenchmarkEligible { get; set; } = true;
    /// <summary>Setting key → value overrides for this variant. Keys absent here fall back to the setting Default.</summary>
    public Dictionary<string, string> Settings { get; set; } = new();
    /// <summary>Optional graphics-preset override applied for this variant (else the profile GraphicsPreset).</summary>
    public string? GraphicsPreset { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Calibration for driving a game's in-game graphics menu by VISION: the applier injects the
/// <see cref="Open"/> sequence to reach the settings page, focuses each <see cref="MenuControl"/>'s row,
/// reads its current value off the capture card (OCR), cycles it to the target, re-reads to VERIFY, then
/// injects <see cref="Back"/> to apply + exit. A setting that can't be set+verified fails the run (no
/// silent mislabeling) exactly like a config-file knob. All counts/labels are filled in from the live menu.
/// </summary>
public sealed class MenuMap
{
    /// <summary>"keyboard" (SendInput scancodes + arrows) or "gamepad" (ViGEm pad — for RE-Engine titles
    /// that ignore injected keyboard/mouse).</summary>
    public string InputDevice { get; set; } = "keyboard";
    /// <summary>Milliseconds to wait after each navigation step for the menu to settle before OCR.</summary>
    public int SettleMs { get; set; } = 700;
    /// <summary>Optional OCR text that must be visible once <see cref="Open"/> has run, proving we reached the
    /// graphics settings page (else the apply aborts rather than blindly cycle the wrong screen).</summary>
    public string? AnchorLabel { get; set; }
    /// <summary>Optional key/pad button that cycles top-level settings categories/tabs (e.g. "RB"). When the
    /// AnchorLabel isn't visible after Open, the applier presses this up to <see cref="AnchorNavMax"/> times,
    /// re-reading each time, until the anchor appears — robust OCR-guided category navigation that beats a
    /// fixed count of presses (which silently drops on a wrapping tab ring).</summary>
    public string? AnchorNavKey { get; set; }
    /// <summary>Max <see cref="AnchorNavKey"/> presses to try while hunting the anchor category.</summary>
    public int AnchorNavMax { get; set; } = 8;
    /// <summary>Optional frame region (0..1 fractions) where the game shows the NAME of the currently-focused
    /// setting (e.g. a description pane header). When set, the applier navigates by OCR-GUIDED STEPPING —
    /// it steps and re-reads this region until it matches the target control's label — which is the only
    /// reliable method for long/scrolling/WRAPPING settings lists. When null, it falls back to the
    /// home-up + down-from-top counts on each control.</summary>
    public MenuRegion? FocusHeader { get; set; }
    /// <summary>Caps how far RIGHT (0..1 fraction of frame width) a row's VALUE text may sit for the applier's
    /// value reads. Set it below a right-side description pane: when a row's small value token fails to OCR
    /// (DOOM's highlighted 'TAA' at a 1080p desktop), the nearest in-band line to the right is a wrapped pane
    /// line ('…your GPU.') and the read must return null (→ retry) instead of that prose (live 2026-07-09).
    /// 1.0 (default) = no cap. DOOM: values end ≈0.46, pane starts ≈0.55 → 0.52.</summary>
    public double ValueMaxXFrac { get; set; } = 1.0;
    /// <summary>Steps from the game's post-launch state to the graphics settings page.</summary>
    public List<MenuAction> Open { get; set; } = new();
    /// <summary>Steps to apply + exit the settings menu back to a neutral state after the knobs are set.</summary>
    public List<MenuAction> Back { get; set; } = new();
    /// <summary>Optional OCR text that must be visible AFTER <see cref="Back"/> runs, proving the applier
    /// actually returned to the target neutral screen (e.g. the main menu). When set and not yet visible, the
    /// applier presses <see cref="BackNavKey"/> up to <see cref="BackNavMax"/> times, re-reading each time,
    /// until it appears — the exit-side mirror of <see cref="AnchorLabel"/>/<see cref="AnchorNavKey"/>. DOOM's
    /// multi-level Settings needs a variable number of Back presses to reach the main menu; a fixed Back list
    /// left the game deep in Settings, so a following benchmark-start bot navigated the settings list blind
    /// (walked its Down-presses to Display Calibration — live 2026-07-05). Null = no exit verification.</summary>
    public string? BackAnchor { get; set; }
    /// <summary>Key/pad button pressed to back out one level while hunting <see cref="BackAnchor"/> (e.g. "B").</summary>
    public string? BackNavKey { get; set; }
    /// <summary>Max <see cref="BackNavKey"/> presses to try while hunting <see cref="BackAnchor"/>.</summary>
    public int BackNavMax { get; set; } = 6;
    /// <summary>Optional steps run AFTER the settings exit (and <see cref="BackAnchor"/> hunt) to restore the
    /// neutral screen to its COLD-BOOT state — the applier's exit contract. A game's main menu keeps focus on
    /// the row you exited from (DOOM: Settings, 3 rows below Campaign), while the benchmark-start bot's
    /// deterministic walk is calibrated from the cold-boot focus (Campaign): without this, the bot's
    /// 'open Extras' Enter re-entered Settings and its Downs landed on Display Calibration (live 2026-07-05
    /// runs 21+23 — this, not capture loss, was the recurring desync). DOOM's postBack = Up×3, exactly
    /// inverting the open's Down×3 (the menu WRAPS, so blind-homing with Up-spam is NOT safe). Same step
    /// vocabulary as <see cref="Open"/>/<see cref="Back"/> (padTap/wait/waitText/tapIfText/tapIfNoText).</summary>
    public List<MenuAction> PostBack { get; set; } = new();
    /// <summary>One descriptor per menu-method setting key the applier can drive.</summary>
    public List<MenuControl> Controls { get; set; } = new();
}

/// <summary>One injected navigation step in a <see cref="MenuMap"/> (a serializable subset of BotAction).</summary>
public sealed class MenuAction
{
    /// <summary>"tap" (key), "wait", "padTap" (pad button), "moveAbs" (cursor to X,Y in 0..1), "click",
    /// "waitText" (poll the capture card until <see cref="Text"/> is on screen, up to Ms — cold-launch gate),
    /// "tapIfText" / "tapIfNoText" (CONDITIONAL tap: press Key only when <see cref="Text"/> is / is not
    /// visible — e.g. confirm an apply NOTICE only when one actually appeared; a verify-only pass that changed
    /// nothing raises no notice, and a blind confirm press then hits the underlying menu instead).</summary>
    public string Type { get; set; } = "tap";
    /// <summary>Key/button name for tap/padTap, e.g. "Esc","Down","Enter","A".</summary>
    public string? Key { get; set; }
    public int Ms { get; set; } = 60;
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>OCR text for the vision-gated step types (waitText / tapIfText / tapIfNoText).</summary>
    public string? Text { get; set; }
    public string? Note { get; set; }
}

/// <summary>A rectangular region of the capture frame, as 0..1 fractions of width/height.</summary>
public sealed class MenuRegion
{
    public double XMin { get; set; }
    public double YMin { get; set; }
    public double XMax { get; set; } = 1;
    public double YMax { get; set; } = 1;
}

/// <summary>How to reach and change one menu-method setting's row.</summary>
public sealed class MenuControl
{
    /// <summary>The <see cref="GameSetting.Key"/> this control drives (e.g. "upscaler").</summary>
    public string SettingKey { get; set; } = "";
    /// <summary>Text shown in the <see cref="MenuMap.FocusHeader"/> region when THIS row is focused — used by
    /// guided stepping to know when the row is reached (e.g. "Upscaler"). Also the default row label.</summary>
    public string Label { get; set; } = "";
    /// <summary>OCR label to find on the LIST ROW when reading the current value (may be a robust substring,
    /// e.g. "scaler", when OCR mangles the full label). Defaults to <see cref="Label"/>.</summary>
    public string? RowValueLabel { get; set; }
    /// <summary>Map from a setting value to the text the menu actually shows for it (e.g. "Off"→"TAA",
    /// "DLSS"→"DLSS"). When a value isn't mapped, the value itself is used as the on-screen text.</summary>
    public Dictionary<string, string> OptionLabels { get; set; } = new();
    /// <summary>"cycle" — the value changes with Left/Right (toggles, sliders); "dropdown" — open it
    /// (<c>A</c>/Enter), step to the option, confirm. For dropdowns set <see cref="OptionOrder"/>.</summary>
    public string Actuation { get; set; } = "cycle";
    /// <summary>For dropdown actuation: the setting-values in their on-screen TOP-TO-BOTTOM order (e.g.
    /// ["Off","DLSS","FSR","XeSS"] when the menu lists TAA/DLSS/FSR/XeSS). The applier homes the open
    /// dropdown to the top then steps down to this value's index — robust to OCR noise in the option text.</summary>
    public List<string> OptionOrder { get; set; } = new();
    /// <summary>Guided stepping: max Down steps to try while hunting this row (one full wrap of the list).</summary>
    public int MaxSteps { get; set; } = 60;
    /// <summary>Count-fallback (no FocusHeader): press Up this many times to home focus to the top.</summary>
    public int HomeUpCount { get; set; } = 25;
    /// <summary>Count-fallback: then press Down this many times to focus this control's row.</summary>
    public int DownFromTop { get; set; }
    /// <summary>Cycle actuation: key/pad button that advances the value (default Right); reverse = CycleLeft.</summary>
    public string CycleRight { get; set; } = "Right";
    public string CycleLeft { get; set; } = "Left";
    /// <summary>Max value-cycle presses to try in each direction before giving up (fail-safe).</summary>
    public int MaxCycles { get; set; } = 10;
}
