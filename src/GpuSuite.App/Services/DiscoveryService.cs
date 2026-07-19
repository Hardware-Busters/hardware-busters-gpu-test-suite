using GpuSuite.Core.Models;
using GpuSuite.Engine.Discovery;

namespace GpuSuite.App.Services;

/// <summary>
/// Installed-game discovery + benchmark-list resolution. Mirrors Program.ResolvePlan: the user's
/// profiles are authoritative, discovery only answers "is this selected game installed, and where".
/// The expensive scan (<see cref="DiscoverAsync"/>) runs off the UI thread and its catalog is reused
/// by <see cref="Resolve"/> so a single enable/disable toggle re-resolves instantly (no rescan).
/// Discovery scans launchers/registry/folders only — never serial, so it is safe on any machine.
/// </summary>
public sealed class DiscoveryService
{
    public Task<GameCatalog> DiscoverAsync() => Task.Run(() => new LauncherDiscovery().DiscoverAll());

    public TestPlan Resolve(IReadOnlyList<GameProfile> profiles, GameCatalog catalog)
        => new TestPlanResolver().Resolve(profiles, catalog);

    /// <summary>
    /// Discover installed games and enrich/rank them (built-in bench, automation, GPU suitability),
    /// off the UI thread. Mirrors the `gpusuite catalog` verb — detection + advice only; it never
    /// selects or runs a game.
    /// </summary>
    public Task<EnrichedCatalog> BuildCatalogAsync()
        => Task.Run(() => new CatalogService().Build(new LauncherDiscovery().DiscoverAll()));
}
