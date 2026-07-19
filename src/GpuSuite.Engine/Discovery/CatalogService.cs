using System.Text;
using GpuSuite.Core.Models;

namespace GpuSuite.Engine.Discovery;

/// <summary>A discovered game joined with its reference knowledge and a first-target ranking score.</summary>
public sealed class CatalogRow
{
    public required DiscoveredGame Game { get; init; }
    public required GameKnowledge Knowledge { get; init; }
    public double Score { get; init; }
    public string BenchLabel => Knowledge.BuiltInBenchmark switch
    {
        BenchSupport.Yes => Knowledge.Delivery == BenchmarkDelivery.SeparateTool ? "Separate" : "Yes",
        BenchSupport.No => "No",
        _ => "Unknown"
    };
    public string AutomationLabel => Knowledge.Automation == AutomationDifficulty.Unknown ? "Unknown" : Knowledge.Automation.ToString();
    public string SuitabilityLabel => Knowledge.Suitability == GpuSuitability.Unknown ? "Unknown" : Knowledge.Suitability.ToString();
}

/// <summary>The enriched, ranked catalog plus the recommended first MVP target.</summary>
public sealed class EnrichedCatalog
{
    public List<CatalogRow> Rows { get; init; } = new();
    public CatalogRow? Recommended { get; init; }
    public int Count => Rows.Count;
}

/// <summary>
/// Joins the discovery catalog with the <see cref="GameKnowledgeBase"/>, scores each title for
/// first-MVP suitability, and selects a recommended target. Pure detection/advice — selecting a game
/// for the benchmark list and running it remain explicit, user-controlled actions elsewhere.
/// </summary>
public sealed class CatalogService
{
    private readonly GameKnowledgeBase _kb = new();

    public EnrichedCatalog Build(GameCatalog catalog)
    {
        var rows = catalog.Games
            .Select(g => { var k = _kb.Lookup(g); return new CatalogRow { Game = g, Knowledge = k, Score = Score(g, k) }; })
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Game.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Recommend the best-scoring title we can actually capture (exe resolved) whose benchmark
        // ships IN this build — a separate-tool benchmark (e.g. Black Myth) doesn't qualify because the
        // retail install can't self-benchmark. Fall back to any capturable row, then the top row.
        var rec = rows.FirstOrDefault(r => !string.IsNullOrEmpty(r.Game.Exe)
                                           && r.Knowledge.BuiltInBenchmark == BenchSupport.Yes
                                           && r.Knowledge.Delivery == BenchmarkDelivery.InGame)
                  ?? rows.FirstOrDefault(r => !string.IsNullOrEmpty(r.Game.Exe))
                  ?? rows.FirstOrDefault();

        return new EnrichedCatalog { Rows = rows, Recommended = rec };
    }

    private static double Score(DiscoveredGame g, GameKnowledge k)
    {
        double s = 0;
        // A built-in benchmark is the biggest automation win — but ONLY when it ships in this installed
        // build. A separate-tool benchmark (different install/appid) can't be assumed present from this
        // row, and the retail build can't self-benchmark, so it earns almost nothing here.
        s += k.BuiltInBenchmark switch
        {
            BenchSupport.Yes when k.Delivery == BenchmarkDelivery.InGame => 40,
            BenchSupport.Yes => 5,            // separate-tool benchmark — not usable from this install
            BenchSupport.Unknown => 6,
            _ => 0
        };
        if (k.ResultFileParse) s += 18;       // a parseable result file dominates automation ease
        if (k.CliTrigger) s += 10;            // a CLI/headless trigger removes menu-bot fragility
        s += k.Automation switch { AutomationDifficulty.Easy => 25, AutomationDifficulty.Medium => 12, AutomationDifficulty.Unknown => 6, _ => 0 };
        s += k.Suitability switch { GpuSuitability.Excellent => 20, GpuSuitability.Good => 14, GpuSuitability.Fair => 6, GpuSuitability.Unknown => 4, _ => 0 };
        if (!string.IsNullOrEmpty(g.Exe)) s += 6;          // capturable by PresentMon
        if (!string.IsNullOrEmpty(g.LaunchUri)) s += 3;    // launchable by URI
        return s;
    }

    // ---------------- rendering ----------------

    /// <summary>Full Markdown table with every requested column (saved to disk / shareable).</summary>
    public static string ToMarkdown(EnrichedCatalog cat)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Discovered games ({cat.Count})");
        sb.AppendLine();
        sb.AppendLine("> Detection only. This table never enables, selects, or runs a game.");
        sb.AppendLine();
        sb.AppendLine("| Game | Store | App/Game ID | Install Path | Main Exe | Process | Built-in Bench | Automation | GPU Suitability | Result Parse | Source | Notes |");
        sb.AppendLine("|------|-------|-------------|--------------|----------|---------|----------------|------------|-----------------|--------------|--------|-------|");
        foreach (var r in cat.Rows)
        {
            var g = r.Game;
            sb.AppendLine($"| {Md(g.Name)} | {g.Store} | {Md(g.GameId)} | {Md(g.InstallDir)} | {Md(g.Exe)} | {Md(g.ProcessName)} | " +
                          $"{r.BenchLabel} | {r.AutomationLabel} | {r.SuitabilityLabel} | {(r.Knowledge.ResultFileParse ? "Yes" : "—")} | {Md(g.Source)} | {Md(r.Knowledge.Notes)} |");
        }
        if (cat.Recommended is { } rec)
        {
            sb.AppendLine();
            sb.AppendLine($"**Recommended first target:** {rec.Game.Name} ({rec.Game.Store}) — score {rec.Score:0}. {rec.Knowledge.Notes}");
        }
        return sb.ToString();
    }

    /// <summary>CSV with every column (Excel/sheets friendly).</summary>
    public static string ToCsv(EnrichedCatalog cat)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Name,Store,GameId,InstallPath,MainExe,Process,BuiltInBenchmark,Automation,GpuSuitability,ResultParse,LaunchUri,Source,Score,Notes");
        foreach (var r in cat.Rows)
        {
            var g = r.Game;
            sb.AppendLine(string.Join(",", new[]
            {
                Csv(g.Name), Csv(g.Store.ToString()), Csv(g.GameId), Csv(g.InstallDir), Csv(g.Exe), Csv(g.ProcessName),
                Csv(r.BenchLabel), Csv(r.AutomationLabel), Csv(r.SuitabilityLabel), Csv(r.Knowledge.ResultFileParse ? "Yes" : "No"),
                Csv(g.LaunchUri), Csv(g.Source), Csv(r.Score.ToString("0")), Csv(r.Knowledge.Notes)
            }));
        }
        return sb.ToString();
    }

    /// <summary>Compact, aligned console view that fits a terminal (full detail is in the saved files).</summary>
    public static string ToConsole(EnrichedCatalog cat)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{"Game",-34} {"Store",-10} {"Bench",-8} {"Auto",-7} {"GPU",-10} Notes");
        sb.AppendLine(new string('-', 110));
        foreach (var r in cat.Rows)
        {
            sb.AppendLine($"{Trunc(r.Game.Name, 34),-34} {r.Game.Store,-10} {r.BenchLabel,-8} {r.AutomationLabel,-7} {r.SuitabilityLabel,-10} {Trunc(r.Knowledge.Notes, 48)}");
        }
        return sb.ToString();
    }

    private static string Md(string? s) => string.IsNullOrEmpty(s) ? "—" : s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
    private static string Csv(string? s)
    {
        s ??= "";
        return s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
    private static string Trunc(string? s, int n)
    {
        s ??= "";
        return s.Length <= n ? s : s[..(n - 1)] + "…";
    }
}
