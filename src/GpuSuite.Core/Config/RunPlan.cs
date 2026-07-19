using GpuSuite.Core.Io;

namespace GpuSuite.Core.Config;

/// <summary>
/// A saved, NAMED run recipe ("setting group"): which games run, which of each game's graphics
/// variants (settings-sets) run for it, and on which resolution subset — e.g. "Cyberpunk settings #1
/// and #2 + Alan Wake settings #1 and #2, at 1440p and 4K". Selecting a plan changes NOTHING in the
/// profiles: per-game picks override each variant's Enabled toggle only for that run (the same
/// in-memory rule as the CLI --variants flag), so dormant (enabled:false) variants stay dormant for
/// every run that doesn't name them. Persisted in plans.json at the suite root; select via
/// `gpusuite run --plan &lt;name&gt;` or the App's Run tab.
/// </summary>
public sealed class RunPlan
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>
    /// gameId → variant ids to run for that game. A game listed with an EMPTY list runs its enabled
    /// variants (the as-set default). Games not listed are excluded from the plan's run.
    /// </summary>
    public Dictionary<string, List<string>> Games { get; set; } = new();

    /// <summary>Resolution subset by name (e.g. ["1440p","4K"]). Empty = the suite's configured resolutions.</summary>
    public List<string> Resolutions { get; set; } = new();
}

/// <summary>The plans.json container: every saved <see cref="RunPlan"/>, loaded/saved at the suite root.</summary>
public sealed class RunPlanFile
{
    public const string DefaultFileName = "plans.json";

    public List<RunPlan> Plans { get; set; } = new();

    public static RunPlanFile Load(string path) => Json.Load<RunPlanFile>(path) ?? new RunPlanFile();

    public void Save(string path) => Json.Save(path, this);

    public RunPlan? Find(string name)
        => Plans.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Add or replace (by case-insensitive name) — the Save button semantics.</summary>
    public void Upsert(RunPlan plan)
    {
        Plans.RemoveAll(p => string.Equals(p.Name, plan.Name, StringComparison.OrdinalIgnoreCase));
        Plans.Add(plan);
        Plans.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    public bool Remove(string name)
        => Plans.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
}
