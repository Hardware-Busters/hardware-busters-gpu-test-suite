using GpuSuite.Core.Io;
using GpuSuite.Core.Models;
using GpuSuite.Reporting;

namespace GpuSuite.Cli;

// `compare` — cross-GPU / cross-suite comparison (roadmap: cross-GPU comparison report). Joins two saved
// suite_result.json files on (game, scene, model, resolution) and renders a self-contained HTML delta
// report. Read-only over past results; it launches nothing and never edits the underlying suites.
internal static partial class Program
{
    private static int Compare(ArgMap a)
    {
        string? baseP = a.Get("--base");
        string? targetP = a.Get("--target");
        if (string.IsNullOrWhiteSpace(baseP) || string.IsNullOrWhiteSpace(targetP))
        {
            Console.Error.WriteLine(
                "Usage: gpusuite compare --base <suite_result.json> --target <suite_result.json> [--out FILE] [--open]\n" +
                "  Joins two suite results on (game, scene, model, resolution) and writes a self-contained HTML\n" +
                "  delta report. Watt columns appear only where both sides share the same power provenance;\n" +
                "  frame-gen-inflated rows are flagged; unmatched cells are listed, never dropped.");
            return 2;
        }
        if (!File.Exists(baseP)) { Console.Error.WriteLine($"compare: baseline not found: {baseP}"); return 1; }
        if (!File.Exists(targetP)) { Console.Error.WriteLine($"compare: target not found: {targetP}"); return 1; }

        SuiteResult? b;
        SuiteResult? t;
        try { b = Json.Load<SuiteResult>(baseP); }
        catch (Exception ex) { Console.Error.WriteLine($"compare: could not parse baseline {baseP} — {ex.Message}"); return 1; }
        try { t = Json.Load<SuiteResult>(targetP); }
        catch (Exception ex) { Console.Error.WriteLine($"compare: could not parse target {targetP} — {ex.Message}"); return 1; }
        if (b is null) { Console.Error.WriteLine($"compare: could not parse baseline {baseP}"); return 1; }
        if (t is null) { Console.Error.WriteLine($"compare: could not parse target {targetP}"); return 1; }

        var cmp = SuiteComparison.Build(b, t);

        // A comparison of two runs by the SAME GPU is legitimate (before/after a driver or settings
        // change), so identical names are not an error — just say what was compared.
        Console.WriteLine($"Baseline : {b.GpuName}  ({b.Aggregates.Count} cells, generated {b.GeneratedUtc.ToLocalTime():yyyy-MM-dd HH:mm})");
        Console.WriteLine($"Target   : {t.GpuName}  ({t.Aggregates.Count} cells, generated {t.GeneratedUtc.ToLocalTime():yyyy-MM-dd HH:mm})");
        var idx = cmp.MatchedIndexDeltaPct;
        Console.WriteLine(idx is null
            ? "No matched cells — nothing to compare. Do the two suites cover the same games/scenes/models/resolutions?"
            : $"Matched geo-mean Δ: {(idx >= 0 ? "+" : "")}{idx:0.0}% over {cmp.Matched.Count()} matched cell(s) " +
              $"({cmp.ImprovedCount} faster, {cmp.RegressedCount} slower, {cmp.OnlyInBaseline.Count()}+{cmp.OnlyInTarget.Count()} unmatched).");

        string outPath = a.Get("--out") ?? Path.Combine("Results",
            $"comparison_{DateTime.Now:yyyyMMdd_HHmmss}.html");
        new SuiteComparisonReportGenerator().Save(outPath, cmp);
        Console.WriteLine($"Report → {Path.GetFullPath(outPath)}");
        if (a.Has("--open")) TryOpen(outPath);
        return idx is null ? 1 : 0;
    }
}
