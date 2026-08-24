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

        if (verb == "compare")
            return Compare(a);

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
  gpusuite compare --base OLD\\suite_result.json --target NEW\\suite_result.json [--out FILE] [--open]
                                  Compare two saved suite results (typically two GPUs, or the same GPU
                                  before/after a driver change): per-cell FPS/1%-low deltas, watts only
                                  where power provenance matches on both sides, FG-flagged rows, and an
                                  unmatched-cells section — nothing is silently dropped.
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
