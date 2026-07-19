using GpuSuite.Core.Models;

namespace GpuSuite.Engine.Discovery;

public enum BenchSupport { Unknown, No, Yes }
public enum AutomationDifficulty { Unknown, Easy, Medium, Hard }
public enum GpuSuitability { Unknown, Poor, Fair, Good, Excellent }

/// <summary>
/// Where a <see cref="BenchSupport.Yes"/> benchmark actually lives.
/// <list type="bullet">
/// <item><see cref="InGame"/> — the benchmark ships inside this installed build (including a
/// benchmark exe that lands in the same install folder). Present whenever the game is.</item>
/// <item><see cref="SeparateTool"/> — the benchmark is a standalone application with its OWN install
/// (e.g. a different Steam appid). The retail build cannot self-benchmark, and the tool is NOT
/// guaranteed present just because the game is. Scored as effectively unavailable for this row.</item>
/// </list>
/// </summary>
public enum BenchmarkDelivery { InGame, SeparateTool }

/// <summary>
/// Advisory annotation for a single title: does it ship a built-in benchmark, how parseable are its
/// results, how hard is it to automate, and how useful is it for GPU reviews. This is reference
/// knowledge used ONLY to describe and rank games that discovery already found installed. It never
/// selects a game, never creates a benchmark-list entry, and never triggers a run.
/// </summary>
public sealed record GameKnowledge
{
    public BenchSupport BuiltInBenchmark { get; init; } = BenchSupport.Unknown;
    /// <summary>
    /// For a <see cref="BenchSupport.Yes"/> title, whether the benchmark ships in this build
    /// (<see cref="BenchmarkDelivery.InGame"/>) or is a separate standalone install
    /// (<see cref="BenchmarkDelivery.SeparateTool"/>). Ignored when there is no benchmark.
    /// </summary>
    public BenchmarkDelivery Delivery { get; init; } = BenchmarkDelivery.InGame;
    public AutomationDifficulty Automation { get; init; } = AutomationDifficulty.Unknown;
    public GpuSuitability Suitability { get; init; } = GpuSuitability.Unknown;
    /// <summary>A machine-parseable result/log file is known to be written (CSV/XML/log).</summary>
    public bool ResultFileParse { get; init; }
    /// <summary>The benchmark can be started headlessly via a CLI flag / command line (no menu bot).</summary>
    public bool CliTrigger { get; init; }
    public string Notes { get; init; } = "";

    public static readonly GameKnowledge UnknownGame = new()
    {
        Notes = "No reference data — inspect the install folder + Documents/AppData on the machine."
    };
}

/// <summary>
/// Maps a <see cref="DiscoveredGame"/> to <see cref="GameKnowledge"/> by Steam appid (most reliable)
/// or normalized title match. Curated from well-documented GPU-review benchmark behavior; unknown
/// titles fall back to <see cref="GameKnowledge.UnknownGame"/>.
/// </summary>
public sealed class GameKnowledgeBase
{
    private sealed record Entry(string? SteamId, string[] Names, GameKnowledge K);

    private static GameKnowledge K(BenchSupport b, AutomationDifficulty a, GpuSuitability s, bool parse, string notes)
        => new() { BuiltInBenchmark = b, Automation = a, Suitability = s, ResultFileParse = parse, Notes = notes };

    // Curated reference set. Steam appids where known; name patterns as a cross-store fallback.
    private static readonly Entry[] Db =
    {
        new("223850", new[]{"3dmark"},
            K(BenchSupport.Yes, AutomationDifficulty.Easy, GpuSuitability.Excellent, true,
              "Industry-standard synthetic; CLI automation + result XML export. Reference, not a game.")
              with { CliTrigger = true }),

        // Tomb Raider line — clean built-in benchmarks; results shown on-screen (no easy result file).
        new("750920", new[]{"shadow of the tomb raider"},
            K(BenchSupport.Yes, AutomationDifficulty.Easy, GpuSuitability.Excellent, false,
              "Built-in benchmark; on-screen results — capture via PresentMon window + scene markers.")),
        new("391220", new[]{"rise of the tomb raider"},
            K(BenchSupport.Yes, AutomationDifficulty.Easy, GpuSuitability.Good, false,
              "Built-in benchmark; on-screen results — PresentMon window capture.")),
        new("203160", new[]{"tomb raider"},
            K(BenchSupport.Yes, AutomationDifficulty.Easy, GpuSuitability.Fair, false,
              "Built-in benchmark (2013); on-screen results.")),

        // Total War — command-line benchmark that writes a CSV: easiest to parse.
        new("1142710", new[]{"total war: warhammer iii","warhammer iii","warhammer 3"},
            K(BenchSupport.Yes, AutomationDifficulty.Easy, GpuSuitability.Excellent, true,
              "Command-line battle benchmark writes a frame-time CSV — easiest result parse.")
              with { CliTrigger = true }),
        new("779340", new[]{"total war: three kingdoms","three kingdoms"},
            K(BenchSupport.Yes, AutomationDifficulty.Easy, GpuSuitability.Good, true,
              "Command-line benchmark with CSV output.")
              with { CliTrigger = true }),

        // Ubisoft built-in benchmarks — on-screen averages, menu-driven.
        new("2369390", new[]{"far cry 6"},
            K(BenchSupport.Yes, AutomationDifficulty.Easy, GpuSuitability.Excellent, false,
              "Built-in benchmark; on-screen averages — menu bot + PresentMon window.")),
        new("552520", new[]{"far cry 5"},
            K(BenchSupport.Yes, AutomationDifficulty.Easy, GpuSuitability.Good, false, "Built-in benchmark.")),
        new("939960", new[]{"far cry new dawn"},
            K(BenchSupport.Yes, AutomationDifficulty.Easy, GpuSuitability.Good, false, "Built-in benchmark.")),
        new(null, new[]{"assassin's creed valhalla","assassins creed valhalla"},
            K(BenchSupport.Yes, AutomationDifficulty.Medium, GpuSuitability.Excellent, false,
              "Built-in benchmark; menu navigation + on-screen results.")),
        new("812140", new[]{"assassin's creed odyssey","assassins creed odyssey"},
            K(BenchSupport.Yes, AutomationDifficulty.Medium, GpuSuitability.Good, false, "Built-in benchmark.")),
        new(null, new[]{"assassin's creed mirage","assassins creed mirage"},
            K(BenchSupport.Yes, AutomationDifficulty.Medium, GpuSuitability.Good, false, "Built-in benchmark.")),
        new(null, new[]{"watch dogs: legion","watch dogs legion"},
            K(BenchSupport.Yes, AutomationDifficulty.Medium, GpuSuitability.Good, false, "Built-in benchmark.")),

        new("1091500", new[]{"cyberpunk 2077"},
            K(BenchSupport.Yes, AutomationDifficulty.Medium, GpuSuitability.Excellent, true,
              "Headless '-benchmark' launch flag runs the benchmark and writes a CSV to " +
              "Documents\\CD Projekt Red\\Cyberpunk 2077\\benchmarkResults\\. The game's own frame-rate " +
              "averaging is unreliable — drive via the CLI flag and use PresentMon as ground truth.")
              with { CliTrigger = true }),
        new("1174180", new[]{"red dead redemption 2","red dead redemption ii"},
            K(BenchSupport.Yes, AutomationDifficulty.Medium, GpuSuitability.Excellent, false,
              "Built-in benchmark; on-screen results — PresentMon window + scene markers.")),

        new("1551360", new[]{"forza horizon 5"},
            K(BenchSupport.Yes, AutomationDifficulty.Medium, GpuSuitability.Good, false,
              "Built-in benchmark; UWP/Store packaging can complicate process capture.")),
        new("2440510", new[]{"forza motorsport"},
            K(BenchSupport.Yes, AutomationDifficulty.Medium, GpuSuitability.Good, false, "Built-in benchmark.")),

        new("2488620", new[]{"f1 24","f1® 24"},
            K(BenchSupport.Yes, AutomationDifficulty.Easy, GpuSuitability.Good, true,
              "Built-in benchmark writes a results file.")),
        new("2108330", new[]{"f1 23","f1® 23"},
            K(BenchSupport.Yes, AutomationDifficulty.Easy, GpuSuitability.Good, true,
              "Built-in benchmark writes a results file.")),

        new("397540", new[]{"borderlands 3"},
            K(BenchSupport.Yes, AutomationDifficulty.Easy, GpuSuitability.Good, true,
              "Built-in benchmark writes a results file.")),
        new("1151640", new[]{"horizon zero dawn"},
            K(BenchSupport.Yes, AutomationDifficulty.Easy, GpuSuitability.Excellent, true,
              "Built-in benchmark writes a result file — good parse target.")),
        new("2420110", new[]{"horizon forbidden west"},
            K(BenchSupport.Yes, AutomationDifficulty.Easy, GpuSuitability.Excellent, false, "Built-in benchmark.")),
        new("412020", new[]{"metro exodus"},
            K(BenchSupport.Yes, AutomationDifficulty.Medium, GpuSuitability.Excellent, true,
              "Separate benchmark tool writes a result file.")),
        new("1659040", new[]{"hitman 3","hitman world of assassination","world of assassination"},
            K(BenchSupport.Yes, AutomationDifficulty.Medium, GpuSuitability.Good, false, "Built-in benchmark.")),
        new(null, new[]{"gears 5"},
            K(BenchSupport.Yes, AutomationDifficulty.Medium, GpuSuitability.Good, false,
              "Built-in benchmark; UWP/Store packaging can complicate capture.")),
        new("312670", new[]{"strange brigade"},
            K(BenchSupport.Yes, AutomationDifficulty.Easy, GpuSuitability.Good, true,
              "Built-in benchmark; CLI + result output.")
              with { CliTrigger = true }),
        new("507490", new[]{"ashes of the singularity"},
            K(BenchSupport.Yes, AutomationDifficulty.Easy, GpuSuitability.Fair, true,
              "Command-line benchmark with CSV output.")
              with { CliTrigger = true }),
        new("1817070", new[]{"marvel's spider-man remastered","spider-man remastered"},
            K(BenchSupport.Yes, AutomationDifficulty.Easy, GpuSuitability.Good, false, "Built-in benchmark.")),
        new("2358720", new[]{"black myth: wukong","black myth wukong"},
            K(BenchSupport.Yes, AutomationDifficulty.Medium, GpuSuitability.Excellent, false,
              "Benchmark is a SEPARATE app — the 'Black Myth: Wukong Benchmark Tool' has its own Steam " +
              "appid and must be installed separately; the retail game (appid 2358720) has no built-in benchmark.")
              with { Delivery = BenchmarkDelivery.SeparateTool }),

        // Popular GPU-review titles WITHOUT a built-in benchmark — need a scripted bot scene.
        new("870780", new[]{"control"},
            K(BenchSupport.No, AutomationDifficulty.Medium, GpuSuitability.Excellent, false,
              "No built-in benchmark — needs a scripted bot scene.")),
        new("1086940", new[]{"baldur's gate 3","baldurs gate 3"},
            K(BenchSupport.No, AutomationDifficulty.Hard, GpuSuitability.Good, false,
              "No benchmark; CPU-heavy in Act 3 — scripted scene needed.")),
        new("1245620", new[]{"elden ring"},
            K(BenchSupport.No, AutomationDifficulty.Hard, GpuSuitability.Fair, false,
              "60 fps engine cap — poor high-refresh scaling; no benchmark.")),
        new("1716740", new[]{"starfield"},
            K(BenchSupport.No, AutomationDifficulty.Medium, GpuSuitability.Good, false, "No built-in benchmark.")),
        new("1593500", new[]{"god of war"},
            K(BenchSupport.No, AutomationDifficulty.Medium, GpuSuitability.Excellent, false, "No built-in benchmark.")),
        new("2322010", new[]{"god of war ragnar"},
            K(BenchSupport.No, AutomationDifficulty.Medium, GpuSuitability.Excellent, false, "No built-in benchmark.")),
        new("1888930", new[]{"the last of us part i","last of us part 1"},
            K(BenchSupport.No, AutomationDifficulty.Medium, GpuSuitability.Excellent, false, "No built-in benchmark.")),
        new("1182900", new[]{"a plague tale: requiem","plague tale requiem"},
            K(BenchSupport.No, AutomationDifficulty.Medium, GpuSuitability.Excellent, false, "No built-in benchmark.")),
        new("1649240", new[]{"returnal"},
            K(BenchSupport.No, AutomationDifficulty.Medium, GpuSuitability.Good, false, "No built-in benchmark.")),
        new("730", new[]{"counter-strike 2","counter strike 2"},
            K(BenchSupport.No, AutomationDifficulty.Hard, GpuSuitability.Fair, false,
              "CPU-bound competitive title; no official benchmark.")),

        // DOOM line (id Tech) — no built-in benchmark; very GPU-heavy, scales well with resolution.
        new(null, new[]{"doom the dark ages","doom- the dark ages"},
            K(BenchSupport.Yes, AutomationDifficulty.Medium, GpuSuitability.Excellent, false,
              "id Tech 8, ray tracing always on — heavy, scales well. In-game Benchmark Mode " +
              "(Extras > Benchmark Mode, ~7 deterministic scenes) shipped in the June 2025 PC update; " +
              "on-screen-only results — capture via PresentMon. Xbox/MS Store AppContainer build complicates process capture.")),
        new("782330", new[]{"doom eternal"},
            K(BenchSupport.No, AutomationDifficulty.Medium, GpuSuitability.Excellent, false,
              "id Tech 7, very high frame rates — great GPU scaling. No benchmark; scripted scene needed.")),
        new("379720", new[]{"doom"},
            K(BenchSupport.No, AutomationDifficulty.Medium, GpuSuitability.Good, false,
              "DOOM (2016), id Tech 6 (Vulkan). No benchmark; scripted scene needed.")),
    };

    public GameKnowledge Lookup(DiscoveredGame g)
    {
        if (g.Store == GameStoreKind.Steam && !string.IsNullOrEmpty(g.GameId))
        {
            var byId = Db.FirstOrDefault(e => e.SteamId == g.GameId);
            if (byId is not null) return byId.K;
        }
        var name = Normalize(g.Name);
        if (name.Length == 0) return GameKnowledge.UnknownGame;
        var byName = Db.FirstOrDefault(e => e.Names.Any(n => name.Contains(Normalize(n))));
        return byName?.K ?? GameKnowledge.UnknownGame;
    }

    private static string Normalize(string s) =>
        new string((s ?? "").ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();
}
