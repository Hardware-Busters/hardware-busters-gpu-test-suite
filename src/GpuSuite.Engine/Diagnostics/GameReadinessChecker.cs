using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using GpuSuite.Core.Models;

namespace GpuSuite.Engine.Diagnostics;

/// <summary>Severity of one per-game pre-flight check. Ordered so the worst check drives the game's overall verdict.</summary>
public enum CheckStatus
{
    /// <summary>Nothing to flag — this aspect is ready.</summary>
    Ok,
    /// <summary>Neutral context the operator may want to know (not a problem).</summary>
    Info,
    /// <summary>Won't stop the run, but may skew or degrade it — worth a look before benchmarking.</summary>
    Warn,
    /// <summary>Would make the run fail or fabricate nothing useful — fix before starting.</summary>
    Blocker,
    /// <summary>Not applicable / intentionally skipped (e.g. the game is disabled in the list).</summary>
    Skip,
}

/// <summary>One pre-flight check result for a game. <see cref="Fix"/>, when set, is a one-line operator hint.</summary>
public sealed record GameCheck(string Name, CheckStatus Status, string Detail, string? Fix = null);

/// <summary>All pre-flight checks for one game, plus the rolled-up verdict.</summary>
public sealed class GameReadiness
{
    public GameProfile Game { get; init; } = new();
    public string Id => Game.Id;
    public string Name => string.IsNullOrWhiteSpace(Game.Name) ? Game.Id : Game.Name;
    public string Store => string.IsNullOrWhiteSpace(Game.Launch.Store) ? "—" : Game.Launch.Store;
    public List<GameCheck> Checks { get; } = new();

    /// <summary>The worst check severity (Skip if the game is disabled). Info never raises the verdict above Ok.</summary>
    public CheckStatus Overall
    {
        get
        {
            if (!Game.Enabled) return CheckStatus.Skip;
            var worst = CheckStatus.Ok;
            foreach (var c in Checks)
            {
                if (c.Status == CheckStatus.Blocker) return CheckStatus.Blocker;
                if (c.Status == CheckStatus.Warn) worst = CheckStatus.Warn;
            }
            return worst;
        }
    }

    /// <summary>True when nothing blocks the run (the game is enabled and has no Blocker check).</summary>
    public bool Ready => Game.Enabled && Checks.All(c => c.Status != CheckStatus.Blocker);

    public int WarnCount => Checks.Count(c => c.Status == CheckStatus.Warn);
    public int BlockerCount => Checks.Count(c => c.Status == CheckStatus.Blocker);
}

/// <summary>The pre-flight readiness report across a set of games.</summary>
public sealed class GameReadinessReport
{
    public List<GameReadiness> Games { get; } = new();
    /// <summary>Games that are fully clean (no warning, no blocker). Partitions with Warn/Blocker/Skipped so the
    /// four counts sum to the total — "ready" means nothing flagged, not merely "not blocked".</summary>
    public int ReadyCount => Games.Count(g => g.Overall == CheckStatus.Ok);
    public int WarnCount => Games.Count(g => g.Overall == CheckStatus.Warn);
    public int BlockerCount => Games.Count(g => g.Overall == CheckStatus.Blocker);
    public int SkippedCount => Games.Count(g => g.Overall == CheckStatus.Skip);
    public bool AllClear => BlockerCount == 0 && WarnCount == 0;
}

/// <summary>
/// "Is each game good to go before I hit Start?" — read-only, launch-nothing pre-flight checks the operator runs
/// on the bench before a sweep. For every game it verifies the things that silently waste a benchmark night:
/// the game is installed and not known to be mid-update, its launcher/account session is usable, its profile is
/// structurally valid, the bot scripts it
/// references exist, its settings/config files + registry keys are present so variant apply won't fail, and any
/// menu-applied settings are calibrated. Purely diagnostic: it never launches a game, edits a file, or touches
/// hardware — safe to run any time. Complements <see cref="DoctorService"/> (which checks the machine/toolchain,
/// not the games).
/// </summary>
public sealed class GameReadinessChecker
{
    private readonly string _profilesDir;
    public GameReadinessChecker(string profilesDir) => _profilesDir = profilesDir;

    public GameReadinessReport Check(IEnumerable<GameProfile> games, GameCatalog catalog)
    {
        var report = new GameReadinessReport();
        foreach (var g in games)
            report.Games.Add(CheckOne(g, catalog));
        return report;
    }

    public GameReadiness CheckOne(GameProfile game, GameCatalog catalog)
    {
        var r = new GameReadiness { Game = game };

        // 0 — enabled? A disabled game won't run; report it and stop (its other state is moot for this sweep).
        if (!game.Enabled)
        {
            r.Checks.Add(new GameCheck("Enabled", CheckStatus.Skip, "Disabled in the benchmark list — it will not run.",
                "Tick the game in the profile (or the Run list) to include it."));
            return r;
        }

        CheckProfileStructure(game, r);
        var match = CheckInstalled(game, catalog, r);
        CheckUpToDate(game, match, catalog, r);
        CheckLauncherSession(game, catalog, r);
        CheckCaptureProcess(game, r);
        CheckBotScripts(game, r);
        CheckSettingsFiles(game, r);
        CheckMenuCalibration(game, r);
        CheckVariants(game, r);

        return r;
    }

    // ---- individual checks ----

    private static void CheckProfileStructure(GameProfile game, GameReadiness r)
    {
        var issues = new List<string>();
        if (game.Scenes.Count == 0) issues.Add("no scenes defined");
        if (GameStore.Parse(game.Launch.Store) == GameStoreKind.Unknown)
            issues.Add($"unknown store '{game.Launch.Store}'");
        if (issues.Count > 0)
        {
            r.Checks.Add(new GameCheck("Profile", CheckStatus.Blocker, string.Join("; ", issues),
                "Fix the profile JSON under profiles/ before running."));
            return;
        }
        if (game.Repeats < 3)
            r.Checks.Add(new GameCheck("Profile", CheckStatus.Info,
                $"Repeats {game.Repeats} < 3 — clamped to 3 at run time."));
        else
            r.Checks.Add(new GameCheck("Profile", CheckStatus.Ok,
                $"{game.Scenes.Count} scene(s), repeats {game.Repeats}."));
    }

    /// <summary>Resolve the install and return the matched catalog entry (for the update check), Blocker if absent.</summary>
    private static DiscoveredGame? CheckInstalled(GameProfile game, GameCatalog catalog, GameReadiness r)
    {
        var store = GameStore.Parse(game.Launch.Store);
        switch (store)
        {
            case GameStoreKind.Standalone:
                var target = game.Launch.Target;
                if (string.IsNullOrWhiteSpace(target))
                    r.Checks.Add(new GameCheck("Installed", CheckStatus.Info, "Standalone with no exe — runs in simulation."));
                else if (File.Exists(target))
                    r.Checks.Add(new GameCheck("Installed", CheckStatus.Ok, $"Standalone exe present: {target}"));
                else
                    r.Checks.Add(new GameCheck("Installed", CheckStatus.Blocker, $"Standalone exe not found: {target}",
                        "Fix launch.target in the profile or install the game."));
                return null;

            case GameStoreKind.Manual:
                r.Checks.Add(new GameCheck("Installed", CheckStatus.Info, "Manual launch — you start the game; the suite waits for the process."));
                return null;

            case GameStoreKind.Xbox:
                if (string.IsNullOrWhiteSpace(game.Launch.GameId))
                {
                    r.Checks.Add(new GameCheck("Installed", CheckStatus.Blocker, "Xbox/MS Store needs the package AUMID in launch.gameId.",
                        "Set launch.gameId to PackageFamilyName!AppId."));
                    return null;
                }
                // Deeper probe: verify the package is actually REGISTERED for this user (WinRT PackageManager)
                // instead of blindly assuming the AUMID will activate. Probe failure (older OS / API error) is
                // never a blocker — fall back to the old honest "launched via AUMID" wording.
                var (found, probeError, version, installPath, _) = TryFindXboxPackage(game.Launch.GameId);
                if (probeError is not null)
                    r.Checks.Add(new GameCheck("Installed", CheckStatus.Ok,
                        $"Xbox/MS Store app — launched via shell AUMID {game.Launch.GameId} (package registry not readable: {probeError})."));
                else if (!found)
                    r.Checks.Add(new GameCheck("Installed", CheckStatus.Blocker,
                        $"Package '{FamilyNameOf(game.Launch.GameId)}' is not registered for this user — not installed?",
                        "Install it from the Xbox app / MS Store, or fix the AUMID in launch.gameId."));
                else
                    r.Checks.Add(new GameCheck("Installed", CheckStatus.Ok,
                        $"Xbox package registered: v{version}{(installPath is null ? "" : " @ " + installPath)}."));
                return null;

            default: // Steam / Epic / Uplay / Origin / Gog
                var m = catalog.FindById(store, game.Launch.GameId) ?? catalog.FindByName(game.Name);
                if (m is null)
                {
                    r.Checks.Add(new GameCheck("Installed", CheckStatus.Blocker,
                        $"Not found by {store} discovery (id '{game.Launch.GameId}').",
                        $"Install it via {store}, or fix launch.gameId in the profile."));
                    return null;
                }
                r.Checks.Add(new GameCheck("Installed", CheckStatus.Ok,
                    $"Installed via {store}{(m.InstallDir is null ? "" : " @ " + m.InstallDir)}."));
                return m;
        }
    }

    /// <summary>The headline "is it updated" check, dispatched per store. Steam is authoritative from the local
    /// appmanifest; Epic reads the launcher's local .item manifest (install-integrity flags + version); Xbox reads
    /// the live package status (WinRT) and asks the Store for update availability when online. Stores with no
    /// readable local state say so honestly rather than claim a false all-clear.</summary>
    private static void CheckUpToDate(GameProfile game, DiscoveredGame? match, GameCatalog catalog, GameReadiness r)
    {
        var store = GameStore.Parse(game.Launch.Store);
        switch (store)
        {
            case GameStoreKind.Steam: CheckSteamUpToDate(game, match, r); return;
            case GameStoreKind.Epic: CheckEpicUpToDate(game, match, r); return;
            case GameStoreKind.Xbox: CheckXboxUpToDate(game, r); return;
            case GameStoreKind.Origin: CheckEaUpToDate(match, r); return;
            case GameStoreKind.Gog: CheckGogUpToDate(game, match, r); return;
            case GameStoreKind.Uplay: CheckUbisoftUpToDate(r); return;
            case GameStoreKind.Standalone:
                // DRM-free games are often launched directly for deterministic capture while Galaxy still owns
                // their update state. If its numeric product id is discoverable, use that authoritative source.
                var gogMatch = catalog.FindById(GameStoreKind.Gog, game.Launch.GameId);
                if (gogMatch is not null) { CheckGogUpToDate(game, gogMatch, r); return; }
                r.Checks.Add(new GameCheck("Up to date", CheckStatus.Warn,
                    "Standalone install has no launcher update state — latest build cannot be confirmed automatically.",
                    "Open the store/installer that owns this game and confirm no update is available."));
                return;
            case GameStoreKind.Manual:
                r.Checks.Add(new GameCheck("Up to date", CheckStatus.Warn,
                    "Manual-launch profile has no automatic update source.",
                    "Confirm the game build manually before benchmarking."));
                return;
            default:
                r.Checks.Add(new GameCheck("Up to date", CheckStatus.Warn,
                    $"{store} update state can't be verified offline — the launcher checks when it starts the game.",
                    "Open the launcher and confirm no update is queued before benchmarking."));
                return;
        }
    }

    private static void CheckSteamUpToDate(GameProfile game, DiscoveredGame? match, GameReadiness r)
    {
        var appid = !string.IsNullOrWhiteSpace(game.Launch.GameId) ? game.Launch.GameId : match?.GameId;
        if (string.IsNullOrWhiteSpace(appid))
        {
            r.Checks.Add(new GameCheck("Up to date", CheckStatus.Info, "No Steam appid on the profile — can't read the update manifest."));
            return;
        }

        var acf = FindSteamAppManifest(appid!);
        if (acf is null)
        {
            r.Checks.Add(new GameCheck("Up to date", CheckStatus.Info, $"No Steam manifest (appmanifest_{appid}.acf) found — is it installed on this account?"));
            return;
        }

        string text;
        try { text = File.ReadAllText(acf); }
        catch (Exception ex) { r.Checks.Add(new GameCheck("Up to date", CheckStatus.Info, $"Could not read the Steam manifest: {ex.Message}")); return; }

        int stateFlags = ParseIntField(text, "StateFlags");
        long toDownload = ParseLongField(text, "BytesToDownload");
        long downloaded = ParseLongField(text, "BytesDownloaded");
        bool downloading = toDownload > 0 && downloaded >= 0 && downloaded < toDownload;

        // A fully-installed, idle Steam game reads StateFlags exactly 4 (StateFullyInstalled). Any extra bit means
        // an update/validation is pending or running; a partial download shows as BytesDownloaded < BytesToDownload.
        if (stateFlags == 4 && !downloading)
        {
            bool steamRunning = Process.GetProcessesByName("steam").Length > 0;
            r.Checks.Add(new GameCheck("Up to date", steamRunning ? CheckStatus.Ok : CheckStatus.Warn,
                steamRunning
                    ? "Steam is running and reports fully installed with no pending update (StateFlags=4)."
                    : "Steam manifest is clean (StateFlags=4), but Steam is not running — a newer online build cannot be confirmed.",
                steamRunning ? null : "Start Steam, let its update check finish, then run full pre-flight again."));
        }
        else if (downloading)
        {
            double remainMb = (toDownload - downloaded) / 1024.0 / 1024.0;
            r.Checks.Add(new GameCheck("Up to date", CheckStatus.Blocker,
                $"Steam update in progress — {remainMb:0} MB left to download (StateFlags={stateFlags}).",
                "Let Steam finish updating before benchmarking."));
        }
        else
        {
            r.Checks.Add(new GameCheck("Up to date", CheckStatus.Blocker,
                $"Steam manifest not in the clean fully-installed state (StateFlags={stateFlags}) — an update/validation is likely pending.",
                "Open Steam, let any update/validation finish, then re-check."));
        }
    }

    // ---- EA / GOG update and launcher-session probes (read-only) ----

    private static void CheckEaUpToDate(DiscoveredGame? match, GameReadiness r)
    {
        string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EA Desktop", "Logs", "EABackgroundServiceVerbose.log");
        if (match?.InstallDir is not { Length: > 0 } installDir || !File.Exists(logPath))
        {
            r.Checks.Add(new GameCheck("Up to date", CheckStatus.Warn,
                "EA update state can't be matched to this install from the current background-service log.",
                "Open EA App, wait for its library refresh, then run pre-flight again."));
            return;
        }

        string text = ReadTailText(logPath, 16 * 1024 * 1024);
        string? softwareId = FindEaSoftwareId(text, installDir);
        bool fresh = DateTime.UtcNow - File.GetLastWriteTimeUtc(logPath) <= TimeSpan.FromHours(2);
        if (!string.IsNullOrWhiteSpace(softwareId) && EaReportsNoUpdate(text, softwareId) && fresh)
        {
            r.Checks.Add(new GameCheck("Up to date", CheckStatus.Ok,
                $"EA App reports nothing eligible for an update ({softwareId}); service evidence is current."));
            return;
        }

        r.Checks.Add(new GameCheck("Up to date", CheckStatus.Warn,
            softwareId is null
                ? "EA App has not yet published update evidence for this install."
                : fresh
                    ? $"EA App has not produced a decisive no-update result for {softwareId}."
                    : $"EA App's last update evidence for {softwareId} is stale.",
            "Keep EA App online until its library refresh finishes, then run pre-flight again."));
    }

    internal static string? FindEaSoftwareId(string text, string installDir)
    {
        string dir = installDir.TrimEnd('\\', '/');
        var matches = Regex.Matches(text,
            $@"watchPath.*?{Regex.Escape(dir)}[\\/]+__Installer[\\/]+installerdata\.xml.*?software \[(?<id>[^\]]+)\]",
            RegexOptions.IgnoreCase);
        return matches.Count == 0 ? null : matches[^1].Groups["id"].Value;
    }

    internal static bool EaReportsNoUpdate(string text, string softwareId)
        => Regex.IsMatch(text,
            $@"softwareId=\[{Regex.Escape(softwareId)}\].*?reason=\[nothing eligible for an update\]",
            RegexOptions.IgnoreCase);

    private static void CheckUbisoftUpToDate(GameReadiness r)
    {
        bool running = Process.GetProcessesByName("upc").Length > 0;
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Ubisoft", "Ubisoft Game Launcher", "logs", "launcher_log.txt");
        string text = File.Exists(path) ? ReadTailText(path, 4 * 1024 * 1024) : "";
        bool catalogTimedOut = text.LastIndexOf("StoreProductsGet.cpp", StringComparison.OrdinalIgnoreCase) >= 0 &&
                               text.LastIndexOf("Get store products timed out", StringComparison.OrdinalIgnoreCase) >= 0;
        r.Checks.Add(new GameCheck("Up to date", CheckStatus.Warn,
            running && catalogTimedOut
                ? "Ubisoft Connect is online, but its latest products/update catalog request timed out; the installed build cannot be compared authoritatively."
                : running
                    ? "Ubisoft Connect is online, but it does not expose a decisive per-game no-update result locally."
                    : "Ubisoft Connect is not running, so its online update catalog was not checked.",
            "Open Ubisoft Connect's Downloads page and confirm no update is queued before benchmarking."));
    }

    private static void CheckGogUpToDate(GameProfile game, DiscoveredGame? match, GameReadiness r)
    {
        if (!long.TryParse(game.Launch.GameId, out long productId))
        {
            r.Checks.Add(new GameCheck("Up to date", CheckStatus.Warn,
                "GOG product id is missing or invalid; Galaxy update state cannot be matched.",
                "Set launch.gameId to the numeric GOG product id."));
            return;
        }

        string db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GOG.com", "Galaxy", "storage", "galaxy-2.0.db");
        string sql = $"SELECT ib.buildId,b.manifest,coalesce(ps.operation,0) FROM InstalledBaseProducts ib " +
                     $"LEFT JOIN Builds b ON b.productId=ib.productId LEFT JOIN ProductStates ps ON ps.productId=ib.productId " +
                     $"WHERE ib.productId={productId} ORDER BY b.createdAt DESC LIMIT 1";
        string error = File.Exists(db) ? "" : "Galaxy database not found";
        string?[] row = [];
        if (!File.Exists(db) || !WindowsSqliteReader.TryQueryFirst(db, sql, out row, out error) || row.Length < 3)
        {
            r.Checks.Add(new GameCheck("Up to date", CheckStatus.Warn,
                $"GOG Galaxy build catalog is not readable{(string.IsNullOrWhiteSpace(error) ? "" : $" ({FirstLine(error)})")}.",
                "Open GOG Galaxy, let it refresh, then run pre-flight again."));
            return;
        }

        string installedBuild = row[0] ?? "";
        int.TryParse(row[2], out int operation);
        if (operation != 0)
        {
            r.Checks.Add(new GameCheck("Up to date", CheckStatus.Blocker,
                $"GOG Galaxy reports an active install/update operation (state {operation}).",
                "Let GOG Galaxy finish before benchmarking."));
            return;
        }

        var latest = ParseLatestGogBuild(row[1] ?? "");
        if (latest is null)
        {
            r.Checks.Add(new GameCheck("Up to date", CheckStatus.Warn,
                $"GOG installed build {installedBuild} is known, but the latest public Windows build is absent from Galaxy's catalog.",
                "Keep GOG Galaxy online until its library refresh finishes."));
            return;
        }

        if (!string.Equals(installedBuild, latest.Value.BuildId, StringComparison.Ordinal))
        {
            r.Checks.Add(new GameCheck("Up to date", CheckStatus.Blocker,
                $"GOG update required: installed build {installedBuild}; latest public build {latest.Value.BuildId} ({latest.Value.Version}).",
                "Update the game in GOG Galaxy before benchmarking."));
            return;
        }

        bool galaxyRunning = Process.GetProcessesByName("GalaxyClient").Length > 0;
        bool fresh = DateTime.UtcNow - File.GetLastWriteTimeUtc(db) <= TimeSpan.FromHours(24);
        var status = galaxyRunning && fresh ? CheckStatus.Ok : CheckStatus.Warn;
        r.Checks.Add(new GameCheck("Up to date", status,
            status == CheckStatus.Ok
                ? $"GOG Galaxy confirms installed build {installedBuild} is the latest public Windows build ({latest.Value.Version})."
                : $"GOG build {installedBuild} matches cached latest ({latest.Value.Version}), but Galaxy is offline or its catalog is stale.",
            status == CheckStatus.Ok ? null : "Start GOG Galaxy and let its library refresh, then re-check."));
    }

    internal static (string BuildId, string Version)? ParseLatestGogBuild(string manifestJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return null;
            (string BuildId, string Version, string Published)? best = null;
            foreach (var item in items.EnumerateArray())
            {
                string os = JsonStr(item, "os");
                bool isPublic = item.TryGetProperty("public", out var pub) && pub.ValueKind == JsonValueKind.True;
                bool defaultBranch = !item.TryGetProperty("branch", out var branch) ||
                                     branch.ValueKind == JsonValueKind.Null ||
                                     (branch.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(branch.GetString()));
                if (!isPublic || !defaultBranch || !os.Equals("windows", StringComparison.OrdinalIgnoreCase)) continue;
                string build = JsonStr(item, "build_id");
                string published = JsonStr(item, "date_published");
                if (build.Length > 0 && (best is null || string.CompareOrdinal(published, best.Value.Published) > 0))
                    best = (build, JsonStr(item, "version_name"), published);
            }
            if (best is { } found) return (found.BuildId, found.Version);
        }
        catch { }
        return null;
    }

    private static void CheckLauncherSession(GameProfile game, GameCatalog catalog, GameReadiness r)
    {
        var store = GameStore.Parse(game.Launch.Store);
        if (store == GameStoreKind.Standalone && catalog.FindById(GameStoreKind.Gog, game.Launch.GameId) is not null)
            store = GameStoreKind.Gog;

        switch (store)
        {
            case GameStoreKind.Steam:
                CheckSteamSession(r);
                break;
            case GameStoreKind.Epic:
                CheckEpicSession(r);
                break;
            case GameStoreKind.Origin:
                CheckEaSession(r);
                break;
            case GameStoreKind.Uplay:
                CheckUbisoftSession(r);
                break;
            case GameStoreKind.Gog:
                CheckGogSession(r);
                break;
            case GameStoreKind.Xbox:
                bool xboxRunning = Process.GetProcessesByName("XboxPcApp").Length > 0;
                r.Checks.Add(new GameCheck("Launcher session", xboxRunning ? CheckStatus.Ok : CheckStatus.Warn,
                    xboxRunning ? "Xbox app is running; account and queue can refresh." : "Xbox app is not running; account/login state cannot be confirmed.",
                    xboxRunning ? null : "Start Xbox app, sign in, and run pre-flight again."));
                break;
        }
    }

    private static void CheckSteamSession(GameReadiness r)
    {
        bool running = Process.GetProcessesByName("steam").Length > 0;
        string? root = SteamPath();
        string log = root is null ? "" : Path.Combine(root, "logs", "connection_log.txt");
        string text = File.Exists(log) ? ReadTailText(log, 2 * 1024 * 1024) : "";
        int loggedOn = text.LastIndexOf("[Logged On", StringComparison.OrdinalIgnoreCase);
        int loggedOff = text.LastIndexOf("[Logged Off", StringComparison.OrdinalIgnoreCase);
        if (running && loggedOn > loggedOff)
            r.Checks.Add(new GameCheck("Launcher session", CheckStatus.Ok, "Steam is running and its latest connection state is Logged On."));
        else
            r.Checks.Add(new GameCheck("Launcher session", running ? CheckStatus.Blocker : CheckStatus.Warn,
                running ? "Steam is running, but its latest connection state is not Logged On." : "Steam is not running; account/login state cannot be confirmed.",
                "Start Steam, sign in and wait for it to show Online, then re-check."));
    }

    private static void CheckEpicSession(GameReadiness r)
    {
        bool running = Process.GetProcessesByName("EpicGamesLauncher").Length > 0;
        string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EpicGamesLauncher", "Saved", "Logs", "EpicGamesLauncher.log");
        string text = File.Exists(log) ? ReadTailText(log, 4 * 1024 * 1024) : "";
        int success = text.LastIndexOf("OnEOSLoginComplete success 1", StringComparison.OrdinalIgnoreCase);
        int failure = text.LastIndexOf("OnEOSLoginComplete success 0", StringComparison.OrdinalIgnoreCase);
        bool fresh = File.Exists(log) && DateTime.UtcNow - File.GetLastWriteTimeUtc(log) <= TimeSpan.FromHours(2);
        if (running && fresh && success > failure)
            r.Checks.Add(new GameCheck("Launcher session", CheckStatus.Ok, "Epic Games Launcher is running with a current successful online login."));
        else if (running && fresh && failure > success)
            r.Checks.Add(new GameCheck("Launcher session", CheckStatus.Blocker,
                "Epic Games Launcher reports that its latest online login attempt failed.",
                "Sign in successfully and wait for the library to load, then re-check."));
        else
            r.Checks.Add(new GameCheck("Launcher session", running ? CheckStatus.Warn : CheckStatus.Blocker,
                running ? "Epic Games Launcher is running, but a current successful online login was not confirmed." : "Epic Games Launcher is not running; login cannot be confirmed.",
                "Start Epic Games Launcher, sign in, wait for the library to load, then re-check."));
    }

    private static void CheckEaSession(GameReadiness r)
    {
        bool running = Process.GetProcessesByName("EADesktop").Length > 0;
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Electronic Arts", "EA Desktop", "Logs", "EADesktop.log");
        string text = File.Exists(path) ? ReadTailText(path, 4 * 1024 * 1024) : "";
        bool fresh = File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) <= TimeSpan.FromHours(2);
        bool authenticated = text.Contains("DesktopFSM[authenticated]", StringComparison.OrdinalIgnoreCase) &&
                             Regex.IsMatch(text, "\\\"authenticated\\\"\\s*:\\s*true.*?\\\"offline\\\"\\s*:\\s*false",
                                 RegexOptions.IgnoreCase);
        if (running && fresh && authenticated)
            r.Checks.Add(new GameCheck("Launcher session", CheckStatus.Ok, "EA App is running, authenticated, and online."));
        else
            r.Checks.Add(new GameCheck("Launcher session", running ? CheckStatus.Warn : CheckStatus.Blocker,
                running ? "EA App is running, but a current authenticated-online session was not confirmed." : "EA App is not running; login cannot be confirmed.",
                "Start EA App, sign in, wait for the library to load, then re-check."));
    }

    private static void CheckUbisoftSession(GameReadiness r)
    {
        bool running = Process.GetProcessesByName("upc").Length > 0;
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Ubisoft", "Ubisoft Game Launcher", "logs", "launcher_log.txt");
        string text = File.Exists(path) ? ReadTailText(path, 4 * 1024 * 1024) : "";
        int startup = text.LastIndexOf("settingsOffline: false", StringComparison.OrdinalIgnoreCase);
        int user = text.LastIndexOf("AccountStartupUser.cpp", StringComparison.OrdinalIgnoreCase);
        bool onlineUser = startup >= 0 && user > startup;
        if (running && onlineUser)
            r.Checks.Add(new GameCheck("Launcher session", CheckStatus.Ok, "Ubisoft Connect is running online with a loaded user account."));
        else
            r.Checks.Add(new GameCheck("Launcher session", running ? CheckStatus.Warn : CheckStatus.Blocker,
                running ? "Ubisoft Connect is running, but an online signed-in session was not confirmed." : "Ubisoft Connect is not running; login cannot be confirmed.",
                "Start Ubisoft Connect, sign in, wait for the library to load, then re-check."));
    }

    private static void CheckGogSession(GameReadiness r)
    {
        bool running = Process.GetProcessesByName("GalaxyClient").Length > 0;
        string db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GOG.com", "Galaxy", "storage", "galaxy-2.0.db");
        bool hasUser = File.Exists(db) && WindowsSqliteReader.TryQueryFirst(db,
            "SELECT id FROM Users WHERE id IS NOT NULL LIMIT 1", out var row, out _) && row.FirstOrDefault() is { Length: > 0 };
        if (running && hasUser)
            r.Checks.Add(new GameCheck("Launcher session", CheckStatus.Ok, "GOG Galaxy is running with a saved signed-in account."));
        else
            r.Checks.Add(new GameCheck("Launcher session", running ? CheckStatus.Warn : CheckStatus.Blocker,
                running ? "GOG Galaxy is running, but no signed-in account was found." : "GOG Galaxy is not running; login cannot be confirmed.",
                "Start GOG Galaxy, sign in, wait for the library to load, then re-check."));
    }

    private static string ReadTailText(string path, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, stream.Length - maxBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch { return ""; }
    }

    private static void CheckCaptureProcess(GameProfile game, GameReadiness r)
    {
        if (string.IsNullOrWhiteSpace(game.CaptureProcessName))
            r.Checks.Add(new GameCheck("Capture target", CheckStatus.Warn,
                "captureProcessName is empty — the frame capture has no process to attach to.",
                "Set captureProcessName to the game exe (e.g. \"Cyberpunk2077.exe\")."));
    }

    private void CheckBotScripts(GameProfile game, GameReadiness r)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? id) { if (!string.IsNullOrWhiteSpace(id)) ids.Add(id!); }
        Add(game.SettingsBotScript);
        foreach (var s in game.Scenes)
        {
            Add(s.BotScript);
            Add(s.StartBotScript);
            Add(s.ReRunBotScript);
            Add(s.Warmup?.Script);
        }
        if (ids.Count == 0) return; // no bot-driven scenes (e.g. a built-in-benchmark auto-start)

        var missing = new List<string>();
        foreach (var id in ids)
        {
            if (id.Equals("camera_path_basic", StringComparison.OrdinalIgnoreCase)) continue; // built-in
            var path = Path.Combine(_profilesDir, "bots", id + ".json");
            if (!File.Exists(path)) missing.Add(id);
        }
        if (missing.Count > 0)
            r.Checks.Add(new GameCheck("Bot scripts", CheckStatus.Blocker,
                $"Missing bot script(s): {string.Join(", ", missing)} (looked in {Path.Combine(_profilesDir, "bots")}).",
                "Restore the bot JSON(s) or fix the profile's script id(s)."));
        else
            r.Checks.Add(new GameCheck("Bot scripts", CheckStatus.Ok, $"{ids.Count} referenced bot script(s) present."));
    }

    /// <summary>Config-file settings/resolution paths + registry keys the pre-launch apply/verify will touch. A
    /// missing file/key doesn't block (some games regenerate on first launch, or a template self-heals it) but it
    /// is the usual reason a variant apply fails, so warn with the exact path.</summary>
    private static void CheckSettingsFiles(GameProfile game, GameReadiness r)
    {
        var missingFiles = new List<string>();
        var missingKeys = new List<string>();
        int checkedCount = 0;

        void CheckFile(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            checkedCount++;
            var p = Environment.ExpandEnvironmentVariables(raw!);
            if (!File.Exists(p)) missingFiles.Add(p);
        }
        void CheckKey(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            checkedCount++;
            if (!RegistryKeyExists(raw!)) missingKeys.Add(raw!);
        }

        if (string.Equals(game.ResolutionApply.Method, "config-file", StringComparison.OrdinalIgnoreCase))
            CheckFile(game.ResolutionApply.ConfigFilePath);

        foreach (var s in game.Settings)
        {
            switch ((s.Apply.Method ?? "none").ToLowerInvariant())
            {
                case "config-file": CheckFile(s.Apply.ConfigFilePath); break;
                case "registry": CheckKey(s.Apply.RegistryKey); break;
            }
        }
        foreach (var f in game.SettingsFingerprintKeys)
        {
            if (!string.IsNullOrWhiteSpace(f.RegistryKey)) CheckKey(f.RegistryKey);
            else CheckFile(f.ConfigFilePath);
        }

        if (checkedCount == 0) return; // nothing config/registry-driven to verify

        if (missingFiles.Count == 0 && missingKeys.Count == 0)
        {
            r.Checks.Add(new GameCheck("Settings files", CheckStatus.Ok, $"{checkedCount} settings file(s)/key(s) present."));
            return;
        }
        var parts = new List<string>();
        if (missingFiles.Count > 0) parts.Add($"file(s) missing: {string.Join("; ", missingFiles)}");
        if (missingKeys.Count > 0) parts.Add($"registry key(s) missing: {string.Join("; ", missingKeys)}");
        r.Checks.Add(new GameCheck("Settings files", CheckStatus.Warn, string.Join(" · ", parts),
            "Launch the game once and apply a video setting to regenerate its config, or set a template — else a variant apply/fingerprint may miss."));
    }

    /// <summary>Menu-applied settings need a calibrated MenuMap or their variants get fail-safe skipped.</summary>
    private static void CheckMenuCalibration(GameProfile game, GameReadiness r)
    {
        var menuKeys = game.Settings
            .Where(s => string.Equals(s.Apply.Method, "menu", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Key).ToList();
        if (menuKeys.Count == 0) return;

        bool calibrated = game.MenuMap is { } mm && mm.Controls.Count > 0;
        if (calibrated)
            r.Checks.Add(new GameCheck("Menu calibration", CheckStatus.Ok,
                $"MenuMap present ({game.MenuMap!.Controls.Count} control(s)) for menu setting(s): {string.Join(", ", menuKeys)}."));
        else
            r.Checks.Add(new GameCheck("Menu calibration", CheckStatus.Warn,
                $"Menu-applied setting(s) {string.Join(", ", menuKeys)} but no calibrated MenuMap — those variants are fail-safe skipped.",
                "Calibrate the game's MenuMap (gpusuite menu-apply) or apply those settings another way."));
    }

    /// <summary>Enabled variants must reference declared setting keys, and their values should be in the setting's options.</summary>
    private static void CheckVariants(GameProfile game, GameReadiness r)
    {
        var enabled = game.Variants.Where(v => v.Enabled).ToList();
        if (enabled.Count == 0) return;

        var keys = new HashSet<string>(game.Settings.Select(s => s.Key), StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        foreach (var v in enabled)
        {
            foreach (var kv in v.Settings)
            {
                if (!keys.Contains(kv.Key)) { problems.Add($"'{v.Id}' → unknown setting '{kv.Key}'"); continue; }
                var setting = game.Settings.First(s => string.Equals(s.Key, kv.Key, StringComparison.OrdinalIgnoreCase));
                if (setting.Options.Count > 0 && !setting.Options.Contains(kv.Value, StringComparer.OrdinalIgnoreCase))
                    problems.Add($"'{v.Id}' → {kv.Key}='{kv.Value}' not in [{string.Join(", ", setting.Options)}]");
            }
        }
        if (problems.Count > 0)
            r.Checks.Add(new GameCheck("Variants", CheckStatus.Warn, string.Join("; ", problems),
                "Fix the variant Settings in the profile so every key/value is declared."));
        else
            r.Checks.Add(new GameCheck("Variants", CheckStatus.Ok, $"{enabled.Count} enabled variant(s) reference valid settings."));
    }

    // ---- Epic manifest probe (read-only) ----

    /// <summary>
    /// Deeper Epic probe: read the launcher's LOCAL install manifest (the same
    /// %ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item files discovery scans). It can't know about a
    /// brand-new build (that requires Epic's online catalog — said honestly in the Ok text) but it verifies what
    /// IS knowable offline: the install is complete (bIsIncompleteInstall), doesn't need validation
    /// (bNeedsValidation), its folder exists, and which build version is installed.
    /// </summary>
    private static void CheckEpicUpToDate(GameProfile game, DiscoveredGame? match, GameReadiness r)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir))
        {
            r.Checks.Add(new GameCheck("Up to date", CheckStatus.Info,
                "Epic manifests folder not found — can't verify locally; the launcher checks when it starts the game."));
            return;
        }

        System.Text.Json.JsonElement? manifest = null;
        try
        {
            foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
            {
                System.Text.Json.JsonDocument doc;
                try { doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(item)); } catch { continue; }
                var root = doc.RootElement;
                string appName = JsonStr(root, "AppName");
                string loc = JsonStr(root, "InstallLocation");
                bool idMatch = !string.IsNullOrWhiteSpace(game.Launch.GameId) &&
                               string.Equals(appName, game.Launch.GameId, StringComparison.OrdinalIgnoreCase);
                bool dirMatch = match?.InstallDir is { Length: > 0 } mi && loc.Length > 0 &&
                                DiscoveredGame.NormalizePathKey(mi) == DiscoveredGame.NormalizePathKey(loc);
                if (idMatch || dirMatch) { manifest = root.Clone(); doc.Dispose(); break; }
                doc.Dispose();
            }
        }
        catch (Exception ex)
        {
            r.Checks.Add(new GameCheck("Up to date", CheckStatus.Info, $"Could not read the Epic manifests: {ex.Message}"));
            return;
        }

        if (manifest is not { } m)
        {
            r.Checks.Add(new GameCheck("Up to date", CheckStatus.Info,
                $"No Epic .item manifest matched (id '{game.Launch.GameId}') — can't verify locally."));
            return;
        }

        bool incomplete = JsonBool(m, "bIsIncompleteInstall");
        bool needsValidation = JsonBool(m, "bNeedsValidation");
        string version = JsonStr(m, "AppVersionString");
        string install = JsonStr(m, "InstallLocation");
        bool installMissing = install.Length > 0 && !Directory.Exists(install);

        if (incomplete)
            r.Checks.Add(new GameCheck("Up to date", CheckStatus.Blocker,
                $"Epic manifest flags the install INCOMPLETE (bIsIncompleteInstall) — v{version}.",
                "Open the Epic launcher and let it finish installing/updating."));
        else if (needsValidation || installMissing)
            r.Checks.Add(new GameCheck("Up to date", CheckStatus.Blocker,
                (needsValidation ? "Epic manifest flags the install as needing VALIDATION (bNeedsValidation)" :
                    $"Epic install folder missing: {install}") + $" — v{version}.",
                "Open the Epic launcher and verify/repair the install before benchmarking."));
        else
        {
            bool launcherRunning = Process.GetProcessesByName("EpicGamesLauncher").Length > 0;
            r.Checks.Add(new GameCheck("Up to date", launcherRunning ? CheckStatus.Ok : CheckStatus.Warn,
                launcherRunning
                    ? $"Epic launcher is running; manifest is fully installed at v{version} with no incomplete/validation flags."
                    : $"Epic manifest is healthy at v{version}, but the launcher is not running — a newer online build cannot be confirmed.",
                launcherRunning ? null : "Start Epic Games Launcher, let its update check finish, then run full pre-flight again."));
        }
    }

    // ---- Xbox / MS Store package probe (read-only WinRT) ----

    /// <summary>PackageFamilyName part of an AUMID ("PFN!AppId" → "PFN").</summary>
    private static string FamilyNameOf(string aumid)
    {
        int i = aumid.IndexOf('!');
        return i > 0 ? aumid[..i] : aumid;
    }

    /// <summary>
    /// Find the game's package registered for the CURRENT user via WinRT PackageManager (no elevation needed).
    /// Returns (found, probeError, version, installPath, package). probeError is non-null when the probe itself
    /// failed (API unavailable / access error) — callers must degrade to the honest "can't verify" wording, never
    /// treat a probe failure as "not installed".
    /// </summary>
    private static (bool found, string? probeError, string version, string? installPath, Windows.ApplicationModel.Package? pkg)
        TryFindXboxPackage(string aumid)
    {
        try
        {
            var pm = new Windows.Management.Deployment.PackageManager();
            var pkg = pm.FindPackagesForUser(string.Empty, FamilyNameOf(aumid)).FirstOrDefault();
            if (pkg is null) return (false, null, "", null, null);
            var v = pkg.Id.Version;
            string version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            string? path = null;
            try { path = pkg.InstalledPath; } catch { /* some packages refuse path access — version still useful */ }
            return (true, null, version, path, pkg);
        }
        catch (Exception ex) { return (false, ex.Message, "", null, null); }
    }

    /// <summary>
    /// Deeper Xbox probe: read the LIVE package status (Servicing = an update is being applied right now;
    /// NeedsRemediation/NotAvailable/etc. = broken), then ask the Store whether an update is AVAILABLE
    /// (CheckUpdateAvailabilityAsync — an online check, bounded by a timeout; failure/offline degrades to an
    /// honest note, never a false all-clear or a false alarm).
    /// </summary>
    private static void CheckXboxUpToDate(GameProfile game, GameReadiness r)
    {
        var (found, probeError, version, _, pkg) = TryFindXboxPackage(game.Launch.GameId);
        if (probeError is not null || !found || pkg is null)
        {
            // Not-registered is already a Blocker on the Installed check; probe failure keeps the old wording.
            if (probeError is not null)
                r.Checks.Add(new GameCheck("Up to date", CheckStatus.Info,
                    $"Xbox package state not readable ({probeError}) — the Xbox app checks when it starts the game."));
            return;
        }

        // 1) Local, authoritative: the package's live status.
        var st = pkg.Status;
        if (st.Servicing)
        {
            r.Checks.Add(new GameCheck("Up to date", CheckStatus.Blocker,
                $"Xbox package v{version} is being SERVICED — an update/repair is in progress right now.",
                "Let the Xbox app / Store finish before benchmarking."));
            return;
        }
        var broken = new List<string>();
        if (st.NeedsRemediation) broken.Add("NeedsRemediation");
        if (st.NotAvailable) broken.Add("NotAvailable");
        if (st.PackageOffline) broken.Add("PackageOffline");
        if (st.Modified) broken.Add("Modified");
        if (st.Tampered) broken.Add("Tampered");
        if (st.LicenseIssue) broken.Add("LicenseIssue");
        if (st.DeploymentInProgress) broken.Add("DeploymentInProgress");
        if (broken.Count > 0)
        {
            r.Checks.Add(new GameCheck("Up to date", CheckStatus.Blocker,
                $"Xbox package v{version} status flags: {string.Join(", ", broken)}.",
                "Open the Xbox app and repair/update the game before benchmarking."));
            return;
        }

        // 2) Online, best-effort: ask the Store if a newer build exists (bounded; degrades honestly).
        string onlineNote;
        var onlineVerdict = CheckStatus.Ok;
        try
        {
            var task = pkg.CheckUpdateAvailabilityAsync().AsTask();
            if (!task.Wait(TimeSpan.FromSeconds(10)))
            {
                onlineVerdict = CheckStatus.Warn;
                onlineNote = "online Store update check timed out — verify in the Xbox app if in doubt.";
            }
            else
                switch (task.Result.Availability)
                {
                    case Windows.ApplicationModel.PackageUpdateAvailability.NoUpdates:
                        onlineNote = "Store confirms NO update available (online check)."; break;
                    case Windows.ApplicationModel.PackageUpdateAvailability.Available:
                    case Windows.ApplicationModel.PackageUpdateAvailability.Required:
                        onlineVerdict = CheckStatus.Blocker;
                        onlineNote = "the Store reports an UPDATE AVAILABLE."; break;
                    default:
                        onlineVerdict = CheckStatus.Warn;
                        onlineNote = "online Store update check inconclusive — verify in the Xbox app if in doubt."; break;
                }
        }
        catch (Exception ex)
        {
            onlineVerdict = CheckStatus.Warn;
            onlineNote = $"online Store update check unavailable ({FirstLine(ex.Message)}) — verify in the Xbox app if in doubt.";
        }

        r.Checks.Add(onlineVerdict != CheckStatus.Ok
            ? new GameCheck("Up to date", onlineVerdict, $"Xbox package v{version} healthy, but {onlineNote}",
                "Update the game in the Xbox app before benchmarking.")
            : new GameCheck("Up to date", CheckStatus.Ok, $"Xbox package v{version} healthy; {onlineNote}"));
    }

    private static string FirstLine(string s)
    {
        int i = s.IndexOfAny(new[] { '\r', '\n' });
        return i > 0 ? s[..i] : s;
    }

    private static string JsonStr(System.Text.Json.JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool JsonBool(System.Text.Json.JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;

    // ---- Steam manifest helpers (read-only) ----

    private static string? FindSteamAppManifest(string appid)
    {
        try
        {
            var steam = SteamPath();
            if (steam is null) return null;
            foreach (var lib in SteamLibraries(steam))
            {
                var acf = Path.Combine(lib, "steamapps", $"appmanifest_{appid}.acf");
                if (File.Exists(acf)) return acf;
            }
        }
        catch { }
        return null;
    }

    private static string? SteamPath()
    {
        try
        {
            if (Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") is string p && Directory.Exists(p)) return p;
            if (Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")?.GetValue("InstallPath") is string p2 && Directory.Exists(p2)) return p2;
        }
        catch { }
        return null;
    }

    private static IEnumerable<string> SteamLibraries(string steam)
    {
        var libs = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string path) { if (Directory.Exists(path) && seen.Add(DiscoveredGame.NormalizePathKey(path))) libs.Add(path); }
        Add(steam);
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            try
            {
                var text = File.ReadAllText(vdf);
                foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]*)\""))
                    Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }
            catch { }
        }
        return libs;
    }

    private static int ParseIntField(string acf, string field)
        => int.TryParse(Regex.Match(acf, $"\"{field}\"\\s+\"(\\d+)\"").Groups[1].Value, out var v) ? v : -1;

    private static long ParseLongField(string acf, string field)
        => long.TryParse(Regex.Match(acf, $"\"{field}\"\\s+\"(\\d+)\"").Groups[1].Value, out var v) ? v : -1;

    private static bool RegistryKeyExists(string path)
    {
        try
        {
            int i = path.IndexOf('\\');
            if (i <= 0) return false;
            var hive = path[..i].ToUpperInvariant();
            var sub = path[(i + 1)..];
            using var root = hive switch
            {
                "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser.OpenSubKey(sub),
                "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine.OpenSubKey(sub),
                _ => null
            };
            return root is not null;
        }
        catch { return false; }
    }
}
