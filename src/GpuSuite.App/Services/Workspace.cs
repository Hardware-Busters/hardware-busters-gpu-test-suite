using System.IO;
using GpuSuite.Core.Config;

namespace GpuSuite.App.Services;

/// <summary>
/// The working root the UI operates on: the folder holding settings.json + profiles/ + Results/
/// (the same layout the CLI assumes via --root). Holds the loaded <see cref="SuiteConfig"/> and
/// resolves the canonical paths. On the dev PC it auto-detects the repo root; the user can repoint
/// it (so on the Test PC it can target the bench's working copy).
/// </summary>
public sealed class Workspace
{
    private readonly ConfigService _config;

    public string Root { get; private set; }
    public SuiteConfig Config { get; private set; }

    public string SettingsPath => Path.Combine(Root, "settings.json");
    public string ProfilesDir => Resolve(Config.ProfilesDir, "profiles");
    public string ResultsDir => Resolve(Config.ResultsRoot, "Results");

    /// <summary>Raised when the root or config changes (so view models reload).</summary>
    public event Action? Changed;

    public Workspace(ConfigService config)
    {
        _config = config;
        Root = ResolveDefaultRoot();
        ApplyWorkingDirectory();
        Config = _config.Load(SettingsPath);
    }

    public void SetRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        Root = root;
        ApplyWorkingDirectory();
        Reload();
    }

    /// <summary>
    /// Make the process working directory the suite root, exactly like the CLI (Program.Main does
    /// Directory.SetCurrentDirectory(root)). The engine resolves several paths relative to the CWD —
    /// PresentMonPath ("tools/PresentMon/..."), ResultsRoot ("Results"), etc. — so without this the
    /// probe wouldn't find PresentMon and run output would land under the app's bin folder.
    /// </summary>
    private void ApplyWorkingDirectory()
    {
        try { Directory.SetCurrentDirectory(Root); } catch { /* best-effort */ }
    }

    /// <summary>Re-read settings.json from disk and notify.</summary>
    public void Reload()
    {
        Config = _config.Load(SettingsPath);
        Changed?.Invoke();
    }

    /// <summary>Persist the current in-memory config to settings.json.</summary>
    public void SaveConfig() => _config.Save(SettingsPath, Config);

    /// <summary>Notify all profile/run views after a pack is installed, enabled, disabled or removed.</summary>
    public void NotifyContentChanged() => Changed?.Invoke();

    private string Resolve(string? configured, string fallback)
    {
        var dir = string.IsNullOrWhiteSpace(configured) ? fallback : configured;
        return Path.IsPathRooted(dir) ? dir : Path.Combine(Root, dir);
    }

    /// <summary>Walk up from the app's base dir to the suite root (settings.json / profiles / .sln).</summary>
    private static string ResolveDefaultRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "settings.json")) ||
                Directory.Exists(Path.Combine(dir.FullName, "profiles")) ||
                File.Exists(Path.Combine(dir.FullName, "GpuTestSuite.sln")))
                return dir.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
