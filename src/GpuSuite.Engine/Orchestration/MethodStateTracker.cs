using System.Security.Cryptography;
using System.Text;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Models;

namespace GpuSuite.Engine.Orchestration;

/// <summary>
/// Keeps the monitoring summary of one suite run current in <see cref="MonitoringState"/>: identities, counts and short labels only.
/// It never reads hardware and never throws into the run. A run that ends without <see cref="Complete"/> is reported as aborted.
/// </summary>
internal sealed class MethodStateTracker : IDisposable
{
    internal const string RulesetId = "gpu-suite-methodology";
    private const string StepMeaning = "one benchmark cell (game / scene / variant / resolution); the repeats run inside a step";
    private const string BetweenCellsPhase = "between cells";
    private const string UnknownGpuId = "unknown-gpu";

    private readonly object _gate = new();
    private readonly Func<DateTime> _utcNow;
    private readonly MethodUnit _unit;
    private readonly MethodPlan _plan;
    private readonly List<MethodIssue> _issues = [];
    private readonly ResultTally _tally = new();
    private MethodSession _session = new(MethodState.Running, null, null, null);
    private IReadOnlyDictionary<string, object?> _extra = StepExtra(null);
    private MethodWindow? _lastWindow;
    private bool _ended;
    private bool _final;

    private MethodStateTracker(MethodUnit unit, MethodPlan plan, Func<DateTime> utcNow)
    {
        _unit = unit;
        _plan = plan;
        _utcNow = utcNow;
    }

    /// <summary>Starts tracking a run whose <paramref name="planSteps"/> each describe one cell, and publishes the initial state.</summary>
    internal static MethodStateTracker Begin(SystemInfo system, IReadOnlyList<string> planSteps, bool simulated, Func<DateTime>? utcNow = null)
    {
        var clock = utcNow ?? (() => DateTime.UtcNow);
        var plan = new MethodPlan($"plan-{clock():yyyyMMdd-HHmmss}", Sha256(string.Join('\n', planSteps)), planSteps.Count);
        var tracker = new MethodStateTracker(UnitFrom(system, simulated), plan, clock);
        tracker.Update(NoChange);
        return tracker;
    }

    internal static string CellKey(SceneResolutionAggregate cell)
        => RunStateManager.Cell(cell.GameId, cell.SceneId, NullIfBlank(cell.VariantId), cell.ResolutionName);

    internal void Progress(BenchmarkProgress progress) => Update(() =>
    {
        _session = progress.CellCompleted
            ? _session with { Step = null, Phase = BetweenCellsPhase, Index = null }
            : _session with
            {
                Step = $"{progress.GameName} / {progress.SceneName} / {progress.VariantId} / {progress.ResolutionName}",
                Phase = progress.Phase,
                Index = progress.CellIndex
            };
        _extra = StepExtra(progress);
    });

    /// <summary>Adds a cell measured in this run: its counts, the verdict on its last run, and an issue for every run it rejected.</summary>
    internal void CellMeasured(SceneResolutionAggregate cell, int requiredRepeats) => Update(() =>
    {
        _tally.Add(cell, requiredRepeats);
        if (cell.Runs.LastOrDefault() is { } lastRun) _lastWindow = new MethodWindow(lastRun.IsValid, lastRun.IsValid ? null : Reason(lastRun));
        foreach (var run in cell.Runs.Where(run => !run.IsValid)) AddIssue(CellKey(cell), Reason(run));
    });

    /// <summary>Adds a cell reloaded from a checkpoint: it counts as finished, but nothing was measured or judged in this run.</summary>
    internal void CellResumed(SceneResolutionAggregate cell, int requiredRepeats) => Update(() => _tally.Add(cell, requiredRepeats));

    /// <summary>
    /// Marks the end of the roster after its result snapshot was saved. The run is done, and its result final, only when
    /// every planned cell finished; otherwise it ended short and the plan returns to sealed.
    /// </summary>
    internal void Complete() => Update(() =>
    {
        _ended = true;
        _final = _tally.Cells == _plan.TotalSteps;
        _session = new MethodSession(_final ? MethodState.Done : MethodState.Aborted, null, null, null);
        if (!_final) AddIssue(null, $"The run ended after {_tally.Cells} of {_plan.TotalSteps} planned cells.");
    });

    public void Dispose() => Update(() =>
    {
        if (_ended) return;
        _ended = true;
        _session = _session with { State = MethodState.Aborted, Index = null };
    });

    private static void NoChange() { }

    private void Update(Action change)
    {
        MonitoringMethod snapshot;
        lock (_gate)
        {
            try
            {
                change();
                snapshot = Snapshot();
            }
            catch
            {
                return; // Monitoring cannot fail the benchmark: an update that throws is dropped and the previous state stays.
            }
        }
        MonitoringState.Method(snapshot);
    }

    private MonitoringMethod Snapshot() => new(RulesetId, _unit, _plan, _session,
        new MethodProgress(_tally.Cells, _plan.TotalSteps), _lastWindow,
        new MethodResults(_tally.Cells > 0 || _final ? _tally.ToProvisional() : null, _final),
        [.. _issues], _extra, _utcNow());

    private void AddIssue(string? field, string message)
    {
        _issues.Add(new MethodIssue(field, message, _utcNow()));
        if (_issues.Count > MonitoringMethod.MaxIssues) _issues.RemoveAt(0);
    }

    // Validation text can quote exception messages, which can carry file paths and the user name.
    private static string Reason(RunResult run) => MethodTextScrubber.Scrub(
        run.ValidationIssues.FirstOrDefault(issue => !string.IsNullOrWhiteSpace(issue)) ?? $"The run verdict was {run.Verdict}.");

    private static Dictionary<string, object?> StepExtra(BenchmarkProgress? progress)
    {
        var extra = new Dictionary<string, object?> { ["operatorPromptsTracked"] = false, ["stepMeaning"] = StepMeaning };
        if (progress is null) return extra;
        extra["gameId"] = progress.GameId;
        extra["sceneId"] = progress.SceneId;
        extra["variantId"] = progress.VariantId;
        extra["resolution"] = progress.ResolutionName;
        extra["repeat"] = progress.RepeatIndex;
        extra["repeats"] = progress.RepeatTarget;
        return extra;
    }

    private static MethodUnit UnitFrom(SystemInfo system, bool simulated)
    {
        var details = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["gpu"] = NullIfBlank(system.GpuName), ["cpu"] = NullIfBlank(system.CpuName), ["osVersion"] = NullIfBlank(system.OsVersion),
            ["driverVersion"] = NullIfBlank(system.DriverVersion), ["totalRamGb"] = system.TotalRamGb, ["simulation"] = simulated,
            ["presentMonAvailable"] = system.PresentMonAvailable, ["lhmAvailable"] = system.LhmAvailable,
            ["poweneticsConnected"] = system.PoweneticsConnected
        };
        string identity = string.Join('\n', details.Select(pair => FormattableString.Invariant($"{pair.Key}={pair.Value}")));
        return new MethodUnit(NullIfBlank(system.GpuName) ?? UnknownGpuId, Sha256(identity), details);
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class ResultTally
    {
        public int Cells { get; private set; }
        private int _fullyValidCells;
        private int _validRuns;
        private int _totalRuns;
        private string? _lastCell;
        private double? _lastAvgFps;
        private double? _lastP1LowFps;
        private double? _lastAvgGpuPowerW;
        private string? _lastPowerSource;

        public void Add(SceneResolutionAggregate cell, int requiredRepeats)
        {
            Cells++;
            if (cell.ValidRuns >= requiredRepeats) _fullyValidCells++;
            _validRuns += cell.ValidRuns;
            _totalRuns += cell.TotalRuns;
            _lastCell = CellKey(cell);
            bool measured = cell.ValidRuns > 0;
            _lastAvgFps = measured ? cell.AvgFps : null;
            _lastP1LowFps = measured ? cell.P1LowFps : null;
            _lastAvgGpuPowerW = measured ? cell.AvgGpuPowerW : null;
            _lastPowerSource = measured && cell.AvgGpuPowerW is not null ? cell.PowerMeasurement.DisplayLabel : null;
        }

        public IReadOnlyDictionary<string, object?> ToProvisional() => new Dictionary<string, object?>
        {
            ["cellsCompleted"] = Cells, ["cellsFullyValid"] = _fullyValidCells, ["validRuns"] = _validRuns, ["totalRuns"] = _totalRuns,
            ["lastCell"] = _lastCell, ["lastCellAvgFps"] = _lastAvgFps, ["lastCellP1LowFps"] = _lastP1LowFps,
            ["lastCellAvgGpuPowerW"] = _lastAvgGpuPowerW, ["lastCellPowerSource"] = _lastPowerSource
        };
    }
}
