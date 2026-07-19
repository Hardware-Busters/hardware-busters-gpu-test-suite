using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpuSuite.App.Services;
using GpuSuite.Core.Models;

namespace GpuSuite.App.ViewModels;

/// <summary>
/// Benchmark List: master/detail over the user's game profiles, resolved against installed games.
/// The list is authoritative; discovery only tags each row's status. Reloads when the workspace changes.
/// </summary>
public partial class GameListViewModel : ObservableObject
{
    private readonly ProfileService _profiles;
    private readonly DiscoveryService _discovery;
    private GameCatalog? _catalog;
    private readonly List<GameRowViewModel> _allGames = new();

    public ObservableCollection<GameRowViewModel> Games { get; } = new();

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string summary = "";
    [ObservableProperty] private bool showDisabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDetail))]
    private GameRowViewModel? selectedGame;

    [ObservableProperty] private GameDetailViewModel? selectedDetail;

    public GameListViewModel(ProfileService profiles, DiscoveryService discovery, Workspace ws)
    {
        _profiles = profiles;
        _discovery = discovery;
        ws.Changed += () => _ = LoadAsync();
        _ = LoadAsync();
    }

    partial void OnSelectedGameChanged(GameRowViewModel? value)
        => SelectedDetail = value is null ? null : new GameDetailViewModel(value, _profiles, OnRowSaved);

    partial void OnShowDisabledChanged(bool value) => RefreshVisibleGames();

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var profiles = await Task.Run(() => _profiles.LoadAll());
            _catalog = await _discovery.DiscoverAsync();
            var plan = _discovery.Resolve(profiles, _catalog);

            _allGames.Clear();
            foreach (var entry in plan.Entries)
                _allGames.Add(new GameRowViewModel(entry, _profiles, OnRowChanged));

            RefreshVisibleGames();
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Re-resolve a single row against the cached catalog (no rescan) after enable/disable or a save.
    private void OnRowChanged(GameRowViewModel row) => ReResolve(row);
    private void OnRowSaved(GameRowViewModel row) => ReResolve(row);

    private void ReResolve(GameRowViewModel row)
    {
        if (_catalog is null) return;
        var state = _discovery.Resolve(new[] { row.Game }, _catalog).Entries[0];
        row.Apply(state);
        RefreshVisibleGames();
    }

    private void RefreshVisibleGames()
    {
        Games.Clear();
        foreach (var game in _allGames.Where(g => ShowDisabled || g.Enabled))
            Games.Add(game);
        if (SelectedGame is not null && !Games.Contains(SelectedGame))
            SelectedGame = null;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int ready = _allGames.Count(g => g.Status == BenchmarkGameStatus.Ready);
        int enabled = _allGames.Count(g => g.Enabled);
        int hidden = _allGames.Count - enabled;
        Summary = $"{enabled} enabled · {ready} ready" + (hidden > 0 && !ShowDisabled ? $" · {hidden} disabled hidden" : "");
    }
}
