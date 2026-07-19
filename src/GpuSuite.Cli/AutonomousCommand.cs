using GpuSuite.Core.Config;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Io;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Orchestration;

namespace GpuSuite.Cli;

internal static partial class Program
{
    private const int BannerWidth = 72;

    /// <summary>
    /// Minimize EVERY window so the captured display (the Elgato) shows ONLY the game during an unattended run.
    /// ROOT-CAUSE FIX (2026-06-30): competing windows on the bench display — the terminal/agent window, the EA
    /// Desktop / Ubisoft Connect launchers, Settings — stole foreground from borderless games, which made ACM
    /// re-present a "NO SIGNAL" / wrong display mode, F1's cold launch never come foreground (false 'crash'), and
    /// TLOU/Ratchet's injected input land on the wrong window. Clearing the desktop once at run start removes the
    /// persistent competitors; per-game EnsureGameForeground then handles the transient launcher pop. Best-effort
    /// (Shell.Application.MinimizeAll via late binding); any failure is logged and ignored. Reversible — the
    /// operator just restores windows afterward.
    /// </summary>
    private static void ClearBenchDisplay(RunLogger log)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) { log.Trace("Display", "ClearBenchDisplay: Shell.Application unavailable — skipped."); return; }
            object? shell = Activator.CreateInstance(shellType);
            shellType.InvokeMember("MinimizeAll", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
            log.Info("Display", "Bench display cleared — all windows minimized so launcher/desktop windows can't steal foreground from the captured (borderless) game.");
        }
        catch (Exception ex) { log.Trace("Display", $"ClearBenchDisplay skipped: {ex.Message}"); }
    }

    /// <summary>
    /// The unmistakable "you can leave now" banner printed BEFORE an unattended full-roster run, so the
    /// operator can confirm at a glance that it is in autonomous mode (never-fake, skip-on-failure,
    /// input-locked, ESC-abort, report on completion) before walking away.
    /// </summary>
    private static void PrintUnattendedBanner(IReadOnlyList<GameProfile> games, IReadOnlyList<Resolution> res, SuiteConfig cfg, RunLogger log)
    {
        string bar = new string('═', BannerWidth);
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine();
        Console.WriteLine(bar);
        Console.WriteLine("  UNATTENDED FULL-ROSTER MODE — SAFE TO LEAVE THE PC");
        Console.WriteLine(bar);
        Console.ForegroundColor = prev;
        Console.WriteLine($"  Games ({games.Count,2})   : {string.Join(", ", games.Select(g => g.Id))}");
        Console.WriteLine($"  Resolutions  : {string.Join(", ", res.Select(r => r.Name))}");
        Console.WriteLine($"  Continue     : one broken game NEVER stops the run — it is classified + skipped");
        Console.WriteLine($"  Never fake   : a failed launch is RECORDED + skipped, never turned into fake numbers");
        Console.WriteLine($"  On failure   : retry only SAFE deterministic actions, then skip → next game");
        Console.WriteLine($"  Crash-safe   : checkpoints after every benchmark — auto-resumes an interrupted run");
        Console.WriteLine($"                 (force with --resume, discard with --fresh)");
        Console.WriteLine($"  Input lock   : physical keyboard+mouse blocked; physical ESC is the ONLY abort");
        Console.WriteLine($"  Output       : {Path.GetFullPath(cfg.ResultsRoot)}  (logs, per-run data, screenshots, report.html)");
        Console.WriteLine($"  On finish    : full HTML report + pass/fail/skip summary — no PC interaction needed");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(bar);
        Console.WriteLine("  Starting the unattended run now. You can leave — it will run to the end.");
        Console.WriteLine(bar);
        Console.ForegroundColor = prev;
        Console.WriteLine();

        log.Info("Unattended", $"AUTONOMOUS FULL-ROSTER MODE armed — {games.Count} games × {res.Count} resolution(s); never-fake ON; skip-broken ON.");
    }

    /// <summary>
    /// The final roster summary: passed / failed / skipped games, the failure class + reason per failed
    /// game, and whether the full roster completed. Printed to the console + mirrored to the log.
    /// </summary>
    private static void PrintRosterSummary(SuiteResult suite, RunLogger log)
    {
        string bar = new string('═', BannerWidth);
        var outcomes = suite.GameOutcomes;
        var passed = outcomes.Where(o => o.Status == GameStatus.Passed).ToList();
        var failed = outcomes.Where(o => o.Status == GameStatus.Failed).ToList();
        var skipped = outcomes.Where(o => o.Status == GameStatus.Skipped).ToList();

        var prev = Console.ForegroundColor;
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(bar);
        Console.WriteLine("  UNATTENDED ROSTER SUMMARY");
        Console.WriteLine(bar);
        Console.ForegroundColor = prev;

        Console.Write("  Full roster completed : ");
        Console.ForegroundColor = suite.RosterCompleted ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine(suite.RosterCompleted ? $"YES — all {outcomes.Count} games attempted" : "NO — campaign aborted early (ESC)");
        Console.ForegroundColor = prev;

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  Passed  ({passed.Count,2}) : {(passed.Count == 0 ? "(none)" : string.Join(", ", passed.Select(o => o.GameId)))}");
        Console.ForegroundColor = prev;

        Console.ForegroundColor = failed.Count > 0 ? ConsoleColor.Red : prev;
        Console.WriteLine($"  Failed  ({failed.Count,2}) :{(failed.Count == 0 ? " (none)" : "")}");
        foreach (var o in failed)
        {
            Console.WriteLine($"       {o.GameId,-26} [{FailureClassifier.Label(o.FailureClass)}]  {Trim(o.Reason, 80)}");
            if (!string.IsNullOrWhiteSpace(o.AdvisoryCause))
                Console.WriteLine($"           ↳ GX10 (advisory): {Trim(o.AdvisoryCause!, 74)} [{o.AdvisoryConfidence}]");
        }
        Console.ForegroundColor = prev;

        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine($"  Skipped ({skipped.Count,2}) :{(skipped.Count == 0 ? " (none)" : "")}");
        foreach (var o in skipped)
            Console.WriteLine($"       {o.GameId,-26} {Trim(o.Reason, 80)}");
        Console.ForegroundColor = prev;

        int validRuns = suite.Aggregates.Sum(a => a.ValidRuns);
        Console.WriteLine($"  Valid results preserved: {validRuns} run(s) across {passed.Count} game(s). No numbers were faked.");
        if (!string.IsNullOrWhiteSpace(suite.VisionCompute))
            Console.WriteLine($"  Vision compute        : {suite.VisionCompute} (auto-selected; unloaded before each measured window).");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(bar);
        Console.ForegroundColor = prev;

        log.Info("Summary", $"Roster {(suite.RosterCompleted ? "COMPLETED" : "ABORTED")} — {passed.Count} passed, {failed.Count} failed, {skipped.Count} skipped.");
        foreach (var o in failed)
            log.Warn("Summary", $"FAILED {o.GameId} [{FailureClassifier.Label(o.FailureClass)}]: {o.Reason}");
    }

    /// <summary>Persist the roster summary as JSON next to the report (machine-readable for later tooling).</summary>
    private static string SaveRosterSummary(SuiteResult suite, string resultsRoot, string gpuName)
    {
        var path = Path.Combine(new RunPaths(resultsRoot, gpuName).GpuDir, "roster_summary.json");
        var summary = new
        {
            generatedUtc = suite.GeneratedUtc,
            gpu = suite.GpuName,
            unattended = suite.Unattended,
            rosterCompleted = suite.RosterCompleted,
            visionCompute = suite.VisionCompute,
            passed = suite.GameOutcomes.Count(o => o.Status == GameStatus.Passed),
            failed = suite.GameOutcomes.Count(o => o.Status == GameStatus.Failed),
            skipped = suite.GameOutcomes.Count(o => o.Status == GameStatus.Skipped),
            overallIndex = suite.OverallPerformanceIndex,
            games = suite.GameOutcomes.Select(o =>
            {
                // Render-settings fingerprint of this game's runs (first run that recorded one): the as-set
                // upscaler / render-res / frame-gen state behind the number — see RunResult.SettingsFingerprint.
                var fpRun = suite.Aggregates.Where(ag => ag.GameId == o.GameId).SelectMany(ag => ag.Runs)
                    .FirstOrDefault(r => r.SettingsFingerprint is { Count: > 0 });
                return new
                {
                    o.GameId, o.Name,
                    status = o.Status.ToString(),
                    failureClass = o.FailureClass.ToString(),
                    failureLabel = FailureClassifier.Label(o.FailureClass),
                    o.Reason, o.ValidRuns, o.TotalRuns, o.Variants,
                    settingsFingerprint = fpRun?.SettingsFingerprint,
                    frameGenActive = fpRun?.FrameGenActive,    // true ⇒ fps counts generated presents
                    advisoryCause = o.AdvisoryCause,           // GX10 hypothesis (advisory, human-approved)
                    advisoryConfidence = o.AdvisoryConfidence
                };
            }).ToList()
        };
        Json.Save(path, summary);
        return path;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}
