// Extracted from Program.cs (CLI god-file split): same partial Program class, grouped by command area.
// Behavior-preserving move only — see HANDOVER.md before editing bench-path logic.
using GpuSuite.Core.Config;
using GpuSuite.Core;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Io;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Aggregation;
using GpuSuite.Engine.Discovery;
using GpuSuite.Engine.Display;
using GpuSuite.Engine.Orchestration;
using GpuSuite.Engine.Profiles;
using GpuSuite.Engine.Scenes;
using GpuSuite.Engine.Validation;
using GpuSuite.Load;
using GpuSuite.Measurement;
using GpuSuite.Reporting;

namespace GpuSuite.Cli;

internal static partial class Program
{
    /// <summary>
    /// `display` verb — read or set the desktop display mode (resolution + refresh). The bench feeds
    /// THROUGH a 60 Hz-capped Elgato, so this honors settings.maxRefreshHz: any --set/--test above the
    /// cap is REFUSED (the panel would blank). Used by the operator and by the "display" resolution-apply
    /// method (borderless games inherit the desktop mode). Examples:
    ///   gpusuite display                       — print the current mode + the cap
    ///   gpusuite display --test 3840x2160@60   — validate a mode (CDS_TEST) without changing anything
    ///   gpusuite display --set 1920x1080@60    — set the desktop mode (≤ cap only)
    /// </summary>
    private static int Display(SuiteConfig cfg, ArgMap a)
    {
        var device = a.Get("--device");
        var cur = DisplayController.GetCurrent(device);
        Console.WriteLine($"Current display mode : {cur?.ToString() ?? "(unknown — driver did not report; transient right after a signal re-sync)"}");
        Console.WriteLine($"Refresh cap          : {cfg.MaxRefreshHz} Hz (settings.maxRefreshHz — Elgato sync limit)");

        var setSpec = a.Get("--set");
        var testSpec = a.Get("--test");
        var spec = setSpec ?? testSpec;
        if (spec is null)
        {
            Console.WriteLine("\nUsage: gpusuite display [--set WxH@Hz | --test WxH@Hz] [--device \"\\\\.\\DISPLAY1\"]");
            return 0;
        }
        if (!TryParseDisplayMode(spec, out int w, out int h, out int hz))
        {
            Console.Error.WriteLine($"Could not parse mode '{spec}'. Expected e.g. 3840x2160@60 (or 1920x1080, which assumes @{cfg.MaxRefreshHz}).");
            return 1;
        }
        if (hz > cfg.MaxRefreshHz)
        {
            Console.Error.WriteLine($"REFUSED: {hz}Hz exceeds the {cfg.MaxRefreshHz}Hz cap (Elgato sync limit) — the panel would blank. Not changing the display.");
            return 1;
        }
        if (testSpec is not null && setSpec is null)
        {
            bool can = DisplayController.CanSet(w, h, hz, cfg.MaxRefreshHz, device);
            Console.WriteLine($"TEST {w}x{h}@{hz}Hz : {(can ? "SETTABLE" : "NOT settable on this display")}");
            return can ? 0 : 1;
        }
        var (ok, detail) = DisplayController.TrySet(w, h, hz, cfg.MaxRefreshHz, device);
        Console.WriteLine($"SET {w}x{h}@{hz}Hz : {(ok ? "OK" : "FAILED")} — {detail}");
        return ok ? 0 : 1;
    }

    /// <summary>Parse "WxH@Hz" (or "WxH", defaulting Hz to 60) into width/height/refresh.</summary>
    private static bool TryParseDisplayMode(string s, out int w, out int h, out int hz)
    {
        w = 0; h = 0; hz = 60;
        var at = s.Split('@');
        if (at.Length == 2 && !int.TryParse(at[1].Trim(), out hz)) return false;
        if (at.Length > 2) return false;
        var wh = at[0].ToLowerInvariant().Split('x');
        if (wh.Length != 2) return false;
        return int.TryParse(wh[0].Trim(), out w) && int.TryParse(wh[1].Trim(), out h) && w > 0 && h > 0;
    }

    /// <summary>
    /// Operator diagnostic for the ViGEm virtual gamepad path (needed to drive RE-Engine games like
    /// Resident Evil 4, which ignore injected keyboard/mouse). Reports whether the ViGEmBus driver is
    /// installed, then actually creates a virtual Xbox 360 pad and exercises it (stick + button). On
    /// failure it prints the one-time install steps. Detection only — it never installs the driver.
    /// </summary>
    private static int VigemCheck()
    {
        Console.WriteLine("ViGEm virtual gamepad check (drives RE-Engine titles that filter injected keyboard/mouse, e.g. RE4):\n");

        string sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "ViGEmBus.sys");
        Console.WriteLine($"  ViGEmBus.sys present : {(File.Exists(sys) ? "yes" : "NO")}  ({sys})");

        using var log = new RunLogger(null, echoToConsole: false);
        using var pad = Engine.Automation.Gamepad.Create(log);
        if (pad.Connected)
        {
            // Exercise it so the operator can confirm a "Xbox 360 Controller" appears (e.g. in joy.cpl).
            pad.LeftStick(0, 1); System.Threading.Thread.Sleep(150);
            pad.LeftStick(0, 0); pad.Button("A", true); System.Threading.Thread.Sleep(80); pad.Button("A", false);
            pad.Neutral();
            Console.WriteLine("  Virtual pad created  : yes — a virtual Xbox 360 controller connected and accepted stick + button input.");
            Console.WriteLine("\nRESULT: PASS — ViGEm is ready. Gamepad bots (e.g. re4_pad_scene) can drive RE-Engine games.");
            return 0;
        }

        Console.WriteLine($"  Virtual pad created  : NO — {pad.Unavailable}");
        Console.WriteLine(@"
RESULT: FAIL — ViGEmBus is not installed, so a virtual gamepad cannot be created.
  Install it (one-time, requires Administrator — this tool will NOT self-elevate):
    1. Download the official signed installer from the Nefarius releases page:
         https://github.com/nefarius/ViGEmBus/releases/latest   (ViGEmBus_<version>_x64_arm64.exe)
    2. Run the installer and approve the driver (WHQL/HVCI-compatible; no test-signing needed).
    3. Re-run:  gpusuite vigem-check   (should turn PASS)
  Then the RE4 profile's gamepad bot (re4_pad_scene) can drive the in-game character.");
        return 1;
    }

    /// <summary>
    /// "Is this machine ready?" — one command that probes every dependency the suite needs (the .NET runtime,
    /// ffmpeg, PresentMon, RTSS, ViGEmBus, the local Ollama LLM + its model, the capture card, the Powenetics
    /// PMD, the profiles) and reports each with a fix. Designed for bringing the suite up ON ANOTHER MACHINE.
    /// Detection is READ-ONLY; <c>--fix</c> auto-installs the software ones (ffmpeg, Ollama+model, ViGEmBus,
    /// the .NET runtime) via winget / ollama. <c>--model</c> overrides the LLM model name (default qwen2.5:7b).
    /// </summary>
    private static async Task<int> Doctor(SuiteConfig cfg, ArgMap a)
    {
        string model = a.Get("--model") ?? "qwen2.5:7b";
        bool fix = a.Has("--fix");
        using var log = new RunLogger(null, echoToConsole: false);
        var svc = new Engine.Diagnostics.DoctorService(cfg, log);

        Console.WriteLine("\nEnvironment check — is this machine ready to run the GPU test suite?\n");
        var checks = await svc.RunAsync(model, CancellationToken.None);
        PrintDoctor(checks);

        int Missing(IEnumerable<Engine.Diagnostics.DoctorCheck> cs) =>
            cs.Count(c => c.Status == Engine.Diagnostics.DoctorStatus.Missing);
        var fixable = checks.Where(c => c.FixCommand is not null &&
            c.Status is Engine.Diagnostics.DoctorStatus.Missing or Engine.Diagnostics.DoctorStatus.Warn).ToList();

        if (!fix)
        {
            if (fixable.Count > 0)
                Console.WriteLine($"\n{fixable.Count} item(s) can be auto-installed — re-run:  gpusuite doctor --fix");
            int miss = Missing(checks);
            Console.WriteLine(miss == 0
                ? "\nRESULT: PASS — all required software present (hardware / optional items are noted above)."
                : $"\nRESULT: ATTENTION — {miss} required dependency(ies) missing; see the fixes above (or run: gpusuite doctor --fix).");
            return miss == 0 ? 0 : 1;
        }

        if (fixable.Count == 0)
        {
            Console.WriteLine("\nNothing to auto-install.");
            return Missing(checks) == 0 ? 0 : 1;
        }

        Console.WriteLine($"\n--fix: installing/repairing {fixable.Count} item(s). Downloads via winget / ollama " +
                          "(needs internet; some installers need Administrator — this tool will NOT self-elevate).\n");
        foreach (var c in fixable)
        {
            Console.WriteLine($"  → {c.Component}:  {c.FixCommand}");
            int rc = await RunShellAsync(c.FixCommand!);
            Console.WriteLine(rc == 0 ? "     done.\n" : $"     FAILED (exit {rc}). {c.FixHint}\n");
        }

        Console.WriteLine("Re-checking...\n");
        var after = await svc.RunAsync(model, CancellationToken.None);
        PrintDoctor(after);
        int stillMissing = Missing(after);
        Console.WriteLine(stillMissing == 0
            ? "\nRESULT: PASS — environment ready."
            : $"\nRESULT: {stillMissing} item(s) still missing (may need Administrator or a manual installer — see the hints).");
        return stillMissing == 0 ? 0 : 1;
    }

    private static void PrintDoctor(IReadOnlyList<Engine.Diagnostics.DoctorCheck> checks)
    {
        foreach (var c in checks)
        {
            string tag = c.Status switch
            {
                Engine.Diagnostics.DoctorStatus.Ok => "[ OK ]",
                Engine.Diagnostics.DoctorStatus.Warn => "[WARN]",
                Engine.Diagnostics.DoctorStatus.Missing => "[MISS]",
                Engine.Diagnostics.DoctorStatus.Hardware => "[ HW ]",
                _ => "[????]",
            };
            Console.WriteLine($"  {tag}  {c.Component}");
            Console.WriteLine($"          {c.Detail}");
            if (c.Status != Engine.Diagnostics.DoctorStatus.Ok && !string.IsNullOrWhiteSpace(c.FixHint))
                Console.WriteLine($"          → {c.FixHint}");
        }
    }

    /// <summary>
    /// Pre-flight GAME checks — the "is every game good to go before I hit go" pass. Read-only and launch-nothing:
    /// for each profile (or a --games subset) it verifies install + (Steam) update state, profile validity, that
    /// the referenced bot scripts exist, that settings/config files + registry keys are present, and that menu
    /// settings are calibrated. Exit 0 when nothing is blocked, 1 when at least one game has a blocker. --blockers
    /// hides the OK/Info lines and shows only what needs attention. Complements `doctor` (machine/toolchain).
    /// </summary>
    private static int Preflight(SuiteConfig cfg, ArgMap a)
    {
        var profiles = new ProfileManager(cfg.ProfilesDir).LoadAll();
        var only = a.Get("--games")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var games = only is { Length: > 0 }
            ? profiles.Where(p => only.Contains(p.Id, StringComparer.OrdinalIgnoreCase)).ToList()
            : profiles.ToList();
        if (games.Count == 0) { Console.Error.WriteLine("\nNo matching profiles" + (only is { Length: > 0 } ? $" for --games {string.Join(",", only)}." : ".")); return 2; }

        bool blockersOnly = a.Has("--blockers") || a.Has("--problems");
        Console.WriteLine("\nPre-flight game checks — is every game installed, updated, and good to go?\n");

        var catalog = new LauncherDiscovery().DiscoverAll();
        var report = new Engine.Diagnostics.GameReadinessChecker(cfg.ProfilesDir).Check(games, catalog);

        foreach (var g in report.Games)
        {
            string ov = g.Overall switch
            {
                Engine.Diagnostics.CheckStatus.Ok => "READY",
                Engine.Diagnostics.CheckStatus.Warn => "WARN ",
                Engine.Diagnostics.CheckStatus.Blocker => "BLOCK",
                Engine.Diagnostics.CheckStatus.Skip => "SKIP ",
                _ => "  -  "
            };
            Console.WriteLine($"  [{ov}]  {g.Name}  ·  {g.Store}");
            foreach (var c in g.Checks)
            {
                if (blockersOnly && c.Status is Engine.Diagnostics.CheckStatus.Ok or Engine.Diagnostics.CheckStatus.Info or Engine.Diagnostics.CheckStatus.Skip)
                    continue;
                string tag = c.Status switch
                {
                    Engine.Diagnostics.CheckStatus.Ok => "ok",
                    Engine.Diagnostics.CheckStatus.Info => "info",
                    Engine.Diagnostics.CheckStatus.Warn => "WARN",
                    Engine.Diagnostics.CheckStatus.Blocker => "BLOCK",
                    Engine.Diagnostics.CheckStatus.Skip => "skip",
                    _ => "?"
                };
                Console.WriteLine($"          {tag,-5} {c.Name}: {c.Detail}");
                if (!string.IsNullOrWhiteSpace(c.Fix) && c.Status is Engine.Diagnostics.CheckStatus.Warn or Engine.Diagnostics.CheckStatus.Blocker)
                    Console.WriteLine($"                → {c.Fix}");
            }
            Console.WriteLine();
        }

        Console.WriteLine($"RESULT: {report.ReadyCount} ready · {report.WarnCount} with warning(s) · {report.BlockerCount} blocked"
                          + (report.SkippedCount > 0 ? $" · {report.SkippedCount} disabled" : "") + ".");
        if (report.BlockerCount == 0 && report.WarnCount == 0)
            Console.WriteLine("All selected games are good to go.");
        return report.BlockerCount == 0 ? 0 : 1;
    }

    /// <summary>
    /// Update the locally installed LLM model used by the smart-bot nav supervisor — re-pulls the latest
    /// published layers (a no-op if already current). The "auto-update for the local LLM" entry point: run it
    /// manually, fold it into <c>doctor --fix</c>, or schedule it (e.g. Windows Task Scheduler) for hands-off
    /// updates. <c>--model</c> overrides the model name (default qwen2.5:7b).
    /// </summary>
    private static async Task<int> LlmUpdate(ArgMap a)
    {
        string model = a.Get("--model") ?? "qwen2.5:7b";
        Console.WriteLine($"\nUpdating local LLM model '{model}' (ollama pull — fetches any newer layers; no-op if current)...\n");
        int rc = await RunShellAsync($"ollama pull {model}");
        if (rc == 0) { Console.WriteLine($"\nRESULT: PASS — '{model}' is up to date."); return 0; }
        Console.WriteLine($"\nRESULT: FAIL (exit {rc}) — is Ollama installed and running? Install it with: gpusuite doctor --fix");
        return 1;
    }

    /// <summary>Run a command line through the OS shell, streaming its output to the console. Used by
    /// <c>doctor --fix</c> and <c>llm-update</c> to drive winget / ollama installs. Never throws.</summary>
    private static async Task<int> RunShellAsync(string command)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c {command}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return -1;
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine("     " + e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine("     " + e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine("     error: " + ex.Message);
            return -1;
        }
    }

    /// <summary>
    /// Probe one or all serial ports for a Powenetics V2 device (opens at 921600 8N1, sends the
    /// CalibrationOk→Stream handshake, counts decoded frames). Reports which port streams frames so
    /// the operator can set poweneticsComPort. Detection only — it never writes settings itself.
    /// </summary>
    private static int ProbePowenetics(ArgMap a)
    {
        var single = a.Get("--port");
        var ports = single is not null
            ? new[] { single }
            : System.IO.Ports.SerialPort.GetPortNames().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        if (ports.Length == 0) { Console.WriteLine("No serial (COM) ports found on this machine."); return 1; }

        Console.WriteLine($"Probing {ports.Length} serial port(s) for a Powenetics V2 device (921600 8N1, CalibrationOk→Stream)...\n");
        string? best = null; long bestFrames = 0;
        var silent = new List<string>();   // opened + handshook but 0 frames
        foreach (var p in ports)
        {
            long frames; bool opened = true;
            try { frames = Measurement.Real.Powenetics.PoweneticsSerialClient.ProbePort(p, 800); }
            catch { frames = 0; opened = false; }
            Console.WriteLine($"  {p,-7} frames={frames,-4} {(frames > 0 ? "✓ Powenetics frames detected" : opened ? "— no Powenetics frames" : "— port failed to open (in use / gone)")}");
            if (frames > bestFrames) { bestFrames = frames; best = p; }
            if (opened && frames == 0) silent.Add(p);
        }
        Console.WriteLine();
        if (best is not null && bestFrames > 0)
        {
            Console.WriteLine($"RESULT: PASS — Powenetics V2 streaming on {best} ({bestFrames} frames).");
            Console.WriteLine($"   → set \"poweneticsComPort\": \"{best}\" in settings.json to enable live power logging.");
            return 0;
        }
        // WEDGE verdict: a silent port that is the PMD's OWN USB device (Microchip VID_04D8) means the
        // board is enumerated and its port opens but the MCU is hung — the recurring bench failure.
        var pmdPorts = Measurement.Real.PoweneticsPowerProvider.PmdUsbPortNames();
        var wedged = silent.Where(p => pmdPorts.Contains(p)).ToList();
        if (wedged.Count > 0)
        {
            Console.WriteLine($"RESULT: FAIL — PMD WEDGED on {string.Join(", ", wedged)}: the Powenetics USB device (VID_04D8) is enumerated and its port opens, but it streams 0 protocol frames.");
            Console.WriteLine("   → the MCU is hung. Physically UNPLUG + REPLUG the PMD's USB cable (a host reboot does NOT reset it), then re-run probe-powenetics.");
            Console.WriteLine("   → if it wedges again within days, reseat/replace its USB cable next, then suspect the board.");
            return 2;
        }
        Console.WriteLine("RESULT: FAIL — no Powenetics device detected. Power logging would fall back to synthetic.");
        return 1;
    }

    /// <summary>
    /// Diagnostic for the live benchmark OSD: launches RTSS if configured, claims an OSD slot, and
    /// animates a demo overlay for a few seconds. The text only becomes VISIBLE on screen while RTSS is
    /// hooking a running 3D app (game) — with nothing hooked, a successful slot claim still proves the
    /// shared-memory write path works. Use this to confirm the overlay before a real run.
    /// </summary>
    private static int OsdTest(SuiteConfig cfg, ArgMap a)
    {
        int seconds = a.GetInt("--seconds") ?? 12;

        // Best-effort: start RTSS from the configured path if it isn't already up (mirrors the run path).
        if (!string.IsNullOrWhiteSpace(cfg.RtssExePath) &&
            System.Diagnostics.Process.GetProcessesByName("RTSS").Length == 0)
        {
            var rp = Environment.ExpandEnvironmentVariables(cfg.RtssExePath);
            if (File.Exists(rp))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(rp)
                    { UseShellExecute = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Minimized, WorkingDirectory = Path.GetDirectoryName(rp) ?? "" });
                    Console.WriteLine($"Launched RTSS from {rp}; giving it 2s to initialize...");
                    System.Threading.Thread.Sleep(2000);
                }
                catch (Exception ex) { Console.WriteLine("Could not launch RTSS: " + ex.Message); }
            }
        }

        using var osd = new Measurement.Real.RtssOsdWriter();
        if (!osd.Open())
        {
            Console.WriteLine("RESULT: FAIL — could not open/claim an RTSS OSD slot.");
            Console.WriteLine("  • Ensure RTSS (RivaTuner Statistics Server) is running (set settings.rtssExePath or start it).");
            Console.WriteLine("  • The overlay only appears while RTSS is hooking a running game/3D app.");
            return 1;
        }

        Console.WriteLine($"RTSS OSD slot claimed. Animating a demo overlay for {seconds}s — if a game is running with RTSS hooked, you should see it now.\n");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int pass = 1;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            double t = sw.Elapsed.TotalSeconds;
            double fps = 78 + 8 * Math.Sin(t);          // wobble so it's obviously live
            double gpuTemp = 63 + 3 * Math.Sin(t / 2);
            double gpuW = 210 + 25 * Math.Sin(t);
            string text =
                "GpuTestSuite | OSD self-test\n" +
                $"Pass {pass}/3  1440p 2560x1440  [DEMO]\n" +
                $"FPS {fps:0.0}  ft {1000.0 / fps:0.0}ms  frames {(int)(fps * t)}  (demo)\n" +
                $"GPU {gpuTemp:0}C hot {gpuTemp + 8:0}C  load 99%  2820MHz\n" +
                $"PWR gpu {gpuW:0}W  sys {gpuW + 130:0}W\n" +
                "CPU 61C  load 24%";
            osd.Update(text);
            System.Threading.Thread.Sleep(250);
            if ((int)t % 4 == 3) pass = pass % 3 + 1;
        }
        osd.Clear();
        Console.WriteLine("RESULT: PASS — OSD shared-memory write path works (slot claimed + updated).");
        Console.WriteLine("  If you didn't SEE it, start a game (or any RTSS-hooked 3D app) and re-run while it's in the foreground.");
        return 0;
    }
}
