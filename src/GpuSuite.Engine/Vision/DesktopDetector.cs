namespace GpuSuite.Engine.Vision;

/// <summary>
/// Recognizes a Windows DESKTOP / shell frame so the nav engine never acts on it. The foreground-pid gate is the
/// primary guard, but a freshly-launched game's window can be foreground BEFORE it has painted (the capture card
/// still shows the desktop behind it), and the menu-anchor OCR then FALSE-MATCHES words off desktop icons or the
/// operator's chat window — diagnosed 2026-06-27 (a vision-nav read the Claude chat's menu words and false-DONE'd).
/// This flags a frame as the desktop when at least <c>minHits</c> strong, game-independent shell anchors appear
/// (whole-word), so the runner WAITs instead of acting. Conservative by design: the anchors essentially never occur
/// in a real game menu and a multi-hit threshold avoids a single stray OCR token tripping it.
/// </summary>
public static class DesktopDetector
{
    /// <summary>Default Windows-shell anchors that essentially never appear in a game's menu (taskbar / shell UI).
    /// Tunable via settings.desktopGuardAnchors.</summary>
    public static readonly string[] DefaultAnchors =
    {
        "Type here to search", "Search highlights", "Task View", "Recycle Bin", "Ask Copilot",
        "Cortana", "Show desktop", "Snipping Tool", "File Explorer", "Quick settings",
        "Action center", "Widgets", "Start menu"
    };

    /// <summary>True when ≥ <paramref name="minHits"/> of the <paramref name="anchors"/> appear (whole-word) in the
    /// frame — i.e. the capture is showing the Windows desktop/shell, not the game.</summary>
    public static bool LooksLikeDesktop(OcrFrame? frame, IReadOnlyList<string>? anchors, int minHits)
    {
        if (frame is null || frame.Lines.Count == 0 || anchors is null || anchors.Count == 0) return false;
        int need = Math.Max(1, minHits);
        int hits = 0;
        foreach (var a in anchors)
        {
            if (string.IsNullOrWhiteSpace(a)) continue;
            if (frame.FindWord(a) is not null && ++hits >= need) return true;
        }
        return false;
    }
}
