using GpuSuite.Core.Config;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Models;
using GpuSuite.Core.Stats;
using GpuSuite.Engine.Orchestration;
using GpuSuite.Engine.Validation;

namespace GpuSuite.Cli;

internal static partial class Program
{
    /// <summary>
    /// Offline self-test for the laboratory-grade reliability features (Milestones 1-3): the failure
    /// classifier, the resume-state manager (checkpoint round-trip + resume/fresh decisions + plan-signature
    /// guard), and the hardware-validation result logic. Deterministic; no hardware or games required.
    /// </summary>
    private static int SelfTestResilience()
    {
        int fail = 0;
        void Check(bool cond, string name)
        {
            Console.ForegroundColor = cond ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}");
            Console.ResetColor();
            if (!cond) fail++;
        }

        Console.WriteLine("Resilience self-test (failure classifier + resume manager + hardware validation):\n");

        // ---- 1. Failure classifier ----
        Check(FailureClassifier.FromIssue("Game did not launch and was not simulated.") == FailureClass.LaunchFailure, "classify: launch failure");
        Check(FailureClassifier.FromIssue("runtime health: gpu-idle — GPU load ~3%") == FailureClass.RuntimeHealth, "classify: runtime health (before frozen/idle)");
        Check(FailureClassifier.FromIssue("FPS capture produced no frames.") == FailureClass.NoFrames, "classify: no frames");
        Check(FailureClassifier.FromIssue("Average FPS implausibly low (2.0 < 5).") == FailureClass.InvalidFps, "classify: invalid FPS");
        Check(FailureClassifier.FromIssue("GPU idle during the measured window (avg load 13%)") == FailureClass.FrozenCapture, "classify: frozen capture");
        Check(FailureClassifier.FromIssue("Stutter 40% exceeds 25% (frame-time behavior broken).") == FailureClass.ShaderHitch, "classify: shader/hitch");
        Check(FailureClassifier.FromIssue("Run threw: NullReferenceException") == FailureClass.Crash, "classify: crash");
        Check(FailureClassifier.FromIssues(new[] { "Telemetry missing/insufficient (0 < 20).", "FPS capture produced no frames." }).Class == FailureClass.NoFrames, "classify: first concrete class wins over Other");

        // ---- 2. Resume state manager ----
        var dir = Path.Combine(Path.GetTempPath(), "gpusuite_resilience_selftest");
        Directory.CreateDirectory(dir);
        var statePath = Path.Combine(dir, "run_state.json");
        if (File.Exists(statePath)) File.Delete(statePath);
        using var log = new RunLogger(null, echoToConsole: false);

        var sigA = RunStateManager.ComputePlanSignature(new[] { "g" }, new[] { "1080p", "1440p" }, Array.Empty<string>(), 3);
        var sigA2 = RunStateManager.ComputePlanSignature(new[] { "g" }, new[] { "1440p", "1080p" }, Array.Empty<string>(), 3);
        var sigB = RunStateManager.ComputePlanSignature(new[] { "g" }, new[] { "1080p" }, Array.Empty<string>(), 3);
        Check(sigA == sigA2, "plan signature: order-independent + deterministic");
        Check(sigA != sigB, "plan signature: changes with the plan");

        var key = RunStateManager.Cell("g", "s", null, "1080p");
        Check(key == "g|s|default|1080p", "cell key format");

        // fresh run → mark a cell done → checkpoint written
        var m1 = new RunStateManager(statePath, log);
        m1.Initialize(ResumeMode.Fresh, "GPU-X", sigA, true);
        Check(!m1.Resuming && File.Exists(statePath), "fresh: not resuming + checkpoint written");
        m1.MarkCellDone(key, "g", "s", null, "1080p", new SceneResolutionAggregate { ValidRuns = 3, TotalRuns = 3, AvgFps = 100 });
        Check(m1.IsCellDone(key), "checkpoint: cell marked done");

        // a NEW manager on the same (incomplete) state with the same signature → RESUMES + sees the done cell
        var m2 = new RunStateManager(statePath, log);
        m2.Initialize(ResumeMode.Auto, "GPU-X", sigA, true);
        Check(m2.Resuming && m2.IsCellDone(key), "auto-detect: resumes the interrupted run + reloads the done cell");

        // complete it → a later run does NOT resume a completed run
        m2.Complete();
        var m3 = new RunStateManager(statePath, log);
        m3.Initialize(ResumeMode.Auto, "GPU-X", sigA, true);
        Check(!m3.Resuming, "completed run is not auto-resumed");

        // plan changed (signature mismatch) on an incomplete run → start fresh, do not resume
        File.Delete(statePath);
        var m4 = new RunStateManager(statePath, log);
        m4.Initialize(ResumeMode.Fresh, "GPU-X", sigA, true);
        m4.MarkCellDone(key, "g", "s", null, "1080p", new SceneResolutionAggregate { ValidRuns = 3, TotalRuns = 3 });
        var m5 = new RunStateManager(statePath, log);
        m5.Initialize(ResumeMode.Resume, "GPU-X", sigB, true);   // different plan
        Check(!m5.Resuming, "plan-signature mismatch: does NOT resume a stale run");

        try { Directory.Delete(dir, true); } catch { }

        // ---- 3. Hardware validation result ----
        var okResult = new HardwareValidationResult();
        okResult.Checks.Add(HardwareCheck.Pass("GPU present", "RTX"));
        okResult.Checks.Add(HardwareCheck.Fail("PCIe", "x8", fatal: false));   // non-fatal warning
        Check(okResult.Ok, "hardware result: non-fatal warning does not abort");

        var badResult = new HardwareValidationResult();
        badResult.Checks.Add(HardwareCheck.Pass("GPU present", "RTX"));
        badResult.Checks.Add(HardwareCheck.Fail("VRAM", "8 GB < 16 GB", fatal: true));
        Check(!badResult.Ok && badResult.FailureReason.Contains("VRAM"), "hardware result: a fatal failure aborts + reports the reason");

        // ---- 4. Frame-gen-aware stutter validation (task #185) ----
        // Frame generation deflates the mean frame time (it inserts cheap generated presents), so a rendered
        // frame at the HIGH end of its perfectly-normal jitter becomes a large multiple of that deflated mean,
        // and the naive 2x-mean metric flags a big fixed fraction of a SMOOTH FG capture (CP FG: 138 fps, 0.2%
        // game cross-check, yet ~25-33% "global" stutter → the run was wrongly rejected). The cadence-robust
        // metric must clear it, the validator must gate an FG-flagged run on that value WITHOUT touching the
        // non-FG path, and the FG-agnostic hard gates must still reject a truly broken FG capture.
        {
            var noPower = new List<PowerSample>(); var noTel = new List<TelemetrySample>();
            var t185 = new ValidationThresholds
            { MinFrameCount = 1, MinCaptureSeconds = 0, MinPowerSamples = 0, MinTelemetrySamples = 0, MinGpuLoadPct = 0 };
            var validator = new RunValidator(t185);

            // A perfectly smooth MFG capture: two cheap generated presents (~0.5ms) + one rendered frame that
            // carries its natural 12-18ms jitter, repeating. No hitches whatsoever.
            var jitter = new[] { 12.0, 18.0, 14.0, 16.0 };   // natural rendered spread (mean 15) — NOT stutter
            var smooth = new List<FrameSample>(); double t = 0;
            for (int i = 0; i < 600; i++)
            {
                double ftMs = (i % 3 == 2) ? jitter[(i / 3) % jitter.Length] : 0.5;   // g, g, r cadence
                t += ftMs / 1000.0;
                smooth.Add(new FrameSample { TimeSec = t, FrameTimeMs = ftMs });
            }
            var smoothStats = Statistics.ComputeFrameStats(smooth);
            Check(smoothStats.StutterPct > 25.0, "FG cadence trips the naive global stutter metric (reproduces the false positive)");
            Check(smoothStats.FrameGenStutterPct < 2.0, "cadence-robust stutter clears a perfectly smooth FG capture");

            var runFg = new RunResult { FrameGenActive = true, Frames = smoothStats, FrameSource = DataSourceMode.Live };
            validator.ValidateSingle(runFg, smooth, noPower, noTel, true);
            Check(runFg.Verdict == RunVerdict.Valid, "FG-flagged smooth run PASSES the stutter gate (unblocks CP rt-dlss-fg)");

            var runNoFg = new RunResult { FrameGenActive = false, Frames = smoothStats, FrameSource = DataSourceMode.Live };
            validator.ValidateSingle(runNoFg, smooth, noPower, noTel, true);
            Check(runNoFg.Verdict == RunVerdict.Invalid, "the SAME series without the FG flag still fails on global stutter (non-FG path unchanged)");

            // A truly broken FG capture (a multi-second hard hitch) is STILL rejected even when FG-flagged: the
            // hard single-frame-time / capture-gap / GPU-idle gates stay FG-agnostic, so relaxing only the
            // stutter metric never blanket-passes a broken run.
            var broken = new List<FrameSample>(smooth);
            double tb = broken[^1].TimeSec + 2.5;
            broken.Add(new FrameSample { TimeSec = tb, FrameTimeMs = 2500.0 });   // 2.5s hard hitch
            var brokenStats = Statistics.ComputeFrameStats(broken);
            var runBrokenFg = new RunResult { FrameGenActive = true, Frames = brokenStats, FrameSource = DataSourceMode.Live };
            validator.ValidateSingle(runBrokenFg, broken, noPower, noTel, true);
            Check(runBrokenFg.Verdict == RunVerdict.Invalid, "a truly broken FG capture (hard hitch) is STILL rejected by the FG-agnostic hard gates");
        }

        Console.WriteLine();
        if (fail == 0) Console.WriteLine("RESULT: PASS — all resilience checks passed.");
        else { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"RESULT: FAIL — {fail} check(s) failed."); Console.ResetColor(); }
        return fail == 0 ? 0 : 1;
    }
}
