using GpuSuite.Engine.Vision;

namespace GpuSuite.Cli;

internal static partial class Program
{
    /// <summary>
    /// `gpusuite selftest-ocrguard` — offline validation of the two nav OCR guards, NO game required:
    /// (#5) <see cref="DesktopDetector"/> flags a Windows-desktop frame so the nav waits instead of false-matching
    /// menu words off desktop icons / the chat window; (#6) <see cref="OcrFrame.WithoutOsdLines"/> strips the RTSS /
    /// benchmark OSD (fps/frametime/pass) so the overlay can't clutter or false-match menu text. Exit 0 = all pass.
    /// </summary>
    private static int SelfTestOcrGuard()
    {
        Console.WriteLine("\nOCR nav-guard self-test (desktop detector + OSD stripping)\n");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}  — {detail}");
            if (ok) pass++; else fail++;
        }

        static OcrFrame Frame(params string[] lines) =>
            new() { Width = 1920, Height = 1080, Lines = lines.Select(t => new OcrLine { Text = t }).ToList() };

        var anchors = DesktopDetector.DefaultAnchors;

        // ---- #5 desktop detector ----
        var desktop = Frame("Type here to search", "Recycle Bin", "File Explorer", "3:42 PM");
        Check("desktop frame (2+ shell anchors) is detected", DesktopDetector.LooksLikeDesktop(desktop, anchors, 2),
              "matched ≥2 shell anchors → desktop");

        var menu = Frame("RESUME", "CONTINUE GAME", "NEW GAME", "SETTINGS", "QUIT");
        Check("game menu is NOT detected as desktop", !DesktopDetector.LooksLikeDesktop(menu, anchors, 2),
              "no shell anchors → game");

        var oneAnchor = Frame("Recycle Bin", "RESUME", "CONTINUE");
        Check("single shell anchor stays below the 2-hit threshold", !DesktopDetector.LooksLikeDesktop(oneAnchor, anchors, 2),
              "1 anchor < minHits 2 → not desktop (avoids a stray-token false trip)");

        // ---- #6 OSD stripping ----
        var withOsd = Frame("120 fps", "8.3 ms", "Pass 1/3  [RUN]", "RESUME", "CONTINUE GAME");
        var stripped = withOsd.WithoutOsdLines();
        Check("OSD lines (fps / ms / Pass n/m [RUN]) are stripped", stripped.Lines.Count == 2,
              $"{withOsd.Lines.Count} lines → {stripped.Lines.Count} kept");
        Check("menu anchor survives OSD stripping", stripped.FindWord("RESUME") is not null,
              "RESUME still matches after stripping");
        Check("OSD fps line cannot be matched after stripping", stripped.Find("fps") is null,
              "'fps' gone from the stripped frame");

        var noOsd = Frame("RESUME", "PLAYER 1", "LOAD GAME");
        Check("a clean menu (incl. 'PLAYER 1') is left untouched", noOsd.WithoutOsdLines().Lines.Count == 3,
              "no OSD signature → no lines dropped (a digit alone is not OSD)");

        Console.WriteLine($"\nRESULT: {(fail == 0 ? "PASS" : "FAIL")} — {pass} passed, {fail} failed.");
        return fail == 0 ? 0 : 1;
    }
}
