using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Display;

namespace GpuSuite.Engine.Launch;

public sealed class LaunchResult
{
    public bool Launched { get; init; }
    public bool Simulated { get; init; }
    public int? Pid { get; init; }
    public Process? Process { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>
/// Outcome of applying a resolution before launch. When <see cref="Ok"/> is false the orchestrator
/// must NOT launch the game and marks the run Invalid — launching after a failed config edit would
/// run at the wrong resolution and (for games that truncate their settings on launch) destroy the
/// settings file. <see cref="Applied"/> is true when an edit actually changed something.
/// </summary>
public sealed class ResolutionApplyResult
{
    public bool Ok { get; init; } = true;
    public bool Applied { get; init; }
    public string Detail { get; init; } = "";
    public static ResolutionApplyResult Pass(bool applied, string detail) => new() { Ok = true, Applied = applied, Detail = detail };
    public static ResolutionApplyResult Fail(string detail) => new() { Ok = false, Applied = false, Detail = detail };
}

/// <summary>
/// Outcome of applying a graphics variant ("extra model") before launch. <see cref="Ok"/> is false when
/// any config-file setting could not be applied AND verified — the orchestrator then marks the run Invalid
/// rather than benchmark the wrong (or a silently-collapsed) model. <see cref="PendingMenu"/> lists knobs
/// whose apply method is the in-game menu; those are realized post-launch by the vision-nav engine.
/// </summary>
public sealed class VariantApplyResult
{
    public bool Ok { get; set; } = true;
    public int AppliedCount { get; set; }
    public string Detail { get; set; } = "";
    public List<string> Issues { get; } = new();
    public List<string> PendingMenu { get; } = new();
    public List<string> EffectiveSummary { get; } = new();
    public static VariantApplyResult Pass(int applied, string detail) => new() { Ok = true, AppliedCount = applied, Detail = detail };
}

/// <summary>
/// Launches a game through its store launcher (protocol URI), then waits for the real game
/// process to appear and resolves its PID for capture. Mirrors the proven launcher pattern
/// (Steam/Epic/Uplay/Origin/GOG) from the existing GPU Automation tools. Missing launchers/games
/// fail honestly by default; synthetic launch fallback is available only through an explicit
/// development option.
/// </summary>
public sealed class GameLauncher
{
    private readonly RunLogger _log;
    private readonly bool _simulateLaunch;
    private readonly bool _simulateOnFailure;
    private readonly bool _attachToRunning;
    private readonly int _maxRefreshHz;
    private readonly string? _profilesDir;
    private HashSet<string>? _allGameProcessBaseNames;
    private DisplayMode? _benchNativeMode;   // desktop mode captured before the FIRST SwitchDesktopMode switch → the restore target
    public GameLauncher(RunLogger log, bool simulateLaunch = false, bool attachToRunning = false, int maxRefreshHz = 60, bool simulateOnFailure = false, string? profilesDir = null)
    {
        _log = log;
        _simulateLaunch = simulateLaunch;
        _simulateOnFailure = simulateOnFailure;
        _attachToRunning = attachToRunning;
        _maxRefreshHz = maxRefreshHz <= 0 ? 60 : maxRefreshHz;
        _profilesDir = profilesDir;
    }

    public LaunchResult Launch(GameProfile game)
    {
        var spec = game.Launch;
        string store = (spec.Store ?? "Steam").Trim();

        // --attach: do NOT launch — the operator already has the game running and prepped (logged in,
        // save loaded, in a representative scene). Find the process and measure it; Cleanup leaves it
        // running. Overrides --no-launch. The grace below is short in practice: the process already
        // exists, so it resolves on the first poll.
        if (_attachToRunning)
        {
            _log.Info("Launch", $"--attach — measuring the already-running '{game.CaptureProcessName}' (not started; will not be killed).");
            return WaitAndResolve(game, "attach (already running)");
        }

        // --no-launch: validate the full pipeline (settings/completion/capture/validation/report)
        // against a real-game profile WITHOUT starting the game.
        if (_simulateLaunch)
        {
            _log.Info("Launch", $"--no-launch — SIMULATED launch for '{game.Name}' (game not started).");
            return Simulated("--no-launch");
        }

        // ONE-GAME-AT-A-TIME guard (operator rule 2026-07-01): before we start this game, make sure NO other
        // game — and no stale instance of THIS one — is still running. A second game left over from a prior run
        // or session steals foreground from the captured (borderless) game (pausing it → "frozen capture"),
        // competes for the GPU (contaminating the numbers), AND its foreground window swallows the nav bot's
        // injected input (the bot's PLAY/Enter leaks to whatever is foreground), stranding this game on its
        // launcher. Runs for every real launch path below (attach/no-launch already returned above).
        SoloGameSweep(game);

        // Standalone: launch a direct exe (only when explicitly configured).
        if (store.Equals("Standalone", StringComparison.OrdinalIgnoreCase))
            return LaunchStandalone(game, spec);

        // Manual: the operator starts the game; we just wait for the process.
        if (store.Equals("Manual", StringComparison.OrdinalIgnoreCase))
        {
            _log.Info("Launch", $"Manual mode — waiting up to {game.StartupGraceSeconds}s for '{game.CaptureProcessName}'.");
            return WaitAndResolve(game, $"manual ({store})");
        }

        // Xbox / MS Store packaged app: activate via the shell AUMID (PackageFamilyName!AppId) — a
        // packaged app cannot be reliably started by running its exe directly (licensing/activation).
        if (GameStore.Parse(store) == GameStoreKind.Xbox)
            return LaunchXbox(game, spec);

        // Steam WITH launch arguments: deliver +set cvars / +com_skipIntroVideo / +r_custom* reliably via
        // `steam.exe -applaunch <id> <args>`. The steam://run//args URI silently DROPS params (verified
        // live 2026-06-24), so any Steam profile carrying launch.arguments must go through -applaunch.
        // This is what makes DOOM run WINDOWED (+set r_fullscreen 0) so it can't take an exclusive >60Hz
        // mode that blanks the 60Hz-capped Elgato. Falls through to the rungameid URI if steam.exe can't be resolved.
        if (store.Equals("Steam", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(spec.Arguments))
        {
            var applaunch = LaunchSteamWithArgs(game, spec);
            if (applaunch is not null) return applaunch;
        }

        // EA/Origin: the origin2 protocol NO-OPS when the title is already running (EA Desktop reports "already
        // running"), so a STALE instance left by a crashed / aborted / manually-poked prior session would be
        // silently attached and measured MID-STATE (e.g. a half-driven menu) instead of cold-launched to the
        // title — exactly what corrupts the benchmark-nav bot. Pre-kill any existing game process + the EA
        // AntiCheat services so this launch starts cold. (Attach/Manual already returned above; attach
        // intentionally reuses a running instance.) Mirrors Cleanup's EA handling, run pre-launch.
        if (store.Equals("EA", StringComparison.OrdinalIgnoreCase) || store.Equals("Origin", StringComparison.OrdinalIgnoreCase))
            KillStaleGame(game, "EA protocol no-ops over a running instance");
        // Uplay/Ubisoft: uplay://launch FOCUSES an already-running title instead of cold-launching it, so a stale
        // instance (e.g. left by calibration or a crashed/aborted prior run) would be silently attached + measured
        // MID-STATE (a half-driven menu) instead of from the title. Kill it first so the run cold-launches clean.
        // (No AntiCheat teardown for Uplay — KillStaleGame gates that to EA; Ubisoft titles use Denuvo, no services.)
        else if (store.Equals("Uplay", StringComparison.OrdinalIgnoreCase) || store.Equals("Ubisoft", StringComparison.OrdinalIgnoreCase))
            KillStaleGame(game, "Uplay focuses a running instance instead of cold-launching");
        // Epic: com.epicgames.launcher://...?action=launch FOCUSES an already-running title (it won't cold-launch a
        // second instance), so a stale one left mid-level would be measured in the wrong state. Kill it first.
        else if (store.Equals("Epic", StringComparison.OrdinalIgnoreCase) || store.Equals("EGS", StringComparison.OrdinalIgnoreCase))
            KillStaleGame(game, "Epic focuses a running instance instead of cold-launching");

        // Store launcher via protocol URI.
        string? uri = BuildLaunchUri(store, spec.GameId);
        if (uri is null)
        {
            _log.Warn("Launch", $"Store '{store}' with id '{spec.GameId}' could not form a launch URI.");
            return LaunchFallback("no launch URI");
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            _log.Info("Launch", $"Requested {store} launch: {uri}{(string.IsNullOrWhiteSpace(spec.Arguments) ? "" : " args=" + spec.Arguments)}");
        }
        catch (Exception ex)
        {
            _log.Warn("Launch", $"Launcher request failed: {ex.Message}.");
            return LaunchFallback("launcher request failed: " + ex.Message);
        }

        return WaitAndResolve(game, $"{store} id {spec.GameId}");
    }

    private LaunchResult LaunchStandalone(GameProfile game, LaunchSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Target) || !File.Exists(spec.Target))
        {
            _log.Warn("Launch", $"Standalone target '{spec.Target}' not found.");
            return LaunchFallback("exe not found");
        }
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = spec.Target,
                Arguments = spec.Arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(spec.WorkingDirectory) ? (Path.GetDirectoryName(spec.Target) ?? "") : spec.WorkingDirectory,
                UseShellExecute = false
            };
            var p = Process.Start(psi);
            _log.Info("Launch", $"Launched standalone '{spec.Target}' (pid={p?.Id.ToString() ?? "n/a"}).");
            // Some games relaunch into a child process; prefer the named capture process if it differs.
            var pid = WaitForGameProcess(game.CaptureProcessName, game.StartupGraceSeconds) ?? p?.Id;
            return new LaunchResult { Launched = pid is not null, Simulated = pid is null, Pid = pid, Process = p, Detail = "standalone" };
        }
        catch (Exception ex)
        {
            _log.Error("Launch", $"Standalone launch failed: {ex.Message} — SIMULATED.");
            return LaunchFallback("standalone launch failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Launch a packaged Xbox / MS Store app by activating its AUMID (PackageFamilyName!AppId) through
    /// the shell, then wait for the real game process (the package's launch helper chains to it).
    /// </summary>
    private LaunchResult LaunchXbox(GameProfile game, LaunchSpec spec)
    {
        var aumid = spec.GameId;
        if (string.IsNullOrWhiteSpace(aumid))
        {
            _log.Warn("Launch", "Xbox launch needs the package AUMID (PackageFamilyName!AppId) in launch.gameId — SIMULATED.");
            return LaunchFallback("no AUMID");
        }
        // Xbox/MS Store AUMID activation FOCUSES an already-running instance (the shell brings the existing
        // window forward instead of cold-launching a second one), so a stale ForzaTech/Asobo title left by a
        // crashed/aborted/manually-poked prior session would be silently attached + measured MID-STATE (a
        // half-driven menu or an in-progress benchmark) instead of from the title. Kill it first so this run
        // cold-launches clean — mirrors the Epic/Uplay handling above (no AntiCheat teardown; Game Pass titles
        // don't ship the EA services KillStaleGame gates to EA).
        KillStaleGame(game, "Xbox AUMID activation focuses a running instance instead of cold-launching");
        ClearStaleXboxLaunchState(game);
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"shell:AppsFolder\\{aumid}", UseShellExecute = true });
            _log.Info("Launch", $"Requested Xbox/MS Store launch via shell AUMID: {aumid}");
        }
        catch (Exception ex)
        {
            _log.Warn("Launch", $"Xbox launch failed: {ex.Message} — SIMULATED.");
            return LaunchFallback("xbox launch failed: " + ex.Message);
        }
        return WaitAndResolve(game, $"Xbox AUMID {aumid}");
    }

    /// <summary>Gaming Services can leave a hidden crash/sync UI plus GameLaunchHelper alive after an Xbox
    /// title fails. A later AUMID activation then silently reuses that poisoned state and never creates the
    /// render process (MSFS 2024: hidden "The game has crashed", 0xffffffff). Clear only when the error/UI
    /// process exists; healthy sequential Xbox launches do not restart services.</summary>
    private void ClearStaleXboxLaunchState(GameProfile game)
    {
        Process[] ui;
        try { ui = Process.GetProcessesByName("gamingservicesui"); }
        catch { ui = Array.Empty<Process>(); }
        bool poisoned = ui.Length > 0;
        int killed = 0;
        foreach (var process in ui.Concat(SafeProcessesByName("gamelaunchhelper")))
        {
            try
            {
                if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); killed++; }
            }
            catch { }
            finally { process.Dispose(); }
        }
        if (killed > 0)
            _log.Warn("Launch", $"Xbox pre-launch: cleared {killed} stale Gaming Services/helper process(es) before {game.Name}.");
        if (!poisoned) return;

        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("Restart-Service -Name GamingServices,GamingServicesNet -Force -ErrorAction Stop");
            using var restart = Process.Start(psi);
            if (restart is null || !restart.WaitForExit(20000) || restart.ExitCode != 0)
            {
                string detail = restart is null ? "process did not start" : restart.StandardError.ReadToEnd().Trim();
                _log.Warn("Launch", "Xbox pre-launch: stale crash UI was cleared, but Gaming Services restart failed: " + detail);
            }
            else
            {
                _log.Info("Launch", "Xbox pre-launch: restarted GamingServices + GamingServicesNet after stale crash UI (prevents AUMID no-op).");
                Thread.Sleep(2000);
            }
        }
        catch (Exception ex) { _log.Warn("Launch", "Xbox pre-launch Gaming Services recovery issue: " + ex.Message); }
    }

    private static IEnumerable<Process> SafeProcessesByName(string name)
    {
        try { return Process.GetProcessesByName(name); }
        catch { return Array.Empty<Process>(); }
    }

    /// <summary>
    /// Launch a Steam game WITH arguments via `steam.exe -applaunch &lt;id&gt; &lt;args&gt;` — the reliable way to pass
    /// +set cvars / launch flags to the game (the steam://run//args URI drops them). Returns null when
    /// steam.exe cannot be resolved so the caller falls back to the rungameid URI.
    /// </summary>
    private LaunchResult? LaunchSteamWithArgs(GameProfile game, LaunchSpec spec)
    {
        var steamExe = SteamExePath();
        if (steamExe is null || !File.Exists(steamExe))
        {
            _log.Warn("Launch", "Steam.exe not found (HKCU\\Software\\Valve\\Steam) — falling back to rungameid URI (launch args dropped).");
            return null;
        }
        try
        {
            var args = $"-applaunch {spec.GameId} {spec.Arguments}".Trim();
            Process.Start(new ProcessStartInfo { FileName = steamExe, Arguments = args, UseShellExecute = false });
            _log.Info("Launch", $"Requested Steam -applaunch: {steamExe} {args}");
            return WaitAndResolve(game, $"Steam -applaunch {spec.GameId} (args: {spec.Arguments})");
        }
        catch (Exception ex)
        {
            _log.Warn("Launch", $"Steam -applaunch failed: {ex.Message} — falling back to rungameid URI.");
            return null;
        }
    }

    /// <summary>Resolve the Steam executable path from the registry (SteamExe, else SteamPath\steam.exe).</summary>
    private static string? SteamExePath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamExe") is string exe && File.Exists(exe)) return exe;
            if (key?.GetValue("SteamPath") is string path)
            {
                var candidate = Path.Combine(path, "steam.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { }
        return null;
    }

    private LaunchResult WaitAndResolve(GameProfile game, string detail)
    {
        var pid = WaitForGameProcess(game.CaptureProcessName, game.StartupGraceSeconds);
        if (pid is null)
        {
            _log.Warn("Launch", $"Game process '{game.CaptureProcessName}' did not appear within {game.StartupGraceSeconds}s — launch failed.");
            return LaunchFallback("process '" + game.CaptureProcessName + "' did not appear within " + game.StartupGraceSeconds + "s");
        }
        _log.Info("Launch", $"Game process '{game.CaptureProcessName}' detected (pid {pid}) via {detail}.");
        HandleF1ModeSelector(game);
        if (!_attachToRunning && game.Launch.ProcessStabilizationSeconds > 0)
        {
            int? stablePid = StabilizeGameProcess(
                game.CaptureProcessName, pid.Value, game.Launch.ProcessStabilizationSeconds);
            if (stablePid is null)
            {
                _log.Warn("Launch", $"Game process '{game.CaptureProcessName}' vanished during its " +
                    $"{game.Launch.ProcessStabilizationSeconds}s stabilization window and no replacement appeared.");
                return LaunchFallback("game process vanished during bootstrap stabilization");
            }
            pid = stablePid;
        }
        // PRE-NAV screen clear (2026-07-02): the launcher that just spawned the game (Epic/Ubisoft/EA/GOG/Xbox)
        // often leaves its own window sitting over the single bench display — which BLINDS the next game's
        // capture-card vision‑nav and trips the scene-static MotionGuard on the covered screen (cost 4 games in
        // the first full RTSS-free sweep: AW2, Black Myth, Cyberpunk ×2). Force-minimize it off the display now
        // (session/login preserved) so the game owns the screen before the bot starts reading it.
        try { ScreenTidy.MinimizeLauncherWindows(_log); } catch { }
        return new LaunchResult { Launched = true, Pid = pid, Detail = detail };
    }

    /// <summary>
    /// F1 25's EA launch chain starts the render-process bootstrap and then opens a separate
    /// VROriginSelector modal (normal game versus VR). Seeing F1_25.exe is therefore not enough to
    /// declare startup complete: unattended runs otherwise sit behind the selector until its bootstrap
    /// exits, and the title-screen bot reports a misleading game crash. Select the normal, non-VR option
    /// and press Play before capture/navigation begins.
    /// </summary>
    private void HandleF1ModeSelector(GameProfile game)
    {
        if (_attachToRunning || !game.Id.Equals("f1-25", StringComparison.OrdinalIgnoreCase)) return;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            GpuSuite.Core.RunHeartbeat.Ping();
            Process? selector = null;
            try
            {
                selector = Process.GetProcessesByName("VROriginSelector")
                    .OrderByDescending(p => { try { return p.StartTime; } catch { return DateTime.MinValue; } })
                    .FirstOrDefault();
                if (selector is null) { Thread.Sleep(250); continue; }

                selector.Refresh();
                var root = selector.MainWindowHandle;
                if (root == IntPtr.Zero) { Thread.Sleep(250); continue; }

                IntPtr normalChoice = IntPtr.Zero;
                IntPtr play = IntPtr.Zero;
                NativeMethods.EnumChildWindows(root, (child, _) =>
                {
                    var text = NativeMethods.WindowText(child);
                    if (text.Equals("Play", StringComparison.OrdinalIgnoreCase)) play = child;
                    else if (text.StartsWith("Play F1", StringComparison.OrdinalIgnoreCase) &&
                             !text.Contains("VR", StringComparison.OrdinalIgnoreCase))
                        normalChoice = child;
                    return true;
                }, IntPtr.Zero);

                if (play == IntPtr.Zero) { Thread.Sleep(250); continue; }
                if (normalChoice != IntPtr.Zero)
                    NativeMethods.SendMessage(normalChoice, NativeMethods.BmSetCheck, new IntPtr(1), IntPtr.Zero);
                NativeMethods.SendMessage(play, NativeMethods.BmClick, IntPtr.Zero, IntPtr.Zero);
                _log.Info("Launch", "F1 25 mode selector detected — selected normal (non-VR) mode and clicked Play.");
                return;
            }
            catch (Exception ex)
            {
                _log.Warn("Launch", "F1 25 mode-selector automation issue: " + ex.Message);
                return;
            }
            finally { selector?.Dispose(); }
        }
        _log.Warn("Launch", "F1 25 mode selector did not appear within 30s; continuing in case EA bypassed it.");
    }

    private static class NativeMethods
    {
        internal const uint BmSetCheck = 0x00F1;
        internal const uint BmClick = 0x00F5;
        internal delegate bool EnumWindowProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        internal static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        internal static string WindowText(IntPtr hwnd)
        {
            var buffer = new StringBuilder(256);
            _ = GetWindowText(hwnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }
    }

    internal readonly record struct ProcessCandidate(int Id, bool HasExited, DateTime StartTimeUtc);

    internal static int? SelectNewestLiveProcess(IReadOnlyList<ProcessCandidate> candidates) =>
        candidates.Where(p => !p.HasExited)
                  .OrderByDescending(p => p.StartTimeUtc)
                  .ThenByDescending(p => p.Id)
                  .Select(p => (int?)p.Id)
                  .FirstOrDefault();

    private int? StabilizeGameProcess(string processName, int initialPid, int seconds)
    {
        string baseName = Path.GetFileNameWithoutExtension(processName);
        int currentPid = initialPid;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(1, seconds));
        _log.Info("Launch", $"Stabilizing '{processName}' for {seconds}s to catch store bootstrap respawns (initial pid {initialPid}).");

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(500);
            GpuSuite.Core.RunHeartbeat.Ping();
            int? newest = SelectNewestLiveProcess(SnapshotProcesses(baseName));
            if (newest is int next && next != currentPid)
            {
                _log.Warn("Launch", $"'{processName}' bootstrap respawn detected — rebinding pid {currentPid} → {next} before capture.");
                currentPid = next;
            }
        }

        return SelectNewestLiveProcess(SnapshotProcesses(baseName));
    }

    private static IReadOnlyList<ProcessCandidate> SnapshotProcesses(string baseName)
    {
        var result = new List<ProcessCandidate>();
        Process[] processes;
        try { processes = Process.GetProcessesByName(baseName); }
        catch { return result; }

        foreach (var process in processes)
        {
            try { result.Add(new ProcessCandidate(process.Id, process.HasExited, process.StartTime.ToUniversalTime())); }
            catch { /* process exited or access denied between enumeration and inspection */ }
            finally { process.Dispose(); }
        }
        return result;
    }

    /// <summary>Poll for the named game process to appear (the launcher spawns it asynchronously).</summary>
    private int? WaitForGameProcess(string processName, int timeoutSeconds)
    {
        var baseName = Path.GetFileNameWithoutExtension(processName);
        if (string.IsNullOrEmpty(baseName)) return null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                // Dispose every Process handle this poll returns — otherwise a slow launcher (EA/Xbox can take
                // minutes) leaks one handle per second per game across a long unattended run.
                var procs = Process.GetProcessesByName(baseName);
                try { if (procs.Length > 0) return procs[0].Id; }
                finally { foreach (var pr in procs) pr.Dispose(); }
            }
            catch { }
            // Waiting on a (possibly slow, cold-starting) launcher — EA/Xbox/Epic can take well over a minute
            // to spawn the game process. This is real progress, so keep the crash/hang beacon fresh; otherwise
            // the per-game hang-watchdog would false-skip a healthy-but-slow launch before its process appears.
            GpuSuite.Core.RunHeartbeat.Ping();
            Thread.Sleep(1000);
        }
        return null;
    }

    private static string? BuildLaunchUri(string store, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return store.ToLowerInvariant() switch
        {
            "steam" => $"steam://rungameid/{id}",
            "epic" or "egs" => $"com.epicgames.launcher://apps/{id}?action=launch&silent=true",
            "uplay" or "ubisoft" => $"uplay://launch/{id}/0",
            "origin" or "ea" => $"origin2://game/launch?offerIds={id}",   // modern EA App; legacy origin://launchgame fails on EA Desktop
            "gog" => $"goggalaxy://openGameView/{id}",
            _ => null
        };
    }

    private LaunchResult Simulated(string why) => new() { Simulated = true, Detail = "simulated (" + why + ")" };

    /// <summary>A genuine, UNFAKED launch failure: neither launched nor simulated. The orchestrator records
    /// the run Invalid ("did not launch and was not simulated"), classifies the game as a launch failure, and
    /// skips it — no synthetic numbers are ever produced.</summary>
    private LaunchResult Failed(string why) => new() { Launched = false, Simulated = false, Detail = "launch failed (" + why + ")" };

    /// <summary>
    /// What a REAL-launch fallback does when the game won't start. Simulation is explicit opt-in
    /// (<c>simulateOnFailure=true</c>) for development smoke tests; the default fails honestly.
    /// </summary>
    private LaunchResult LaunchFallback(string why)
    {
        if (_simulateOnFailure) return Simulated(why);
        _log.Error("Launch", $"Launch fallback: {why} — NOT simulating (unattended mode: never fake numbers). Recording a launch failure.");
        return Failed(why);
    }

    /// <summary>
    /// (4-step) Apply a resolution before the scene, returning whether it succeeded. NOTE: per-game
    /// config-file rewriting is fragile for modern titles (a game may rewrite/truncate its own settings
    /// on launch). For config-file we restore from a template when the live file is empty, then require
    /// the resolution pattern to actually match — a no-op edit is reported as a FAILURE so the run is
    /// flagged invalid BEFORE launch instead of silently benchmarking the wrong resolution.
    /// </summary>
    public ResolutionApplyResult ApplyResolution(GameProfile game, Resolution res)
    {
        var m = game.ResolutionApply;
        switch (m.Method.ToLowerInvariant())
        {
            case "config-file" when !string.IsNullOrWhiteSpace(m.ConfigFilePath):
                return ApplyConfigFileResolution(m, res);
            case "registry" when !string.IsNullOrWhiteSpace(m.RegistryKey):
                return ApplyRegistryResolution(m, res);
            case "display":
                return ApplyDisplayResolution(res);
            case "launch-arg":
                _log.Info("Resolution", $"Resolution {res} applied via launch arguments.");
                return ResolutionApplyResult.Pass(true, "launch-arg");
            case "bot":
                _log.Info("Resolution", $"Resolution {res} will be set by the in-game bot.");
                return ResolutionApplyResult.Pass(false, "bot (set in-game)");
            case "none":
            default:
                // Explicit development simulation and operator-prepared attach runs carry their own assertion.
                if (_simulateLaunch)
                    return ResolutionApplyResult.Pass(false, "none (explicit simulated run)");
                if (_attachToRunning)
                    return ResolutionApplyResult.Pass(false, "none (operator-prepared attach resolution)");

                // A no-op method is honest only for an explicitly pinned single resolution. Historically this
                // branch admitted every requested label and produced valid 1080p/1440p Ratchet runs whose own
                // fingerprint said 3840x2160. Fail before launch instead of manufacturing another mislabeled cell.
                string? fixedRes = string.IsNullOrWhiteSpace(m.VerifiedFixedResolution)
                    ? (game.SupportedResolutions.Count == 1 ? game.SupportedResolutions[0] : null)
                    : m.VerifiedFixedResolution;
                if (fixedRes is not null && fixedRes.Equals(res.Name, StringComparison.OrdinalIgnoreCase))
                {
                    _log.Info("Resolution", $"Using profile-pinned fixed resolution {fixedRes}; no per-run apply needed.");
                    return ResolutionApplyResult.Pass(false, $"fixed verified resolution {fixedRes}");
                }
                return ResolutionApplyResult.Fail($"profile has no resolution-apply method for requested {res.Name}; " +
                    "declare verifiedFixedResolution for a genuinely fixed mode or implement apply+verify");
        }
    }

    /// <summary>
    /// Resolution-apply method "display": set the DESKTOP mode to the target resolution at the safe cap
    /// refresh (the 60 Hz Elgato limit), which a borderless / desktop-resolution game (Forza,
    /// Cyberpunk-borderless, MSFS) then inherits — a real per-resolution matrix with no per-game config.
    /// If the resolution has no settable mode at the cap refresh the set FAILS (run flagged Invalid);
    /// <see cref="DisplayController"/> never pushes an unvalidated or above-cap mode, so the panel can't
    /// be blanked by this path.
    /// </summary>
    private ResolutionApplyResult ApplyDisplayResolution(Resolution res)
    {
        var before = DisplayController.GetCurrent();
        // Snapshot bench-native ONCE (first display-method switch too, not just SwitchDesktopMode) so
        // RestoreDesktopModeIfSwitched can put the desktop back after the game. Live 2026-07-10: a DOOM
        // 1080p roster (method=display) completed cleanly and LEFT the desktop at 1920x1080 — the restore
        // only covered config-file+SwitchDesktopMode games; on the historical 4K-native desktop this could
        // never show (4K games left it at 4K).
        _benchNativeMode ??= before;
        var (ok, detail) = DisplayController.TrySetResolutionAtCap(res.Width, res.Height, _maxRefreshHz);
        if (!ok && _benchNativeMode is { } nat && (before is not { } cur || cur.Width != nat.Width || cur.Height != nat.Height))
        {
            // Same via-native retry as the SwitchDesktopMode path: the settable-mode list depends on the
            // CURRENT mode (1080p desktop → no 2560x1440@60 enumerates, CDS_TEST=-2), while the target sets
            // fine from bench-native.
            _log.Warn("Resolution", $"Display-mode set to {res.Width}x{res.Height}@{_maxRefreshHz}Hz failed from {before?.ToString() ?? "?"} ({detail}) — retrying VIA bench-native {nat}.");
            var (rok, _) = DisplayController.TrySetResolutionAtCap(nat.Width, nat.Height, _maxRefreshHz);
            if (rok)
            {
                Thread.Sleep(2000);
                (ok, detail) = DisplayController.TrySetResolutionAtCap(res.Width, res.Height, _maxRefreshHz);
            }
        }
        if (!ok)
            return ResolutionApplyResult.Fail($"display-mode set to {res.Width}x{res.Height}@{_maxRefreshHz}Hz failed: {detail} (current {before?.ToString() ?? "unknown"})");
        _log.Info("Resolution", $"Set desktop to {res.Width}x{res.Height}@{_maxRefreshHz}Hz for borderless game (was {before?.ToString() ?? "unknown"}).");
        return ResolutionApplyResult.Pass(true, detail);
    }

    private ResolutionApplyResult ApplyConfigFileResolution(ResolutionApplyMethod m, Resolution res)
    {
        try
        {
            // The profile stores the path with env vars (e.g. %LOCALAPPDATA%) — expand it.
            var path = Environment.ExpandEnvironmentVariables(m.ConfigFilePath!);

            // Self-heal: if the live settings file is missing/empty, restore it from the template
            // (some games truncate their own settings on launch and only rewrite on clean exit).
            RestoreFromTemplateIfEmpty(m, path);

            if (!File.Exists(path))
                return ResolutionApplyResult.Fail($"config file not found: {path} — resolution NOT applied");
            if (new FileInfo(path).Length == 0)
                return ResolutionApplyResult.Fail($"config file is empty (0 bytes): {path} — launch the game once and Apply a video setting to regenerate it, or set resolutionApply.templateFilePath");

            var text = File.ReadAllText(path);
            int matches = 0;
            foreach (var edit in m.Edits)
            {
                var repl = edit.Replacement
                    .Replace("{WIDTH}", res.Width.ToString())
                    .Replace("{HEIGHT}", res.Height.ToString())
                    .Replace("{NAME}", res.Name);
                int n = Regex.Matches(text, edit.Pattern).Count;
                matches += n;
                if (n > 0) text = Regex.Replace(text, edit.Pattern, repl);
            }
            if (m.Edits.Count > 0 && matches == 0)
                return ResolutionApplyResult.Fail($"no resolution pattern matched in {path} (settings format changed?) — resolution NOT applied");

            File.WriteAllText(path, text);
            var readBack = File.ReadAllText(path);
            if (!string.Equals(readBack, text, StringComparison.Ordinal))
                return ResolutionApplyResult.Fail($"config file read-back mismatch after writing {path}");
            _log.Info("Resolution", $"Applied {res} via config file ({matches} edit match(es)): {path}");

            // BORDERLESS desktop-mode switch: a borderless window fills the primary screen, so its render is
            // the DESKTOP resolution regardless of the config's window size. Switch the desktop to the target
            // so the fill = the target = a genuine sub-native render (see ResolutionApplyMethod.SwitchDesktopMode).
            if (m.SwitchDesktopMode && !SwitchDesktopMode(res, out var switchError))
                return ResolutionApplyResult.Fail($"config edits applied but {switchError}");

            return ResolutionApplyResult.Pass(matches > 0, $"{matches} match(es)");
        }
        catch (Exception ex) { return ResolutionApplyResult.Fail("config edit failed: " + ex.Message); }
    }

    private ResolutionApplyResult ApplyRegistryResolution(ResolutionApplyMethod m, Resolution res)
    {
        try
        {
            if (m.RegistryEdits.Count == 0)
                return ResolutionApplyResult.Fail("no registryEdits configured");
            var (root, sub) = SplitRegistryPath(m.RegistryKey!);
            using var key = root.OpenSubKey(sub, writable: true);
            if (key is null)
                return ResolutionApplyResult.Fail($"registry key not found: {m.RegistryKey}");

            foreach (var edit in m.RegistryEdits)
            {
                string mapped = edit.ValueMap.TryGetValue(res.Name, out var configured)
                    ? configured
                    : res.Name;
                object boxed;
                RegistryValueKind kind;
                switch ((edit.Kind ?? "dword").ToLowerInvariant())
                {
                    case "string": boxed = mapped; kind = RegistryValueKind.String; break;
                    case "qword": boxed = long.Parse(mapped, System.Globalization.CultureInfo.InvariantCulture); kind = RegistryValueKind.QWord; break;
                    case "dword": boxed = unchecked((int)uint.Parse(mapped, System.Globalization.CultureInfo.InvariantCulture)); kind = RegistryValueKind.DWord; break;
                    default: return ResolutionApplyResult.Fail($"unsupported registry kind '{edit.Kind}' for '{edit.ValueName}'");
                }
                key.SetValue(edit.ValueName, boxed, kind);
                var back = key.GetValue(edit.ValueName);
                string backText = back switch
                {
                    int i => unchecked((uint)i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _ => back?.ToString() ?? ""
                };
                if (!string.Equals(backText, mapped, StringComparison.Ordinal))
                    return ResolutionApplyResult.Fail($"registry read-back mismatch on '{edit.ValueName}': wrote '{mapped}', read '{backText}'");
            }

            _log.Info("Resolution", $"Applied {res} via registry ({m.RegistryEdits.Count} verified value(s)): {m.RegistryKey}");
            if (m.SwitchDesktopMode && !SwitchDesktopMode(res, out var switchError))
                return ResolutionApplyResult.Fail($"registry edits applied but {switchError}");
            return ResolutionApplyResult.Pass(true, $"{m.RegistryEdits.Count} registry value(s)");
        }
        catch (Exception ex) { return ResolutionApplyResult.Fail("registry resolution apply failed: " + ex.Message); }
    }

    private bool SwitchDesktopMode(Resolution res, out string error)
    {
        _benchNativeMode ??= DisplayController.GetCurrent();
        var (ok, detail) = DisplayController.TrySetResolutionAtCap(res.Width, res.Height, _maxRefreshHz);
        if (!ok && _benchNativeMode is { } native &&
            (DisplayController.GetCurrent() is not { } current || current.Width != native.Width || current.Height != native.Height))
        {
            _log.Warn("Resolution", $"Desktop-mode switch to {res.Width}x{res.Height}@{_maxRefreshHz}Hz failed ({detail}) — retrying via bench-native {native}.");
            var (restored, restoreDetail) = DisplayController.TrySetResolutionAtCap(native.Width, native.Height, _maxRefreshHz);
            if (restored)
            {
                Thread.Sleep(2000);
                (ok, detail) = DisplayController.TrySetResolutionAtCap(res.Width, res.Height, _maxRefreshHz);
            }
            else detail += $"; via-native restore failed: {restoreDetail}";
        }
        if (!ok)
        {
            error = $"desktop-mode switch to {res.Width}x{res.Height}@{_maxRefreshHz}Hz failed ({detail}) — borderless render cannot be verified";
            return false;
        }
        _log.Info("Resolution", $"Switched desktop to {res.Width}x{res.Height}@{_maxRefreshHz}Hz for borderless {res.Name} render (bench-native {_benchNativeMode?.ToString() ?? "?"} restored after the game).");
        error = "";
        return true;
    }

    /// <summary>Restore the desktop to the bench-native mode captured before a <see cref="ResolutionApplyMethod.SwitchDesktopMode"/>
    /// switch. Call after each game that switched — so the desktop is sub-native ONLY during that game's runs and a
    /// following config-file/borderless game can't inherit a leftover sub-native desktop. No-op if nothing switched.</summary>
    public void RestoreDesktopModeIfSwitched(GameProfile game)
    {
        // Covers BOTH desktop-switching apply methods: config-file+SwitchDesktopMode AND method=display
        // (which sets the desktop directly — DOOM). Games that never switch the desktop no-op here.
        if (game.ResolutionApply is not { Method: not null } m) return;
        bool switches = ((m.Method.Equals("config-file", StringComparison.OrdinalIgnoreCase) ||
                          m.Method.Equals("registry", StringComparison.OrdinalIgnoreCase)) && m.SwitchDesktopMode)
                        || m.Method.Equals("display", StringComparison.OrdinalIgnoreCase);
        if (!switches) return;
        if (_benchNativeMode is not { } native) return;

        // Let the just-killed game finish tearing down its swap chain before restoring. Live Forza validation
        // (2026-07-10) caught the old immediate restore reporting 4K success, then the exiting game/driver
        // applying its former 1080p mode a few seconds later; the next CLI invocation consequently mistook
        // 1080p for bench-native. Set after that delayed transition, then verify after another settle interval.
        Thread.Sleep(4000);
        var before = DisplayController.GetCurrent();
        bool alreadyNative = before is { } b && b.Width == native.Width && b.Height == native.Height;
        var (ok, detail) = alreadyNative
            ? (true, "already native after game-exit settle")
            : DisplayController.TrySetResolutionAtCap(native.Width, native.Height, _maxRefreshHz);

        Thread.Sleep(2000);
        var verified = DisplayController.GetCurrent();
        bool atNative = verified is { } v && v.Width == native.Width && v.Height == native.Height;
        if (ok && !atNative)
        {
            _log.Warn("Resolution", $"Desktop drifted to {verified?.ToString() ?? "unknown"} after the first restore — retrying bench-native {native} once.");
            (ok, detail) = DisplayController.TrySetResolutionAtCap(native.Width, native.Height, _maxRefreshHz);
            Thread.Sleep(1500);
            verified = DisplayController.GetCurrent();
            atNative = ok && verified is { } retry && retry.Width == native.Width && retry.Height == native.Height;
        }

        if (ok && atNative)
            _log.Info("Resolution", $"Restored and verified desktop at bench-native {native} after {game.Id} (was {before?.ToString() ?? "?"}).");
        else
            _log.Warn("Resolution", $"Could NOT restore/verify bench-native {native} after {game.Id}: {detail} — desktop is {verified?.ToString() ?? "unknown"}.");
    }

    private void RestoreFromTemplateIfEmpty(ResolutionApplyMethod m, string livePath)
    {
        if (string.IsNullOrWhiteSpace(m.TemplateFilePath)) return;
        bool liveUsable = File.Exists(livePath) && new FileInfo(livePath).Length > 0;
        if (liveUsable) return;
        var tpl = Environment.ExpandEnvironmentVariables(m.TemplateFilePath);
        if (!File.Exists(tpl) || new FileInfo(tpl).Length == 0)
        {
            _log.Warn("Resolution", $"Settings file empty and template '{tpl}' is missing/empty — cannot self-heal.");
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
            File.Copy(tpl, livePath, overwrite: true);
            _log.Info("Resolution", $"Restored empty settings file from template: {tpl} → {livePath}");
        }
        catch (Exception ex) { _log.Warn("Resolution", $"Template restore failed: {ex.Message}"); }
    }

    /// <summary>The raw token an edit writes for a friendly value: per-edit map, else setting-level map, else identity.</summary>
    private static string MapValue(GameSetting s, ConfigEdit edit, string value) =>
        edit.ValueMap.TryGetValue(value, out var ev) ? ev
        : s.Apply.ValueMap.TryGetValue(value, out var sv) ? sv
        : value;

    /// <summary>Effective value for a setting under a variant: the variant override, else the setting Default.</summary>
    public static string EffectiveValue(GameSetting s, GameVariant? variant) =>
        variant is not null && variant.Settings.TryGetValue(s.Key, out var v) && !string.IsNullOrEmpty(v) ? v : s.Default;

    /// <summary>
    /// Apply a graphics variant's settings before launch. For each config-file knob: resolve the effective
    /// value (variant override else default), map it to the file's raw token, regex-edit the settings file
    /// with the {VALUE} token, then READ THE FILE BACK and require the value to be present — an edit that
    /// matched nothing, or whose value didn't land, fails the variant so the run is flagged Invalid rather
    /// than silently benchmarking the wrong model (the exact failure that would make "DLSS on" and "DLSS off"
    /// collapse to the same data). Menu knobs are recorded as pending for the post-launch vision-nav engine.
    /// A null/implicit-default variant with no profile settings is a no-op pass.
    /// </summary>
    public VariantApplyResult ApplyVariant(GameProfile game, GameVariant? variant, Resolution? res = null)
    {
        if (game.Settings.Count == 0)
            return VariantApplyResult.Pass(0, variant is null ? "no settings declared" : $"variant '{variant.Id}' (no settings declared)");

        var result = new VariantApplyResult();
        int applied = 0;
        foreach (var s in game.Settings)
        {
            var value = EffectiveValue(s, variant);
            if (string.IsNullOrWhiteSpace(value)) continue;       // nothing selected and no default — leave game as-is
            result.EffectiveSummary.Add($"{s.Key}={value}");

            switch ((s.Apply.Method ?? "none").ToLowerInvariant())
            {
                case "config-file" when !string.IsNullOrWhiteSpace(s.Apply.ConfigFilePath):
                    var (ok, detail) = ApplyConfigFileSetting(s, value, res);
                    if (ok) applied++;
                    else result.Issues.Add($"{s.Key} ('{value}'): {detail}");
                    break;
                case "registry":
                    var (rok, rdetail) = ApplyRegistrySetting(s, value);
                    if (rok) applied++;
                    else result.Issues.Add($"{s.Key} ('{value}'): {rdetail}");
                    break;
                case "menu":
                    result.PendingMenu.Add($"{s.Key}={value}{(s.Apply.MenuPath is { } mp ? $" [{mp}]" : "")}");
                    break;
                default:
                    // "none" / unconfigured — informational only.
                    break;
            }
        }

        result.Ok = result.Issues.Count == 0;
        result.AppliedCount = applied;
        var summary = result.EffectiveSummary.Count > 0 ? string.Join(", ", result.EffectiveSummary) : "(defaults)";
        var pend = result.PendingMenu.Count > 0 ? $"; menu-pending: {string.Join(", ", result.PendingMenu)}" : "";
        result.Detail = $"[{summary}]{pend}";
        var label = variant is null ? "default" : variant.Id;
        if (result.Ok)
            _log.Info("Settings", $"Variant '{label}' applied [{summary}] ({applied} config edit(s){pend}).");
        else
            _log.Error("Settings", $"Variant '{label}' apply FAILED [{summary}]: {string.Join("; ", result.Issues)}");
        return result;
    }

    /// <summary>
    /// Write one registry-backed setting (Nixxes ports keep settings in HKCU) and verify by read-back:
    /// every value written is read again and must round-trip exactly, or the variant fails and the run is
    /// Invalid — the same anti-collapse guarantee as config-file. The key must already exist (the game
    /// creates it on first run); apply never creates keys so a wrong path can't silently write junk.
    /// </summary>
    private (bool ok, string detail) ApplyRegistrySetting(GameSetting s, string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(s.Apply.RegistryKey)) return (false, "no registryKey configured");
            if (s.Apply.RegistryEdits.Count == 0) return (false, "no registryEdits configured");
            var (root, sub) = SplitRegistryPath(s.Apply.RegistryKey);
            using var key = root.OpenSubKey(sub, writable: true);
            if (key is null) return (false, $"registry key not found: {s.Apply.RegistryKey} (game never ran on this machine?)");

            foreach (var e in s.Apply.RegistryEdits)
            {
                var mapped = e.ValueMap.TryGetValue(value, out var ev) ? ev
                           : s.Apply.ValueMap.TryGetValue(value, out var sv) ? sv
                           : value;
                object boxed;
                Microsoft.Win32.RegistryValueKind kind;
                switch ((e.Kind ?? "dword").ToLowerInvariant())
                {
                    case "string":
                        boxed = mapped; kind = Microsoft.Win32.RegistryValueKind.String; break;
                    case "qword":
                        boxed = long.Parse(mapped, System.Globalization.CultureInfo.InvariantCulture);
                        kind = Microsoft.Win32.RegistryValueKind.QWord; break;
                    case "dword":
                        boxed = unchecked((int)uint.Parse(mapped, System.Globalization.CultureInfo.InvariantCulture));
                        kind = Microsoft.Win32.RegistryValueKind.DWord; break;
                    default:
                        return (false, $"unsupported registry kind '{e.Kind}' for '{e.ValueName}' (dword/qword/string only)");
                }
                key.SetValue(e.ValueName, boxed, kind);

                var back = key.GetValue(e.ValueName);
                string backStr = back switch
                {
                    int i => unchecked((uint)i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _ => back?.ToString() ?? ""
                };
                if (!string.Equals(backStr, mapped, StringComparison.Ordinal))
                    return (false, $"read-back mismatch on '{e.ValueName}': wrote '{mapped}', read '{backStr}'");
            }
            return (true, "ok");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Resolve "HKCU\..."/"HKEY_CURRENT_USER\..." (or HKLM) into a hive root + subkey path.</summary>
    private static (Microsoft.Win32.RegistryKey root, string sub) SplitRegistryPath(string path)
    {
        int i = path.IndexOf('\\');
        if (i <= 0) throw new InvalidOperationException($"registry path has no subkey: '{path}'");
        var hive = path[..i].ToUpperInvariant();
        var sub = path[(i + 1)..];
        return hive switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => (Microsoft.Win32.Registry.CurrentUser, sub),
            "HKLM" or "HKEY_LOCAL_MACHINE" => (Microsoft.Win32.Registry.LocalMachine, sub),
            _ => throw new InvalidOperationException($"unsupported registry hive '{hive}' (HKCU/HKLM only)")
        };
    }

    /// <summary>
    /// Edit one config-file setting to the friendly <paramref name="value"/> (mapped per-edit to the raw
    /// token each field stores) and verify it landed. Verification is per-edit and idempotence-based:
    /// after writing, re-running the same edit must produce NO further change — i.e. every targeted field
    /// already holds the requested token. This catches both a pattern that matched nothing and one that
    /// matched the wrong field, so two variants can never silently collapse to identical settings.
    /// Replacement tokens: {VALUE} = the mapped value; {RESW}/{RESH} = the run's target output resolution;
    /// {VALUExRESW}/{VALUExRESH} = round(mapped-value × width/height) for games that store an upscaler TIER
    /// as a literal render resolution (Alan Wake 2's renderer.ini) — the tier's valueMap maps the friendly
    /// option to a scale factor (Quality→0.6667, Performance→0.5, Native→1.0).
    /// </summary>
    private (bool ok, string detail) ApplyConfigFileSetting(GameSetting s, string value, Resolution? res)
    {
        try
        {
            var path = Environment.ExpandEnvironmentVariables(s.Apply.ConfigFilePath!);
            if (!File.Exists(path)) return (false, $"config file not found: {path}");
            if (new FileInfo(path).Length == 0) return (false, $"config file empty: {path}");
            if (s.Apply.Edits.Count == 0) return (false, "no edits configured");

            string Expand(ConfigEdit edit)
            {
                var mapped = MapValue(s, edit, value);
                var r = edit.Replacement.Replace("{VALUE}", mapped);
                if (!r.Contains("{RES", StringComparison.Ordinal) && !r.Contains("{VALUEx", StringComparison.Ordinal))
                    return r;
                if (res is null)
                    throw new InvalidOperationException("replacement uses {RESW}/{RESH} tokens but no target resolution was provided (apply is per-run)");
                r = r.Replace("{RESW}", res.Width.ToString(System.Globalization.CultureInfo.InvariantCulture))
                     .Replace("{RESH}", res.Height.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (r.Contains("{VALUExRESW}", StringComparison.Ordinal) || r.Contains("{VALUExRESH}", StringComparison.Ordinal))
                {
                    if (!double.TryParse(mapped, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) || f <= 0)
                        throw new InvalidOperationException($"mapped value '{mapped}' is not a positive scale factor for {{VALUEx…}} tokens");
                    // Render targets must be EVEN — AW2 live-proven 2026-07-02: an odd render width
                    // (1707 = round(2560×0.6667)) black-screened the 3D scene (HUD still drew).
                    static long Even(double v) => (long)Math.Round(v) & ~1L;
                    r = r.Replace("{VALUExRESW}", Even(res.Width * f).ToString(System.Globalization.CultureInfo.InvariantCulture))
                         .Replace("{VALUExRESH}", Even(res.Height * f).ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                return r;
            }

            var text = File.ReadAllText(path);
            int matches = 0;
            foreach (var edit in s.Apply.Edits)
            {
                var repl = Expand(edit);
                int n = Regex.Matches(text, edit.Pattern).Count;
                matches += n;
                if (n > 0) text = Regex.Replace(text, edit.Pattern, repl);
            }
            if (matches == 0) return (false, "no pattern matched (settings format changed?) — NOT applied");

            File.WriteAllText(path, text);

            // Verify: each edit must now be idempotent (re-applying changes nothing ⇒ the token landed).
            var after = File.ReadAllText(path);
            foreach (var edit in s.Apply.Edits)
            {
                var repl = Expand(edit);
                if (Regex.Matches(after, edit.Pattern).Count == 0) continue;  // field absent for this build — tolerated
                if (Regex.Replace(after, edit.Pattern, repl) != after)
                    return (false, $"verify failed — value '{value}' did not land for pattern /{edit.Pattern}/");
            }
            // Optional extra guard.
            if (s.Apply.VerifyPattern is { Length: > 0 } vp)
            {
                var v = vp.Replace("{VALUE}", Regex.Escape(MapValue(s, s.Apply.Edits[0], value)));
                if (!Regex.IsMatch(after, v)) return (false, $"verify pattern /{v}/ not present after write");
            }
            return (true, $"{matches} edit(s), verified");
        }
        catch (Exception ex) { return (false, "edit error: " + ex.Message); }
    }

    /// <summary>
    /// ONE-GAME-AT-A-TIME guard. Kills EVERY known game process — the current game's own stale instances AND any
    /// FOREIGN game still running from a prior run/session — so this launch starts on a bench with exactly one game.
    /// The foreign-game set is built once from ALL profiles' captureProcessName (not just the games selected this
    /// run), because the orphan that strands a borderless game on its launcher is usually a DIFFERENT game than the
    /// one being measured. Best-effort; never throws. A short settle lets the OS release foreground + GPU before the
    /// fresh launch. (KillStaleGame still runs afterward for EA/Uplay/Epic/Xbox to do the AntiCheat/lock teardown.)
    /// </summary>
    private void SoloGameSweep(GameProfile current)
    {
        try
        {
            var names = AllGameProcessBaseNames();
            // Always include THIS game's process (covers Steam, which otherwise has no pre-launch kill at all).
            var curBase = Path.GetFileNameWithoutExtension(current.CaptureProcessName);
            if (!string.IsNullOrEmpty(curBase)) names.Add(curBase);

            int killed = 0;
            var killedNames = new List<string>();
            foreach (var baseName in names)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(baseName); } catch { continue; }
                foreach (var p in procs)
                {
                    try { p.Kill(true); killed++; if (!killedNames.Contains(baseName)) killedNames.Add(baseName); }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            if (killed > 0)
            {
                _log.Info("Launch", $"Solo-game guard: killed {killed} running game process(es) [{string.Join(", ", killedNames)}] before launching '{current.Name}' — only ONE game runs at a time (no foreground-steal, no GPU contention, no input leak).");
                System.Threading.Thread.Sleep(2000); // let Windows release foreground + the GPU before the fresh launch
            }
        }
        catch (Exception ex) { _log.Warn("Launch", "Solo-game sweep issue: " + ex.Message); }
    }

    /// <summary>The set of process base-names for ALL roster games, loaded once from the profiles dir. Used by the
    /// one-game-at-a-time guard to find foreign orphans. Returns a fresh copy so callers can add the current game.</summary>
    private HashSet<string> AllGameProcessBaseNames()
    {
        if (_allGameProcessBaseNames is null)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!string.IsNullOrWhiteSpace(_profilesDir) && Directory.Exists(_profilesDir))
                    foreach (var prof in new GpuSuite.Engine.Profiles.ProfileManager(_profilesDir).LoadAll())
                    {
                        var bn = Path.GetFileNameWithoutExtension(prof.CaptureProcessName);
                        if (!string.IsNullOrEmpty(bn)) set.Add(bn);
                    }
            }
            catch (Exception ex) { _log.Warn("Launch", "Solo-game guard could not enumerate profiles: " + ex.Message); }
            _allGameProcessBaseNames = set;
        }
        return new HashSet<string>(_allGameProcessBaseNames, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Kill any already-running instance of the game's capture process plus the EA AntiCheat services,
    /// so an EA/Origin launch (whose protocol no-ops over a running title) starts cold from the title instead of
    /// silently reusing a stale, mid-state instance left by a crashed/aborted prior session. Best-effort; never
    /// throws. A short settle follows a kill so EA Desktop releases its "already running" lock before relaunch.</summary>
    private void KillStaleGame(GameProfile game, string why)
    {
        try
        {
            var store = (game.Launch.Store ?? "").Trim();
            bool isEa = store.Equals("EA", StringComparison.OrdinalIgnoreCase) || store.Equals("Origin", StringComparison.OrdinalIgnoreCase);
            var baseName = Path.GetFileNameWithoutExtension(game.CaptureProcessName);
            int killed = 0;
            if (!string.IsNullOrEmpty(baseName))
                foreach (var stray in Process.GetProcessesByName(baseName))
                    try { stray.Kill(true); killed++; } catch { }
            if (killed > 0)
            {
                // EA ONLY: clear the AntiCheat services that hold EA Desktop's "already running" lock the origin2
                // protocol no-ops on. Do this ONLY when we actually killed a stale instance; on a clean first launch
                // leave AntiCheat ALONE (killing it mid-init every launch can WEDGE EA's state — observed 2026-06-25).
                // Uplay/Ubisoft titles have no such services (Denuvo only), so skip the teardown + don't claim it.
                if (isEa)
                    foreach (var acName in new[] { "EAAntiCheat.GameService", "EAAntiCheat.GameServiceLauncher" })
                        foreach (var ac in Process.GetProcessesByName(acName))
                            try { ac.Kill(true); } catch { }
                _log.Info("Launch", $"Pre-launch: killed {killed} stale '{baseName}' instance(s){(isEa ? " + EA AntiCheat" : "")} ({why}).");
                System.Threading.Thread.Sleep(3000); // let the launcher notice the exit + release any "already running" lock
            }
        }
        catch (Exception ex) { _log.Warn("Launch", "Pre-launch stale-instance cleanup issue: " + ex.Message); }
    }

    public void Cleanup(GameProfile game, LaunchResult launch)
    {
        // In attach (or Manual store) mode the operator owns the game process — never kill it. This
        // also keeps the prepped session alive across repeats / multiple resolutions.
        if (_attachToRunning || string.Equals((game.Launch.Store ?? "").Trim(), "Manual", StringComparison.OrdinalIgnoreCase))
        {
            _log.Info("Cleanup", "Attach/Manual mode — leaving the game running (operator owns it).");
            return;
        }
        try
        {
            if (launch.Process is { HasExited: false } p) { p.Kill(true); _log.Info("Cleanup", $"Terminated pid {p.Id}."); }
            var baseName = Path.GetFileNameWithoutExtension(game.CaptureProcessName);
            if (!string.IsNullOrEmpty(baseName))
                foreach (var stray in Process.GetProcessesByName(baseName))
                    try { stray.Kill(true); _log.Trace("Cleanup", $"Killed {baseName} pid {stray.Id}."); } catch { }

            // EA games run behind EA AntiCheat. If the game is killed (e.g. between repeats) while the AntiCheat
            // services are mid-init, EA Desktop is left believing the title is "already running" and REFUSES the
            // next origin2 launch (the launch silently no-ops -> the next repeat's process never appears). Tear the
            // AntiCheat services down so each repeat starts from a clean state. (Discovered live 2026-06-24: a
            // stale AntiCheat lock blocked every relaunch until these were killed.)
            var store = (game.Launch.Store ?? "").Trim();
            if (store.Equals("EA", StringComparison.OrdinalIgnoreCase) || store.Equals("Origin", StringComparison.OrdinalIgnoreCase))
                foreach (var acName in new[] { "EAAntiCheat.GameService", "EAAntiCheat.GameServiceLauncher" })
                    foreach (var ac in Process.GetProcessesByName(acName))
                        try { ac.Kill(true); _log.Trace("Cleanup", $"Killed {acName} pid {ac.Id} (clear EA 'already running' lock)."); } catch { }

            // Dismiss any crash dialog the game left up (e.g. Forza's "Video Card Crash FHC00" Exit box, WER)
            // so it doesn't pollute the screen / steal foreground for the rest of the run. Launcher windows are
            // left alone here — those are closed once at suite end (ScreenTidy.CloseLauncherWindows).
            ScreenTidy.DismissCrashArtifacts(_log);
            ScreenTidy.ScheduleDelayedCrashReporterSweep(_log);
        }
        catch (Exception ex) { _log.Warn("Cleanup", "Cleanup issue: " + ex.Message); }
    }
}
