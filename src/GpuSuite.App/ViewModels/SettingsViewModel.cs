using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpuSuite.App.Services;
using GpuSuite.Core.Config;
using GpuSuite.Core.Security;

namespace GpuSuite.App.ViewModels;

/// <summary>
/// Settings editor: a form over the suite's <see cref="SuiteConfig"/> (settings.json). Binds directly
/// to the live config instance; Save serializes it to disk. Reloads when the workspace folder changes.
///
/// Also hosts the "Vision compute" selector — choose whether the menu-nav VISION model runs on the local
/// GPU or on the lab GX10 box. The GX10 option is GREYED OUT until a live probe confirms the box exists;
/// if it does not, the local GPU handles the vision model. "Check GX10" runs the probe and shows the box's
/// statistics so you can confirm it before a test run.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly Workspace _ws;
    private readonly Gx10ProbeService _gx10;

    public SuiteConfig Config => _ws.Config;
    public ValidationThresholds Validation => _ws.Config.Validation;
    public string SettingsPath => _ws.SettingsPath;

    public string[] PowerSourceOptions { get; } = { "auto", "powenetics", "lhm", "synthetic" };
    public string[] FrameProviderOptions { get; } = { "presentmon", "rtss", "auto" };
    public string[] EffortOptions { get; } = { "none", "low", "medium", "high" };
    public string[] OpenAiModelOptions { get; } = { "gpt-5-mini", "gpt-5", "gpt-4.1-mini", "gpt-4.1" };
    public string[] AnthropicModelOptions { get; } = { "claude-sonnet-4-20250514", "claude-opus-4-20250514" };

    [ObservableProperty] private string saveStatus = "";

    /// <summary>True only when a live probe has confirmed the GX10 is reachable — gates the GX10 radio (greyout).</summary>
    [ObservableProperty] private bool gx10Available;
    /// <summary>True while a probe is in flight (disables the Check button + shows a spinner caption).</summary>
    [ObservableProperty] private bool checkingGx10;
    /// <summary>Human-readable result of the last GX10 probe (version / latency / models / loaded VRAM).</summary>
    [ObservableProperty] private string gx10StatusText = "Not checked yet — click \"Check Custom AI\".";
    /// <summary>What WILL actually run given the current mode + the last probe — the auto-selected target.</summary>
    [ObservableProperty] private string effectiveComputeText = "Checking Custom AI…";

    /// <summary>Reachability from the last probe — drives the auto-selected effective compute.</summary>
    private bool _lastGx10Up;

    /// <summary>Global repeats-per-cell (game × resolution × variant), edited in the UI and CLAMPED to the ≥3
    /// reviewer minimum. This is the baseline for every game; a game can request MORE via its own per-game
    /// override (Game detail), never fewer. The orchestrator floors at 3 too, so the run can't drop below it.</summary>
    public int RepeatsPerScene
    {
        get => Math.Max(3, Config.RepeatsPerScene);
        set { Config.RepeatsPerScene = Math.Max(3, value); OnPropertyChanged(); }
    }

    /// <summary>Resolutions edited as a comma-separated list; pushed into Config.Resolutions on edit.</summary>
    public string ResolutionsCsv
    {
        get => string.Join(", ", Config.Resolutions);
        set
        {
            Config.Resolutions = (value ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            OnPropertyChanged();
        }
    }

    // ── Vision-compute selection (radio buttons bind to these; only one is true at a time) ──
    /// <summary>Auto = use the GX10 when reachable, else the local GPU. Always selectable (safe fallback).</summary>
    public bool IsComputeAuto { get => ComputeIs("auto"); set { if (value) SetCompute("auto"); } }
    /// <summary>Force the LOCAL GPU for the vision model.</summary>
    public bool IsComputeGpu { get => ComputeIs("gpu"); set { if (value) SetCompute("gpu"); } }
    /// <summary>Force the GX10 box. Only selectable when <see cref="Gx10Available"/> (the radio is greyed out otherwise).</summary>
    public bool IsComputeGx10 { get => ComputeIs("gx10"); set { if (value) SetCompute("gx10"); } }
    public bool IsComputeOpenAi { get => ComputeIs("openai"); set { if (value) SetCompute("openai"); } }
    public bool IsComputeAnthropic { get => ComputeIs("anthropic"); set { if (value) SetCompute("anthropic"); } }
    public bool IsComputeCustom { get => ComputeIs("custom"); set { if (value) SetCompute("custom"); } }
    public bool HasOpenAiKey => !string.IsNullOrWhiteSpace(ApiKeyStore.ReadOrEnvironment("openai", "OPENAI_API_KEY"));
    public bool HasAnthropicKey => !string.IsNullOrWhiteSpace(ApiKeyStore.ReadOrEnvironment("anthropic", "ANTHROPIC_API_KEY"));
    public string CloudKeyStatus => $"OpenAI: {KeyStatus("openai", "OPENAI_API_KEY")}  ·  Anthropic: {KeyStatus("anthropic", "ANTHROPIC_API_KEY")}. Keys are never saved in this workspace.";

    public SettingsViewModel(Workspace ws, Gx10ProbeService gx10)
    {
        _ws = ws;
        _gx10 = gx10;
        _ws.Changed += OnWorkspaceChanged;
        _ = CheckGx10Async();   // probe once on startup so the greyout reflects reality
    }

    private bool ComputeIs(string v) => string.Equals((Config.VisionCompute ?? "auto").Trim(), v, StringComparison.OrdinalIgnoreCase);

    private void SetCompute(string v)
    {
        Config.VisionCompute = v;
        OnPropertyChanged(nameof(IsComputeAuto));
        OnPropertyChanged(nameof(IsComputeGpu));
        OnPropertyChanged(nameof(IsComputeGx10));
        OnPropertyChanged(nameof(IsComputeOpenAi));
        OnPropertyChanged(nameof(IsComputeAnthropic));
        OnPropertyChanged(nameof(IsComputeCustom));
        UpdateEffectiveCompute();
    }

    /// <summary>Resolve what WILL actually run (the auto-selection): GX10 when active, else the local GPU.</summary>
    private void UpdateEffectiveCompute()
    {
        string mode = (Config.VisionCompute ?? "auto").Trim().ToLowerInvariant();
        if (mode == "openai")
        {
            EffectiveComputeText = HasOpenAiKey
                ? $"▶ Active vision compute: OpenAI API ({Config.OpenAiVisionModel}) — off-bench."
                : "▶ Active vision compute: Local GPU — OpenAI selected but OPENAI_API_KEY is not set.";
            return;
        }
        if (mode == "anthropic")
        {
            EffectiveComputeText = HasAnthropicKey
                ? $"▶ Active vision compute: Anthropic API ({Config.AnthropicVisionModel}) — off-bench."
                : "▶ Active vision compute: Local GPU — Anthropic selected but ANTHROPIC_API_KEY is not set.";
            return;
        }
        if (mode == "custom")
        {
            EffectiveComputeText = string.IsNullOrWhiteSpace(Config.CustomVisionEndpoint)
                ? "▶ Active vision compute: Local GPU — custom local endpoint is not configured."
                : $"▶ Active vision compute: custom local Ollama server ({Config.CustomVisionEndpoint}).";
            return;
        }
        if (mode == "auto" && !_lastGx10Up && HasOpenAiKey)
        {
            EffectiveComputeText = $"▶ Active vision compute: OpenAI API ({Config.OpenAiVisionModel}) — Custom AI unavailable.";
            return;
        }
        if (mode == "auto" && !_lastGx10Up && HasAnthropicKey)
        {
            EffectiveComputeText = $"▶ Active vision compute: Anthropic API ({Config.AnthropicVisionModel}) — Custom AI/OpenAI unavailable.";
            return;
        }
        bool gx10 = mode switch
        {
            "gpu" => false,
            "gx10" => _lastGx10Up,   // explicit GX10 still auto-falls-back to the GPU when the box is offline
            _ => _lastGx10Up         // auto: GX10 if active, else GPU
        };
        EffectiveComputeText = gx10
            ? "▶ Active vision compute: Custom AI (online) — menu-nav model runs off-bench."
            : mode == "gpu" ? "▶ Active vision compute: Local GPU (forced)."
            : mode == "gx10" ? "▶ Active vision compute: Local GPU — Custom AI selected but offline (auto fallback)."
            : "▶ Active vision compute: Local GPU (Custom AI not active).";
    }

    private void OnWorkspaceChanged()
    {
        OnPropertyChanged(nameof(Config));
        OnPropertyChanged(nameof(Validation));
        OnPropertyChanged(nameof(RepeatsPerScene));
        OnPropertyChanged(nameof(ResolutionsCsv));
        OnPropertyChanged(nameof(SettingsPath));
        OnPropertyChanged(nameof(IsComputeAuto));
        OnPropertyChanged(nameof(IsComputeGpu));
        OnPropertyChanged(nameof(IsComputeGx10));
        OnPropertyChanged(nameof(IsComputeOpenAi));
        OnPropertyChanged(nameof(IsComputeAnthropic));
        OnPropertyChanged(nameof(IsComputeCustom));
        OnPropertyChanged(nameof(HasOpenAiKey));
        OnPropertyChanged(nameof(HasAnthropicKey));
        OnPropertyChanged(nameof(CloudKeyStatus));
        SaveStatus = "";
        _ = CheckGx10Async();   // re-probe against the (possibly new) endpoint
    }

    [RelayCommand]
    private void Save()
    {
        _ws.SaveConfig();
        SaveStatus = $"Saved · {DateTime.Now:HH:mm:ss}";
    }

    [RelayCommand]
    private void Reload()
    {
        _ws.Reload();   // raises Changed → OnWorkspaceChanged (which re-probes the GX10)
        SaveStatus = "Reloaded from disk";
    }

    /// <summary>Called by the Settings view's PasswordBox buttons. The clear-text value exists only for this call,
    /// then is encrypted by Windows Credential Manager; it is never assigned to Config or written to a log.</summary>
    public bool SaveApiKey(string provider, string secret)
    {
        bool saved = ApiKeyStore.Save(provider, secret);
        SaveStatus = saved ? $"{provider} API key saved securely in Windows Credential Manager." : $"Could not save the {provider} API key.";
        RefreshKeyStatus();
        UpdateEffectiveCompute();
        return saved;
    }

    [RelayCommand]
    private void RemoveOpenAiKey() => RemoveApiKey("openai");

    [RelayCommand]
    private void RemoveAnthropicKey() => RemoveApiKey("anthropic");

    private void RemoveApiKey(string provider)
    {
        bool removed = ApiKeyStore.Delete(provider);
        SaveStatus = removed ? $"{provider} saved API key removed." : $"No saved {provider} API key was found.";
        RefreshKeyStatus();
        UpdateEffectiveCompute();
    }

    private static string KeyStatus(string provider, string environmentVariable) => ApiKeyStore.IsStored(provider)
        ? "saved securely"
        : !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentVariable)) ? "available from environment"
        : "not configured";

    private void RefreshKeyStatus()
    {
        OnPropertyChanged(nameof(HasOpenAiKey));
        OnPropertyChanged(nameof(HasAnthropicKey));
        OnPropertyChanged(nameof(CloudKeyStatus));
    }

    /// <summary>Probe the GX10 box and update availability + the statistics readout. Never throws.</summary>
    [RelayCommand]
    private async Task CheckGx10Async()
    {
        if (CheckingGx10) return;
        CheckingGx10 = true;
        Gx10StatusText = $"Probing {Config.Gx10Endpoint} …";
        try
        {
            var s = await _gx10.ProbeAsync();
            Gx10Available = s.Reachable;
            _lastGx10Up = s.Reachable;
            // Auto-selection: when GX10 is active and the mode is "auto", select it (effective = GX10); else GPU.
            if (s.Reachable && string.Equals((Config.VisionCompute ?? "auto").Trim(), "auto", StringComparison.OrdinalIgnoreCase))
                OnPropertyChanged(nameof(IsComputeAuto));   // keep "Auto" mode but reflect the resolved target below
            if (s.Reachable)
            {
                bool hasVision = s.HasModel(Config.NavSupervisorVisionModel);
                string loaded = s.Loaded.Count == 0
                    ? "none loaded"
                    : "loaded: " + string.Join(", ", s.Loaded.Select(l => $"{l.Name} {l.VramGb:0.0} GB"));
                Gx10StatusText =
                    $"ONLINE · v{s.Version} · {s.LatencyMs:0} ms · {s.Models.Count} models " +
                    $"({s.VisionModels.Count()} vision) · {loaded} · " +
                    (hasVision ? $"'{Config.NavSupervisorVisionModel}' present ✓" : $"'{Config.NavSupervisorVisionModel}' NOT pulled");
            }
            else
            {
                Gx10StatusText = $"Offline ({s.Error}) — Custom AI unavailable; the local GPU will handle the vision model.";
            }
        }
        catch (Exception ex)
        {
            Gx10Available = false;
            Gx10StatusText = $"Probe error: {ex.Message}";
        }
        finally
        {
            CheckingGx10 = false;
            // The GX10 radio's enabled state changed — refresh anything bound to it, and recompute what the
            // auto-selection now resolves to (GX10 when active, else the local GPU).
            OnPropertyChanged(nameof(IsComputeGx10));
            UpdateEffectiveCompute();
        }
    }
}
