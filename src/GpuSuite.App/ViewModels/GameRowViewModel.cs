using CommunityToolkit.Mvvm.ComponentModel;
using GpuSuite.App.Services;
using GpuSuite.Core.Models;

namespace GpuSuite.App.ViewModels;

/// <summary>
/// One row in the Benchmark List: a <see cref="GameProfile"/> plus its resolved
/// <see cref="BenchmarkGameStatus"/>. Toggling <see cref="Enabled"/> persists the profile JSON
/// immediately and asks the parent to re-resolve this row's status.
/// </summary>
public partial class GameRowViewModel : ObservableObject
{
    private readonly ProfileService _profiles;
    private readonly Action<GameRowViewModel> _onEnabledChanged;

    public GameProfile Game { get; }

    public string Id => Game.Id;
    public string Name => string.IsNullOrWhiteSpace(Game.Name) ? Game.Id : Game.Name;
    public string StoreLabel => string.IsNullOrWhiteSpace(Game.Launch.Store) ? "—" : Game.Launch.Store;
    public int SceneCount => Game.Scenes.Count;
    public int ModelCount => Game.Variants.Count == 0 ? 1 : Game.Variants.Count(v => v.Enabled);
    public int VariantCount => Game.Variants.Count;

    [ObservableProperty] private BenchmarkGameStatus status;
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private string note = "";

    public bool Enabled
    {
        get => Game.Enabled;
        set
        {
            if (Game.Enabled == value) return;
            Game.Enabled = value;
            OnPropertyChanged();
            _profiles.Save(Game);
            _onEnabledChanged(this);
        }
    }

    public GameRowViewModel(BenchmarkGameState state, ProfileService profiles, Action<GameRowViewModel> onEnabledChanged)
    {
        _profiles = profiles;
        _onEnabledChanged = onEnabledChanged;
        Game = state.Game;
        Apply(state);
    }

    /// <summary>Update the resolved status from a fresh resolve.</summary>
    public void Apply(BenchmarkGameState state)
    {
        Status = state.Status;
        StatusText = Humanize(state.Status);
        Note = state.Notes.Count > 0 ? state.Notes[0] : "";
        RefreshComputed();
    }

    /// <summary>Re-raise computed properties after an edit (e.g. variant toggles in the detail).</summary>
    public void RefreshComputed()
    {
        OnPropertyChanged(nameof(ModelCount));
        OnPropertyChanged(nameof(VariantCount));
        OnPropertyChanged(nameof(SceneCount));
        OnPropertyChanged(nameof(Name));
    }

    private static string Humanize(BenchmarkGameStatus s) => s switch
    {
        BenchmarkGameStatus.NotInstalled => "Not installed",
        _ => s.ToString()
    };
}
