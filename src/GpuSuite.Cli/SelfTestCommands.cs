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

    /// <summary>
    /// Offline validation of the Powenetics PMD decoder: build a frame with known per-rail
    /// V/I, push it through the real frame assembler + decoder, and assert the derived rails.
    /// Proves the ported protocol is correct WITHOUT the device attached.
    /// </summary>
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
}
