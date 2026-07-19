using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpuSuite.App.Services;
using GpuSuite.Engine.Discovery;

namespace GpuSuite.App.ViewModels;

/// <summary>
/// Installed Games (Catalog): a read-only view of every game discovered across launchers/registry,
/// enriched with built-in-benchmark / automation / GPU-suitability annotations and a recommended
/// first target. Mirrors `gpusuite catalog`. Detection + advice only — it never selects or runs a game.
/// Loaded lazily on first view so it doesn't double-scan at app startup alongside the Benchmark List.
/// </summary>
public partial class CatalogViewModel : ObservableObject
{
    private readonly DiscoveryService _discovery;
    private bool _loadedOnce;

    public ObservableCollection<CatalogRow> Rows { get; } = new();

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private string summary = "";
    [ObservableProperty] private CatalogRow? recommended;
    [ObservableProperty] private bool hasRecommended;

    public CatalogViewModel(DiscoveryService discovery) => _discovery = discovery;

    /// <summary>Load once on first navigation; subsequent visits reuse the result (use Reload to refresh).</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loadedOnce) return;
        _loadedOnce = true;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var cat = await _discovery.BuildCatalogAsync();
            Rows.Clear();
            foreach (var r in cat.Rows) Rows.Add(r);
            Recommended = cat.Recommended;
            HasRecommended = cat.Recommended is not null;
            IsEmpty = cat.Count == 0;
            Summary = cat.Count == 1 ? "1 installed game discovered" : $"{cat.Count} installed games discovered";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
