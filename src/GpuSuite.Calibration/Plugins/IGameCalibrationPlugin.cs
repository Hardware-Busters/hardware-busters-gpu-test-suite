using GpuSuite.Calibration.Schema;

namespace GpuSuite.Calibration.Plugins;

/// <summary>
/// One screen a game's calibration plugin expects the framework to find and map. Anchors are the robust
/// whole-word OCR markers that prove "this is that screen". <see cref="SyntheticOcr"/> is the offline-demo
/// data the stub observer replays so the Phase-2 skeleton can run the full pipeline without a live game;
/// a real calibration session reads these words off the capture card instead.
/// </summary>
public sealed class ExpectedScreen
{
    public string Id { get; set; } = "";
    public string SemanticHint { get; set; } = "";
    /// <summary>Whole-word OCR anchors that should be present on this screen.</summary>
    public string[] Anchors { get; set; } = Array.Empty<string>();
    /// <summary>The nav input that reaches this screen from the previous one (becomes a graph edge).</summary>
    public string InputFromPrevious { get; set; } = "";
    /// <summary>
    /// OPTIONAL machine-executable nav to reach this screen from the previous one, in the `send-input`
    /// key-sequence format (e.g. "Down,Down,Enter,wait:1200"). When set and <c>calibrate --drive</c> is used,
    /// the deterministic input ENGINE injects it (engine drives, NOT the LLM) so calibration is hands-off; empty
    /// means the operator navigates that step (<c>--guided</c>). The executable sibling of <see cref="InputFromPrevious"/>.
    /// </summary>
    public string Drive { get; set; } = "";
    /// <summary>Graphics controls expected on this screen (e.g. for the graphics page).</summary>
    public string[] Controls { get; set; } = Array.Empty<string>();
    /// <summary>Offline-demo OCR words for the stub observer (mirror of what a real grab would read).</summary>
    public string[] SyntheticOcr { get; set; } = Array.Empty<string>();
}

/// <summary>
/// A per-game calibration plugin (P4): the thin, game-specific KNOWLEDGE the generic framework consumes.
/// It declares the screens to look for, which one launches the benchmark (or null when the game has none),
/// and the graphics controls of interest. It contains DATA and hints, not behavior — the generic engine
/// does the observing, reasoning, graphing, and reporting.
/// </summary>
public interface IGameCalibrationPlugin
{
    /// <summary>Stable game id (matches the suite's GameProfile.Id), e.g. "cyberpunk-2077".</summary>
    string Game { get; }
    string Name { get; }
    IReadOnlyList<ExpectedScreen> ExpectedScreens { get; }
    /// <summary>The screen id that launches the built-in benchmark, or null if the game has no benchmark.</summary>
    string? BenchmarkScreenId { get; }
    /// <summary>The graphics-setting controls the suite cares about (Resolution, DLSS, RT, Frame Gen, …).</summary>
    IReadOnlyList<string> SettingsControls { get; }

    /// <summary>Build the stub-observer screen script (screen id → synthetic OCR words) for the offline demo.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<OcrWord>> BuildSyntheticScript();
}

/// <summary>Shared helper for plugins to turn anchor/word arrays into <see cref="OcrWord"/> lists.</summary>
public static class PluginScript
{
    public static IReadOnlyDictionary<string, IReadOnlyList<OcrWord>> From(IEnumerable<ExpectedScreen> screens)
    {
        var map = new Dictionary<string, IReadOnlyList<OcrWord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in screens)
        {
            var words = (s.SyntheticOcr.Length > 0 ? s.SyntheticOcr : s.Anchors)
                .Select(w => new OcrWord { Text = w, Confidence = 0.95 })
                .ToList();
            map[s.Id] = words;
        }
        return map;
    }
}
