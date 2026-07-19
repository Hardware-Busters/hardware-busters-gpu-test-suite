using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using GpuSuite.Core.Diagnostics;

namespace GpuSuite.Engine.Launch;

/// <summary>
/// Keeps the bench screen clean for an unattended run: gracefully closes the store-launcher windows the
/// suite causes to open (Steam, Epic, EA, GOG, Xbox app) and dismisses leftover game-crash dialogs
/// (e.g. Forza's "Video Card Crash FHC00" Exit box, Windows Error Reporting).
///
/// Launchers are CLOSED TO TRAY (WM_CLOSE via <see cref="Process.CloseMainWindow"/>), never killed — they
/// keep running and stay logged in, so the next launch and any login-walled store are unaffected; only the
/// on-screen window goes away. Crash dialogs ARE pure pollution, so they're closed and, if they won't close,
/// killed. Everything here is best-effort and never throws — tidying must not fail a run.
/// </summary>
public static class ScreenTidy
{
    // Store launchers the suite starts via protocol/exe. These are CLOSED TO TRAY (session preserved).
    // Game processes are NOT in this set — Cleanup kills those; this never touches the game or the suite.
    private static readonly string[] LauncherProcessNames =
    {
        "steam", "steamwebhelper",                 // Steam (the visible window may live on either)
        "EpicGamesLauncher",                        // Epic Games Launcher
        "EADesktop", "Origin",                      // EA app / legacy Origin
        "GalaxyClient",                             // GOG Galaxy
        "upc", "UbisoftConnect", "UbisoftGameLauncher", // Ubisoft Connect (was MISSING — its window blinded
                                                    // the vision-nav of the NEXT game in the 2026-07-02 sweep)
        "XboxPcApp", "GamingApp", "WinStore.App",   // Xbox app / Microsoft Store
    };

    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
    private const int SW_FORCEMINIMIZE = 11;   // minimize even a hung/other-thread window, non-interactively

    // Dedicated crash-REPORTER processes, killed BY NAME: their windows can be INVISIBLE to enumeration
    // (TLOU's 'crs-handler' PlayStation-PC reporter, spawned elevated by an elevated kill, sat on the bench
    // display for hours poisoning capture-card OCR while EnumWindows/MainWindowTitle from the suite saw
    // nothing — 2026-07-07), so the title scan below can't catch them. Pure pollution — no session to preserve.
    private static readonly string[] CrashReporterProcessNames =
    {
        "crs-handler",        // PlayStation-PC crash reporter (TLOU)
        "crs-video",          // PlayStation-PC video reporter (Ratchet); an orphan blocks Steam relaunch
        "BsSndRpt64", "BsSndRpt",   // BugSplat reporter — a leftover instance blocks Steam relaunches
        "CrashReportClient",  // Unreal Engine crash reporter
    };

    // Substrings (lower-case) that mark a top-level window as a game-crash / error dialog. Deliberately does
    // NOT include "not responding" — Windows appends that to ANY momentarily-busy window and we'd kill a
    // healthy app that was about to recover.
    private static readonly string[] CrashTitleMarkers =
    {
        "unexpected error", "has stopped working", "video card crash",
        "has crashed", "fatal error", "directx error", "device removed",
    };

    /// <summary>Close every open store-launcher window to the tray (graceful WM_CLOSE — sessions/logins survive).
    /// Returns how many were closed. Best-effort; never throws.</summary>
    public static int CloseLauncherWindows(RunLogger log)
    {
        int closed = 0;
        foreach (var name in LauncherProcessNames)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); } catch { continue; }
            foreach (var p in procs)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.MainWindowTitle) && p.CloseMainWindow())
                    {
                        closed++;
                        log.Info("Tidy", $"Closed launcher window to tray: {name} (\"{p.MainWindowTitle}\").");
                    }
                }
                catch { /* launcher exited / access denied — ignore */ }
                finally { p.Dispose(); }
            }
        }
        return closed;
    }

    /// <summary>FORCE-MINIMIZE every open store-launcher window (ShowWindowAsync SW_FORCEMINIMIZE) — the
    /// PRE-LAUNCH clear. Unlike <see cref="CloseLauncherWindows"/> (WM_CLOSE-to-tray, which Epic/Ubisoft can
    /// ignore or answer with a confirm dialog — it closed 0 windows in the 2026-07-02 sweep while Epic and
    /// Ubisoft blinded the next game's vision‑nav), a forced minimize ALWAYS clears the window off the single
    /// bench display without a prompt and without touching the session/login. Called right after a game
    /// process is detected, so the launcher that just spawned it can't sit over the capture card during nav.
    /// Returns how many were minimized. Best-effort; never throws.</summary>
    public static int MinimizeLauncherWindows(RunLogger log)
    {
        int n = 0;
        foreach (var name in LauncherProcessNames)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); } catch { continue; }
            foreach (var p in procs)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.MainWindowTitle)
                        && ShowWindowAsync(p.MainWindowHandle, SW_FORCEMINIMIZE))
                    {
                        n++;
                        log.Info("Tidy", $"Force-minimized launcher window off the display: {name} (\"{p.MainWindowTitle}\").");
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        return n;
    }

    /// <summary>Dismiss leftover game-crash / error dialogs: the Windows Error Reporting host plus any window
    /// whose title looks like a crash box (e.g. Forza's FHC00 Exit dialog). Closes gracefully, kills if that
    /// fails. Returns how many were dismissed. Best-effort; never throws.</summary>
    public static int DismissCrashArtifacts(RunLogger log)
    {
        int n = 0;
        // Windows Error Reporting host (the generic app-crash dialog).
        try
        {
            foreach (var w in Process.GetProcessesByName("WerFault"))
            {
                try { w.Kill(); n++; log.Trace("Tidy", $"Dismissed WerFault crash dialog (pid {w.Id})."); }
                catch { }
                finally { w.Dispose(); }
            }
        }
        catch { }

        n += DismissNamedCrashReporters(log);

        Process[] all;
        try { all = Process.GetProcesses(); } catch { return n; }
        foreach (var p in all)
        {
            try
            {
                var title = p.MainWindowTitle;
                if (p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(title))
                {
                    var t = title.ToLowerInvariant();
                    if (CrashTitleMarkers.Any(m => t.Contains(m)))
                    {
                        if (!p.CloseMainWindow()) { try { p.Kill(); } catch { } }
                        n++;
                        log.Info("Tidy", $"Dismissed crash dialog: \"{title}\" ({p.ProcessName}).");
                    }
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
        return n;
    }

    /// <summary>
    /// A crash reporter can spawn several seconds after the game process is killed. Run a short background
    /// sweep after cleanup so that delayed PlayStation/BugSplat reporters cannot cover the capture display or
    /// hold Steam's relaunch lock. This never blocks the benchmark cooldown or the next launch.
    /// </summary>
    public static void ScheduleDelayedCrashReporterSweep(RunLogger log, int durationMs = 20_000)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var deadline = Environment.TickCount64 + Math.Max(1_000, durationMs);
                while (Environment.TickCount64 < deadline)
                {
                    DismissNamedCrashReporters(log);
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
            catch { /* fire-and-forget bench hygiene must never fault the run */ }
        });
    }

    private static int DismissNamedCrashReporters(RunLogger log)
    {
        int n = 0;
        // Known crash reporters, by process name (their windows may not enumerate — see the list's comment).
        foreach (var name in CrashReporterProcessNames)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); } catch { continue; }
            foreach (var r in procs)
            {
                try
                {
                    if (!r.HasExited) { r.Kill(); n++; log.Info("Tidy", $"Killed crash reporter {name} (pid {r.Id})."); }
                }
                catch (Exception ex)
                {
                    // An ELEVATED reporter can't be killed from a non-elevated suite — say so loudly; it is
                    // probably sitting over the bench display and will poison OCR until removed.
                    log.Warn("Tidy", $"Crash reporter {name} (pid {r.Id}) could not be killed ({ex.GetType().Name}) — if it is elevated it may be camped INVISIBLY over the bench display.");
                }
                finally { r.Dispose(); }
            }
        }
        return n;
    }

    /// <summary>Full post-run tidy: dismiss crash dialogs, then close launcher windows to tray. Returns a short summary.</summary>
    public static string TidyAll(RunLogger log)
    {
        int dialogs = DismissCrashArtifacts(log);
        int launchers = CloseLauncherWindows(log);
        return $"{launchers} launcher window(s) closed, {dialogs} crash dialog(s) dismissed";
    }
}
