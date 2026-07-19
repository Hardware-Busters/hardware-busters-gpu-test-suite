using GpuSuite.Core.Models;
using GpuSuite.Calibration.Schema;

namespace GpuSuite.Calibration.Plugins;

/// <summary>
/// Generic calibration knowledge projected from an ordinary game profile. This gives every roster game
/// a useful Learn/ValidateRoute path without requiring a hand-written plugin first; specialist plugins can
/// still override it when they have stronger OCR anchors and executable menu navigation.
/// </summary>
public sealed class ProfileCalibrationPlugin : IGameCalibrationPlugin
{
    public string Game { get; }
    public string Name { get; }
    public IReadOnlyList<ExpectedScreen> ExpectedScreens { get; }
    public string? BenchmarkScreenId { get; }
    public IReadOnlyList<string> SettingsControls { get; }

    public ProfileCalibrationPlugin(GameProfile profile)
    {
        Game = profile.Id;
        Name = profile.Name;
        SettingsControls = profile.Settings.Select(s => s.Label).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

        var screens = new List<ExpectedScreen>
        {
            new()
            {
                Id = "main_menu", SemanticHint = $"{profile.Name} main menu",
                Anchors = new[] { "Settings", "Play" }, SyntheticOcr = new[] { profile.Name, "Play", "Settings", "Quit" }
            }
        };
        if (profile.MenuMap is not null || profile.Settings.Any(s => string.Equals(s.Apply.Method, "menu", StringComparison.OrdinalIgnoreCase)))
        {
            screens.Add(new ExpectedScreen
            {
                Id = "graphics_settings", SemanticHint = "Graphics/settings menu declared by the game profile",
                Anchors = SettingsControls.SelectMany(Words).Take(2).DefaultIfEmpty("Graphics").ToArray(),
                Controls = SettingsControls.ToArray(), InputFromPrevious = "profile menuMap open sequence",
                SyntheticOcr = SettingsControls.Prepend("Graphics").ToArray()
            });
        }

        var scene = profile.Scenes.FirstOrDefault();
        if (scene is not null)
        {
            BenchmarkScreenId = "scene_" + scene.Id;
            screens.Add(new ExpectedScreen
            {
                Id = BenchmarkScreenId, SemanticHint = $"Launcher/readiness screen for {scene.Name}",
                Anchors = Words(scene.Name).Take(2).DefaultIfEmpty("Start").ToArray(),
                InputFromPrevious = !string.IsNullOrWhiteSpace(scene.StartBotScript) ? $"bot:{scene.StartBotScript}"
                    : !string.IsNullOrWhiteSpace(scene.BotScript) ? $"bot:{scene.BotScript}" : "launch scene",
                SyntheticOcr = new[] { scene.Name, "Start", "Benchmark" }
            });
        }
        ExpectedScreens = screens;
    }

    private static IEnumerable<string> Words(string value) => value.Split(
        new[] { ' ', '\t', ':', '-', '—', '(', ')', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

    public IReadOnlyDictionary<string, IReadOnlyList<OcrWord>> BuildSyntheticScript() => PluginScript.From(ExpectedScreens);
}
