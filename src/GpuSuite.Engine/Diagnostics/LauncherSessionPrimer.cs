using System.Diagnostics;
using Microsoft.Win32;
using GpuSuite.Core.Models;

namespace GpuSuite.Engine.Diagnostics;

/// <summary>Starts required store clients without launching games so their update and account state can refresh.</summary>
public static class LauncherSessionPrimer
{
    public static async Task<IReadOnlyList<string>> EnsureRunningAsync(IEnumerable<GameProfile> games,
        CancellationToken ct, TimeSpan? refreshDelay = null)
    {
        var messages = new List<string>();
        bool startedAny = false;
        var stores = games.Where(g => g.Enabled).Select(EffectiveStore).ToHashSet();

        if (stores.Contains(GameStoreKind.Steam))
            startedAny |= StartDesktopClient("Steam", "steam", SteamExe(), null, messages);
        if (stores.Contains(GameStoreKind.Epic))
            startedAny |= StartDesktopClient("Epic Games Launcher", "EpicGamesLauncher",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe"), null, messages);
        if (stores.Contains(GameStoreKind.Origin))
            startedAny |= StartDesktopClient("EA App", "EADesktop",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Electronic Arts", "EA Desktop", "EA Desktop", "EADesktop.exe"), null, messages);
        if (stores.Contains(GameStoreKind.Uplay))
            startedAny |= StartDesktopClient("Ubisoft Connect", "upc",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Ubisoft", "Ubisoft Game Launcher", "UbisoftConnect.exe"), null, messages);
        if (stores.Contains(GameStoreKind.Gog))
            startedAny |= StartDesktopClient("GOG Galaxy", "GalaxyClient",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "GOG Galaxy", "GalaxyClient.exe"), null, messages);
        if (stores.Contains(GameStoreKind.Xbox))
            startedAny |= StartDesktopClient("Xbox app", "XboxPcApp", "explorer.exe",
                "shell:AppsFolder\\Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App", messages);

        if (startedAny)
            await Task.Delay(refreshDelay ?? TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
        return messages;
    }

    private static GameStoreKind EffectiveStore(GameProfile game)
    {
        var store = GameStore.Parse(game.Launch.Store);
        if (store == GameStoreKind.Standalone &&
            (game.Launch.Target?.Contains("GOG Galaxy", StringComparison.OrdinalIgnoreCase) == true ||
             long.TryParse(game.Launch.GameId, out _)))
            return GameStoreKind.Gog;
        return store;
    }

    private static bool StartDesktopClient(string label, string processName, string? executable,
        string? arguments, List<string> messages)
    {
        if (Process.GetProcessesByName(processName).Length > 0) return false;
        if (string.IsNullOrWhiteSpace(executable) ||
            (!executable.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) && !File.Exists(executable)))
        {
            messages.Add($"{label}: client executable not found.");
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized,
                Arguments = arguments ?? ""
            });
            messages.Add($"{label}: started for pre-flight refresh.");
            return true;
        }
        catch (Exception ex)
        {
            messages.Add($"{label}: failed to start ({ex.Message}).");
            return false;
        }
    }

    private static string? SteamExe()
    {
        try
        {
            string? path = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamExe") as string;
            if (!string.IsNullOrWhiteSpace(path)) return path.Replace('/', '\\');
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steam.exe");
    }
}
