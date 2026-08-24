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
    /// Dry-run a bot script (built-in id or profiles/bots/&lt;id&gt;.json): parse it, then replay the action
    /// timeline with NO input injected (waits fast-forwarded), printing each step and the detected
    /// MarkStart/MarkEnd window boundaries. The way to author/iterate a menu-navigation bot before
    /// driving the live game. Does not launch anything.
    /// </summary>
    private static async Task<int> BotDryRun(ArgMap a)
    {
        var id = a.Get("--bot");
        if (id is null) { Console.Error.WriteLine("Usage: gpusuite bot-dryrun --bot <script-id>  (e.g. ratchet_benchmark_nav)"); return 1; }
        var script = Engine.Automation.BotScriptLibrary.Resolve(id);
        if (script is null) { Console.Error.WriteLine($"Bot script '{id}' not found (built-in, or profiles/bots/{id}.json)."); return 1; }

        using var log = new RunLogger(null, echoToConsole: true);
        int graphScreens = script.Graph?.Screens.Count ?? 0;
        Console.WriteLine($"Dry-run '{script.Id}' — {script.Actions.Count} action(s), {graphScreens} graph screen(s), loop={script.Loop}. NO input is injected.\n  {script.Description}\n");
        using var engine = new Engine.Automation.InputAutomationEngine(log, inject: false)
        {
            FastForward = true,
            SkipInteractiveNavigation = true,
            MaxActionIterations = 1
        };
        var res = await engine.RunAsync(script, TimeSpan.FromSeconds(a.GetInt("--seconds") ?? 15), CancellationToken.None);

        Console.WriteLine($"\n  Markers: start={res.StartOffsetSec?.ToString("0.0") ?? "—"}s  end={res.EndOffsetSec?.ToString("0.0") ?? "—"}s  iterations={res.Iterations}");
        if (res.Aborted)
        {
            Console.WriteLine($"RESULT: FAIL — {res.AbortReason}");
            return 1;
        }
        Console.WriteLine("RESULT: PASS — bot script parsed and its deterministic action route replayed once (dry-run).");
        return 0;
    }

    /// <summary>
    /// Capture-card VISION grab: pull one clean still frame from the HDMI capture card (e.g. Elgato 4K Pro)
    /// via ffmpeg, for the operator-prep phase to SEE the bench (read game menus/settings, confirm the
    /// scene) — a resolution-independent, pixel-exact picture that also captures exclusive-fullscreen,
    /// unlike a GDI screenshot. `--list` enumerates the DirectShow video devices ffmpeg can see. This is a
    /// vision feed only; PresentMon/RTSS remain the FPS path.
    /// </summary>
    private static async Task<int> GrabFrame(SuiteConfig cfg, ArgMap a)
    {
        using var log = new RunLogger(null, echoToConsole: true);
        var device = a.Get("--device") ?? cfg.CaptureCardDevice;
        var grabber = new Engine.Vision.CaptureCardGrabber(cfg.FfmpegPath, device, log);

        if (a.Has("--list"))
        {
            var devs = await grabber.ListVideoDevicesAsync();
            Console.WriteLine($"\nDirectShow video capture devices ffmpeg can see ({devs.Count}):");
            foreach (var d in devs) Console.WriteLine($"  • \"{d}\"");
            if (devs.Count == 0)
                Console.WriteLine("  (none — is ffmpeg installed and a capture device connected? set settings.ffmpegPath)");
            else
                Console.WriteLine("\n  Set one as settings.captureCardDevice (or pass --device) for `gpusuite grab`.");
            return devs.Count > 0 ? 0 : 1;
        }

        if (string.IsNullOrWhiteSpace(device))
        {
            Console.Error.WriteLine("No capture device. Set settings.captureCardDevice or pass --device \"Elgato 4K Pro\" (list with: gpusuite grab --list).");
            return 1;
        }

        var outPng = a.Get("--out") ?? Path.Combine(cfg.ResultsRoot, $"grab_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        int warmup = a.GetInt("--warmup") ?? 12;
        bool ok = await grabber.GrabAsync(outPng, warmup);
        if (ok)
        {
            Console.WriteLine($"\nRESULT: PASS — clean frame grabbed → {Path.GetFullPath(outPng)}");
            if (a.Has("--open")) TryOpen(outPng);
            return 0;
        }
        Console.WriteLine("\nRESULT: FAIL — no frame grabbed (see warnings above). Check `gpusuite grab --list` and settings.ffmpegPath.");
        return 1;
    }

    /// <summary>
    /// Smart-bot Tier-0 SENSOR calibration: repeatedly sample in-world MOTION off the capture card (ffmpeg
    /// scene-change, no benchmark-GPU cost) and print the score, so the operator can calibrate the "stuck"
    /// threshold live — move the character to see it rise, face a wall / stand still to see it drop. The
    /// smart navigator uses the same SampleMotionAsync to detect wall-jams and turn to unstick.
    /// </summary>
    private static async Task<int> MotionProbe(SuiteConfig cfg, ArgMap a)
    {
        using var log = new RunLogger(null, echoToConsole: true);
        var device = a.Get("--device") ?? cfg.CaptureCardDevice;
        if (string.IsNullOrWhiteSpace(device))
        {
            Console.Error.WriteLine("No capture device. Set settings.captureCardDevice or pass --device \"Elgato 4K Pro\" (list with: gpusuite grab --list).");
            return 1;
        }
        var grabber = new Engine.Vision.CaptureCardGrabber(cfg.FfmpegPath, device, log);
        int count = a.GetInt("--count") ?? 10;
        int frames = a.GetInt("--frames") ?? 12;
        Console.WriteLine($"\nMotion probe on \"{device}\" — {count} sample(s), {frames} frames each.");
        Console.WriteLine("LOW score ≈ stuck / not translating (wall, idle); HIGH ≈ moving through space.");
        Console.WriteLine("Calibrate the smart-bot stuck-threshold: drive the character to see the score rise, face a wall to see it drop.\n");
        double lo = double.MaxValue, hi = 0; int ok = 0;
        for (int i = 1; i <= count; i++)
        {
            double m = await grabber.SampleMotionAsync(frames);
            if (m >= 0) { ok++; lo = Math.Min(lo, m); hi = Math.Max(hi, m); }
            Console.WriteLine($"  sample {i,2}: motion = {(m < 0 ? "n/a" : m.ToString("0.00"))}");
        }
        if (ok > 0) Console.WriteLine($"\nRange seen: {lo:0.00} .. {hi:0.00} — set the stuck-threshold a bit above the LOW (wall/idle) end.");
        else Console.WriteLine("\nNo scores parsed — ffmpeg may be too old for scdet/metadata, or the device was busy. (Vision unaffected.)");
        return ok > 0 ? 0 : 1;
    }

    /// <summary>
    /// Vision-nav EYE: grab a clean frame off the capture card and OCR it (Windows.Media.Ocr via
    /// tools/vision/ocr.ps1), so the operator — and, live, the menu applier — can SEE the menu text and
    /// where each control sits. `--devices` lists capture cards; `--read` dumps every OCR'd line with its
    /// position; `--find "Ray Tracing"` locates a label and reads the value beside it; `--ocr file.png`
    /// OCRs an existing image (no capture). This is the calibration tool for per-game menu maps.
    /// </summary>
    private static async Task<int> Vision(SuiteConfig cfg, ArgMap a)
    {
        using var log = new RunLogger(null, echoToConsole: true);
        var device = a.Get("--device") ?? cfg.CaptureCardDevice;
        var grabber = new Engine.Vision.CaptureCardGrabber(cfg.FfmpegPath, device, log);
        var reader = new Engine.Vision.ScreenReader(grabber, log);

        if (a.Has("--devices"))
        {
            var devs = await grabber.ListVideoDevicesAsync();
            Console.WriteLine($"\nDirectShow video capture devices ffmpeg can see ({devs.Count}):");
            foreach (var d in devs) Console.WriteLine($"  • \"{d}\"");
            return devs.Count > 0 ? 0 : 1;
        }

        // VISION-NAV probe: grab a live frame and ask the multimodal model for the next action toward a goal — the
        // fast calibration loop (point a game at a menu, see what the navigator would do) without a full game run.
        string? navGoal = a.Get("--nav");
        if (navGoal is not null)
        {
            if (string.IsNullOrWhiteSpace(device)) { Console.Error.WriteLine("No capture device (set settings.captureCardDevice or --device)."); return 1; }
            var backend = Engine.Automation.VisionNavSupervisor.ResolveBackend(cfg, log);
            var visModel = a.Get("--model") ?? backend.Model;
            var sup = new Engine.Automation.VisionNavSupervisor(backend.Endpoint, visModel, grabber, cfg.NavSupervisorVisionWidth, backend.Gpu, log, backend.Provider, backend.ApiKey, backend.Effort);
            Console.WriteLine($"\nVISION NAV — model '{visModel}' on {backend.Source} ({backend.Endpoint}), goal: \"{navGoal}\"");
            var step = await sup.NextStepAsync(navGoal, default);
            if (step is null) { Console.WriteLine("RESULT: FAIL — no decision (grab/HTTP/parse failed; see warnings)."); return 1; }
            Console.WriteLine($"  decision : {(step.Done ? "DONE (goal already on screen)" : step.Action?.ToString().ToUpperInvariant() ?? "WAIT/none")}");
            Console.WriteLine($"  rationale: {step.Rationale}");
            return 0;
        }

        Engine.Vision.OcrFrame? frame;
        string? ocrPng = a.Get("--ocr");
        if (ocrPng is not null)
        {
            frame = await reader.OcrImageAsync(ocrPng);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(device))
            {
                Console.Error.WriteLine("No capture device. Set settings.captureCardDevice or pass --device (list: gpusuite vision --devices).");
                return 1;
            }
            string png = a.Get("--png") ?? Path.Combine(Path.GetTempPath(), $"vision_{DateTime.Now:HHmmss}.png");
            if (!await grabber.GrabAsync(png, a.GetInt("--warmup") ?? 12))
            {
                Console.WriteLine("\nRESULT: FAIL — no frame grabbed (see warnings). Check `gpusuite vision --devices`.");
                return 1;
            }
            frame = await reader.OcrImageAsync(png);
            if (a.Has("--png")) Console.WriteLine($"  (frame saved → {Path.GetFullPath(png)})");
            else { try { File.Delete(png); } catch { } }
        }

        if (frame is null)
        {
            Console.WriteLine("\nRESULT: FAIL — OCR produced no result (no language pack? blank frame? see warnings).");
            return 1;
        }

        string? find = a.Get("--find");
        if (find is not null)
        {
            var hits = frame.FindAll(find).ToList();
            Console.WriteLine($"\nMatches for \"{find}\" ({hits.Count}) in a {frame.Width}x{frame.Height} frame:");
            foreach (var h in hits)
                Console.WriteLine($"  {h}   value→ {frame.ValueForLine(h, find) ?? "(none beside / same-line)"}");
            return hits.Count > 0 ? 0 : 1;
        }

        Console.WriteLine();
        Console.Write(frame.Dump());
        Console.WriteLine($"RESULT: PASS — {frame.Lines.Count} line(s) OCR'd from a {frame.Width}x{frame.Height} frame.");
        return 0;
    }

    /// <summary>
    /// Offline proof of the vision-nav READ core (no capture card, no game): parse a canned OCR frame that
    /// mimics a graphics menu and confirm the engine finds each labelled setting and reads the value beside
    /// it — both the "value on a separate line to the right" layout and the "Label    Value" single-line
    /// layout — and that the match test distinguishes On from Off. The live OCR path is exercised by
    /// `gpusuite vision --read` / `--ocr`.
    /// </summary>
    private static int VisionSelfTest()
    {
        const string json = """
            {"width":2560,"height":1440,"lines":[
              {"text":"Display","x":120,"y":80,"w":160,"h":44,"cx":200,"cy":102},
              {"text":"Ray Tracing","x":200,"y":300,"w":300,"h":40,"cx":350,"cy":320},
              {"text":"On","x":1100,"y":300,"w":70,"h":40,"cx":1135,"cy":320},
              {"text":"Upscaling Method","x":200,"y":380,"w":420,"h":40,"cx":410,"cy":400},
              {"text":"DLSS","x":1100,"y":380,"w":110,"h":40,"cx":1155,"cy":400},
              {"text":"Upscaler Quality","x":200,"y":460,"w":420,"h":40,"cx":410,"cy":480},
              {"text":"Balanced","x":1100,"y":460,"w":180,"h":40,"cx":1190,"cy":480},
              {"text":"Frame Generation     Off","x":200,"y":540,"w":620,"h":40,"cx":510,"cy":560}
            ]}
            """;
        var frame = Engine.Vision.OcrFrame.FromJson(json);
        Console.WriteLine("Vision-nav OCR read/find/value core — offline self-test:\n");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string got)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}  → {got}");
            if (ok) pass++; else fail++;
        }
        if (frame is null) { Console.WriteLine("RESULT: FAIL — canned frame did not parse."); return 1; }
        Check("parse 8 lines", frame.Lines.Count == 8, $"{frame.Lines.Count} lines");
        Check("find 'Ray Tracing'", frame.Find("ray tracing") is not null, frame.Find("ray tracing")?.ToString() ?? "null");
        Check("value-right 'Ray Tracing' == On", frame.ValueFor("Ray Tracing") == "On", frame.ValueFor("Ray Tracing") ?? "null");
        Check("value-right 'Upscaling Method' == DLSS", frame.ValueFor("Upscaling Method") == "DLSS", frame.ValueFor("Upscaling Method") ?? "null");
        Check("value-right 'Upscaler Quality' == Balanced", frame.ValueFor("Upscaler Quality") == "Balanced", frame.ValueFor("Upscaler Quality") ?? "null");
        Check("same-line tail 'Frame Generation' == Off", frame.ValueFor("Frame Generation") == "Off", frame.ValueFor("Frame Generation") ?? "null");
        Check("match On (true)", frame.ValueMatches("Ray Tracing", "On"), "On matches");
        Check("match Off (false)", !frame.ValueMatches("Ray Tracing", "Off"), "Off does not match On");
        Check("missing label → null", frame.ValueFor("Nonexistent Setting") is null, frame.ValueFor("Nonexistent Setting") ?? "null");
        Console.WriteLine($"\nRESULT: {(fail == 0 ? "PASS" : "FAIL")} — {pass} passed, {fail} failed.");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>A tiny solid PNG so <c>inworld-nav --synth</c> can exercise the LIVE GX10 movement path even when no
    /// capture signal is present — a box-answers smoke-test independent of the bench display.</summary>
    private const string SynthFrameB64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    /// <summary>LIVE smoke-test of the Tier-1 in-world navigator end to end: pick the GX10 (when reachable) via the
    /// same compute resolver the run path uses, grab a capture-card frame (or a synthetic one with <c>--synth</c>),
    /// ask the box for the next MOVEMENT, and print the parsed decision + raw reply. <c>--count N</c> repeats;
    /// <c>--goal "..."</c> overrides the goal. Proves the Elgato→GX10-vision→movement path works off-bench before it
    /// is wired into a bot. Read-only: it injects NOTHING (no character actually moves).</summary>
    private static async Task<int> InWorldNav(SuiteConfig cfg, ArgMap a)
    {
        using var log = new RunLogger(null, echoToConsole: true);
        string goal = a.Get("--goal") ?? "explore the open area ahead; keep moving through traversable space; do not grind against walls";
        int count = Math.Max(1, a.GetInt("--count") ?? 1);
        bool synth = a.Has("--synth");
        string? framePath = a.Get("--frame");
        string? frameB64 = null;
        if (framePath is not null)
        {
            if (!File.Exists(framePath)) { Console.Error.WriteLine($"inworld-nav: --frame not found: {framePath}"); return 1; }
            frameB64 = Convert.ToBase64String(await File.ReadAllBytesAsync(framePath));
        }

        _ = Engine.Automation.VisionNavSupervisor.ResolveCompute(cfg, log, out var endpoint, out var src);
        string model = cfg.NavSupervisorVisionModel;
        var device = a.Get("--device") ?? cfg.CaptureCardDevice;
        var grabber = new Engine.Vision.CaptureCardGrabber(cfg.FfmpegPath, device, log);
        var nav = new Engine.Automation.Gx10InWorldNavigator(endpoint, model, grabber, cfg.NavInWorldVisionWidth, log) { StuckThreshold = cfg.NavStuckThreshold };

        string srcLabel = frameB64 is not null ? $"FILE {Path.GetFileName(framePath)}" : synth ? "SYNTHETIC frame" : "capture card";
        Console.WriteLine($"\nIn-world nav probe — compute={src} ({endpoint}), model '{model}', source={srcLabel}.");
        Console.WriteLine($"Goal: {goal}\n");

        for (int i = 1; i <= count; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Engine.Automation.MoveStep? step = frameB64 is not null
                ? await nav.DecideAsync(frameB64, goal, CancellationToken.None)
                : synth
                    ? await nav.DecideAsync(SynthFrameB64, goal, CancellationToken.None)
                    : await nav.NextMoveAsync(goal, CancellationToken.None);
            sw.Stop();
            Console.WriteLine(step is null
                ? $"  {i,2}: (no decision — grab failed or box unreachable)   [{sw.ElapsedMilliseconds} ms]"
                : $"  {i,2}: {(step.Done ? "DONE" : step.Action?.ToString() ?? "none")}   «{step.Raw.Replace("\r", " ").Replace("\n", " ")}»   [{sw.ElapsedMilliseconds} ms]");
        }
        return 0;
    }

    /// <summary>
    /// LIVE in-world DRIVE: attach to a RUNNING game (by --pid or --game's CaptureProcessName, already in a playable
    /// scene) and let the Tier-1 GX10 navigator STEER the character for --seconds (default 45), holding the translation
    /// CONTINUOUSLY while it re-decides off the capture card (perception off-bench). Gamepad by default (--keyboard for
    /// WASD games). Grabs a BEFORE + AFTER frame so movement can be confirmed (measured-route-must-move). --record PATH
    /// saves the driven route for a later deterministic ReplayRoute. INJECTS input — the game is foreground-restored by
    /// PID; keep it short and watch the screen.
    /// </summary>
    private static async Task<int> InWorldDrive(SuiteConfig cfg, ArgMap a)
    {
        using var log = new RunLogger(null, echoToConsole: true);
        int? pid = a.GetInt("--pid");
        var gameId = a.Get("--games") ?? a.Get("--game");
        if (pid is null && gameId is not null)
        {
            var game = new ProfileManager(cfg.ProfilesDir).LoadAll().FirstOrDefault(p => string.Equals(p.Id, gameId, StringComparison.OrdinalIgnoreCase));
            if (game is not null)
            {
                var procs = System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(game.CaptureProcessName));
                if (procs.Length > 0) pid = procs[0].Id;
            }
        }
        if (pid is null) { Console.Error.WriteLine("inworld-drive: no running game found. Pass --pid N, or --game ID with the game already in a playable scene."); return 1; }

        var device = a.Get("--device") ?? cfg.CaptureCardDevice;
        if (string.IsNullOrWhiteSpace(device)) { Console.Error.WriteLine("inworld-drive: no captureCardDevice set."); return 1; }
        int seconds = Math.Max(5, a.GetInt("--seconds") ?? 45);
        string goal = a.Get("--goal") ?? "explore the open area ahead; follow corridors and open space; do not grind against walls";
        string? record = a.Get("--record");
        bool keyboard = a.Has("--keyboard");

        _ = Engine.Automation.VisionNavSupervisor.ResolveCompute(cfg, log, out var endpoint, out var src);
        var grabber = new Engine.Vision.CaptureCardGrabber(cfg.FfmpegPath, device, log);
        var nav = new Engine.Automation.Gx10InWorldNavigator(endpoint, cfg.NavSupervisorVisionModel, grabber, cfg.NavInWorldVisionWidth, log) { StuckThreshold = cfg.NavStuckThreshold };

        Console.WriteLine($"\nIn-world DRIVE — pid {pid}, {(keyboard ? "keyboard/WASD" : "gamepad")}, {seconds}s, compute={src} ({endpoint}), model '{cfg.NavSupervisorVisionModel}', {cfg.NavInWorldVisionWidth}px, stuck-reflex thr {cfg.NavStuckThreshold:0.00}.");
        Console.WriteLine($"Goal: {goal}\n  (INJECTING movement into the foreground game.)\n");

        var beforePng = Path.Combine(Path.GetTempPath(), $"drive_before_{DateTime.Now:HHmmss}.png");
        await grabber.GrabAsync(beforePng, 4, CancellationToken.None, 1600);
        Console.WriteLine($"  before frame → {beforePng}");

        // Pre-warm the vision model so the one-time ~40s cold VRAM load is paid HERE, not during the timed drive.
        Console.WriteLine("  warming vision model (one-time ~40s cold load happens now, not during the drive)…");
        await nav.WarmupAsync(CancellationToken.None);

        var script = new Engine.Automation.BotScript
        {
            Id = "inworld_drive", Loop = false,
            InputDevice = keyboard ? Engine.Automation.BotInputDevice.Keyboard : Engine.Automation.BotInputDevice.Gamepad,
            Actions = new() { Engine.Automation.BotAction.Gx10Smart(seconds * 1000, goal, record) }
        };
        using (var engine = new Engine.Automation.InputAutomationEngine(log, inject: true) { TargetPid = pid, Gx10Navigator = nav })
            await engine.RunAsync(script, TimeSpan.FromSeconds(seconds + 30), CancellationToken.None);

        var afterPng = Path.Combine(Path.GetTempPath(), $"drive_after_{DateTime.Now:HHmmss}.png");
        await grabber.GrabAsync(afterPng, 4, CancellationToken.None, 1600);
        Console.WriteLine($"  after frame  → {afterPng}");
        if (record is not null) Console.WriteLine($"  recorded route → {record} (replay deterministically with a ReplayRoute bot).");
        Console.WriteLine("\nDone. Compare the before/after frames to confirm the character translated (real movement, not idle).");
        return 0;
    }

    /// <summary>
    /// Low-level input primitive for live menu-map calibration: inject a comma-separated key sequence into a
    /// running game (foreground-restored by PID). Tokens: a key name (Enter/Down/Right/Esc/Space/W…),
    /// "wait:Nms", "click", or "moveabs:x:y" (0..1). `--pad` routes through the ViGEm gamepad. Pair with
    /// `gpusuite vision --read` between calls to learn a game's menu, then encode it into the profile menuMap.
    /// </summary>
    private static async Task<int> SendInput(SuiteConfig cfg, ArgMap a)
    {
        using var log = new RunLogger(null, echoToConsole: true);
        var keys = a.Get("--keys");
        if (keys is null) { Console.Error.WriteLine("Usage: gpusuite send-input --keys \"Enter,wait:800,Down,Right\" [--game ID | --pid N] [--pad] [--vk]\n  --vk: send ALL keyboard taps as VIRTUAL KEYS (wVk) instead of DirectInput scancodes.\n  Or per-key with the \"vk:\" prefix to MIX (scancode nav + VK confirm): --keys \"Down,Down,vk:Enter\"\n  — for mouse-first menus that navigate on scancode arrows but only confirm on a virtual key (e.g. Cyberpunk)."); return 1; }

        int? pid = a.GetInt("--pid");
        var gameId = a.Get("--games") ?? a.Get("--game");
        if (pid is null && gameId is not null)
        {
            var game = new ProfileManager(cfg.ProfilesDir).LoadAll().FirstOrDefault(p => string.Equals(p.Id, gameId, StringComparison.OrdinalIgnoreCase));
            if (game is not null)
            {
                var procs = System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(game.CaptureProcessName));
                if (procs.Length > 0) pid = procs[0].Id;
            }
        }

        bool pad = a.Has("--pad");
        bool vk = a.Has("--vk");   // route keyboard taps through VIRTUAL KEYS (wVk) instead of DirectInput scancodes
        var actions = ParseKeySequence(keys, pad);

        string device = pad ? "gamepad" : (vk ? "keyboard/virtual-key" : "keyboard/scancode");
        Console.WriteLine($"Injecting {actions.Count} action(s) to {(pid is int p ? $"pid {p}" : "the foreground window")} (device={device})...");
        var engine = new Engine.Automation.InputAutomationEngine(log, inject: true) { TargetPid = pid, UseVirtualKeys = vk };
        var script = new Engine.Automation.BotScript
        {
            Id = "send-input", Loop = false,
            InputDevice = pad ? Engine.Automation.BotInputDevice.Gamepad : Engine.Automation.BotInputDevice.Keyboard,
            Actions = actions
        };
        await engine.RunAsync(script, TimeSpan.FromSeconds(120), CancellationToken.None);
        Console.WriteLine("Done.");
        return 0;
    }

    /// <summary>Parse a `send-input` key sequence ("Enter,wait:800,Down,Right,click,moveabs:0.5:0.5") into bot
    /// actions. Reused by <c>send-input</c> and the bot-driven calibration observer.</summary>
    internal static List<Engine.Automation.BotAction> ParseKeySequence(string keys, bool pad)
    {
        var actions = new List<Engine.Automation.BotAction>();
        foreach (var tok in (keys ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (tok.StartsWith("wait:", StringComparison.OrdinalIgnoreCase))
                actions.Add(Engine.Automation.BotAction.Pause(int.TryParse(tok.AsSpan(5).TrimEnd("ms"), out var ms) ? ms : 500));
            else if (tok.StartsWith("moveabs:", StringComparison.OrdinalIgnoreCase))
            {
                var pp = tok.Split(':');
                actions.Add(Engine.Automation.BotAction.MoveAbs(double.Parse(pp[1]), double.Parse(pp[2]), 80));
            }
            else if (tok.StartsWith("moverel:", StringComparison.OrdinalIgnoreCase))
            {
                // "moverel:dx:dy" — RELATIVE mouse move in raw pixels. Needed for raw-input engines (REDengine)
                // that ignore injected ABSOLUTE cursor jumps and only track relative deltas (live 2026-07-03:
                // moveabs left Cyberpunk's software cursor parked; a relative delta moves it).
                var pp = tok.Split(':');
                actions.Add(Engine.Automation.BotAction.Move(int.Parse(pp[1]), int.Parse(pp[2]), 80));
            }
            else if (tok.Equals("click", StringComparison.OrdinalIgnoreCase))
                actions.Add(Engine.Automation.BotAction.Click("left", 40));
            else if (tok.StartsWith("lstick:", StringComparison.OrdinalIgnoreCase) || tok.StartsWith("rstick:", StringComparison.OrdinalIgnoreCase))
            {
                // "lstick:X:Y:MS" — deflect the LEFT (or RIGHT) analog stick to (X,Y), each -1..1, and HOLD for MS ms
                // (deflection persists until changed). Expresses analog MOVEMENT a button tap can't — e.g. run
                // forward = lstick:0:1:10000, reverse = lstick:0:-1:10000, recenter = lstick:0:0:100. Pad device only.
                var pp = tok.Split(':');
                double sx = pp.Length > 1 && double.TryParse(pp[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vx) ? vx : 0;
                double sy = pp.Length > 2 && double.TryParse(pp[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vy) ? vy : 0;
                int sms = pp.Length > 3 && int.TryParse(pp[3], out var sm) ? sm : 1000;
                actions.Add(tok.StartsWith("l", StringComparison.OrdinalIgnoreCase)
                    ? Engine.Automation.BotAction.LStick(sx, sy, sms)
                    : Engine.Automation.BotAction.RStick(sx, sy, sms));
            }
            else if (!pad && tok.StartsWith("vk:", StringComparison.OrdinalIgnoreCase))
                // "vk:Enter" — tap as a VIRTUAL KEY (wVk), for a mouse-first menu's confirm/back that ignores a
                // scancode key (live-proven on Cyberpunk: scancode arrows nav, vk:Enter confirms). Lets one sequence
                // scancode-navigate then VK-confirm: "Down,Down,vk:Enter".
                actions.Add(Engine.Automation.BotAction.TapVk(tok.Substring(3), 50));
            else
            {
                // "Key" taps with the device default; "Key:MS" holds for MS ms — some games ignore short taps
                // (Black Myth drops pad taps <200ms), so menu confirms there need e.g. "A:300".
                string keyName = tok; int tapMs = pad ? 80 : 50;
                int ci = tok.LastIndexOf(':');
                if (ci > 0 && int.TryParse(tok.AsSpan(ci + 1), out var customMs) && customMs > 0) { keyName = tok[..ci]; tapMs = customMs; }
                actions.Add(pad ? Engine.Automation.BotAction.PadTap(keyName, tapMs) : Engine.Automation.BotAction.Tap(keyName, tapMs));
            }
        }
        return actions;
    }

    /// <summary>
    /// Vision-nav menu APPLIER: drive a running game's graphics menu to set a variant's menu-method knobs
    /// (DLSS/FSR, RT, frame-gen, …) and OCR-VERIFY each off the capture card. This is the live calibration +
    /// proof tool for a game's MenuMap. The operator gets the game to its (paused/menu) state, then:
    ///   gpusuite menu-apply --games doom-the-dark-ages --variant dlss-q
    /// `--dry` plans offline (no game, no injection). A knob that can't be set+verified fails (fail-safe).
    /// </summary>
    private static async Task<int> MenuApply(SuiteConfig cfg, ArgMap a)
    {
        using var log = new RunLogger(null, echoToConsole: true);
        var gameId = a.Get("--games") ?? a.Get("--game");
        if (gameId is null) { Console.Error.WriteLine("Usage: gpusuite menu-apply --games <id> [--variant <id>] [--dry] [--device \"\"]"); return 1; }

        var game = new ProfileManager(cfg.ProfilesDir).LoadAll()
            .FirstOrDefault(p => string.Equals(p.Id, gameId, StringComparison.OrdinalIgnoreCase));
        if (game is null) { Console.Error.WriteLine($"Profile '{gameId}' not found in {cfg.ProfilesDir}."); return 1; }

        GameVariant? variant = null;
        var vid = a.Get("--variant");
        if (vid is not null)
        {
            variant = game.Variants.FirstOrDefault(v => string.Equals(v.Id, vid, StringComparison.OrdinalIgnoreCase));
            if (variant is null)
            {
                Console.Error.WriteLine($"Variant '{vid}' not in '{game.Id}'. Have: {string.Join(", ", game.Variants.Select(v => v.Id))}");
                return 1;
            }
        }

        bool dry = a.Has("--dry");
        int? pid = null;
        if (!dry)
        {
            var procName = Path.GetFileNameWithoutExtension(game.CaptureProcessName);
            var procs = System.Diagnostics.Process.GetProcessesByName(procName);
            if (procs.Length == 0)
            {
                Console.Error.WriteLine($"No running '{game.CaptureProcessName}'. Launch the game, get it to a menu, then re-run (or use --dry to plan offline).");
                return 1;
            }
            pid = procs[0].Id;
            Console.WriteLine($"Attached to {game.CaptureProcessName} (pid {pid}). Driving the settings menu — keep hands off the keyboard/mouse.");
        }

        var grabber = new Engine.Vision.CaptureCardGrabber(cfg.FfmpegPath, a.Get("--device") ?? cfg.CaptureCardDevice, log);
        var reader = new Engine.Vision.ScreenReader(grabber, log);
        var applier = new Engine.Scenes.VisionMenuApplier(reader, log);
        var res = await applier.ApplyAsync(game, variant, pid, inject: !dry, CancellationToken.None, skipOpen: a.Has("--skip-open"));

        Console.WriteLine();
        foreach (var s in res.Applied) Console.WriteLine($"  [OK]   {s}");
        foreach (var s in res.Failed) Console.WriteLine($"  [FAIL] {s}");
        Console.WriteLine($"\nRESULT: {(res.Ok ? "PASS" : "FAIL")} — {res.Detail}");
        return res.Ok ? 0 : 1;
    }
}
