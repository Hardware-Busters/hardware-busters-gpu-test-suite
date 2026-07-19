using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GpuSuite.App.ViewModels;

public sealed record HelpTopic(string Section, string Title, string Body, string Keywords);

public partial class HelpViewModel : ObservableObject
{
    private readonly List<HelpTopic> _all =
    [
        new("Dashboard", "Dashboard status", "Shows the active workspace, detected measurement providers, installed-game readiness, and recent result status. Use it as the lab's at-a-glance health page.", "overview providers status"),
        new("Benchmark List", "Benchmark profiles", "Enable only production benchmark profiles. Select a game to edit supported resolutions, graphics models, scenes, automation, completion detection, and validation timing.", "profiles enable scenes models"),
        new("Installed Games", "Installation discovery", "Lists games detected through Steam, Epic, Ubisoft, Xbox/MS Store, and standalone paths. Discovery confirms installation; the full pre-flight additionally checks update confidence and automation readiness.", "discovery stores update ready"),
        new("Profile packs", "Install benchmark knowledge", "Profile packs add game profiles, bots, routes and templates without replacing the benchmark engine. Hardware Busters Verified packs are bundled and curated; community/local packs are clearly labelled, validated for compatibility, and can be disabled or removed independently.", "pack install community verified profile bot route"),
        new("Author Studio", "Quick start: create your first pack", "1. Select Create draft and choose an empty working folder.\n2. Complete the pack metadata on the Pack tab and save it.\n3. Create a profile, add its resolutions, scenes, settings and variants, then select Save profile.\n4. Create the required bots and routes, saving each document explicitly.\n5. Select Validate and resolve every problem.\n6. Export the draft as a .gtsprofilepack file.\n7. Open Profile Packs, install the exported file, and run Full pre-flight. Qualify a new pack with one repeat before using it in a long sweep.", "author studio beginner tutorial first pack create draft save validate export install preflight"),
        new("Author Studio", "Profiles, settings, and variants", "A profile tells the engine how to find and launch a game, which process to capture, which resolutions and graphics models are supported, and which scenes to measure. Define each setting and its allowed options before using it in a variant. Config-file and registry operations apply the selected values before launch. Use variants when one game needs distinct launch or configuration behavior, and select Save profile when the document is ready.", "author studio profile launch store process resolution scene setting option variant config registry"),
        new("Author Studio", "Bots and navigation graphs", "A bot is an ordered list of actions that takes the game from launch to a repeatable measured scene. Start with the friendly common-step presets; each adds safe defaults and shows guidance for the selected step. Select the correct input device, place MarkStart and MarkEnd around only the measured section, and require OCR or other evidence before important input. Required OCR gates fail closed when the expected screen is not proven. Open the advanced reactive graph only when alternate menu states cannot be handled by a linear timeline. Graph screens use Any, All and Excluded text signatures to choose steps toward a declared goal. Author Studio edits definitions only; it never sends live mouse, keyboard or controller input to a game.", "author studio bot action preset common step markstart markend ocr gate fail closed graph any all excluded screen goal safety input"),
        new("Author Studio", "Record, import, and replay a route", "Route discovery runs on the real bench outside Author Studio. Configure a SmartTraverse or Gx10Traverse bot action with a Record path, run the discovery workflow, then import the generated route JSON on the Routes tab. Preview the recorded device and steps, make any deterministic edits, save the route, and reference it from a ReplayRoute action. Keep MarkStart and MarkEnd around the stable measured window, not menus or discovery movement.", "author studio route record discovery smarttraverse gx10traverse import json replayroute measured window"),
        new("Author Studio", "Templates and assets", "Templates are editable text files used to produce game configuration. Assets are supporting images or documentation; they are not executable content. Import them through the Templates & assets tab so they are copied into the draft. Every pack path must be relative, stay inside its declared folder, and pass validation. An imported asset may be at most 16 MiB. Never include passwords, tokens, personal data, copyrighted game files, or save-game data.", "author studio template asset import relative path 16 mib secret copyrighted save data"),
        new("Author Studio", "Save, validate, export, and install", "Author Studio saves each document explicitly: metadata, profiles, bots, routes and templates have their own Save action. Validate after structural changes and again before export. Export creates a portable .gtsprofilepack archive without modifying the draft. Install that archive from Profile Packs, review its publisher and validation status, enable it, then run Full pre-flight. A clean pre-flight proves readiness; a successful one-repeat qualification proves the authored path on the current machine.", "author studio save validate problems export gtsprofilepack profile packs install enable full preflight qualification"),
        new("Author Studio", "Safe editing and pack trust", "Author Studio works only in unpacked draft folders. Installed and bundled packs remain read-only: clone one into a new draft before changing it. Undo and Redo apply to the current edited document, while unmodeled JSON fields are retained when possible. Review imported content and trust the publisher before installation because profiles can launch programs, change declared configuration or registry values, and drive game input during a real run. ESC aborts a live run; pressing ESC three times force-releases an input lock.", "author studio safety trust clone installed bundled read only undo redo unknown json publisher escape abort"),
        new("Run", "Quick, Standard, and Full presets", "Quick runs every runnable game at the highest configured resolution with default models and 3 repeats. Standard adds all configured resolutions. Full adds every model and uses 5 repeats.", "preset plan selection"),
        new("Run", "Measured time versus scene budget", "Fixed scenes measure their Capture value. Marker-driven bots report only the actions between MarkStart and MarkEnd. A value such as TLOU's 510 seconds is the maximum automation deadline; its measured traversal is 35 seconds.", "capture warmup budget 510 35 timing"),
        new("Run", "Live progress and ETA", "The progress strip reports the current game, scene, resolution, model, repeat, and cell. ETA learns from completed cells in the current sweep, so it becomes more accurate as the run proceeds.", "progress remaining repeat cell"),
        new("Run", "Full pre-flight and override", "Start automatically checks the machine, measurement bench, every enabled game's installation, launcher login, update state, automation, and settings without launching a game. Known failures block the sweep; inconclusive online checks remain visible warnings. During a long campaign, each game is checked again immediately before its turn so a later logout or queued update cannot silently waste the roster. The advanced override is intentionally explicit and should be used only after reviewing every blocker.", "preflight blocker override login update roster"),
        new("Cooler", "Cooler evaluation", "Runs controlled GPU load steps while recording temperatures, fan speed, power, and settling behavior. Each new fan curve gets an unrecorded low-power conditioning soak. Missing/stale temperature telemetry or a thermal safety ceiling stops the load and restores the automatic fan curve. Use this separately from game-performance sweeps.", "fan temperature load thermal conditioning safety"),
        new("Results", "Saved results and comparisons", "Select a saved result to inspect aggregates and open its HTML report. The comparison panel matches identical game, scene, resolution, and model cells and reports target-versus-baseline percentage changes.", "compare fps low power efficiency"),
        new("Bot diagnostics", "Bot and runtime-health evidence", "Summarizes the latest saved result for each game, even after a targeted rerun. Open validation files, incident screenshots, and evidence folders when navigation, motion, capture, or process health fails.", "bot retry incident screenshot evidence"),
        new("Settings", "Bench configuration", "Configures measurement providers, capture card, paths, safety limits, validation thresholds, vision navigation, and unattended input protection. Save only after confirming the active workspace.", "configuration presentmon rtss powenetics gx10"),
        new("General", "Hardware Busters disclaimer", "The complete software, safety, privacy, third-party service, warranty, and liability notice is shown for acknowledgement on first launch and whenever its version changes. Hardware Busters and Cybenetics LTD cooperate but are separate entities; Powenetics is supplied and sold by Cybenetics LTD. Reopen the notice at any time with Disclaimer in the title bar.", "disclaimer hardware busters cybenetics powenetics legal safety privacy warranty liability acknowledgement"),
        new("Support", "Optional Patreon support", "Patreon support is optional and unrelated to features, support priority, benchmark results, or Hardware Busters Verified status.", "patreon optional support hardware busters"),
        new("General", "Open public issue", "Use Open public issue in the title bar whenever you find a bug or improvement. The app opens the public issue form in your browser. Describe the finding without including secrets or private information.", "public issue feedback privacy"),
        new("General", "Never-fake validation", "A run is rejected when navigation misses the measured scene, frames are absent, the process exits, the render freezes, the scene becomes static, or telemetry is untrustworthy. Invalid attempts are never silently averaged into results.", "validation invalid retry trustworthy"),
        new("General", "Emergency stop", "During a real unattended sweep physical input is locked to protect automation. Press ESC to abort; ESC three times force-releases the input lock if the engine stops responding.", "escape abort input lock")
    ];

    public ObservableCollection<HelpTopic> Topics { get; } = new();

    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string summary = "";

    public HelpViewModel() => Filter();

    partial void OnSearchTextChanged(string value) => Filter();

    public void OpenSection(string section)
    {
        SearchText = section;
    }

    private void Filter()
    {
        string query = (SearchText ?? "").Trim();
        var matches = string.IsNullOrWhiteSpace(query)
            ? _all
            : _all.Where(topic => string.Join(' ', topic.Section, topic.Title, topic.Body, topic.Keywords)
                .Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        Topics.Clear();
        foreach (var topic in matches) Topics.Add(topic);
        Summary = string.IsNullOrWhiteSpace(query)
            ? $"{Topics.Count} help topics · press F1 anywhere for page-specific help"
            : $"{Topics.Count} topic(s) matching “{query}”";
    }
}
