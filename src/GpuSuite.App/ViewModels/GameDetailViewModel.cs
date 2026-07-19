using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpuSuite.App.Services;
using GpuSuite.Core.Models;

namespace GpuSuite.App.ViewModels;

/// <summary>
/// Detail + editor for one game profile. Surfaces launch/capture (read-only here) and provides
/// structured editing of run config, Scenes, and Variants ("extra models"), persisted with Save.
/// The MenuMap (vision navigation) is shown structured: its top-level timing/anchor scalars are
/// editable, while the Open/Back step lists and per-control descriptors stay read-only because they
/// are calibrated off the capture card via the CLI vision tools (`vision`, `menu-apply`, `send-input`).
/// </summary>
public partial class GameDetailViewModel : ObservableObject
{
    private readonly GameRowViewModel _row;
    private readonly ProfileService _profiles;
    private readonly Action<GameRowViewModel> _onSaved;

    public GameProfile Game => _row.Game;

    public string Title => _row.Name;
    public string Subtitle => $"{Game.Id}  ·  {(string.IsNullOrWhiteSpace(Game.Engine) ? "engine n/a" : Game.Engine)}";

    // ----- read-only launch/capture facts -----
    public string LaunchStore => string.IsNullOrWhiteSpace(Game.Launch.Store) ? "—" : Game.Launch.Store;
    public string LaunchId => string.IsNullOrWhiteSpace(Game.Launch.GameId) ? "—" : Game.Launch.GameId;
    public string LaunchArgs => string.IsNullOrWhiteSpace(Game.Launch.Arguments) ? "—" : Game.Launch.Arguments;
    public string CaptureProcess => string.IsNullOrWhiteSpace(Game.CaptureProcessName) ? "—" : Game.CaptureProcessName;
    public string FrameProvider => string.IsNullOrWhiteSpace(Game.FrameProvider) ? "(global)" : Game.FrameProvider!;
    public string ResolutionsText => Game.SupportedResolutions.Count > 0
        ? string.Join(", ", Game.SupportedResolutions)
        : "(suite default set)";
    public bool HasMenuMap => Game.MenuMap is not null;
    public string MenuMapText => Game.MenuMap is null
        ? "not calibrated"
        : $"calibrated ({Game.MenuMap.InputDevice}, {Game.MenuMap.Controls.Count} control(s))";

    // ----- menu-map (vision navigation) structured view -----
    public IReadOnlyList<string> MenuInputDevices { get; } = new[] { "keyboard", "gamepad" };
    public bool HasOpenSteps => Game.MenuMap?.Open.Count > 0;
    public bool HasBackSteps => Game.MenuMap?.Back.Count > 0;
    public bool HasMenuControls => Game.MenuMap?.Controls.Count > 0;
    public string FocusHeaderText => Game.MenuMap?.FocusHeader is { } r
        ? $"x [{r.XMin:0.00}–{r.XMax:0.00}]   y [{r.YMin:0.00}–{r.YMax:0.00}]"
        : "none — falls back to count-based stepping (home-up + down-from-top)";

    /// <summary>Repeats for THIS game — the per-game override. Clamped to the ≥3 reviewer minimum; set it ABOVE the
    /// global default (Settings) to run this game more times for tighter confidence. Persisted to the profile on Save.</summary>
    public int Repeats
    {
        get => Math.Max(3, Game.Repeats);
        set { Game.Repeats = Math.Max(3, value); OnPropertyChanged(); }
    }

    public IReadOnlyList<GameSetting> Settings => Game.Settings;
    public bool HasSettings => Game.Settings.Count > 0;

    // ----- editable collections -----
    public IReadOnlyList<SceneKind> SceneKinds { get; } = Enum.GetValues<SceneKind>();
    public ObservableCollection<SceneProfile> SceneList { get; } = new();
    public ObservableCollection<VariantEditViewModel> VariantList { get; } = new();

    [ObservableProperty] private string saveStatus = "";

    public GameDetailViewModel(GameRowViewModel row, ProfileService profiles, Action<GameRowViewModel> onSaved)
    {
        _row = row;
        _profiles = profiles;
        _onSaved = onSaved;

        foreach (var s in Game.Scenes) SceneList.Add(s);
        foreach (var v in Game.Variants) VariantList.Add(new VariantEditViewModel(v, Game.Settings));
    }

    [RelayCommand]
    private void AddScene()
    {
        var n = SceneList.Count + 1;
        SceneList.Add(new SceneProfile { Id = $"scene-{n}", Name = $"Scene {n}", Kind = SceneKind.FixedWindow });
    }

    [RelayCommand]
    private void RemoveScene(SceneProfile? scene)
    {
        if (scene is not null) SceneList.Remove(scene);
    }

    [RelayCommand]
    private void AddVariant()
    {
        var n = VariantList.Count + 1;
        var v = new GameVariant { Id = $"variant-{n}", Name = $"Model {n}", Enabled = true };
        VariantList.Add(new VariantEditViewModel(v, Game.Settings));
    }

    [RelayCommand]
    private void RemoveVariant(VariantEditViewModel? variant)
    {
        if (variant is not null) VariantList.Remove(variant);
    }

    [RelayCommand]
    private void Save()
    {
        // Push the edited collections back onto the profile, then persist.
        Game.Repeats = Math.Max(3, Game.Repeats);   // enforce the ≥3 minimum on persist (normalizes any legacy repeats:1)
        Game.Scenes = SceneList.ToList();
        Game.Variants = VariantList.Select(v => v.Model).ToList();
        _profiles.Save(Game);
        _row.RefreshComputed();
        _onSaved(_row);
        SaveStatus = $"Saved {DateTime.Now:HH:mm:ss}";
    }
}

/// <summary>Editable wrapper for one <see cref="GameVariant"/> ("extra model"): its identity fields
/// bind to the POCO; its setting overrides are edited per declared <see cref="GameSetting"/>.</summary>
public sealed class VariantEditViewModel
{
    public GameVariant Model { get; }
    public ObservableCollection<SettingOverrideRow> Overrides { get; } = new();
    public bool HasOverrides => Overrides.Count > 0;

    public VariantEditViewModel(GameVariant model, IReadOnlyList<GameSetting> settings)
    {
        Model = model;
        foreach (var s in settings) Overrides.Add(new SettingOverrideRow(model.Settings, s));
    }
}

/// <summary>
/// One row in a variant's settings-override editor: a declared <see cref="GameSetting"/> whose value
/// for this variant is "(default)" (not overridden) or one of the setting's options. Reads/writes
/// straight into the variant's override dictionary.
/// </summary>
public sealed class SettingOverrideRow
{
    public const string DefaultToken = "(default)";

    private readonly Dictionary<string, string> _dict;
    private readonly GameSetting _setting;

    public SettingOverrideRow(Dictionary<string, string> dict, GameSetting setting)
    {
        _dict = dict;
        _setting = setting;
        Options = new[] { DefaultToken }.Concat(setting.Options).ToList();
    }

    public string Key => _setting.Key;
    public string Label => string.IsNullOrWhiteSpace(_setting.Label) ? _setting.Key : _setting.Label;
    public string DefaultHint => $"default: {(_setting.Default.Length == 0 ? "—" : _setting.Default)}";
    public IReadOnlyList<string> Options { get; }
    public bool HasOptions => _setting.Options.Count > 0;

    /// <summary>The variant's value for this setting; "(default)" means no override (key removed).</summary>
    public string Value
    {
        get => _dict.TryGetValue(_setting.Key, out var v) ? v : DefaultToken;
        set
        {
            if (string.IsNullOrEmpty(value) || value == DefaultToken) _dict.Remove(_setting.Key);
            else _dict[_setting.Key] = value;
        }
    }
}
