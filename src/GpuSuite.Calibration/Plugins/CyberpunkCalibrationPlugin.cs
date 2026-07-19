using GpuSuite.Calibration.Schema;

namespace GpuSuite.Calibration.Plugins;

/// <summary>
/// Cyberpunk 2077 — the Phase-2 calibration target (the suite's best-understood game: a real config file,
/// a built-in benchmark, RTSS-pinned capture). This plugin declares the front-end → Settings → Graphics →
/// Benchmark path the framework maps, the graphics controls to confirm, and the benchmark launcher screen.
///
/// The SyntheticOcr fields let the Phase-2 skeleton exercise the whole observe→graph→persist→report
/// pipeline offline. When the real capture-card observer is wired, the same screens/anchors are matched
/// against live OCR and these synthetic words are ignored.
///
/// LIVE VALIDATION (2026-06-28, against a running Cyberpunk2077.exe, windowed). Two rounds:
/// ROUND 1 — REDengine accepts injected DIRECTIONAL keys: Up/Down move the menu cursor, the menu CLAMPS at the top
/// &amp; bottom (no wrap, so Up-spam "homes"); but the engine's default SCANCODE confirm (Enter/Space/F) did NOTHING.
/// ROUND 2 (the fix) — the engine already sent scancodes; the real lever was a VIRTUAL-KEY (wVk) confirm. With the
/// new `send-input --vk` / "vk:" path: VK Enter OPENED the Settings page and VK Esc CLOSED it back to the main menu
/// (both proven on the capture card). So Cyberpunk's main menu navigates on scancode arrows but reads its
/// CONFIRM/BACK off the message queue (a virtual key) — the recipe is "scancode nav + vk:Enter confirm".
/// REMAINING BLOCKER for a hands-off OPEN-LOOP Drive: the cursor's first-tap handling is ±1 unreliable (sometimes
/// doubles, sometimes a tap is eaten) AND the selection highlight fades, so a FIXED sequence can't reliably land on
/// a MIDDLE item like Settings (I reached it only by observing the cursor and correcting — closed-loop). A robust
/// hands-off Cyberpunk menu nav therefore needs the suite's CLOSED-LOOP navigator (NavGraph / vision-nav, which
/// observe→act→re-observe) wired to emit the confirm as a VK — NOT the open-loop Drive field. Operationally this is
/// moot: Cyberpunk sets graphics via its config FILE and benchmarks via `-benchmark` (no menu), so the Drive fields
/// stay EMPTY and `calibrate --guided` (human navigates, plane observes) remains the live menu-mapping path. The
/// reusable win is the engine's VK-confirm capability (BotAction.Vk / send-input vk:), now available to every bot
/// for OTHER mouse-first menus.
///
/// DECISION RE-CONFIRMED 2026-07-02 evening (task #171, bounded): the CP config-file + `-benchmark` path was
/// re-proven 3/3 the same day in the first Run-Plan comparison sweep (rt-dlss-q 50.3 fps, rt-native 27.9 fps,
/// both 0 invalid) — Cyberpunk needs NO menu navigation to set graphics or run the benchmark, so an open-loop
/// CP Drive nav graph would be a throwaway with zero operational value AND is known-blocked (±1 first-tap +
/// fading highlight). The Drive fields therefore stay EMPTY BY DESIGN, not as a gap. Note the closed-loop
/// "grab → send-input → read-back" pattern (a human/vision eye in the loop) was used the same day to verify the
/// TLOU and FH6 menu enums live — so the capability to drive these menus closed-loop is proven; it is simply not
/// wired as an autonomous CP Drive because CP is the one roster game that categorically doesn't need it.
/// </summary>
public sealed class CyberpunkCalibrationPlugin : IGameCalibrationPlugin
{
    public string Game => "cyberpunk-2077";
    public string Name => "Cyberpunk 2077";
    public string? BenchmarkScreenId => "benchmark_launcher";
    public IReadOnlyList<string> SettingsControls => new[] { "Resolution", "DLSS", "Ray Tracing", "Frame Generation" };

    public IReadOnlyList<ExpectedScreen> ExpectedScreens { get; } = new[]
    {
        new ExpectedScreen
        {
            Id = "main_menu",
            SemanticHint = "Cyberpunk 2077 main menu",
            Anchors = new[] { "Continue", "Settings", "Quit" },
            InputFromPrevious = "",
            SyntheticOcr = new[] { "CYBERPUNK", "Continue", "New", "Game", "Load", "Settings", "Quit" }
        },
        new ExpectedScreen
        {
            Id = "settings",
            SemanticHint = "Settings hub (Gameplay / Video / Graphics / Audio tabs)",
            Anchors = new[] { "Graphics", "Video", "Audio" },
            InputFromPrevious = "From the main menu: home up, Down x3 to Settings, then confirm",
            // LIVE-VALIDATED 2026-06-28 (Cyberpunk2077.exe, windowed): scancode arrows navigate, and a VIRTUAL-KEY
            // confirm OPENS this page — "Up,Up,Up,Up,Up,wait:300,Down,Down,Down,vk:Enter" reached + opened SETTINGS
            // (scancode Enter did nothing; vk:Enter worked, vk:Esc closed it). The recipe is proven. It is NOT baked
            // as a Drive because open-loop positioning is ±1 unreliable (first-tap doubles/eats + the highlight fades),
            // so a FIXED sequence can't dependably land on this MIDDLE item — I only reached it by observing the
            // cursor and correcting (closed-loop). A hands-off Cyberpunk menu nav needs the suite's closed-loop
            // navigator emitting a VK confirm, not this open-loop field. Operationally moot (CP = config-file +
            // -benchmark). Drive left EMPTY; use `calibrate --guided`. See the class summary.
            Drive = "",
            SyntheticOcr = new[] { "SETTINGS", "Gameplay", "Video", "Graphics", "Audio", "Controls" }
        },
        new ExpectedScreen
        {
            Id = "graphics_settings",
            SemanticHint = "Graphics options page exposing the upscaler, ray tracing and frame generation",
            Anchors = new[] { "Resolution", "Graphics" },
            InputFromPrevious = "Right to Graphics tab (reachable only once inside Settings)",
            // Unreachable by --drive on Cyberpunk: entering Settings needs a confirm the menu won't accept from
            // injection (see the 'settings' note + class summary). Left EMPTY → use --guided. Once inside
            // Settings the in-page tab move is "Right,Right".
            Drive = "",
            Controls = new[] { "Resolution", "DLSS", "Ray Tracing", "Frame Generation" },
            SyntheticOcr = new[] { "GRAPHICS", "Resolution", "3840x2160", "DLSS", "Quality", "Ray", "Tracing", "Frame", "Generation", "Run", "Benchmark" }
        },
        new ExpectedScreen
        {
            Id = "benchmark_launcher",
            SemanticHint = "The Run Benchmark entry on the graphics page",
            Anchors = new[] { "Benchmark" },
            InputFromPrevious = "PressUntilText 'Benchmark' + Enter",
            SyntheticOcr = new[] { "Run", "Benchmark", "Start" }
        }
    };

    public IReadOnlyDictionary<string, IReadOnlyList<OcrWord>> BuildSyntheticScript() => PluginScript.From(ExpectedScreens);
}

/// <summary>Resolves a calibration plugin by game id. The registry is the seam new games plug into (P4).</summary>
public static class CalibrationPluginRegistry
{
    private static readonly IGameCalibrationPlugin[] All = { new CyberpunkCalibrationPlugin() };

    public static IGameCalibrationPlugin? Resolve(string game)
        => All.FirstOrDefault(p => string.Equals(p.Game, game, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(p.Name, game, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<IGameCalibrationPlugin> Available => All;
}
