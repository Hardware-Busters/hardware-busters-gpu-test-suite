namespace GpuSuite.Core.Models;

public enum GameStoreKind { Steam, Epic, Uplay, Origin, Gog, Battlenet, Rockstar, Xbox, Standalone, Manual, Unknown }

public static class GameStore
{
    public static GameStoreKind Parse(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "steam" => GameStoreKind.Steam,
        "epic" or "egs" => GameStoreKind.Epic,
        "uplay" or "ubisoft" or "ubisoftconnect" => GameStoreKind.Uplay,
        "origin" or "ea" or "eadesktop" or "eaapp" => GameStoreKind.Origin,
        "gog" or "goggalaxy" => GameStoreKind.Gog,
        "battlenet" or "battle.net" or "blizzard" => GameStoreKind.Battlenet,
        "rockstar" or "rgl" => GameStoreKind.Rockstar,
        "xbox" or "msstore" or "microsoft" or "gamepass" => GameStoreKind.Xbox,
        "standalone" => GameStoreKind.Standalone,
        "manual" => GameStoreKind.Manual,
        _ => GameStoreKind.Unknown
    };

    /// <summary>Launcher-URI scheme used to start a game by id, where one exists.</summary>
    public static string? LaunchUri(GameStoreKind store, string gameId) => store switch
    {
        GameStoreKind.Steam when !string.IsNullOrEmpty(gameId) => $"steam://rungameid/{gameId}",
        GameStoreKind.Epic when !string.IsNullOrEmpty(gameId) => $"com.epicgames.launcher://apps/{gameId}?action=launch&silent=true",
        GameStoreKind.Uplay when !string.IsNullOrEmpty(gameId) => $"uplay://launch/{gameId}/0",
        GameStoreKind.Origin when !string.IsNullOrEmpty(gameId) => $"origin://launchgame/{gameId}",
        GameStoreKind.Gog when !string.IsNullOrEmpty(gameId) => $"goggalaxy://openGameView/{gameId}",
        _ => null
    };
}

/// <summary>One game found installed on the machine by scanning a launcher's manifests.</summary>
public sealed class DiscoveredGame
{
    public string Name { get; set; } = "";
    public GameStoreKind Store { get; set; } = GameStoreKind.Unknown;
    /// <summary>Store-specific id (Steam appid, Epic AppName, etc.).</summary>
    public string GameId { get; set; } = "";
    public string? InstallDir { get; set; }
    public string? Exe { get; set; }
    /// <summary>File name of the main executable, e.g. "Cyberpunk2077.exe" (the PresentMon capture target).</summary>
    public string? ProcessName { get; set; }
    /// <summary>How this entry was discovered, e.g. "Steam manifest", "Registry uninstall", "Common folder".</summary>
    public string Source { get; set; } = "";

    /// <summary>Launcher-URI to start it by id, where the store supports one.</summary>
    public string? LaunchUri => GameStore.LaunchUri(Store, GameId);

    /// <summary>Stable key for de-duplication across overlapping scanners.</summary>
    public string DedupeKey =>
        !string.IsNullOrEmpty(InstallDir)
            ? "dir:" + NormalizePathKey(InstallDir)
            : $"id:{Store}:{GameId.ToLowerInvariant()}:{Name.ToLowerInvariant()}";

    /// <summary>
    /// Case-insensitive path key with a single slash direction, so the same install folder
    /// reached via differently-formatted paths (e.g. "${LOCAL_WINDOWS_PATH}/program files/steam\..." from Steam's
    /// libraryfolders.vdf vs "${LOCAL_WINDOWS_PATH}/Program Files\Steam\..." from the registry) collapses to one row.
    /// </summary>
    public static string NormalizePathKey(string path) =>
        path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
}

/// <summary>
/// All installed games discovered from launchers. This is a DETECTION HELPER ONLY — it never
/// drives the test plan. It exists so the suite can answer "is this benchmark game installed,
/// where, and how do I launch it" for the games the user explicitly chose.
/// </summary>
public sealed class GameCatalog
{
    public List<DiscoveredGame> Games { get; } = new();

    public DiscoveredGame? FindById(GameStoreKind store, string gameId) =>
        Games.FirstOrDefault(g => g.Store == store &&
                                  !string.IsNullOrEmpty(gameId) &&
                                  string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase));

    public DiscoveredGame? FindByName(string name) =>
        Games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? Games.FirstOrDefault(g => g.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                                  || name.Contains(g.Name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Resolved state of one user-selected benchmark game against the catalog.</summary>
public enum BenchmarkGameStatus
{
    Disabled,      // user turned it off
    NotInstalled,  // selected, but not found by discovery
    Installed,     // found installed, but profile not fully valid yet
    Ready,         // installed + valid → eligible for the test plan
    Invalid        // profile is malformed (no scenes, repeats < 3, no resolutions, etc.)
}

/// <summary>A benchmark-list entry resolved against the catalog: status + the matched install.</summary>
public sealed class BenchmarkGameState
{
    public GameProfile Game { get; init; } = new();
    public BenchmarkGameStatus Status { get; set; } = BenchmarkGameStatus.NotInstalled;
    public DiscoveredGame? Match { get; set; }
    public List<string> Notes { get; } = new();
    public bool Runnable => Status == BenchmarkGameStatus.Ready;
}

/// <summary>
/// The resolved test plan: every benchmark-list entry with its status. Only <see cref="Runnable"/>
/// (enabled + installed + valid) entries are executed. Discovered-but-unselected games are never here.
/// </summary>
public sealed class TestPlan
{
    public List<BenchmarkGameState> Entries { get; } = new();
    public IEnumerable<BenchmarkGameState> Runnable => Entries.Where(e => e.Runnable);
}
