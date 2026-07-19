using System.Text.Json;
using System.Text.RegularExpressions;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Models;
using Microsoft.Win32;

namespace GpuSuite.Engine.Discovery;

/// <summary>
/// Builds the <see cref="GameCatalog"/> by scanning installed launchers and the registry. DETECTION
/// ONLY — it reports what is installed (id, path, exe, process name) so the user's chosen benchmark
/// games can be matched to real installs. It never selects, enables, or runs anything. Ported in
/// spirit from the existing GPU Automation tools' FindGameIDs.py (Steam .acf, Epic manifests, …)
/// and extended to GOG, Ubisoft Connect, EA/Origin, Battle.net, Rockstar, Xbox/MS Store, plus a
/// registry-uninstall + common-folder fallback.
/// </summary>
public sealed class LauncherDiscovery
{
    private readonly RunLogger? _log;
    public LauncherDiscovery(RunLogger? log = null) => _log = log;

    public GameCatalog DiscoverAll()
    {
        var cat = new GameCatalog();
        var raw = new List<DiscoveredGame>();
        SafeAdd(raw, DiscoverSteam, "Steam");
        SafeAdd(raw, DiscoverEpic, "Epic");
        SafeAdd(raw, DiscoverGog, "GOG");
        SafeAdd(raw, DiscoverUbisoft, "Ubisoft");
        SafeAdd(raw, DiscoverEaOrigin, "EA/Origin");
        SafeAdd(raw, DiscoverBattlenet, "Battle.net");
        SafeAdd(raw, DiscoverRockstar, "Rockstar");
        SafeAdd(raw, DiscoverXbox, "Xbox");
        SafeAdd(raw, DiscoverRegistryUninstall, "Registry");

        // De-dupe: a game can surface from both its store scanner and the registry fallback.
        // Keep the first (store scanners run first and carry the richer store id / launch URI).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in raw)
        {
            if (string.IsNullOrWhiteSpace(g.Name)) continue;
            if (seen.Add(g.DedupeKey)) cat.Games.Add(g);
        }

        _log?.Info("Discovery", $"Catalog: {cat.Games.Count} installed game(s) across launchers " +
                                $"(from {raw.Count} raw hits before de-dupe).");
        return cat;
    }

    private void SafeAdd(List<DiscoveredGame> sink, Func<IEnumerable<DiscoveredGame>> scan, string label)
    {
        try
        {
            int before = sink.Count;
            sink.AddRange(scan());
            _log?.Trace("Discovery", $"{label}: {sink.Count - before} game(s).");
        }
        catch (Exception ex) { _log?.Warn("Discovery", $"{label} scan failed: {ex.Message}"); }
    }

    // ---------------- Steam ----------------
    private IEnumerable<DiscoveredGame> DiscoverSteam()
    {
        var steam = SteamPath();
        if (steam is null) yield break;

        foreach (var lib in SteamLibraries(steam))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                string text;
                try { text = File.ReadAllText(acf); } catch { continue; }
                string id = Match(text, "\"appid\"\\s+\"(\\d+)\"");
                string name = Match(text, "\"name\"\\s+\"([^\"]*)\"");
                string dir = Match(text, "\"installdir\"\\s+\"([^\"]*)\"");
                if (string.IsNullOrEmpty(id)) continue;
                // Skip Steamworks redistributables / tools that are not games.
                if (name.Contains("Steamworks", StringComparison.OrdinalIgnoreCase)) continue;
                string? install = string.IsNullOrEmpty(dir) ? null : Path.Combine(apps, "common", dir);
                var exe = install is not null && Directory.Exists(install) ? GuessExe(install, name) : null;
                yield return new DiscoveredGame
                {
                    Store = GameStoreKind.Steam,
                    GameId = id,
                    Name = name,
                    InstallDir = install,
                    Exe = exe,
                    ProcessName = ProcName(exe),
                    Source = "Steam manifest"
                };
            }
        }
    }

    private static string? SteamPath()
    {
        try
        {
            var p = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
            p = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
        }
        catch { }
        return null;
    }

    private static IEnumerable<string> SteamLibraries(string steam)
    {
        // Dedupe on a normalized key (case + slash direction). libraryfolders.vdf lists the primary
        // library too, sometimes formatted differently than the registry path (e.g. "${LOCAL_WINDOWS_PATH}/.../steam"
        // vs "${LOCAL_WINDOWS_PATH}/...\Steam"); without normalizing it would be scanned twice → duplicate catalog rows.
        var libs = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string path)
        {
            if (Directory.Exists(path) && seen.Add(DiscoveredGame.NormalizePathKey(path))) libs.Add(path);
        }

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

    // ---------------- Epic ----------------
    private IEnumerable<DiscoveredGame> DiscoverEpic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;

        foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
        {
            DiscoveredGame? g = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(item));
                var r = doc.RootElement;
                string id = Str(r, "AppName");
                string name = Str(r, "DisplayName");
                string loc = Str(r, "InstallLocation");
                string exeRel = Str(r, "LaunchExecutable");
                if (string.IsNullOrEmpty(id)) continue;
                string? exe = (!string.IsNullOrEmpty(loc) && !string.IsNullOrEmpty(exeRel)) ? Path.Combine(loc, exeRel) : null;
                g = new DiscoveredGame
                {
                    Store = GameStoreKind.Epic,
                    GameId = id,
                    Name = name,
                    InstallDir = string.IsNullOrEmpty(loc) ? null : loc,
                    Exe = exe,
                    ProcessName = ProcName(exe),
                    Source = "Epic manifest"
                };
            }
            catch { }
            if (g is not null) yield return g;
        }
    }

    // ---------------- GOG Galaxy ----------------
    private IEnumerable<DiscoveredGame> DiscoverGog()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            RegistryKey? games = null;
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                games = hklm.OpenSubKey(@"SOFTWARE\GOG.com\Games");
            }
            catch { }
            if (games is null) continue;
            using (games)
            {
                foreach (var sub in games.GetSubKeyNames())
                {
                    DiscoveredGame? g = null;
                    try
                    {
                        using var k = games.OpenSubKey(sub);
                        if (k is null) continue;
                        string id = (k.GetValue("gameID") as string) ?? sub;
                        string name = (k.GetValue("gameName") as string) ?? (k.GetValue("name") as string) ?? sub;
                        string? path = k.GetValue("path") as string;
                        string? exeFile = k.GetValue("exe") as string ?? k.GetValue("exeFile") as string;
                        string? exe = !string.IsNullOrEmpty(exeFile) && !string.IsNullOrEmpty(path) && !Path.IsPathRooted(exeFile)
                            ? Path.Combine(path, exeFile)
                            : exeFile;
                        if (string.IsNullOrEmpty(exe) && Directory.Exists(path)) exe = GuessExe(path!, name);
                        g = new DiscoveredGame
                        {
                            Store = GameStoreKind.Gog, GameId = id, Name = name,
                            InstallDir = path, Exe = exe, ProcessName = ProcName(exe), Source = "GOG registry"
                        };
                    }
                    catch { }
                    if (g is not null) yield return g;
                }
            }
        }
    }

    // ---------------- Ubisoft Connect (Uplay) ----------------
    private IEnumerable<DiscoveredGame> DiscoverUbisoft()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            RegistryKey? installs = null;
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                installs = hklm.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs");
            }
            catch { }
            if (installs is null) continue;
            using (installs)
            {
                foreach (var id in installs.GetSubKeyNames())
                {
                    DiscoveredGame? g = null;
                    try
                    {
                        using var k = installs.OpenSubKey(id);
                        var dir = k?.GetValue("InstallDir") as string;
                        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                        var name = NameFromFolder(dir);
                        var exe = GuessExe(dir, name);
                        g = new DiscoveredGame
                        {
                            Store = GameStoreKind.Uplay, GameId = id, Name = name,
                            InstallDir = dir, Exe = exe, ProcessName = ProcName(exe), Source = "Ubisoft registry"
                        };
                    }
                    catch { }
                    if (g is not null) yield return g;
                }
            }
        }
    }

    // ---------------- EA app / Origin ----------------
    private IEnumerable<DiscoveredGame> DiscoverEaOrigin()
    {
        // EA installs land in common roots; Origin manifests under ProgramData carry the content id.
        var roots = new[]
        {
            Path.Combine(ProgramFiles(), "EA Games"),
            Path.Combine(ProgramFilesX86(), "EA Games"),
            Path.Combine(ProgramFilesX86(), "Origin Games"),
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in SafeDirs(root))
            {
                var name = NameFromFolder(dir);
                var exe = GuessExe(dir, name);
                if (exe is null) continue;
                yield return new DiscoveredGame
                {
                    Store = GameStoreKind.Origin, GameId = "", Name = name,
                    InstallDir = dir, Exe = exe, ProcessName = ProcName(exe), Source = "EA/Origin folder"
                };
            }
        }
    }

    // ---------------- Battle.net (Blizzard) ----------------
    private IEnumerable<DiscoveredGame> DiscoverBattlenet()
    {
        // Blizzard titles register normal Uninstall entries with InstallLocation; the agent DB is
        // protobuf so we lean on the uninstall keys + the default install root.
        var root = Path.Combine(ProgramFilesX86());
        foreach (var folder in new[] { "Overwatch", "Diablo IV", "Diablo III", "Hearthstone",
                                        "World of Warcraft", "StarCraft II", "Call of Duty" })
        {
            var dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir)) continue;
            var exe = GuessExe(dir, folder);
            if (exe is null) continue;
            yield return new DiscoveredGame
            {
                Store = GameStoreKind.Battlenet, GameId = "", Name = folder,
                InstallDir = dir, Exe = exe, ProcessName = ProcName(exe), Source = "Battle.net folder"
            };
        }
    }

    // ---------------- Rockstar Games Launcher ----------------
    private IEnumerable<DiscoveredGame> DiscoverRockstar()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            RegistryKey? games = null;
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                games = hklm.OpenSubKey(@"SOFTWARE\Rockstar Games");
            }
            catch { }
            if (games is null) continue;
            using (games)
            {
                foreach (var sub in games.GetSubKeyNames())
                {
                    DiscoveredGame? g = null;
                    try
                    {
                        using var k = games.OpenSubKey(sub);
                        var dir = k?.GetValue("InstallFolder") as string;
                        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                        var exe = GuessExe(dir, sub);
                        g = new DiscoveredGame
                        {
                            Store = GameStoreKind.Rockstar, GameId = "", Name = sub,
                            InstallDir = dir, Exe = exe, ProcessName = ProcName(exe), Source = "Rockstar registry"
                        };
                    }
                    catch { }
                    if (g is not null) yield return g;
                }
            }
        }
    }

    // ---------------- Xbox / Microsoft Store ----------------
    private IEnumerable<DiscoveredGame> DiscoverXbox()
    {
        // Modern Xbox app installs unpacked games under <drive>:\XboxGames\<Game>\Content.
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            var root = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            if (!Directory.Exists(root)) continue;
            foreach (var dir in SafeDirs(root))
            {
                var content = Path.Combine(dir, "Content");
                var search = Directory.Exists(content) ? content : dir;
                var name = NameFromFolder(dir);
                var exe = GuessExe(search, name);
                if (exe is null) continue;
                // Cosmetic-DLC / add-on content folders only contain the Xbox stub launcher — skip them.
                if (string.Equals(ProcName(exe), "gamelaunchhelper.exe", StringComparison.OrdinalIgnoreCase)) continue;
                yield return new DiscoveredGame
                {
                    Store = GameStoreKind.Xbox, GameId = "", Name = name,
                    InstallDir = search, Exe = exe, ProcessName = ProcName(exe), Source = "XboxGames folder"
                };
            }
        }
    }

    // ---------------- Registry uninstall fallback ----------------
    private IEnumerable<DiscoveredGame> DiscoverRegistryUninstall()
    {
        var hits = new List<DiscoveredGame>();
        foreach (var (hive, view) in new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
        })
        {
            RegistryKey? uninstall = null;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            }
            catch { }
            if (uninstall is null) continue;
            using (uninstall)
            {
                foreach (var sub in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var k = uninstall.OpenSubKey(sub);
                        if (k is null) continue;
                        var name = k.GetValue("DisplayName") as string;
                        var loc = k.GetValue("InstallLocation") as string;
                        var publisher = k.GetValue("Publisher") as string ?? "";
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(loc)) continue;
                        if (!Directory.Exists(loc)) continue;
                        if (IsNonGameName(name)) continue;             // launcher / runtime / driver entry
                        if (!LooksLikeGame(loc, publisher)) continue;
                        var exe = GuessExe(loc, name);
                        if (exe is null) continue;
                        hits.Add(new DiscoveredGame
                        {
                            Store = GameStoreKind.Unknown, GameId = "", Name = name.Trim(),
                            InstallDir = loc, Exe = exe, ProcessName = ProcName(exe), Source = "Registry uninstall"
                        });
                    }
                    catch { }
                }
            }
        }
        return hits;
    }

    /// <summary>Heuristic: keep uninstall entries that sit under a known game root or a games publisher.</summary>
    private static bool LooksLikeGame(string installLoc, string publisher)
    {
        var p = installLoc.Replace('/', '\\').ToLowerInvariant();
        string[] gameRoots =
        {
            "\\steamapps\\common\\", "\\epic games\\", "\\gog galaxy\\games", "\\gog games\\",
            "\\ubisoft\\", "\\ea games\\", "\\origin games\\", "\\rockstar games\\", "\\xboxgames\\",
            "\\games\\"
        };
        if (gameRoots.Any(r => p.Contains(r))) return true;
        string[] pubs = { "ubisoft", "electronic arts", "rockstar", "bethesda", "square enix",
                          "bandai", "valve", "cd projekt", "2k", "capcom", "sega", "blizzard",
                          "warner bros", "epic games", "gog" };
        return pubs.Any(pub => publisher.Contains(pub, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------- helpers ----------------
    private static string Match(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value : "";
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string ProgramFiles() =>
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static string ProgramFilesX86() =>
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root); }
        catch { return Array.Empty<string>(); }
    }

    private static string NameFromFolder(string dir) =>
        new DirectoryInfo(dir.TrimEnd('\\', '/')).Name;

    private static string? ProcName(string? exe) =>
        string.IsNullOrEmpty(exe) ? null : Path.GetFileName(exe);

    // Exes that are launchers/installers/redists/tools — never the game we want PresentMon to track.
    private static readonly string[] ExeBlacklist =
    {
        "unins", "uninstall", "setup", "install", "redist", "vcredist", "dxsetup", "directx",
        "crashpad", "crashreport", "crashhandler", "launcher", "bootstrap", "activation",
        "touchup", "cleanup", "config", "settings", "benchmark_setup", "easyanticheat",
        "battleye", "be_service", "anticheat", "dotnet", "oalinst", "vc_redist", "ueprereq",
        "prerequisites", "epicgameslauncher", "notification", "helper", "service",
        "gamelaunchhelper"   // Xbox/MS-Store stub launcher, not the game
    };

    // Uninstall-entry names that are launchers/runtimes/drivers, not games.
    private static readonly string[] NonGameNames =
    {
        "launcher", "ubisoft connect", "ubisoft game", "epic games", "ea app", "ea desktop",
        "origin", "battle.net", "steam", "gog galaxy", "rockstar games launcher", "xbox",
        "redistributable", "runtime", "framework", ".net", "directx", "visual c++", "vcredist",
        "driver", "geforce", "nvidia", "radeon", "amd software", "microsoft edge", "overlay",
        "anticheat", "easyanticheat", "battleye", "afterburner", "rivatuner", "msi center"
    };

    private static bool IsNonGameName(string name) =>
        NonGameNames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Best-effort main-exe pick when the manifest carries no executable. Searches the install dir
    /// (then one/two levels down), filters out launchers/installers/redists, and prefers an exe whose
    /// name resembles the game/folder name and which is reasonably large.
    /// </summary>
    private static string? GuessExe(string installDir, string name)
    {
        try
        {
            if (!Directory.Exists(installDir)) return null;
            var exes = new List<string>();
            exes.AddRange(Directory.EnumerateFiles(installDir, "*.exe", SearchOption.TopDirectoryOnly));
            if (exes.Count == 0)
            {
                // some installs nest the binary one or two levels down (e.g. \bin\x64\game.exe)
                foreach (var sub in SafeDirs(installDir))
                {
                    exes.AddRange(Directory.EnumerateFiles(sub, "*.exe", SearchOption.TopDirectoryOnly));
                    foreach (var sub2 in SafeDirs(sub))
                        exes.AddRange(Directory.EnumerateFiles(sub2, "*.exe", SearchOption.TopDirectoryOnly));
                }
            }
            var candidates = exes
                .Where(e => !ExeBlacklist.Any(b =>
                    Path.GetFileNameWithoutExtension(e).Contains(b, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (candidates.Count == 0) candidates = exes; // nothing survived the filter — fall back
            if (candidates.Count == 0) return null;

            var token = new string((name ?? "").Where(char.IsLetterOrDigit).ToArray());
            string Norm(string f) => new string(Path.GetFileNameWithoutExtension(f).Where(char.IsLetterOrDigit).ToArray());

            // 1) exe whose name resembles the game name
            var named = candidates.FirstOrDefault(e =>
                token.Length > 2 && Norm(e).Contains(token, StringComparison.OrdinalIgnoreCase));
            if (named is not null) return named;

            // 2) otherwise the largest exe (the engine binary is usually the biggest)
            return candidates
                .OrderByDescending(e => { try { return new FileInfo(e).Length; } catch { return 0L; } })
                .First();
        }
        catch { return null; }
    }
}
