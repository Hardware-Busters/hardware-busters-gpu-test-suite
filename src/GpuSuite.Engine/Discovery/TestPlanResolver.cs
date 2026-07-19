using GpuSuite.Core.Models;

namespace GpuSuite.Engine.Discovery;

/// <summary>
/// Resolves the user-controlled benchmark list against the discovered catalog into a TestPlan.
/// The list is authoritative: discovery only answers "is this selected game installed, and where".
/// A discovered game that is NOT in the benchmark list is never executed.
/// </summary>
public sealed class TestPlanResolver
{
    public TestPlan Resolve(IReadOnlyList<GameProfile> benchmarkList, GameCatalog catalog)
    {
        var plan = new TestPlan();
        foreach (var game in benchmarkList)
            plan.Entries.Add(ResolveOne(game, catalog));
        return plan;
    }

    private static BenchmarkGameState ResolveOne(GameProfile game, GameCatalog catalog)
    {
        var state = new BenchmarkGameState { Game = game };

        if (!game.Enabled)
        {
            state.Status = BenchmarkGameStatus.Disabled;
            state.Notes.Add("Disabled in the benchmark list.");
            return state;
        }

        // Structural validity first.
        if (game.Scenes.Count == 0)
        {
            state.Status = BenchmarkGameStatus.Invalid;
            state.Notes.Add("No scenes defined.");
            return state;
        }
        if (game.Repeats < 3)
            state.Notes.Add($"Repeats {game.Repeats} < 3 — will be clamped to 3.");

        var store = GameStore.Parse(game.Launch.Store);

        switch (store)
        {
            case GameStoreKind.Standalone:
            {
                var target = game.Launch.Target;
                if (string.IsNullOrWhiteSpace(target))
                {
                    state.Status = BenchmarkGameStatus.Ready; // simulated placeholder (e.g. pipeline test)
                    state.Notes.Add("Standalone with no exe — runs in simulation.");
                }
                else if (File.Exists(target))
                {
                    state.Status = BenchmarkGameStatus.Ready;
                    state.Notes.Add($"Standalone exe present: {target}");
                }
                else
                {
                    state.Status = BenchmarkGameStatus.NotInstalled;
                    state.Notes.Add($"Standalone exe not found: {target}");
                }
                break;
            }

            case GameStoreKind.Manual:
                state.Status = BenchmarkGameStatus.Ready;
                state.Notes.Add("Manual launch — operator starts the game; suite waits for the process.");
                break;

            case GameStoreKind.Xbox:
                if (string.IsNullOrWhiteSpace(game.Launch.GameId))
                {
                    state.Status = BenchmarkGameStatus.Invalid;
                    state.Notes.Add("Xbox store requires the package AUMID (PackageFamilyName!AppId) in launch.gameId.");
                }
                else
                {
                    state.Status = BenchmarkGameStatus.Ready;
                    state.Notes.Add($"Xbox/MS Store app — launched via shell AUMID {game.Launch.GameId}.");
                }
                break;

            case GameStoreKind.Unknown:
                state.Status = BenchmarkGameStatus.Invalid;
                state.Notes.Add($"Unknown store '{game.Launch.Store}'.");
                break;

            default: // Steam / Epic / Uplay / Origin / Gog
            {
                var match = catalog.FindById(store, game.Launch.GameId)
                            ?? catalog.FindByName(game.Name);
                if (match is null)
                {
                    state.Status = BenchmarkGameStatus.NotInstalled;
                    state.Notes.Add($"Not found via {store} discovery (id '{game.Launch.GameId}').");
                }
                else
                {
                    state.Match = match;
                    state.Status = BenchmarkGameStatus.Ready;
                    state.Notes.Add($"Installed via {store}: {match.Name}{(match.InstallDir is null ? "" : " @ " + match.InstallDir)}");
                    if (string.IsNullOrEmpty(game.Launch.GameId) && !string.IsNullOrEmpty(match.GameId))
                        state.Notes.Add($"Tip: set gameId \"{match.GameId}\" in the profile for exact launch.");
                }
                break;
            }
        }

        return state;
    }
}
