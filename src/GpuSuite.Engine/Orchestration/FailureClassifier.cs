using GpuSuite.Core.Models;

namespace GpuSuite.Engine.Orchestration;

/// <summary>
/// Maps a run's validation-issue text (and crash/watchdog reasons) to a clear <see cref="FailureClass"/>,
/// so an unattended full-roster run can tell the operator WHY each failed game failed — launch failure vs.
/// crash vs. frozen capture vs. no frames vs. invalid FPS vs. menu-nav vs. gameplay-gate vs. shader hitch
/// vs. launcher-not-ready. Pure string matching over the exact reasons the RunValidator / orchestrator emit;
/// the raw reason text is always preserved alongside the class, so an "Other" still carries its detail.
/// </summary>
public static class FailureClassifier
{
    /// <summary>Classify a single reason/issue string. Most-specific patterns first.</summary>
    public static FailureClass FromIssue(string issue)
    {
        if (string.IsNullOrWhiteSpace(issue)) return FailureClass.Other;
        var s = issue.ToLowerInvariant();

        // runtime health watchdog (check first — its text contains "frozen"/"idle" substrings)
        if (s.Contains("runtime health")) return FailureClass.RuntimeHealth;

        // launcher / sign-in not ready (check before the generic launch-failure phrase)
        if (s.Contains("sign in") || s.Contains("sign-in") || s.Contains("signing in") ||
            s.Contains("launcher not ready") || s.Contains("authentication") || s.Contains("attempting to sign"))
            return FailureClass.LauncherNotReady;

        // launch failure
        if (s.Contains("did not launch") || s.Contains("launch failed") || s.Contains("did not appear") ||
            s.Contains("not detected") || s.Contains("no launch uri") || s.Contains("exe not found") || s.Contains("no aumid"))
            return FailureClass.LaunchFailure;

        // crash / threw
        if (s.Contains("run threw") || s.Contains("threw:") || s.Contains("crashed") || s.Contains("access violation") || s.Contains("exited"))
            return FailureClass.Crash;

        // settings / menu navigation
        if (s.Contains("menumap") || s.Contains("menu setting") || s.Contains("vision-nav") || s.Contains("vision nav") ||
            s.Contains("resolution apply") || s.Contains("variant settings apply") || s.Contains("could not set") ||
            s.Contains("not benchmarked") || s.Contains("nav") || s.Contains("menu"))
            return FailureClass.MenuNavFailure;

        // gameplay-band gate
        if (s.Contains("gameplay band") || s.Contains("gameplay-band") || s.Contains("band never") || s.Contains("never settled"))
            return FailureClass.GameplayGateFailure;

        // frozen / non-rendering capture
        if (s.Contains("frozen") || s.Contains("idle during") || s.Contains("nearly identical") ||
            s.Contains("non-gameplay") || s.Contains("paused"))
            return FailureClass.FrozenCapture;

        // shader-compile / stutter instability
        if (s.Contains("stutter") || s.Contains("hitch"))
            return FailureClass.ShaderHitch;

        // capture discontinuity (a trace gap) — treat as a frozen/broken capture
        if (s.Contains("capture gap") || s.Contains("discontinuity") || s.Contains("seam"))
            return FailureClass.FrozenCapture;

        // no / too few frames
        if (s.Contains("no frames") || s.Contains("too few frames") || s.Contains("capture too short"))
            return FailureClass.NoFrames;

        // implausible / outlier FPS
        if (s.Contains("implausibly low") || s.Contains("implausible") || s.Contains("deviates"))
            return FailureClass.InvalidFps;

        // data-source shortfalls that invalidate but aren't a game-failure mode
        if (s.Contains("powenetics") || s.Contains("telemetry"))
            return FailureClass.Other;

        return FailureClass.Other;
    }

    /// <summary>
    /// Pick the dominant class from a run's issues: prefer a concrete game-failure class over "Other", and
    /// take the first concrete one in priority order. Returns the class plus the reason string that produced it.
    /// </summary>
    public static (FailureClass Class, string Reason) FromIssues(IReadOnlyList<string> issues)
    {
        if (issues is null || issues.Count == 0) return (FailureClass.Other, "no valid runs (no reason recorded)");
        FailureClass best = FailureClass.Other;
        string bestReason = issues[0];
        foreach (var issue in issues)
        {
            var c = FromIssue(issue);
            if (c != FailureClass.Other) { return (c, issue); }   // first concrete class wins
        }
        return (best, bestReason);
    }

    /// <summary>Short human label for a class (for the console summary + report).</summary>
    public static string Label(FailureClass c) => c switch
    {
        FailureClass.None => "ok",
        FailureClass.LaunchFailure => "launch failure",
        FailureClass.Crash => "crash",
        FailureClass.FrozenCapture => "frozen capture",
        FailureClass.NoFrames => "no frames",
        FailureClass.InvalidFps => "invalid FPS",
        FailureClass.MenuNavFailure => "menu navigation failure",
        FailureClass.GameplayGateFailure => "gameplay gate failure",
        FailureClass.ShaderHitch => "shader/hitch instability",
        FailureClass.LauncherNotReady => "launcher not ready",
        FailureClass.HardwarePrecheck => "hardware validation failed",
        FailureClass.RuntimeHealth => "runtime health failure",
        _ => "other"
    };
}
