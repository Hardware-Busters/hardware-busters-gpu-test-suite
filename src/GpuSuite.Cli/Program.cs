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
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($"{GpuSuite.BuildInfo.DisplayVersion}  (build {GpuSuite.BuildInfo.BuildTimestamp}, commit {GpuSuite.BuildInfo.CommitHash})");
        var a = new ArgMap(args);
        string? verbArg = args.FirstOrDefault(x => !x.StartsWith('-'));
        string verb = verbArg ?? "run";

        // HIT-GO GUARD (2026-07-02): the default verb used to be "run" for ANY dash-only invocation, so a
        // stray `gpusuite --version` (or any typo'd flag call with no verb) silently launched a FULL-ROSTER
        // CAMPAIGN — armed the input lock, started RTSS for the OSD (the global hook that crashes Ratchet,
        // see [[ratchet-rtss-crash]]), and cold-launched the first game as a zombie console process (caught
        // live 2026-07-02: a `--version` banner check collided with a real validation run). Version queries
        // exit after the banner above; a verbless call must now say `run` explicitly to hit go.
        if (args.Any(x => x is "--version" or "-v")) return 0;
        if (verbArg is null && args.Length > 0) { Console.WriteLine("\nNo verb given — refusing to default to a full `run` (pass the verb explicitly, e.g. `gpusuite run --games ...`).\n"); PrintHelp(); return 2; }
        if (verbArg is null) { PrintHelp(); return 0; }

        // Working root: where settings/profiles/Results/plans.json live. Default = current dir — but a
        // background shell can lose the repo CWD between invocations (live 2026-07-03: a `--plan` campaign
        // launched from the parent dir exited "no plans saved yet"), so if the CWD doesn't LOOK like a
        // suite root, walk up from the exe (…\src\GpuSuite.Cli\bin\… sits inside the repo) and use the
        // first ancestor that does. An explicit --root always wins.
        static bool LooksLikeSuiteRoot(string dir) =>
            Directory.Exists(Path.Combine(dir, "profiles")) && File.Exists(Path.Combine(dir, "settings.json"));
        string root = a.Get("--root") ?? Directory.GetCurrentDirectory();
        if (a.Get("--root") is null && !LooksLikeSuiteRoot(root))
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
                if (LooksLikeSuiteRoot(d.FullName)) { root = d.FullName; break; }
            if (!string.Equals(root, Directory.GetCurrentDirectory(), StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"(cwd is not a suite root — using '{root}' from the exe location; pass --root to override)");
        }
        Directory.SetCurrentDirectory(root);

        var cfg = LoadConfig(a);

        if (verb is "help" or "-h" or "--help") { PrintHelp(); return 0; }

        if (verb == "selftest-powenetics")
            return SelfTestPowenetics();

        if (verb == "selftest-completion")
            return await SelfTestCompletion();

        if (verb == "selftest-completion-resultfile")
            return await SelfTestCompletionResultFile();

        if (verb == "selftest-framestats")
            return SelfTestFrameStats();

        if (verb == "selftest-variant")
            return SelfTestVariant();

        if (verb == "selftest-cooler")
            return SelfTestCooler();

        if (verb == "selftest-inputlock")
            return SelfTestInputLock();

        if (verb == "selftest-powerrate")
            return SelfTestPowerRate();

        if (verb == "selftest-llm")
            return await SelfTestLlm(cfg);

        if (verb == "selftest-resilience")
            return SelfTestResilience();

        if (verb == "selftest-band")
            return SelfTestBand();

        if (verb == "selftest-ocrguard")
            return SelfTestOcrGuard();

        if (verb == "selftest-route")
            return await SelfTestRoute();

        if (verb == "selftest-inworld")
            return SelfTestInWorld();

        if (verb == "tidy")
        {
            using var tlog = new RunLogger(Path.Combine(Path.GetTempPath(), "gpusuite_tidy.log"), echoToConsole: true);
            var summary = GpuSuite.Engine.Launch.ScreenTidy.TidyAll(tlog);
            Console.WriteLine($"Screen tidy — {summary}.");
            return 0;
        }

        if (verb == "probe-powenetics")
            return ProbePowenetics(a);

        if (verb == "probe")
        {
            using var f = new MeasurementFactory(cfg);
            Console.WriteLine("Probing all measurement sources (frames / power / telemetry)...\n");
            foreach (var line in f.ProbeAll()) Console.WriteLine("  " + line);
            Console.WriteLine($"\n  Detected GPU (device under test) : {f.DetectedGpuName}");
            Console.WriteLine($"  Detected CPU                     : {f.DetectedCpuName}");
            if (!string.IsNullOrWhiteSpace(cfg.PreferredGpu))
                Console.WriteLine($"  (GPU pinned by settings.preferredGpu = \"{cfg.PreferredGpu}\")");
            return 0;
        }

        if (verb == "osd-test")
            return OsdTest(cfg, a);

        if (verb == "cooler-load")
            return await CoolerLoad(cfg, a);

        if (verb == "cooler")
            return await Cooler(cfg, a);

        if (verb == "cooler-report")
            return CoolerReport(cfg, a);

        if (verb == "fan-test")
            return FanTest(cfg, a);

        if (verb == "bot-dryrun")
            return await BotDryRun(a);

        if (verb == "vigem-check")
            return VigemCheck();

        if (verb == "doctor" || verb == "check")
            return await Doctor(cfg, a);

        if (verb == "preflight" || verb == "ready")
            return Preflight(cfg, a);

        if (verb == "llm-update")
            return await LlmUpdate(a);

        if (verb == "grab")
            return await GrabFrame(cfg, a);

        if (verb == "motion-probe")
            return await MotionProbe(cfg, a);

        if (verb == "inworld-nav")
            return await InWorldNav(cfg, a);

        if (verb == "inworld-drive")
            return await InWorldDrive(cfg, a);

        if (verb == "vision")
            return await Vision(cfg, a);

        if (verb == "vision-selftest")
            return VisionSelfTest();

        if (verb == "calibrate")
            return await Calibrate(cfg, a);

        if (verb == "gx10" || verb == "gx10-status")
            return await Gx10StatusCommand(cfg, a);

        if (verb == "display")
            return Display(cfg, a);

        if (verb == "menu-apply")
            return await MenuApply(cfg, a);

        if (verb == "send-input")
            return await SendInput(cfg, a);

        if (verb == "dump-sensors")
        {
            using var f = new MeasurementFactory(cfg);
            var dump = f.DumpSensorInventory();
            var path = Path.Combine(cfg.ResultsRoot, "sensor_inventory.txt");
            Directory.CreateDirectory(cfg.ResultsRoot);
            File.WriteAllText(path, dump);
            Console.WriteLine(dump);
            Console.WriteLine($"\nSaved sensor inventory → {path}");
            return 0;
        }

        var profileManager = new ProfileManager(cfg.ProfilesDir);
        var profiles = profileManager.LoadAll();

        if (verb == "list")
        {
            Console.WriteLine($"Benchmark list in '{cfg.ProfilesDir}':");
            foreach (var p in profiles)
            {
                int models = p.Variants.Count(v => v.Enabled);
                var mtag = p.Variants.Count > 0 ? $"  {{{models}/{p.Variants.Count} model(s)}}" : "";
                Console.WriteLine($"  [{(p.Enabled ? "x" : " ")}] {p.Id,-22} {p.Name}  [{p.Scenes.Count} scene(s)]{mtag}");
            }
            return 0;
        }

        if (verb == "variants" || verb == "settings")
        {
            var only = a.Get("--games")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Console.WriteLine("Adjustable graphics settings + variants (\"extra models\") per game:\n");
            foreach (var p in profiles.Where(p => only is null || only.Contains(p.Id, StringComparer.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"● {p.Id}  ({p.Name}){(p.Enabled ? "" : "  [disabled]")}");
                if (p.Settings.Count == 0 && p.Variants.Count == 0)
                {
                    Console.WriteLine($"    (no adjustable settings declared yet — runs at preset '{p.GraphicsPreset ?? "default"}')\n");
                    continue;
                }
                if (p.Settings.Count > 0)
                {
                    Console.WriteLine("    settings:");
                    foreach (var s in p.Settings)
                    {
                        var opts = s.Options.Count > 0 ? string.Join("/", s.Options) : "(free)";
                        Console.WriteLine($"      - {s.Key,-16} {s.Label,-22} [{opts}]  default={s.Default}  via {s.Apply.Method}");
                    }
                }
                if (p.Variants.Count > 0)
                {
                    Console.WriteLine("    models (variants):");
                    foreach (var v in p.Variants)
                    {
                        var ov = v.Settings.Count > 0 ? string.Join(", ", v.Settings.Select(kv => $"{kv.Key}={kv.Value}")) : "(profile defaults)";
                        Console.WriteLine($"      [{(v.Enabled ? "x" : " ")}] {v.Id,-14} {v.Name,-30} {ov}");
                    }
                }
                Console.WriteLine();
            }
            Console.WriteLine("Toggle a model in its profile (variants[].enabled), or run a subset with: run --variants id1,id2");
            Console.WriteLine("Resolutions are selected with --res / settings.resolutions; \"hit go\" = `gpusuite run` runs every enabled game × resolution × enabled model.");
            return 0;
        }

        if (verb == "fingerprint")
        {
            // Offline preview of the render-settings fingerprint each run records: reads the games' OWN
            // config files right now (no launch) and prints exactly what RunResult.SettingsFingerprint
            // would contain. Use it to sanity-check every number's as-set upscaler/render-res/frame-gen
            // state before (or without) a run — see GameProfile.SettingsFingerprintKeys.
            var fpOnly = a.Get("--games")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Console.WriteLine("As-set render-settings fingerprint (read live off each game's own config — what a run would record):\n");
            foreach (var p in profiles.Where(p => fpOnly is null || fpOnly.Contains(p.Id, StringComparer.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"● {p.Id}  ({p.Name})");
                if (p.SettingsFingerprintKeys.Count == 0)
                {
                    Console.WriteLine("    (no fingerprint keys — settings store unreadable externally: menu-only or unknown store; see profile notes)\n");
                    continue;
                }
                var (fpVals, fpFg) = GpuSuite.Engine.Launch.SettingsFingerprinter.Extract(p, null);
                foreach (var kv in fpVals!)
                    Console.WriteLine($"    {kv.Key,-18} = {kv.Value}");
                if (fpFg == true)
                    Console.WriteLine("    ⚠ FRAME GENERATION ON — measured fps counts generated presents, not rendered frames.");
                Console.WriteLine();
            }
            return 0;
        }

        if (verb == "catalog")
        {
            var raw = new LauncherDiscovery().DiscoverAll();
            var enriched = new CatalogService().Build(raw);

            Console.WriteLine($"Installed games discovered: {enriched.Count} (across Steam/Epic/GOG/Ubisoft/EA/Battle.net/Rockstar/Xbox + registry fallback)\n");
            Console.WriteLine(CatalogService.ToConsole(enriched));

            if (enriched.Recommended is { } rec)
            {
                Console.WriteLine($"Recommended first MVP target: {rec.Game.Name}  ({rec.Game.Store}, id '{rec.Game.GameId}')  [score {rec.Score:0}]");
                Console.WriteLine($"   exe     : {rec.Game.Exe ?? "(unresolved)"}");
                Console.WriteLine($"   process : {rec.Game.ProcessName ?? "(unknown)"}");
                Console.WriteLine($"   why     : {rec.Knowledge.Notes}");
            }
            else
            {
                Console.WriteLine("No games discovered — install at least one, or run with the relevant launcher present.");
            }

            // Persist the full table (every column) for sharing + the upcoming WPF UI to consume.
            Directory.CreateDirectory(cfg.ResultsRoot);
            var mdPath = Path.Combine(cfg.ResultsRoot, "catalog.md");
            var csvPath = Path.Combine(cfg.ResultsRoot, "catalog.csv");
            File.WriteAllText(mdPath, CatalogService.ToMarkdown(enriched));
            File.WriteAllText(csvPath, CatalogService.ToCsv(enriched));
            Console.WriteLine($"\nFull table (all columns) → {Path.GetFullPath(mdPath)}");
            Console.WriteLine($"                          → {Path.GetFullPath(csvPath)}");
            Console.WriteLine("\n(Discovery + annotation are a detection helper only — they never add games to the benchmark plan or run anything.)");
            return 0;
        }

        if (verb == "plan")
        {
            PrintPlan(ResolvePlan(profiles));
            return 0;
        }

        if (verb == "plans")
        {
            var pf = GpuSuite.Core.Config.RunPlanFile.Load(GpuSuite.Core.Config.RunPlanFile.DefaultFileName);
            if (pf.Plans.Count == 0)
            {
                Console.WriteLine($"\nNo saved run plans ({GpuSuite.Core.Config.RunPlanFile.DefaultFileName}). Save one from the App's Run tab, or author the file by hand.");
                return 0;
            }
            Console.WriteLine($"\nSaved run plans ({GpuSuite.Core.Config.RunPlanFile.DefaultFileName}):");
            foreach (var p in pf.Plans)
            {
                Console.WriteLine($"\n  {p.Name}" + (string.IsNullOrWhiteSpace(p.Description) ? "" : $" — {p.Description}"));
                Console.WriteLine($"    resolutions : {(p.Resolutions.Count > 0 ? string.Join(", ", p.Resolutions) : "(suite default)")}");
                foreach (var kv in p.Games)
                    Console.WriteLine($"    {kv.Key,-26} {(kv.Value.Count > 0 ? string.Join(", ", kv.Value) : "(enabled variants)")}");
            }
            Console.WriteLine("\nRun one with: gpusuite run --plan <name>   (works with `autonomous` too)");
            return 0;
        }

        // Autonomous full-roster mode: `autonomous` / `unattended` verb, or `run --unattended`. The full
        // roster runs unattended — a broken game is classified + skipped, the run never stops, and a failed
        // launch is RECORDED (never simulated into fake numbers). Set up below, right before the engine builds.
        bool unattended = verb is "autonomous" or "unattended" || a.Has("--unattended");
        cfg.Unattended = unattended;
        if (verb != "run" && !unattended) { Console.Error.WriteLine($"Unknown command '{verb}'."); PrintHelp(); return 1; }

        // ----- resolve the test plan (the benchmark list is authoritative; discovery only matches) -----
        var plan = ResolvePlan(profiles);
        PrintPlan(plan);

        var wanted = a.Get("--games")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // ----- saved run plan (--plan <name>): supplies the game set, per-game variant picks and the
        // resolution subset from plans.json. Explicit --games / --res still win over the plan's own. -----
        string? planResCsv = null;
        if (a.Get("--plan") is { } runPlanName)
        {
            var planFile = GpuSuite.Core.Config.RunPlanFile.Load(GpuSuite.Core.Config.RunPlanFile.DefaultFileName);
            var runPlan = planFile.Find(runPlanName);
            if (runPlan is null)
            {
                Console.Error.WriteLine($"\nNo run plan named '{runPlanName}' in {GpuSuite.Core.Config.RunPlanFile.DefaultFileName}. " +
                    (planFile.Plans.Count > 0 ? "Saved plans: " + string.Join(", ", planFile.Plans.Select(p => p.Name)) : "No plans saved yet — see `gpusuite plans`."));
                return 1;
            }
            cfg.GameVariantSelections = runPlan.Games;
            if (wanted is null || wanted.Length == 0) wanted = runPlan.Games.Keys.ToArray();
            if (runPlan.Resolutions.Count > 0) planResCsv = string.Join(',', runPlan.Resolutions);
            Console.WriteLine($"\nRun plan '{runPlan.Name}': " +
                string.Join("; ", runPlan.Games.Select(kv => $"{kv.Key}=[{(kv.Value.Count > 0 ? string.Join(",", kv.Value) : "enabled")}]")) +
                $" @ {(runPlan.Resolutions.Count > 0 ? string.Join(",", runPlan.Resolutions) : "configured resolutions")}");
        }

        List<GameProfile> games;
        if (cfg.AttachToRunning)
        {
            // --attach measures an already-running game, so the enabled/installed/valid plan filter is
            // moot — select straight from the loaded profiles. Require an explicit --games target.
            if (wanted is null || wanted.Length == 0)
            {
                Console.Error.WriteLine("\n--attach requires --games <id> — the id of the already-running game to measure.");
                return 1;
            }
            games = profiles.Where(g => wanted.Contains(g.Id, StringComparer.OrdinalIgnoreCase)).ToList();
            if (games.Count == 0)
            {
                Console.Error.WriteLine($"\n--attach: no profile matches --games {string.Join(",", wanted)}.");
                return 1;
            }
            Console.WriteLine($"\nATTACH mode — will measure already-running: {string.Join(", ", games.Select(g => g.CaptureProcessName))} (not launching; will not kill).");
        }
        else
        {
            games = plan.Runnable.Select(e => e.Game)
                .Where(g => wanted is null || wanted.Contains(g.Id, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (games.Count == 0)
            {
                Console.Error.WriteLine("\nNo runnable games (need enabled + installed + valid). See the plan above.");
                return 1;
            }
        }
        if (a.GetInt("--repeats") is { } repOverride)
            foreach (var g in games) g.Repeats = repOverride;

        var resNames = (a.Get("--res") ?? planResCsv ?? string.Join(',', cfg.Resolutions)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var resolutions = resNames.Select(Resolution.FromName).Where(r => r is not null).Cast<Resolution>().ToList();
        if (resolutions.Count == 0) { Console.Error.WriteLine("No valid resolutions. Use --res 1080p,1440p,4K."); return 1; }

        // ----- per-run scene overrides (for fast validation) -----
        ApplySceneOverrides(games, a);

        // ----- logging -----
        Directory.CreateDirectory(cfg.ResultsRoot);
        var logPath = Path.Combine(cfg.ResultsRoot, $"suite_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        using var log = new RunLogger(logPath, echoToConsole: true);
        log.Info("Start", $"GPU Test Suite — root={root}");
        var modelSel = cfg.GameVariantSelections.Count > 0
            ? "plan: " + string.Join("; ", cfg.GameVariantSelections.Select(kv => $"{kv.Key}=[{string.Join(",", kv.Value)}]"))
            : cfg.SelectedVariantIds.Count > 0 ? string.Join(",", cfg.SelectedVariantIds) : "enabled per game";
        log.Info("Start", $"Games: {string.Join(", ", games.Select(g => g.Id))} | Res: {string.Join(", ", resolutions.Select(r => r.Name))} | Models: {modelSel} | Repeats: {cfg.RepeatsPerScene}");

        // ----- autonomous full-roster setup (never-fake + banner) -----
        if (unattended)
        {
            if (cfg.AttachToRunning)
            {
                Console.Error.WriteLine("Unattended mode launches the full roster itself; it is incompatible with --attach.");
                return 1;
            }
            if (cfg.SimulateLaunch)
            {
                log.Warn("Unattended", "--no-launch is ignored in unattended mode — unattended performs REAL runs and never simulates.");
                cfg.SimulateLaunch = false;
            }
            cfg.SimulateOnLaunchFailure = false;   // NEVER fake: a failed launch is recorded + skipped, not simulated
            PrintUnattendedBanner(games, resolutions, cfg, log);
            ClearBenchDisplay(log);   // clear competing windows off the captured display so launchers/desktop can't steal foreground from a borderless game (the 2026-06-30 root cause of ACM 'NO SIGNAL' + F1/TLOU/Ratchet input/focus failures)
        }

        // ----- build engine -----
        using var factory = new MeasurementFactory(cfg);
        var validator = new RunValidator(cfg.Validation);
        var aggregator = new ResultAggregator();
        var orchestrator = new TestOrchestrator(cfg, factory, validator, aggregator, log);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); log.Warn("Abort", "Ctrl+C — cancelling after current step."); };

        // Physical input lock: during an unattended run every PHYSICAL key + mouse event is
        // swallowed so stray human input can't pollute bot navigation or the frame-time window.
        // Injected input (bot SendInput) and the ViGEm pad pass through; the suite's own commands
        // run as separate processes and are unaffected. Physical ESC is the sole abort and cancels
        // gracefully. Opt out with --no-input-lock (interactive/dev).
        PhysicalInputGuard? guard = null;
        if (cfg.SimulateLaunch)
        {
            // A --no-launch run has NOTHING to protect (no game, no measured window) — and locking anyway
            // freezes the operator's whole PC with zero on-screen cue (live incident 2026-07-02: a headless
            // smoke test swallowed physical input for 72s and read as a hung app). Never arm for simulations.
            log.Info("InputLock", "SKIPPED — simulated (--no-launch) run; physical input stays live.");
        }
        else if (!a.Has("--no-input-lock"))
        {
            // Crash/hang escape hatch: the engine pings RunHeartbeat on real progress (frames presented,
            // or the game read in the foreground during nav). The orchestrator's PER-GAME watchdog skips a
            // crashed/hung game after InputHangReleaseSeconds and continues the campaign (lock stays armed),
            // so this guard's hang-release is now only a LAST RESORT for an orchestrator that is itself stuck:
            // it fires at 3× the per-game timeout (the per-game skip pings the beacon for the next game, so a
            // normal skip never reaches this). Begin() now so the beacon is fresh before the first launch.
            int hangMs = Math.Max(0, cfg.InputHangReleaseSeconds) * 1000;
            int lastResortHangMs = hangMs > 0 ? hangMs * 3 : 0;
            GpuSuite.Core.RunHeartbeat.Begin();
            guard = PhysicalInputGuard.Arm(
                onAbort: () => { cts.Cancel(); log.Warn("Abort", "ESC — cancelling after current step."); },
                log: m => log.Warn("InputLock", m),
                isHung: lastResortHangMs > 0 ? () => GpuSuite.Core.RunHeartbeat.IsStale(lastResortHangMs) : null);
            if (guard.IsArmed)
            {
                log.Info("InputLock", $"ARMED — physical keyboard+mouse blocked; ESC aborts (graceful), ESC×3 force-unlocks. Auto-releases if a game crashes/hangs for {cfg.InputHangReleaseSeconds}s. Injected/pad input passes.");
                Console.WriteLine($"  Input lock     : ARMED — physical kb+mouse blocked; ESC aborts, ESC×3 force-unlocks, auto-release after {cfg.InputHangReleaseSeconds}s crash/hang (--no-input-lock to disable)");
            }
            else
            {
                log.Warn("InputLock", "Could not install input hooks — continuing WITHOUT the lock.");
                guard.Dispose();
                guard = null;
            }
        }

        // Resume mode (Milestone 1): --fresh/--discard start over, --resume forces resume, default auto-detects.
        var resumeMode = (a.Has("--fresh") || a.Has("--discard")) ? GpuSuite.Engine.Orchestration.ResumeMode.Fresh
                       : a.Has("--resume") ? GpuSuite.Engine.Orchestration.ResumeMode.Resume
                       : GpuSuite.Engine.Orchestration.ResumeMode.Auto;

        SuiteResult suite;
        try
        {
            // Autonomous runs checkpoint after every cell and auto-resume an interrupted run; an explicit
            // --resume/--fresh enables the same crash-safe checkpoints for a normal `run` too.
            bool checkpoint = unattended || resumeMode != GpuSuite.Engine.Orchestration.ResumeMode.Auto;
            suite = await orchestrator.RunSuiteAsync(games, resolutions, cts.Token, enableCheckpoints: checkpoint, resumeMode: resumeMode);
        }
        catch (OperationCanceledException)
        {
            log.Warn("Abort", "Run cancelled.");
            return 130;
        }
        finally
        {
            GpuSuite.Core.RunHeartbeat.End();   // stop the crash/hang beacon (no-op if never begun)
            if (guard is not null)
            {
                guard.Dispose();
                log.Info("InputLock", $"RELEASED — physical input restored (blocked keys={guard.BlockedKeys}, mouse={guard.BlockedMouse}).");
            }
            // Tidy the bench screen after every run (success, abort, or crash): close the store-launcher
            // windows we opened to the tray (sessions preserved) and dismiss leftover game-crash dialogs.
            if (cfg.CloseLaunchersWhenDone)
            {
                var tidy = GpuSuite.Engine.Launch.ScreenTidy.TidyAll(log);
                log.Info("Tidy", $"Screen tidy — {tidy}.");
            }
        }

        // Advisory GX10 failure-triage (sidecar): if the box is reachable, ask it to hypothesize WHY each failed
        // game failed. ADVISORY ONLY — it never changes the deterministic class/reason; it adds a human-reviewed
        // note to the outcome (shown in the report + roster summary). Opt out with --no-gx10-triage.
        await Gx10TriageFailures(suite, cfg, enabled: !a.Has("--no-gx10-triage"), log);

        // ----- report -----
        var report = new HtmlReportGenerator
        {
            GameNames = games.ToDictionary(g => g.Id, g => g.Name),
            SceneNames = games.SelectMany(g => g.Scenes).GroupBy(s => s.Id).ToDictionary(gr => gr.Key, gr => gr.First().Name)
        };
        suite.System.SuiteVersion = GpuSuite.BuildInfo.Version;
        suite.Unattended = unattended;
        var paths = new RunPaths(cfg.ResultsRoot, suite.GpuName);
        report.Save(paths.ReportPath, suite);
        log.Info("Report", $"HTML report → {Path.GetFullPath(paths.ReportPath)}");

        Console.WriteLine();
        Console.WriteLine($"  Overall index : {suite.OverallPerformanceIndex:0.0} (geo-mean avg FPS)");
        Console.WriteLine($"  Report        : {Path.GetFullPath(paths.ReportPath)}");
        Console.WriteLine($"  Suite JSON    : {Path.GetFullPath(paths.SuiteResultJson)}");

        // ----- final roster summary (passed / failed / skipped + reason per failed game) -----
        if (suite.GameOutcomes.Count > 0)
        {
            PrintRosterSummary(suite, log);
            var summaryPath = SaveRosterSummary(suite, cfg.ResultsRoot, suite.GpuName);
            Console.WriteLine($"  Roster summary: {Path.GetFullPath(summaryPath)}");
        }

        if (a.Has("--open")) TryOpen(paths.ReportPath);
        return 0;
    }

    /// <summary>
    /// Offline validation of the Powenetics PMD decoder: build a frame with known per-rail
    /// V/I, push it through the real frame assembler + decoder, and assert the derived rails.
    /// Proves the ported protocol is correct WITHOUT the device attached.
    /// </summary>
    /// <summary>
    /// Verify the input-lock crash/hang auto-release WITHOUT needing a game. Arms the REAL InputGuard
    /// with a progress beacon we deliberately let go stale (simulating a crashed/hung game), then asserts
    /// the guard fires the graceful abort and force-unlocks on its own within the stale+grace window —
    /// the safety net that lets the operator regain the keyboard and mouse when a game dies mid-run. The
    /// bench machine's PHYSICAL kb/mouse lock for the few seconds of the test and release automatically;
    /// injected input (a remote-desktop session) passes through the whole time. A finally-Dispose backstop
    /// guarantees input is restored even if the net under test is broken.
    /// </summary>
    private static int SelfTestInputLock()
    {
        const int staleMs = 1500;   // "no progress for 1.5s" ⇒ treat as hung (short, for the test)
        const int graceMs = 1500;   // force-unlock 1.5s after a graceful abort isn't honoured
        Console.WriteLine("Input-lock crash/hang auto-release selftest:");
        Console.WriteLine($"  Locking physical input now and simulating a hung game (no heartbeat). Expect auto-release in ~4s.");

        int aborts = 0;
        GpuSuite.Core.RunHeartbeat.Begin();   // start the beacon, then NEVER ping ⇒ it goes stale ⇒ "hung"
        var sw = System.Diagnostics.Stopwatch.StartNew();
        PhysicalInputGuard? g = null;
        try
        {
            g = PhysicalInputGuard.Arm(
                onAbort: () => System.Threading.Interlocked.Increment(ref aborts),
                log: m => Console.WriteLine($"  [guard] {m}"),
                autoUnlockAfterAbortMs: graceMs,
                isHung: () => GpuSuite.Core.RunHeartbeat.IsStale(staleMs));
            if (!g.IsArmed) { Console.WriteLine("  FAIL — could not install hooks (need an interactive session)."); return 1; }

            // Wait for the guard to self-release (its pump thread tears the hooks down). Bounded backstop.
            var deadline = TimeSpan.FromSeconds(8);
            while (g.IsArmed && sw.Elapsed < deadline) System.Threading.Thread.Sleep(50);
            sw.Stop();

            bool released = !g.IsArmed;
            bool aborted = System.Threading.Volatile.Read(ref aborts) > 0;
            Console.WriteLine($"  graceful abort fired : {(aborted ? "yes" : "NO")}");
            Console.WriteLine($"  auto-released        : {(released ? "yes" : "NO")} after {sw.Elapsed.TotalSeconds:0.0}s");
            Console.WriteLine($"  swallowed while locked: keys={g.BlockedKeys}, mouse={g.BlockedMouse}");
            bool pass = released && aborted && sw.Elapsed.TotalSeconds < (staleMs + graceMs) / 1000.0 + 3.5;
            Console.WriteLine(pass
                ? "  PASS — a crashed/hung game auto-releases the input lock; physical kb+mouse restored."
                : "  FAIL — the lock did not auto-release as expected.");
            return pass ? 0 : 1;
        }
        finally
        {
            g?.Dispose();   // backstop: restore physical input even if the net under test failed
            GpuSuite.Core.RunHeartbeat.End();
        }
    }

    /// <summary>
    /// Validate the LOCAL-LLM nav supervisor (Ollama) without a game or the GPU: feed it representative menu
    /// screens + goals and show which button it picks. The model only ever runs as a FALLBACK when the
    /// deterministic graph is lost; high agreement here = a trustworthy fallback. Runs on CPU.
    /// </summary>
    private static async Task<int> SelfTestLlm(SuiteConfig cfg)
    {
        using var log = new RunLogger(Path.Combine(Path.GetTempPath(), "gpusuite_llm.log"), echoToConsole: false);
        var sup = new GpuSuite.Engine.Automation.OllamaNavSupervisor(cfg.NavSupervisorEndpoint, cfg.NavSupervisorModel, log);
        Console.WriteLine($"LLM nav-supervisor selftest — model '{sup.Model}' via {sup.Endpoint} (CPU). Sample menu screens:");
        var cases = new[]
        {
            (screen: new[]{"WHAT'S NEW","RACE","CARS","CHALLENGE HUB","DRIVER","MEDIA","STORE","SETTINGS","EXIT TO DESKTOP"}, goal:"Open SETTINGS from this home menu; the highlight is at the top (WHAT'S NEW)", expect:"Down"),
            (screen: new[]{"PLAY","ACCESSIBILITY","SETTINGS"}, goal:"Select the highlighted PLAY to enter the game", expect:"A"),
            (screen: new[]{"WELCOME CENTER","PLAY THE CAREER","JUMP INTO MULTIPLAYER","SEE WHAT'S NEW","A Select","B Main Menu"}, goal:"Go to the main menu (the prompt shows B = Main Menu)", expect:"B"),
            (screen: new[]{"DRIVING ASSISTS","ACCESSIBILITY","GAMEPLAY AND HUD","AUDIO","DISPLAY","GRAPHICS"}, goal:"Switch to the DISPLAY tab; tabs move with the shoulder buttons and DISPLAY is to the right of the current DRIVING ASSISTS tab", expect:"RB"),
            (screen: new[]{"PAUSED","RESUME","RESTART CHECKPOINT","OPTIONS","QUIT"}, goal:"Resume gameplay from the pause menu; RESUME is highlighted", expect:"A"),
        };
        int ok = 0; var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var c in cases)
        {
            string? key;
            try { key = await sup.SuggestKeyAsync(c.screen, c.goal, default); }
            catch (Exception ex) { Console.WriteLine($"  ERROR talking to Ollama: {ex.Message}"); Console.WriteLine("  Is the Ollama server running and the model pulled? (ollama serve / ollama pull " + sup.Model + ")"); return 1; }
            bool match = string.Equals(key, c.expect, StringComparison.OrdinalIgnoreCase);
            if (match) ok++;
            Console.WriteLine($"  [{(match ? "OK  " : "diff")}] goal: {c.goal[..Math.Min(70, c.goal.Length)]}…");
            Console.WriteLine($"         LLM → '{key ?? "(no valid button)"}'   (sensible: '{c.expect}')");
        }
        Console.WriteLine($"  {ok}/{cases.Length} matched the sensible button in {sw.Elapsed.TotalSeconds:0.0}s. The supervisor runs ONLY as a fallback when the graph is lost, so 'diff' isn't necessarily wrong — but high agreement means a trustworthy fallback.");
        return ok >= cases.Length - 1 ? 0 : 1;   // allow one miss
    }

    /// <summary>
    /// Offline validation of the Powenetics rate decimation (PowerSampleHz → PowerLogIntervalMs → the serial
    /// client's KeepSample throttle). Simulates a 2 kHz PMD stream over a window and asserts the KEPT rate hits
    /// each common target (1000/500/250/125/50/10 Hz). No device, no GPU — pure logic.
    /// </summary>
    private static int SelfTestPowerRate()
    {
        const int inputHz = 2000;       // simulate a stream FASTER than the targets so decimation is exact (no fp boundary)
        const double windowSec = 4.0;
        int total = (int)(inputHz * windowSec);
        int[] targets = { 1000, 500, 250, 125, 50, 10 };
        Console.WriteLine($"Powenetics rate-decimation selftest — {inputHz} Hz simulated stream over {windowSec:0}s, assert the kept rate per PowerSampleHz:");
        bool allPass = true;
        foreach (var hz in targets)
        {
            int minIntervalMs = new GpuSuite.Core.Config.SuiteConfig { PowerSampleHz = hz }.PowerLogIntervalMs;
            double minIntervalSec = minIntervalMs / 1000.0;
            double lastKept = double.NegativeInfinity;
            int kept = 0;
            for (int i = 0; i < total; i++)
                if (GpuSuite.Measurement.Real.Powenetics.PoweneticsSerialClient.KeepSample(i / (double)inputHz, ref lastKept, minIntervalSec))
                    kept++;
            double keptHz = kept / windowSec;
            double expected = Math.Min(inputHz, hz);
            bool ok = Math.Abs(keptHz - expected) <= Math.Max(1.0, expected * 0.08);
            allPass &= ok;
            Console.WriteLine($"  PowerSampleHz {hz,4} (min {minIntervalMs,3} ms): kept {kept,5} = {keptHz,7:0.0}/s  (expect ~{expected,4:0})  [{(ok ? "PASS" : "FAIL")}]");
        }
        long perHour3 = (long)(50 * 3600 * 3);   // default 50 Hz over a 3-hour sweep, for context
        Console.WriteLine($"  Context: default 50 Hz over a 3-hour sweep ≈ {perHour3:N0} power rows (was ~{(long)(1000 * 3600 * 3):N0} at 1 kHz).");
        Console.WriteLine(allPass ? "  PASS — decimation hits every target rate." : "  FAIL.");
        return allPass ? 0 : 1;
    }

    private static int SelfTestPowenetics()
    {
        // (V, A) per channel 0..12 — see PoweneticsProtocol channel map.
        var ch = new (double v, double a)[13];
        ch[0] = (3.3, 2.0);   // ATX 3.3V  -> 6.60
        ch[1] = (5.0, 0.5);   // 5VSB      -> 2.50
        ch[2] = (12.0, 1.0);  // ATX 12V   -> 12.00
        ch[3] = (5.0, 1.0);   // ATX 5V    -> 5.00
        ch[4] = (12.0, 8.0);  // EPS1      -> 96.00
        ch[5] = (0, 0);       // 12Vsb     -> 0
        ch[6] = (0, 0);       // EPS3      -> 0
        ch[7] = (12.0, 2.0);  // EPS2      -> 24.00
        ch[8] = (0, 0);       // PCIe#3    -> 0
        ch[9] = (12.0, 10.0); // PCIe#2    -> 120.00
        ch[10] = (3.3, 0.5);  // Slot 3.3V -> 1.65
        ch[11] = (12.0, 4.0); // Slot 12V  -> 48.00
        ch[12] = (12.0, 10.0);// PCIe#1    -> 120.00

        var frame = Measurement.Real.Powenetics.PoweneticsProtocol.EncodeFrame(42, ch);

        // Prepend a junk byte to also exercise resync, then push through the assembler.
        var stream = new byte[frame.Length + 1];
        stream[0] = 0x77;
        Array.Copy(frame, 0, stream, 1, frame.Length);

        var asm = new Measurement.Real.Powenetics.PoweneticsFrameAssembler();
        Core.Models.PowerSample? sample = null;
        foreach (var payload in asm.Push(stream))
            sample = Measurement.Real.Powenetics.PoweneticsProtocol.Decode(payload, 0.0);

        if (sample is null) { Console.Error.WriteLine("FAIL: no frame decoded."); return 1; }

        (string name, double got, double want)[] checks =
        {
            ("PcieSlot12vW", sample.PcieSlot12vW ?? -1, 48.00),
            ("PcieSlot3v3W", sample.PcieSlot3v3W ?? -1, 1.65),
            ("Pcie8pin1W",   sample.Pcie8pin1W ?? -1, 120.00),
            ("Pcie8pin2W",   sample.Pcie8pin2W ?? -1, 120.00),
            ("GpuTotalW",    sample.GpuTotalW ?? -1, 289.65),
            ("CpuTotalW",    sample.CpuTotalW ?? -1, 84.00),
            ("SystemTotalW", sample.SystemTotalW ?? -1, 435.75),
        };

        bool ok = true;
        Console.WriteLine("Powenetics decoder self-test (offline):");
        foreach (var (name, got, want) in checks)
        {
            bool pass = Math.Abs(got - want) < 0.01;
            ok &= pass;
            Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}  {name,-14} got {got,9:0.###}  want {want,9:0.###}");
        }
        Console.WriteLine($"  frames parsed = {asm.FramesParsed}, malformed dropped = {asm.MalformedDropped}");
        Console.WriteLine(ok ? "\nRESULT: PASS — decoder matches the proven Powenetics math." : "\nRESULT: FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// Offline proof of log-file completion detection: a background writer emits START, then
    /// FINISH + a result line on a timer; the real CompletionDetector must detect the window and
    /// parse the game-reported FPS. No game required.
    /// </summary>
    private static async Task<int> SelfTestCompletion()
    {
        using var log = new RunLogger(null, echoToConsole: true);
        var tmp = Path.Combine(Path.GetTempPath(), $"gpusuite_bench_{Guid.NewGuid():N}.log");
        var c = new CompletionDetection
        {
            Method = "log-file",
            LogFilePath = tmp,
            StartPattern = "BENCH START",
            FinishPattern = "BENCH COMPLETE",
            ResultFpsPattern = "avg_fps=([0-9.]+)",
            MinValidSeconds = 1,
            MaxTimeoutSeconds = 15,
            FallbackToActivity = false
        };

        var writer = Task.Run(async () =>
        {
            await Task.Delay(600);
            File.AppendAllText(tmp, "loading scene...\n");
            File.AppendAllText(tmp, "BENCH START\n");
            await Task.Delay(2000);
            File.AppendAllText(tmp, "BENCH COMPLETE\navg_fps=123.4\n");
        });

        var start = DateTime.UtcNow;
        var r = await new CompletionDetector(log).DetectAsync(c, 30, start, () => 0, CancellationToken.None);
        await writer;
        try { File.Delete(tmp); } catch { }

        bool ok = r.StartDetected && r.FinishDetected
                  && r.CrossCheckFps is double f && Math.Abs(f - 123.4) < 0.01
                  && r.WindowSeconds >= 1;
        Console.WriteLine($"\n  start={r.StartDetected}@{r.StartOffsetSec:0.0}s  finish={r.FinishDetected}@{r.FinishOffsetSec:0.0}s  " +
                          $"window={r.WindowSeconds:0.0}s  crosscheck={r.CrossCheckFps?.ToString("0.0") ?? "—"} fps");
        Console.WriteLine(ok
            ? "RESULT: PASS — log-file start/finish detection + result parsing work."
            : "RESULT: FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// Offline proof of the result-file completion method (used by Cyberpunk 2077's -benchmark, whose
    /// run writes a fresh CSV to its benchmarkResults folder): a ramping synthetic frame counter drives
    /// START, then a background writer drops a new CSV into a temp dir — the real CompletionDetector must
    /// detect that new file as FINISH and parse its avg FPS. No game required.
    /// </summary>
    private static async Task<int> SelfTestCompletionResultFile()
    {
        using var log = new RunLogger(null, echoToConsole: true);
        var dir = Path.Combine(Path.GetTempPath(), $"gpusuite_bench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var c = new CompletionDetection
        {
            Method = "result-file",
            ResultDirPath = dir,
            ResultFileGlob = "*.csv",
            ResultFpsPattern = "avg_fps,([0-9.]+)",
            MinValidSeconds = 1,
            ActivityIdleSeconds = 1,
            MaxTimeoutSeconds = 20,
            FallbackToActivity = true,
            ResultGraceSeconds = 10
        };

        // Simulated live frame counter: ramps for ~1.5s, then STOPS (rendering ends → frame stall).
        int frames = 0;
        var ramp = Task.Run(async () => { for (int i = 0; i < 15; i++) { frames += 50; await Task.Delay(100); } });

        // Background "game": writes the result CSV into a fresh SUBFOLDER ~3.5s in — AFTER rendering
        // has stalled — mirroring Cyberpunk writing summary.json well after frames stop. Exercises the
        // frame-stall-window + grace-wait-for-result-file path AND recursive subfolder detection.
        var writer = Task.Run(async () =>
        {
            await Task.Delay(3500);
            var runSub = Path.Combine(dir, $"benchmark_{DateTime.Now:HHmmss}");
            Directory.CreateDirectory(runSub);
            File.WriteAllText(Path.Combine(runSub, "summary.csv"), "metric,value\navg_fps,123.4\nmin_fps,98.0\n");
        });

        var start = DateTime.UtcNow;
        var r = await new CompletionDetector(log).DetectAsync(c, 30, start, () => frames, CancellationToken.None);
        await Task.WhenAll(writer, ramp);
        try { Directory.Delete(dir, true); } catch { }

        bool ok = r.StartDetected && r.FinishDetected
                  && r.CrossCheckFps is double f && Math.Abs(f - 123.4) < 0.01
                  && r.WindowSeconds >= 1;
        Console.WriteLine($"\n  start={r.StartDetected}@{r.StartOffsetSec:0.0}s  finish={r.FinishDetected}@{r.FinishOffsetSec:0.0}s  " +
                          $"window={r.WindowSeconds:0.0}s  crosscheck={r.CrossCheckFps?.ToString("0.0") ?? "—"} fps  mode={r.Mode}");
        Console.WriteLine(ok
            ? "RESULT: PASS — result-file (new-file) finish detection + cross-check FPS parse work."
            : "RESULT: FAIL");
        return ok ? 0 : 1;
    }

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

    /// <summary>
    /// Offline self-test of the DISCOVER-THEN-REPLAY machinery (no GX10, no capture card, no bench): build a
    /// <see cref="Engine.Automation.RecordedRoute"/>, round-trip it through JSON, then REPLAY it through a DRY
    /// (non-injecting, fast-forwarded) input engine and assert the route format + replay path are sound. This proves
    /// the reproducible half of the smart-bot navigator before any of it touches the bench.
    /// </summary>
    private static async Task<int> SelfTestRoute()
    {
        using var log = new RunLogger(null, echoToConsole: true);
        Console.WriteLine("Discover-then-replay route machinery — offline self-test:\n");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string got) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}  → {got}"); if (ok) pass++; else fail++; }

        // 1) Build a representative smart route covering the full vocabulary (small holds keep the test fast).
        var route = new Engine.Automation.RecordedRoute { Note = "selftest", RecordedDevice = "pad" };
        route.Steps.Add(Engine.Automation.RouteStep.Fwd(120));
        route.Steps.Add(Engine.Automation.RouteStep.TurnBy(65));
        route.Steps.Add(Engine.Automation.RouteStep.Fwd(80));
        route.Steps.Add(Engine.Automation.RouteStep.TurnBy(-85));
        route.Steps.Add(Engine.Automation.RouteStep.Fwd(150));
        route.Steps.Add(Engine.Automation.RouteStep.BackStep(60));
        route.Steps.Add(Engine.Automation.RouteStep.StrafeL(70));
        route.Steps.Add(Engine.Automation.RouteStep.StrafeR(70));
        route.Steps.Add(Engine.Automation.RouteStep.JumpStep());

        // 2) JSON must round-trip loss-lessly AND serialize the enum by NAME (so the file is human-editable).
        var json = route.ToJson();
        var back = Engine.Automation.RecordedRoute.FromJson(json);
        Check("route JSON parses", back is not null, back is null ? "null" : $"{back.Steps.Count} steps");
        bool same = back is not null && back.Steps.Count == route.Steps.Count
            && back.Steps.Zip(route.Steps).All(p => p.First.Kind == p.Second.Kind
                && p.First.DurationMs == p.Second.DurationMs && p.First.Deg == p.Second.Deg);
        Check("round-trip is loss-less", same, same ? "identical" : "MISMATCH");
        Check("enum serialized as name", json.Contains("\"Forward\"") && json.Contains("\"Turn\"") && json.Contains("\"Back\"") && json.Contains("\"StrafeLeft\"") && json.Contains("\"Jump\""), json.Contains("\"Forward\"") ? "named" : "numeric");

        // 3) Malformed JSON is tolerated (returns null, never throws) so a bad file can't wedge a run.
        Check("malformed JSON → null", Engine.Automation.RecordedRoute.FromJson("{not json") is null, "handled");

        // 4) TotalMs = move holds (fwd/back/strafe + jump's 80) + each turn's clamped |deg|*7 ∈ [150,900] (matches TurnAsync).
        int expectMs = 120 + 80 + 150 + 60 + 70 + 70 + 80 + Math.Clamp(65 * 7, 150, 900) + Math.Clamp(85 * 7, 150, 900);
        Check("TotalMs estimate", route.TotalMs == expectMs, $"{route.TotalMs} (expect {expectMs})");

        // 5) File save→load round-trips through disk.
        var tmp = Path.Combine(Path.GetTempPath(), $"gpusuite_route_{Guid.NewGuid():N}.json");
        bool fileOk;
        try { route.Save(tmp); fileOk = Engine.Automation.RecordedRoute.Load(tmp)?.Steps.Count == route.Steps.Count; }
        catch { fileOk = false; }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        Check("file save/load round-trip", fileOk, fileOk ? "ok" : "FAILED");

        // 6) Replay through a DRY, fast-forwarded engine (no injection) must execute every step without throwing.
        var engine = new Engine.Automation.InputAutomationEngine(log, inject: false) { FastForward = true };
        bool replayOk = true;
        try { await engine.ReplayRouteAsync(back ?? route, System.Threading.CancellationToken.None); }
        catch (Exception ex) { replayOk = false; Console.WriteLine($"  [info] dry replay threw: {ex.Message}"); }
        Check("dry replay completed", replayOk, replayOk ? "ok" : "threw");

        Console.WriteLine($"\nRESULT: {(fail == 0 ? "PASS" : "FAIL")} — {pass} passed, {fail} failed.");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>Offline self-test of the Tier-1 in-world GX10 navigator's parser + prompt (no network): feed canned
    /// model replies through <c>Parse</c> and assert each maps to the right movement (incl. spaced "TURN RIGHT" not
    /// being read as a strafe), and that the prompt carries the goal + the full movement vocabulary. Proves the
    /// decision-decoding before any of it touches the box.</summary>
    private static int SelfTestInWorld()
    {
        Console.WriteLine("Tier-1 in-world GX10 navigator parse/prompt — offline self-test:\n");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string got) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}  → {got}"); if (ok) pass++; else fail++; }
        static (Engine.Automation.MoveAction? a, bool d) P(string s) => Engine.Automation.Gx10InWorldNavigator.Parse(s);

        Check("FORWARD", P("FORWARD").a == Engine.Automation.MoveAction.Forward, P("FORWARD").a?.ToString() ?? "null");
        Check("run-on 'I'd go FORWARD here'", P("I'd go FORWARD here").a == Engine.Automation.MoveAction.Forward, "ok");
        Check("TURNLEFT (one word)", P("TURNLEFT").a == Engine.Automation.MoveAction.TurnLeft, P("TURNLEFT").a?.ToString() ?? "null");
        Check("TURN RIGHT (spaced) != strafe", P("you should TURN RIGHT").a == Engine.Automation.MoveAction.TurnRight, P("you should TURN RIGHT").a?.ToString() ?? "null");
        Check("LEFT = strafe", P("LEFT").a == Engine.Automation.MoveAction.StrafeLeft, P("LEFT").a?.ToString() ?? "null");
        Check("RIGHT = strafe", P("RIGHT").a == Engine.Automation.MoveAction.StrafeRight, P("RIGHT").a?.ToString() ?? "null");
        Check("JUMP", P("JUMP").a == Engine.Automation.MoveAction.Jump, "ok");
        Check("WAIT on loading", P("WAIT - it's a loading screen").a == Engine.Automation.MoveAction.Wait, "ok");
        Check("DONE flag", P("DONE").d && P("DONE").a is null, "ok");
        Check("garbage -> no action, not done", P("the weather is nice").a is null && !P("the weather is nice").d, "ok");

        string prompt = Engine.Automation.Gx10InWorldNavigator.BuildPrompt("reach the courtyard fountain");
        Check("prompt carries the goal", prompt.Contains("reach the courtyard fountain"), "ok");
        Check("prompt lists FORWARD + TURNLEFT + DONE", prompt.Contains("FORWARD") && prompt.Contains("TURNLEFT") && prompt.Contains("DONE"), "ok");
        string hinted = Engine.Automation.Gx10InWorldNavigator.BuildPrompt("go", "NOTE: you appear STUCK — turn.");
        Check("prompt folds in the stuck hint", hinted.Contains("you appear STUCK"), "ok");

        // ---- Stuck-reflex state machine (deterministic, no device) ----
        Console.WriteLine("\nStuck-reflex (CPU frame-diff escape) — offline self-test:\n");
        // thr 0.5, 2 low samples to fire.
        var r = new Engine.Automation.StuckReflex(0.5, 2);
        Check("high motion → no force (moving fine)", !r.Observe(true, 1.2).ForceTurn, $"streak {r.LowStreak}");
        Check("1st low → arming, no force yet", !r.Observe(true, 0.1).ForceTurn, $"streak {r.LowStreak}");
        var fire1 = r.Observe(true, 0.1);
        Check("2nd low → FORCE turn", fire1.ForceTurn, fire1.ForceTurn ? $"{fire1.TurnDeg}°" : "no force");
        Check("forced turn carries a TURN hint", fire1.Hint is not null && fire1.Hint.Contains("STUCK"), "ok");
        Check("streak resets after firing", r.LowStreak == 0, $"streak {r.LowStreak}");
        r.Observe(true, 0.1);                          // streak 1 again
        var fire2 = r.Observe(true, 0.1);              // streak 2 → 2nd FORCE
        Check("2nd fire alternates direction", Math.Sign(fire2.TurnDeg) != Math.Sign(fire1.TurnDeg) && fire2.ForceTurn, $"{fire1.TurnDeg}° → {fire2.TurnDeg}°");
        Check("2nd fire escalates magnitude", Math.Abs(fire2.TurnDeg) > Math.Abs(fire1.TurnDeg), $"{Math.Abs(fire1.TurnDeg)} → {Math.Abs(fire2.TurnDeg)}");
        // sensor-unavailable must never manufacture a turn, and must not consume the streak.
        var r2 = new Engine.Automation.StuckReflex(0.5, 2);
        r2.Observe(true, 0.1);                                   // streak 1
        Check("unknown motion (−1) is a no-op", !r2.Observe(true, -1).ForceTurn && r2.LowStreak == 1, $"streak {r2.LowStreak}");
        Check("not-translating resets streak", !r2.Observe(false, 0.0).ForceTurn && r2.LowStreak == 0, $"streak {r2.LowStreak}");
        // recovery: a high sample after a low must reset, so brief dips don't accumulate to a false jam.
        var r3 = new Engine.Automation.StuckReflex(0.5, 2);
        r3.Observe(true, 0.1);
        r3.Observe(true, 1.0);                                   // recovered
        Check("high sample clears the streak", !r3.Observe(true, 0.1).ForceTurn && r3.LowStreak == 1, $"streak {r3.LowStreak}");

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
    /// Offline proof of the capture-artifact (PresentMon trace-gap) rejection in the frame-time stats:
    ///  (1) a clean 125 fps capture with ONE 385 ms seam — the hardened path excludes it so the 1% low
    ///      stays sane (~125 fps), while the un-hardened path collapses to ~2.6 fps;
    ///  (2) genuine stutter (50 × 200 ms hitches) is NOT removed — real frame-time behavior is preserved.
    /// No hardware required.
    /// </summary>
    private static int SelfTestFrameStats()
    {
        // (1) single trace-gap seam
        var s1 = BuildFrames(3000, 8.0);
        s1[1500].FrameTimeMs = 385.0;
        var hardened = Core.Stats.Statistics.ComputeFrameStats(s1);
        var unhardened = Core.Stats.Statistics.ComputeFrameStats(s1, artifactFloorMs: 1e9); // rejection off
        bool ok1 = hardened.CaptureArtifactCount == 1 && hardened.P1LowFps > 100 && hardened.AvgFps > 115
                   && unhardened.CaptureArtifactCount == 0 && unhardened.P1LowFps < 5;

        // (2) genuine stutter must survive
        var s2 = BuildFrames(3000, 8.0);
        for (int i = 0; i < 50; i++) s2[i * 60].FrameTimeMs = 200.0;
        var real = Core.Stats.Statistics.ComputeFrameStats(s2);
        bool ok2 = real.CaptureArtifactCount == 0 && real.StutterPct > 1.0;

        Console.WriteLine("Frame-time capture-artifact rejection self-test (offline):");
        Console.WriteLine($"  (1) one 385ms seam  → hardened: 1%low {hardened.P1LowFps,6:0.0} fps, avg {hardened.AvgFps,6:0.0}, seams excluded {hardened.CaptureArtifactCount}");
        Console.WriteLine($"                        un-hardened: 1%low {unhardened.P1LowFps,6:0.0} fps  (a single seam wrecks it)");
        Console.WriteLine($"  (2) 50×200ms stutter → kept as real: seams excluded {real.CaptureArtifactCount}, stutter {real.StutterPct:0.00}%");
        bool ok = ok1 && ok2;
        Console.WriteLine(ok
            ? "\nRESULT: PASS — capture seams are excluded from the lows; genuine stutter is preserved."
            : "\nRESULT: FAIL");
        return ok ? 0 : 1;
    }

    private static List<Core.Models.FrameSample> BuildFrames(int n, double ftMs)
    {
        var list = new List<Core.Models.FrameSample>(n);
        double t = 0;
        for (int i = 0; i < n; i++) { t += ftMs / 1000.0; list.Add(new Core.Models.FrameSample(t, ftMs)); }
        return list;
    }

    /// <summary>
    /// Offline proof of the graphics-variant ("extra model") settings applier against a CP2077-shaped
    /// settings file: (1) an RT+DLSS+FG variant must set RayTracing=true, DLSS=Performance, the FG string
    /// AND its bool together; (2) a raster variant must flip every one of those back; (3) a knob whose
    /// pattern matches nothing must FAIL the variant (not silently pass) — the guarantee that two models
    /// can never collapse to the same data. No game required.
    /// </summary>
    private static int SelfTestVariant()
    {
        using var log = new RunLogger(null, echoToConsole: true);
        var tmp = Path.Combine(Path.GetTempPath(), $"gpusuite_cfg_{Guid.NewGuid():N}.json");
        File.WriteAllText(tmp, """
        { "groups": [ { "options": [
          { "name": "RayTracing", "type": "bool", "value": false },
          { "name": "ResolutionScaling", "type": "string_list", "value": "DLSS", "index": 1 },
          { "name": "DLSS", "type": "string_list", "value": "Balanced", "index": 3 },
          { "name": "FrameGeneration", "type": "string_list", "value": "Off", "index": 1 },
          { "name": "DLSSFrameGen", "type": "bool", "value": false }
        ] } ] }
        """);

        SettingApply Cfg(params ConfigEdit[] edits) => new() { Method = "config-file", ConfigFilePath = tmp, Edits = edits.ToList() };
        var game = new GameProfile
        {
            Id = "selftest-variant", CaptureProcessName = "none.exe",
            Settings = new()
            {
                new GameSetting { Key = "rt", Default = "On", Apply = Cfg(
                    new ConfigEdit { Pattern = @"(""RayTracing""[^}]*?""value""\s*:\s*)(?:true|false)", Replacement = "${1}{VALUE}",
                        ValueMap = new() { ["On"] = "true", ["Off"] = "false" } }) },
                new GameSetting { Key = "upscalerQuality", Default = "Quality", Apply = Cfg(
                    new ConfigEdit { Pattern = @"(""DLSS""[^}]*?""value""\s*:\s*"")[A-Za-z]+", Replacement = "${1}{VALUE}" }) },
                new GameSetting { Key = "frameGen", Default = "Off", Apply = Cfg(
                    new ConfigEdit { Pattern = @"(""FrameGeneration""[^}]*?""value""\s*:\s*"")[A-Za-z]+", Replacement = "${1}{VALUE}",
                        ValueMap = new() { ["On"] = "On", ["Off"] = "Off" } },
                    new ConfigEdit { Pattern = @"(""DLSSFrameGen""[^}]*?""value""\s*:\s*)(?:true|false)", Replacement = "${1}{VALUE}",
                        ValueMap = new() { ["On"] = "true", ["Off"] = "false" } }) },
            },
            Variants = new()
            {
                new GameVariant { Id = "rt-dlss-fg", Settings = new() { ["rt"] = "On", ["upscalerQuality"] = "Performance", ["frameGen"] = "On" } },
                new GameVariant { Id = "raster",     Settings = new() { ["rt"] = "Off", ["upscalerQuality"] = "Quality", ["frameGen"] = "Off" } },
            }
        };

        bool M(string text, string pat) => System.Text.RegularExpressions.Regex.IsMatch(text, pat);
        var launcher = new Engine.Launch.GameLauncher(log);

        var r1 = launcher.ApplyVariant(game, game.Variants[0]);
        var a1 = File.ReadAllText(tmp);
        bool ok1 = r1.Ok
            && M(a1, @"""RayTracing""[^}]*?""value""\s*:\s*true")
            && M(a1, @"""DLSS""[^}]*?""value""\s*:\s*""Performance""")
            && M(a1, @"""FrameGeneration""[^}]*?""value""\s*:\s*""On""")
            && M(a1, @"""DLSSFrameGen""[^}]*?""value""\s*:\s*true");

        var r2 = launcher.ApplyVariant(game, game.Variants[1]);
        var a2 = File.ReadAllText(tmp);
        bool ok2 = r2.Ok
            && M(a2, @"""RayTracing""[^}]*?""value""\s*:\s*false")
            && M(a2, @"""DLSS""[^}]*?""value""\s*:\s*""Quality""")
            && M(a2, @"""FrameGeneration""[^}]*?""value""\s*:\s*""Off""")
            && M(a2, @"""DLSSFrameGen""[^}]*?""value""\s*:\s*false");

        // Negative: a knob whose pattern matches nothing must fail the variant (fail-safe, no silent collapse).
        game.Settings.Add(new GameSetting { Key = "bogus", Default = "X", Apply = Cfg(
            new ConfigEdit { Pattern = @"(""NoSuchKey""[^}]*?""value""\s*:\s*"")[A-Za-z]+", Replacement = "${1}{VALUE}" }) });
        var r3 = launcher.ApplyVariant(game, game.Variants[1]);
        bool ok3 = !r3.Ok && r3.Issues.Count > 0;

        try { File.Delete(tmp); } catch { }
        bool ok = ok1 && ok2 && ok3;
        Console.WriteLine($"\n  v1 rt-dlss-fg applied+verified={ok1}; v2 raster flipped+verified={ok2}; unmatched-knob fails-safe={ok3}");
        Console.WriteLine(ok
            ? "RESULT: PASS — variants apply + verify + flip cleanly; an unmatched knob fails the run (two models can't silently collapse)."
            : "RESULT: FAIL");
        return ok ? 0 : 1;
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

    /// <summary>
    /// Cooler-eval load selftest (the linchpin of the GPU heatsink evaluation): start the controllable
    /// GPU compute load and CLOSED-LOOP it to a target wattage using the live power provider — Powenetics
    /// on the bench, LHM GPU board power on a dev box — printing achieved W + GPU temp/util/clock. `--max`
    /// ramps to full load to find the card's sustainable power ceiling. Proves we can dial an exact heat load.
    /// </summary>
    /// <summary>
    /// Input lock for the long unattended cooler paths — same semantics as `run` (physical kb+mouse
    /// swallowed, physical ESC = graceful cancel, ESC×3 force-unlocks, --no-input-lock opts out). No
    /// heartbeat hang-release here: a cooler soak presents no frames, so a stale-beacon check would
    /// false-trip; process death still frees the hooks. Null when opted out or hooks fail (never fatal).
    /// </summary>
    private static PhysicalInputGuard? ArmCoolerInputLock(ArgMap a, CancellationTokenSource cts)
    {
        if (a.Has("--no-input-lock")) return null;
        var guard = PhysicalInputGuard.Arm(
            onAbort: () => { cts.Cancel(); Console.WriteLine("  [InputLock] ESC — cancelling."); },
            log: m => Console.WriteLine("  [InputLock] " + m),
            isHung: null);
        if (!guard.IsArmed) { guard.Dispose(); return null; }
        Console.WriteLine("  Input lock     : ARMED — physical kb+mouse blocked; ESC aborts, ESC×3 force-unlocks (--no-input-lock to disable)");
        return guard;
    }

    private static async Task<int> CoolerLoad(SuiteConfig cfg, ArgMap a)
    {
        using var factory = new MeasurementFactory(cfg);
        Console.WriteLine("Probing power / telemetry...");
        foreach (var line in factory.ProbeAll()) Console.WriteLine("  " + line);

        var power = factory.SelectPowerProvider();
        var telemetry = factory.SelectTelemetryProvider();
        Console.WriteLine($"\n  GPU under test : {factory.DetectedGpuName}");
        Console.WriteLine($"  Power source   : {power.Name} (live={power.IsLive})");
        if (!power.IsLive)
            Console.WriteLine("  ! Power is SYNTHETIC — closed-loop targeting needs a LIVE source (Powenetics or LHM board power).");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        using var inputGuard = ArmCoolerInputLock(a, cts);

        await using var powerSession = await power.StartAsync(cfg.PowerLogIntervalMs, new WorkloadHint(), cts.Token);
        await using var telSession = await telemetry.StartAsync(Math.Max(100, cfg.TelemetryIntervalMs), new WorkloadHint(), cts.Token);

        int? lockClock = a.GetInt("--lock-clock");
        ComputeSharpGpuLoad load;
        try
        {
            load = new ComputeSharpGpuLoad(string.IsNullOrWhiteSpace(cfg.PreferredGpu) ? null : cfg.PreferredGpu, lockClock);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\nFailed to create the GPU load (no D3D12 device?): {ex.Message}");
            return 1;
        }

        Console.WriteLine($"  Load device    : {load.GpuName}");
        Console.WriteLine($"  Load kernel    : {load.LoadDescription}");
        load.Start();
        if (lockClock is int lc)
            Console.WriteLine(load.ClockLocked
                ? $"  Clock lock     : LOCKED at {lc} MHz (boost hysteresis removed)"
                : $"  Clock lock     : FAILED ({lc} MHz) — needs Administrator + NVIDIA. {load.ClockLockDetail}");

        Func<double?> readWatts = () => powerSession.Latest?.GpuTotalW ?? telSession.Latest?.GpuBoardPowerW;
        var controller = new PowerController(load, readWatts);

        void PrintTick(PowerTick t)
        {
            var tel = telSession.Latest;
            Console.WriteLine($"  t={t.ElapsedSec,5:0.0}s  target={t.TargetW,4:0}W  power={(t.Watts?.ToString("0") ?? "—"),4}W  " +
                $"intensity={t.Intensity,4:0.00}  gpu={(tel?.GpuTempC?.ToString("0") ?? "—")}C  " +
                $"util={(tel?.GpuLoadPct?.ToString("0") ?? "—")}%  clk={(tel?.GpuCoreClockMhz?.ToString("0") ?? "—")}MHz");
        }

        int seconds = a.GetInt("--seconds") ?? 30;
        int rc = 0;
        try
        {
            if (a.Has("--max"))
            {
                Console.WriteLine($"\nRamping to MAX for {seconds}s (intensity = 100%)...\n");
                double peak = await controller.FindMaxAsync(TimeSpan.FromSeconds(seconds), PrintTick, cts.Token);
                Console.WriteLine($"\nRESULT: max sustained GPU power ≈ {peak:0} W.");
            }
            else
            {
                double watts = a.GetInt("--watts") ?? 150;
                Console.WriteLine($"\nDriving to {watts:0} W for {seconds}s (Ctrl+C to stop)...\n");
                var ticks = await controller.RunAsync(watts, TimeSpan.FromSeconds(seconds), PrintTick, cts.Token);
                var tail = ticks.Where(t => t.ElapsedSec >= seconds - 5 && t.Watts is > 0).Select(t => t.Watts!.Value).ToList();
                if (tail.Count > 0)
                {
                    double mean = tail.Average();
                    Console.WriteLine($"\nRESULT: target {watts:0} W → held {mean:0} W (last-5s mean, error {mean - watts:+0;-0} W).");
                }
                else
                {
                    Console.WriteLine("\nRESULT: no live power samples captured — is the power source live?");
                    rc = 1;
                }
            }
        }
        finally
        {
            load.Stop();
            load.Dispose();
        }
        return rc;
    }

    /// <summary>
    /// Phase B of the cooler evaluation: the full noise- and power-normalized sweep. For each calibrated
    /// fan speed (the operator's 25/30/35/40 dBA @ 1 m points) it holds the fan, then steps the heat load
    /// across the power axis, converging each step with the closed-loop controller and soaking until the
    /// on-die GPU temperature settles — producing a family of temp-vs-power curves + a reference-power
    /// summary, saved as a CoolerSweepResult JSON. Fan is set programmatically (GpuFanController) unless
    /// --manual-fan, in which case the operator pre-locks each fan speed and this records the RPM.
    /// </summary>
    private static async Task<int> Cooler(SuiteConfig cfg, ArgMap a)
    {
        // --- noise levels (required): "25:30,30:38,35:46,40:55" = dBA:fan% pairs (from the operator's calibration)
        var levels = ParseNoiseLevels(a.Get("--levels"));
        if (levels.Count == 0)
        {
            Console.Error.WriteLine(
                "cooler: supply --levels \"dBA:fan%,...\" (your calibrated fan settings), e.g.\n" +
                "  --levels \"25:30,30:38,35:46,40:55\"   (find each fan% with your meter once, per card)\n" +
                "Add --manual-fan if you will hold each fan speed yourself instead of letting the suite set it.");
            return 2;
        }

        double fromW = a.GetDouble("--from") ?? 80;
        double toW = a.GetDouble("--to") ?? 250;
        double stepW = a.GetDouble("--step") ?? 25;
        double refPower = a.GetDouble("--ref-power") ?? toW;
        int? lockClock = a.GetInt("--lock-clock");
        bool manualFan = a.Has("--manual-fan");
        string? pref = a.Get("--gpu") ?? (string.IsNullOrWhiteSpace(cfg.PreferredGpu) ? null : cfg.PreferredGpu);

        using var factory = new MeasurementFactory(cfg);
        Console.WriteLine("Probing power / telemetry...");
        foreach (var line in factory.ProbeAll()) Console.WriteLine("  " + line);

        var power = factory.SelectPowerProvider();
        var telemetry = factory.SelectTelemetryProvider();
        Console.WriteLine($"\n  GPU under test : {factory.DetectedGpuName}");
        Console.WriteLine($"  Power source   : {power.Name} (live={power.IsLive})");
        if (!power.IsLive)
            Console.WriteLine("  ! Power is SYNTHETIC — the heat-load targeting is only meaningful with a LIVE source (Powenetics/LHM).");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        using var inputGuard = ArmCoolerInputLock(a, cts);

        await using var powerSession = await power.StartAsync(cfg.PowerLogIntervalMs, new WorkloadHint(), cts.Token);
        await using var telSession = await telemetry.StartAsync(Math.Max(100, cfg.TelemetryIntervalMs), new WorkloadHint(), cts.Token);

        ComputeSharpGpuLoad load;
        try { load = new ComputeSharpGpuLoad(pref, lockClock); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\nFailed to create the GPU load (no D3D12 device?): {ex.Message}");
            return 1;
        }
        Console.WriteLine($"  Load device    : {load.GpuName}");

        GpuFanController? fan = null;
        if (!manualFan)
        {
            fan = new GpuFanController(pref);
            Console.WriteLine($"  Fan control    : {(fan.CanControl ? $"AVAILABLE ({fan.Vendor})" : "NOT available — " + fan.Diagnostics)}");
            if (!fan.CanControl)
            {
                Console.Error.WriteLine("  → automatic sweep refused. Re-run with --manual-fan only when an operator will hold every calibrated fan speed.");
                fan.Dispose();
                return 1;
            }
            Console.WriteLine("  Verifying fan response at 60%...");
            bool fanVerified = fan.TrySetPercentVerified(60, TimeSpan.FromSeconds(8), out string fanEvidence);
            fan.ResetAuto();
            if (!fanVerified)
            {
                Console.Error.WriteLine("  → automatic sweep refused: command was ignored — " + fanEvidence);
                fan.Dispose();
                return 1;
            }
            Console.WriteLine("  Fan verified   : " + fanEvidence);
        }
        else Console.WriteLine("  Fan control    : MANUAL (operator holds each calibrated fan speed).");

        load.Start();
        if (lockClock is int lc)
            Console.WriteLine(load.ClockLocked
                ? $"  Clock lock     : LOCKED at {lc} MHz"
                : $"  Clock lock     : FAILED ({lc} MHz) — needs Administrator + NVIDIA. {load.ClockLockDetail}");

        var opt = new CoolerSweepOptions
        {
            FromW = fromW,
            ToW = toW,
            StepW = stepW,
            ReferencePowerW = refPower,
            NoiseLevels = levels,
            MinSoak = TimeSpan.FromSeconds(a.GetDouble("--soak-min") ?? 30),
            MaxSoak = TimeSpan.FromSeconds(a.GetDouble("--soak-max") ?? 180),
            SettleWindow = TimeSpan.FromSeconds(a.GetDouble("--settle-window") ?? 45),
            SettleSlopeCPerMin = a.GetDouble("--settle-slope") ?? 0.3,
            FanNote = a.Get("--fan-note") ?? "",
            AmbientC = a.GetDouble("--ambient") ?? 0,
        };

        Console.WriteLine(
            $"\nSweep: {opt.FromW:0}→{opt.ToW:0} W step {opt.StepW:0}, ref {opt.ReferencePowerW:0} W, " +
            $"{levels.Count} noise level(s), soak {opt.MinSoak.TotalSeconds:0}-{opt.MaxSoak.TotalSeconds:0}s/step. Ctrl+C to stop.\n");

        Func<double?> readWatts = () => powerSession.Latest?.GpuTotalW ?? telSession.Latest?.GpuBoardPowerW;
        var runner = new CoolerTestRunner(load, readWatts, () => telSession.Latest, fan, Console.WriteLine);

        // throttled live readout (~ every 8 s) so a long soak shows progress without spamming
        double lastPrint = -100;
        void OnTick(PowerTick t)
        {
            if (t.ElapsedSec < lastPrint) lastPrint = -100;   // step elapsed reset → print its first tick
            if (t.ElapsedSec - lastPrint < 8) return;
            lastPrint = t.ElapsedSec;
            var tel = telSession.Latest;
            Console.WriteLine(
                $"      t={t.ElapsedSec,5:0}s  target={t.TargetW,4:0}W  power={(t.Watts?.ToString("0") ?? "—"),4}W  " +
                $"gpu={(tel?.GpuTempC?.ToString("0") ?? "—")}C  fan={(tel?.MaxFanRpm?.ToString("0") ?? "—")}rpm");
        }

        CoolerSweepResult result;
        try
        {
            result = await runner.RunAsync(opt, OnTick, cts.Token);
        }
        finally
        {
            load.Stop();
            load.Dispose();
            fan?.Dispose();
        }

        result.PowerSource = power.Name;
        if (string.IsNullOrWhiteSpace(result.Vendor) || result.Vendor == "Unknown")
            result.Vendor = InferVendor(load.GpuName);

        string outPath = a.Get("--out") ?? Path.Combine(cfg.ResultsRoot,
            $"cooler_{Sanitize(load.GpuName)}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        Json.Save(outPath, result);
        string htmlPath = Path.ChangeExtension(outPath, ".html");
        new CoolerReportGenerator().Save(htmlPath, result);

        Console.WriteLine("\n— Cooler sweep summary —");
        Console.WriteLine($"  {"dBA",4}  {"fan%",5}  {"rpm",6}  {"steps",6}   GPU@{refPower:0}W   Mem@{refPower:0}W");
        foreach (var lvl in result.Levels)
            Console.WriteLine(
                $"  {lvl.TargetDba,4:0}  {lvl.FanPercent,5:0}  {(lvl.FanRpm?.ToString("0") ?? "—"),6}  {lvl.Steps.Count,6}   " +
                $"{(lvl.RefGpuTempC?.ToString("0.0") ?? "—"),7}C   {(lvl.RefMemTempC?.ToString("0") ?? "—"),7}C");
        Console.WriteLine($"\nSaved cooler result → {outPath}");
        Console.WriteLine($"Saved cooler report → {htmlPath}");
        if (a.Has("--open")) TryOpen(htmlPath);
        if (cts.IsCancellationRequested) Console.WriteLine("(cancelled — result holds the steps completed before stop.)");
        return 0;
    }

    /// <summary>Render a saved CoolerSweepResult JSON to a self-contained HTML cooler report (offline).</summary>
    private static int CoolerReport(SuiteConfig cfg, ArgMap a)
    {
        string? inPath = a.Get("--in");
        if (string.IsNullOrWhiteSpace(inPath))
        {
            // default to the newest cooler_*.json in the results root
            var dir = cfg.ResultsRoot;
            inPath = Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "cooler_*.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
        }
        if (string.IsNullOrWhiteSpace(inPath) || !File.Exists(inPath))
        {
            Console.Error.WriteLine("cooler-report: pass --in <CoolerSweepResult.json> (or run `cooler` first to produce one).");
            return 2;
        }

        var result = Json.Load<CoolerSweepResult>(inPath);
        if (result is null) { Console.Error.WriteLine($"cooler-report: could not parse {inPath}."); return 1; }

        string outPath = a.Get("--out") ?? Path.ChangeExtension(inPath, ".html");
        new CoolerReportGenerator().Save(outPath, result);
        Console.WriteLine($"Rendered cooler report → {outPath}  ({result.Levels.Count} noise level(s), {result.Levels.Sum(l => l.Steps.Count)} steps)");
        if (a.Has("--open")) TryOpen(outPath);
        return 0;
    }

    /// <summary>
    /// Offline selftest for the cooler-eval pure core: noise-level parsing, the power axis, the settle-test
    /// least-squares slope, reference-power interpolation, and the HTML report rendering. No GPU/hardware.
    /// </summary>
    private static int SelfTestCooler()
    {
        int fail = 0;
        void Check(string name, bool ok) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); if (!ok) fail++; }
        static bool Near(double? a, double b, double eps = 1e-6) => a is double d && Math.Abs(d - b) <= eps;
        static int Count(string hay, string needle)
        {
            int n = 0, i = 0;
            while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        Console.WriteLine("Cooler offline selftest:\n");

        // 1) noise-level parsing
        var lv = CoolerLevels.Parse("25:30, 30:40 , bad, 35:200, 40:55");
        Check("levels: 4 valid pairs, malformed dropped", lv.Count == 4);
        Check("levels: fan% clamped to 100 (35:200→100)", lv.Count == 4 && Near(lv[2].FanPercent, 100));
        Check("levels: 25 dBA → 30%", lv.Count == 4 && Near(lv[0].TargetDba, 25) && Near(lv[0].FanPercent, 30));
        Check("levels: garbage → empty", CoolerLevels.Parse("nonsense").Count == 0);

        // 2) power axis
        var asc = CoolerMath.PowerAxis(100, 250, 50);
        Check("axis: ascending [100..250]/50 = 4 pts, inclusive ends",
            asc.Count == 4 && Near(asc[0], 100) && Near(asc[^1], 250));
        var desc = CoolerMath.PowerAxis(250, 100, 50);
        Check("axis: descending 250→100 starts high ends low",
            desc.Count == 4 && Near(desc[0], 250) && Near(desc[^1], 100));
        var zero = CoolerMath.PowerAxis(100, 200, 0);
        Check("axis: step≤0 falls back to 25 W", zero.Count == 5 && Near(zero[1], 125));

        // 3) settle slope (least squares, per minute)
        var rising = new List<(double, double)> { (0, 5), (10, 25), (20, 45), (30, 65) };   // y = 2t+5 → 120 °C/min
        Check("slope: rising 2/s → 120/min", Near(CoolerMath.SlopePerMinute(rising), 120, 1e-6));
        var flat = new List<(double, double)> { (0, 50), (10, 50), (20, 50) };
        Check("slope: flat → 0", Near(CoolerMath.SlopePerMinute(flat), 0, 1e-9));
        Check("slope: <3 points → null", CoolerMath.SlopePerMinute(new List<(double, double)> { (0, 1), (1, 2) }) is null);

        // 4) reference interpolation
        var pts = new List<(double, double)> { (100, 40), (200, 60) };
        Check("interp: midpoint 150 → 50", Near(CoolerMath.InterpolateAt(pts, 150), 50));
        Check("interp: clamp low 50 → 40", Near(CoolerMath.InterpolateAt(pts, 50), 40));
        Check("interp: clamp high 300 → 60", Near(CoolerMath.InterpolateAt(pts, 300), 60));
        Check("interp: empty → null", CoolerMath.InterpolateAt(new List<(double, double)>(), 150) is null);

        // 5) report rendering (with memory temps → 4 SVGs: 2 line charts + 2 bar charts)
        var withMem = SynthSweep(includeMem: true);
        string html = new CoolerReportGenerator().Generate(withMem);
        Check("report: 4 balanced SVGs", Count(html, "<svg") == 4 && Count(html, "</svg>") == 4);
        Check("report: 2 polylines per chart (4 series → 4 lines)", Count(html, "stroke-width=\"2\"") == 4);
        Check("report: axis titles present", html.Contains("Heat load (W)") && html.Contains("GPU temperature"));
        Check("report: memory chart present", html.Contains("Memory temperature vs heat load"));
        Check("report: reference bars header", html.Contains("Cooler Performance @ 250 W"));

        // …and the no-memory path → only GPU line + GPU bars (2 SVGs), no memory chart
        string htmlNoMem = new CoolerReportGenerator().Generate(SynthSweep(includeMem: false));
        Check("report (no mem): 2 SVGs only", Count(htmlNoMem, "<svg") == 2);
        Check("report (no mem): memory chart omitted", !htmlNoMem.Contains("Memory temperature vs heat load"));

        Console.WriteLine($"\n{(fail == 0 ? "RESULT: PASS — all cooler-eval offline checks passed." : $"RESULT: FAIL — {fail} check(s) failed.")}");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>Build a deterministic 2-level × 3-step sweep result for the report selftest.</summary>
    private static CoolerSweepResult SynthSweep(bool includeMem)
    {
        var result = new CoolerSweepResult
        {
            GpuName = "Synthetic GPU", Vendor = "NVIDIA", ReferencePowerW = 250,
            FanAutoControlled = true, PowerSource = "Powenetics V2", FanNote = "selftest",
        };
        (double dba, double fan, double b, double k)[] levels = { (30, 40, 39, 0.10), (40, 70, 33, 0.078) };
        double[] powers = { 100, 175, 250 };
        foreach (var (dba, fan, b, k) in levels)
        {
            var lvl = new CoolerNoiseLevel { TargetDba = dba, FanPercent = fan, FanRpm = fan * 28 };
            foreach (var p in powers)
            {
                double t = b + k * p;
                lvl.Steps.Add(new CoolerStep
                {
                    TargetW = p, AchievedW = p, GpuTempC = t, GpuHotspotC = t + 11,
                    MemTempC = includeMem ? t + 7 : null, FanRpm = fan * 28, GpuClockMhz = 1800,
                    SoakSeconds = 90, Settled = true,
                });
            }
            lvl.RefGpuTempC = b + k * 250;
            lvl.RefMemTempC = includeMem ? b + k * 250 + 7 : null;
            result.Levels.Add(lvl);
        }
        return result;
    }

    /// <summary>Parse "25:30,30:38,..." (dBA:fan%) into noise settings; skips malformed/empty entries.</summary>
    private static List<CoolerNoiseSetting> ParseNoiseLevels(string? spec) => CoolerLevels.Parse(spec);

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Replace(' ', '_');
    }

    /// <summary>Best-effort GPU vendor from the device name (used when no fan controller reported it).</summary>
    private static string InferVendor(string gpuName)
    {
        string n = gpuName.ToLowerInvariant();
        if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("rtx") || n.Contains("gtx")) return "NVIDIA";
        if (n.Contains("radeon") || n.Contains("amd") || n.Contains("rx ")) return "AMD";
        if (n.Contains("intel") || n.Contains("arc")) return "Intel";
        return "Unknown";
    }

    /// <summary>
    /// GPU fan-control selftest (for the cooler eval's noise normalization): set the GPU fan to a fixed
    /// percentage via LibreHardwareMonitor (NVAPI on NVIDIA, ADL on AMD), watch the RPM change, then
    /// restore the automatic curve. Confirms we can hold the fan at each 25/30/35/40 dBA point.
    /// </summary>
    private static int FanTest(SuiteConfig cfg, ArgMap a)
    {
        var pref = a.Get("--gpu") ?? (string.IsNullOrWhiteSpace(cfg.PreferredGpu) ? null : cfg.PreferredGpu);
        using var fan = new GpuFanController(pref);
        Console.WriteLine($"GPU            : {fan.GpuName} ({fan.Vendor})");
        Console.WriteLine($"Fan control    : {(fan.CanControl ? "AVAILABLE" : "NOT available — " + fan.Diagnostics)}");
        if (!fan.CanControl) return 1;

        int pct = a.GetInt("--pct") ?? 60;
        Console.WriteLine($"\nBaseline       : {fan.FanPercent()?.ToString("0") ?? "—"}%  /  {fan.MaxFanRpm()?.ToString("0") ?? "—"} RPM");
        Console.WriteLine($"Setting fan to {pct}% (forcing software control)...\n");
        bool verified = fan.TrySetPercentVerified(pct, TimeSpan.FromSeconds(9), out string evidence);
        Console.WriteLine("  " + evidence);

        Console.WriteLine("\nRestoring automatic fan curve...");
        fan.ResetAuto();
        System.Threading.Thread.Sleep(2000);
        Console.WriteLine($"After reset    : {fan.FanPercent()?.ToString("0") ?? "—"}%  /  {fan.MaxFanRpm()?.ToString("0") ?? "—"} RPM");
        Console.WriteLine(verified
            ? "\nRESULT: PASS — a real duty/RPM response was observed and the automatic curve was restored."
            : "\nRESULT: FAIL — the control object accepted the command but no duty/RPM response was observed; auto-fan cooler sweeps are blocked.");
        return verified ? 0 : 1;
    }

    private static TestPlan ResolvePlan(IReadOnlyList<GameProfile> benchmarkList)
        => new TestPlanResolver().Resolve(benchmarkList, new LauncherDiscovery().DiscoverAll());

    private static void PrintPlan(TestPlan plan)
    {
        Console.WriteLine("\nTest plan (benchmark list resolved against installed games):");
        Console.WriteLine($"  {"GAME",-24} {"STATUS",-13} NOTES");
        foreach (var e in plan.Entries)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = e.Status switch
            {
                BenchmarkGameStatus.Ready => ConsoleColor.Green,
                BenchmarkGameStatus.NotInstalled => ConsoleColor.Yellow,
                BenchmarkGameStatus.Invalid => ConsoleColor.Red,
                BenchmarkGameStatus.Disabled => ConsoleColor.DarkGray,
                _ => prev
            };
            Console.WriteLine($"  {e.Game.Id,-24} {e.Status,-13} {(e.Notes.Count > 0 ? e.Notes[0] : "")}");
            Console.ForegroundColor = prev;
        }
        int ready = plan.Runnable.Count();
        Console.WriteLine($"  → {ready} runnable / {plan.Entries.Count} listed.\n");
    }

    private static SuiteConfig LoadConfig(ArgMap a)
    {
        var path = a.Get("--config") ?? "settings.json";
        var cfg = Json.Load<SuiteConfig>(path) ?? new SuiteConfig();
        if (a.Get("--results") is { } r) cfg.ResultsRoot = r;
        if (a.Get("--profiles") is { } pf) cfg.ProfilesDir = pf;
        if (a.GetInt("--repeats") is { } rep) { cfg.RepeatsPerScene = rep; cfg.RepeatsOverride = rep; }   // honored EXACTLY (incl. 1) — bypasses the ≥3 floor for fast iteration
        if (a.GetInt("--cooldown") is { } cd) cfg.CooldownSeconds = cd;
        if (a.GetInt("--power-hz") is { } phz) cfg.PowerSampleHz = phz;     // power-log rate (readings/s): 1000|500|250|125|50|10 (default 50)
        if (a.Has("--force-synth")) { cfg.ForceSyntheticFrames = cfg.ForceSyntheticPower = cfg.ForceSyntheticTelemetry = true; }
        if (a.Has("--no-launch")) cfg.SimulateLaunch = true;
        if (a.Has("--attach")) { cfg.AttachToRunning = true; cfg.SimulateLaunch = false; }   // attach to an already-running game; overrides --no-launch
        if (a.Get("--frames") is { } fpv) cfg.FrameProvider = fpv;          // presentmon | rtss | auto
        if (a.Get("--variants") is { } vsel)                                // graphics "extra models" to run (by id)
            cfg.SelectedVariantIds = vsel.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (a.Get("--rtss-path") is { } rp) cfg.RtssExePath = rp;
        if (a.Has("--no-osd")) cfg.Osd = false;
        if (a.Has("--osd")) cfg.Osd = true;
        return cfg;
    }

    private static void ApplySceneOverrides(IReadOnlyList<GameProfile> games, ArgMap a)
    {
        var cap = a.GetInt("--capture");
        var warm = a.GetInt("--warmup");
        foreach (var g in games)
            foreach (var s in g.Scenes)
            {
                if (cap is { } c) s.CaptureSeconds = c;
                if (warm is { } w) s.WarmupSeconds = w;
            }
    }

    private static void TryOpen(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"GPU Test Suite CLI

Usage:
  gpusuite run     [options]      Resolve the test plan and run only enabled+installed+valid games
  gpusuite autonomous [options]   UNATTENDED full-roster mode (alias: `unattended`, or `run --unattended`).
                                  Runs the whole roster hands-off: a broken game is classified + SKIPPED and
                                  the run never stops; a failed launch is RECORDED, never faked into synthetic
                                  numbers; prints a confirm banner before starting and a pass/fail/skip summary
                                  + report.html + roster_summary.json at the end. Safe to leave the PC.
  gpusuite list                   List the benchmark game list (with enabled flag + model count)
  gpusuite variants [--games id]  Show each game's adjustable graphics settings (upscaler/RT/frame-gen/
                                  preset/…) and its defined ""extra models"" (variants), with which are
                                  enabled. `settings` is an alias.
  gpusuite fingerprint [--games]  Read each game's OWN config file NOW and print the as-set render-settings
                                  fingerprint a run would record (upscaler / render res / frame gen / RT) —
                                  flags frame-gen-inflated numbers before you quote them.
  gpusuite catalog                Discover installed games across all launchers + registry, annotate
                                  (built-in bench / automation / GPU suitability), recommend a first
                                  target, and save catalog.md + catalog.csv (detection only)
  gpusuite plan                   Show benchmark-list status (Ready/NotInstalled/Invalid/Disabled)
  gpusuite plans                  List the SAVED run plans (named setting-groups: per-game variant
                                  picks + a resolution subset, stored in plans.json). Run one with
                                  `run --plan <name>` — profiles are untouched; dormant variants stay
                                  dormant for every run that doesn't name them.
  gpusuite probe                  Probe all sources (frames/power/telemetry) and show the detected
                                  device-under-test GPU/CPU — a fast pre-run gate
  gpusuite osd-test [--seconds N] Animate a demo benchmark OSD via RTSS (verify the live overlay)
  gpusuite cooler-load --watts N [--seconds N] [--max] [--lock-clock MHz]
                                  Cooler-eval load: drive the GPU to an exact wattage with a controllable
                                  compute load + closed-loop power control (Powenetics/LHM feedback), or
                                  --max to find the sustainable power ceiling. --lock-clock pins the NVIDIA
                                  graphics clock (needs Administrator) to remove boost hysteresis for
                                  precise, steady power. The basis of the cooler test.
  gpusuite cooler  --levels ""25:30,30:38,35:46,40:55"" [--from 80 --to 250 --step 25]
                   [--ref-power 250] [--manual-fan] [--lock-clock MHz] [--gpu substr]
                   [--soak-min 30 --soak-max 180 --settle-window 45 --settle-slope 0.3]
                   [--fan-note ""…"" --ambient C --out file.json]
                                  Noise- + power-normalized COOLER SWEEP (TPU-style). For each calibrated
                                  fan speed (dBA:fan% from your one-time meter calibration) it holds the fan
                                  and steps the heat load across the power axis, soaking each step to thermal
                                  equilibrium, recording on-die GPU + memory temps → a family of temp-vs-power
                                  curves + a reference-power summary, saved as a CoolerSweepResult JSON. Fan is
                                  set programmatically (NVIDIA/AMD via LHM) unless --manual-fan. Pair --lock-clock
                                  + Powenetics on the elevated bench for precise heat-load control. Writes a
                                  matching HTML report next to the JSON (add --open to view).
  gpusuite cooler-report [--in file.json] [--out file.html] [--open]
                                  Render a saved cooler-sweep JSON to a self-contained HTML report (temp-vs-power
                                  curves + reference-power bars). Defaults --in to the newest cooler_*.json.
  gpusuite bot-dryrun --bot ID    Parse + replay a bot script (no input injected) to author/verify it
  gpusuite vigem-check            Verify the ViGEm virtual gamepad path (needed to drive RE-Engine
                                  games like RE4, which ignore injected keyboard/mouse)
  gpusuite doctor [--fix] [--model qwen2.5:7b]   (alias: check)
                                  Check this machine is ready: probes the .NET runtime, ffmpeg, PresentMon,
                                  RTSS, ViGEmBus, the local Ollama LLM + model, the capture card, the
                                  Powenetics PMD, and the profiles — each with a fix. Read-only by default;
                                  --fix auto-installs the software ones (ffmpeg / Ollama+model / ViGEmBus /
                                  .NET) via winget/ollama. The go-to command when running on a new machine.
  gpusuite llm-update [--model qwen2.5:7b]
                                  Update the local smart-bot LLM model (ollama pull — fetches newer layers,
                                  no-op if current). Schedule it for hands-off LLM auto-updates.
  gpusuite grab [--out F] [--device ""] [--warmup N] [--open]
                                  Grab one clean still frame from the HDMI capture card (Elgato 4K Pro)
                                  via ffmpeg — operator-prep vision (read menus/settings; sees
                                  exclusive-fullscreen). `gpusuite grab --list` lists capture devices.
  gpusuite vision [--read | --find ""label"" | --ocr F.png | --devices] [--png F] [--device """"]
                                  Vision-nav EYE: OCR the capture-card frame and dump each menu line with
                                  its position (--read), locate a setting + read the value beside it
                                  (--find), or OCR an existing PNG (--ocr). Calibrates per-game menu maps.
  gpusuite menu-apply --games ID [--variant ID] [--dry]
                                  Vision-nav menu APPLIER: drive a RUNNING game's graphics menu to set a
                                  variant's menu-method knobs (DLSS/RT/frame-gen) and OCR-verify each off
                                  the capture card. Needs the game's profile menuMap. `--dry` plans offline.
  gpusuite send-input --keys ""Enter,wait:800,Down,Right"" [--game ID|--pid N] [--pad]
                                  Inject a key/pad sequence into a running game (foreground-restored) — the
                                  low-level primitive for live menuMap calibration (pair with `vision --read`).
  gpusuite dump-sensors           Dump the LibreHardwareMonitor sensor inventory
  gpusuite selftest-powenetics    Validate the Powenetics PMD decoder offline
  gpusuite selftest-completion-resultfile  Validate result-file (new-file) finish detection offline
  gpusuite selftest-framestats    Validate capture-seam rejection in the frame-time stats offline
  gpusuite selftest-variant       Validate the graphics-variant settings applier offline (apply/verify/
                                  flip + fail-safe on an unmatched knob)
  gpusuite selftest-cooler        Validate the cooler-eval math offline (level parsing, power axis,
                                  settle-slope, reference interpolation) + the HTML report rendering
  gpusuite vision-selftest        Validate the vision-nav OCR read/find/value core offline (canned frame)
  gpusuite selftest-route         Validate the discover-then-replay route machinery offline (JSON round-trip +
                                  dry deterministic replay of a recorded smart route — no GX10 / capture / bench)
  gpusuite selftest-inworld       Validate the Tier-1 in-world GX10 navigator's movement parser/prompt offline
  gpusuite inworld-nav [--synth | --frame F.png] [--goal ""...""] [--count N]
                                  LIVE smoke-test: grab a capture-card frame (or --synth, or a saved --frame F.png)
                                  → ask the GX10 vision model for the next in-world MOVEMENT → print the decision
                                  + round-trip latency [ms]. Injects nothing. --frame measures real-image latency
                                  / decision quality offline against any saved game still.
  gpusuite inworld-drive --pid N | --game ID [--seconds S] [--goal ""...""] [--record PATH] [--keyboard]
                                  LIVE DRIVE: attach to a running game (in a playable scene) and let the GX10
                                  navigator STEER the character — continuous hold while it re-decides off the
                                  capture card (perception off-bench). A CPU frame-diff STUCK-REFLEX samples motion
                                  off-bench and FORCES an escape-turn when wall-grinding (navStuckThreshold; tune
                                  with `motion-probe`). Grabs before/after frames. INJECTS input.
  gpusuite selftest-resilience    Validate the reliability features offline: failure classifier, resume-state
                                  checkpoint round-trip + resume/fresh/signature decisions, hardware-check logic
  gpusuite calibrate [--game ID] [--mode calibrate|validate-route|learn|gameplay|phase5]
                                  Complete calibration plane: observe menus, validate recorded routes,
                                  learn shortest graph-path bot drafts, or validate Phase-3 gameplay from
                                  --run-result result.json. --evidence accepts scene/spawn/HUD/shader evidence;
                                  --require-visual-evidence makes it mandatory. --recording learns an OCR-gated
                                  route from a NavRecording JSON. Thresholds: --scene-min, --spawn-min,
                                  --max-late-hitches, --shader-stable-seconds. --goal selects the route goal; --route can supply
                                  a RouteRecord JSON. --live uses the REAL capture card (real
                                  screenshots + OCR to the GX10) instead of the offline stub. --guided walks
                                  YOU through each screen (you navigate, the plane observes) to build a real
                                  multi-screen graph from live frames. --drive injects each screen's executable
                                  Drive sequence so the ENGINE navigates hands-off (--drive-dry logs the planned
                                  nav without sending input). --no-gx10 forces the abstaining reasoner.
                                  Phase 5: --suite-result FILE [--baseline FILE] distils shared patterns,
                                  drafts data-driven thresholds, detects like-for-like regressions, and queues
                                  inert proposals. Review with --list-approvals; record a human decision with
                                  --approval ID --decision approve|reject (never auto-applies the change).
                                  Learned behavior stays a persisted draft until explicitly promoted.
                                  See docs/ai-calibration-architecture.md
  gpusuite gx10 [--gx10 URL] [--model ID] [--describe]   (alias: gx10-status)
                                  Connect to the lab GX10 / GB10 box and print its live statistics (version,
                                  latency, installed + loaded models with VRAM) so you can confirm it before
                                  testing. Read-only. Exit 0 = online, 1 = offline. Pick GPU vs GX10 for the
                                  vision model with settings.visionCompute (auto|gpu|gx10).
                                  --describe grabs ONE live capture-card frame and asks the GX10 vision model
                                  what is on screen (real screenshot → structured/consensus/OCR-capped answer).
  gpusuite probe-powenetics [--port COMx]  Probe serial port(s) for a live Powenetics V2 device
  gpusuite help

Options:
  --games a,b          Only run these profile ids (default: all)
  --res 1080p,1440p,4K Resolutions to test (default: from settings)
  --variants a,b       Only run these graphics ""extra models"" (variant ids), across every game that
                       defines them (overrides each variant's enabled flag). Default: each game's
                       enabled variants. See `gpusuite variants`.
  --plan NAME          Run a SAVED run plan (named setting-group from plans.json): its game set,
                       per-game variant picks, and resolution subset. Explicit --games/--res still
                       override the plan's own. See `gpusuite plans`.
  --repeats N          Repeats per scene/resolution (default: 3)
  --capture N          Override capture seconds per scene (fast validation)
  --warmup N           Override warmup seconds per scene
  --cooldown N         Inter-run cooldown seconds
  --force-synth        Force synthetic data for all three sources
  --no-launch          Never start the game (SIMULATED launch) — validate a profile end-to-end
  --attach             Measure an ALREADY-RUNNING game instead of launching it (requires --games).
                       The operator preps the game (login, save loaded, in a scene); the suite
                       attaches by capture process name and never kills it. Best for scripted-scene
                       games and friction-heavy ports. Pair with a single --res set in-game.
  --no-input-lock      Don't lock the physical keyboard+mouse during the run. By default a run
                       swallows ALL physical kb+mouse input (bot/injected input + the ViGEm pad
                       still pass) so stray human input can't corrupt nav or the frame window;
                       physical ESC is the only abort (graceful cancel). Use this for interactive
                       or dev runs where you need to drive the machine by hand.
  --profiles DIR       Profiles directory (default: profiles)
  --results DIR        Results output directory (default: Results)
  --config FILE        Settings JSON (default: settings.json)
  --root DIR           Working root (default: current dir)
  --open               Open the HTML report when done");
    }
}

/// <summary>Tiny argument parser: --key value or --flag.</summary>
internal sealed class ArgMap
{
    private readonly string[] _a;
    public ArgMap(string[] a) => _a = a;

    public bool Has(string name) => _a.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));

    public string? Get(string name)
    {
        for (int i = 0; i < _a.Length - 1; i++)
            if (string.Equals(_a[i], name, StringComparison.OrdinalIgnoreCase)) return _a[i + 1];
        return null;
    }

    public int? GetInt(string name) => int.TryParse(Get(name), out var v) ? v : null;

    public double? GetDouble(string name) =>
        double.TryParse(Get(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
}
