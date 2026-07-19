using System.Text.RegularExpressions;
using GpuSuite.Engine.Vision;

namespace GpuSuite.Engine.Diagnostics;

public enum SmokeFrameVerdict { Clear, Warning, Blocked }

public sealed record SmokeFrameFinding(SmokeFrameVerdict Verdict, string Detail);

/// <summary>Conservative OCR classification for the launch-only roster smoke test. Only explicit,
/// actionable launcher/game prompts block; ordinary menu words such as "account" do not.</summary>
public static class SmokeFrameClassifier
{
    private static readonly (Regex Pattern, string Detail)[] Blockers =
    [
        (Rx(@"\b(update required|must update|install update|update now)\b"), "An update-required prompt is visible."),
        (Rx(@"\b(sign[ -]?in required|please sign[ -]?in|log[ -]?in required|please log[ -]?in|session expired)\b"), "A login/session prompt is visible."),
        (Rx(@"\b(license agreement|end user license|accept (the )?(terms|agreement)|privacy agreement)\b"), "A first-run agreement prompt is visible."),
        (Rx(@"\b(game files? (are )?corrupt|repair required|missing executable|failed to launch|launch error)\b"), "A launch/installation error is visible.")
    ];

    private static readonly (Regex Pattern, string Detail)[] Warnings =
    [
        (Rx(@"\b(update available|downloading update|updating)\b"), "An update may be pending or in progress."),
        (Rx(@"\b(last session ended unexpectedly|start in safe mode)\b"), "The game reports an unclean prior exit; its benchmark bot must dismiss the recovery prompt."),
        (Rx(@"\b(press any key|press enter|continue to accept)\b"), "A first-run or title-screen interaction is waiting.")
    ];

    public static SmokeFrameFinding Classify(OcrFrame? frame)
    {
        if (frame is null) return new(SmokeFrameVerdict.Warning, "OCR was unavailable; inspect the saved frame.");
        if (frame.IsNoSignalSlate) return new(SmokeFrameVerdict.Blocked, "The capture card reports NO SIGNAL.");

        string text = string.Join(' ', frame.Lines.Select(l => l.Text));
        foreach (var item in Blockers)
            if (item.Pattern.IsMatch(text)) return new(SmokeFrameVerdict.Blocked, item.Detail);
        foreach (var item in Warnings)
            if (item.Pattern.IsMatch(text)) return new(SmokeFrameVerdict.Warning, item.Detail);
        if (frame.Lines.Count == 0)
            return new(SmokeFrameVerdict.Warning, "A frame was captured but OCR found no text; it may still be a loading or black screen.");
        return new(SmokeFrameVerdict.Clear,
            $"Live frame captured and {frame.Lines.Count} OCR line(s) contained no known blocker.");
    }

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
