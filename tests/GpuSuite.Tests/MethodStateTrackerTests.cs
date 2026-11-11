using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Orchestration;
using Xunit;

namespace GpuSuite.Tests;

[Collection("Monitoring")]
public sealed class MethodStateTrackerTests : IDisposable
{
    private static readonly string[] PlanSteps = ["g1|s1|default|1080p|repeats=3", "g1|s1|default|4K|repeats=3", "g2|s2|default|1080p|repeats=3"];
    private DateTime _now = new(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc);

    public MethodStateTrackerTests() => MonitoringState.Method(null);

    public void Dispose() => MonitoringState.Method(null);

    private static MonitoringMethod Published => MonitoringState.ReadMethod()!;

    private static SystemInfo Machine(string gpu = "Test GPU") => new()
    {
        GpuName = gpu, CpuName = "Test CPU", OsVersion = "Test OS", TotalRamGb = 32, LhmAvailable = true
    };

    private MethodStateTracker Begin(IReadOnlyList<string>? steps = null, SystemInfo? system = null, bool simulated = false)
        => MethodStateTracker.Begin(system ?? Machine(), steps ?? PlanSteps, simulated, () => _now);

    private static BenchmarkProgress Progress(int cellIndex = 2, string phase = "Running", bool completed = false)
        => new("g1", "Game One", "s1", "Scene One", "default", "4K", 2, 3, cellIndex, PlanSteps.Length, phase, completed);

    private static RunResult Run(bool valid, params string[] issues)
    {
        var run = new RunResult
        {
            GameId = "g1", SceneId = "s1", ResolutionName = "4K", Verdict = valid ? RunVerdict.Valid : RunVerdict.Invalid
        };
        run.ValidationIssues.AddRange(issues);
        return run;
    }

    private static SceneResolutionAggregate Cell(string resolution = "4K", int validRuns = 3, int totalRuns = 3, params RunResult[] runs)
    {
        var cell = new SceneResolutionAggregate
        {
            GameId = "g1", SceneId = "s1", ResolutionName = resolution, ValidRuns = validRuns, TotalRuns = totalRuns,
            AvgFps = 144.5, P1LowFps = 90.25, AvgGpuPowerW = 250.5
        };
        cell.Runs.AddRange(runs);
        return cell;
    }

    [Fact]
    public void BeginPublishesARunningPlanWithNothingMeasuredYet()
    {
        using var tracker = Begin();

        var method = Published;
        Assert.Equal(MethodState.Running, method.Session.State);
        Assert.Null(method.Session.Index);
        Assert.Equal(PlanSteps.Length, method.Plan.TotalSteps);
        Assert.Equal(new MethodProgress(0, PlanSteps.Length), method.Progress);
        Assert.Null(method.LastWindow);
        Assert.Null(method.Results.Provisional);
        Assert.False(method.Results.Final);
        Assert.Empty(method.Issues);
        Assert.Equal(_now, method.UpdatedUtc);
    }

    [Fact]
    public void ThePublishedStateSaysThatOperatorPromptsAreNotTracked()
    {
        using var tracker = Begin();

        Assert.Equal(false, Published.Extra["operatorPromptsTracked"]);
        Assert.IsType<string>(Published.Extra["stepMeaning"]);
    }

    [Fact]
    public void ThePlanIdentityFollowsTheStepsAndTheUnitIdentityFollowsTheMachine()
    {
        using var first = Begin();
        var firstPublished = Published;
        using var same = Begin();
        var samePublished = Published;
        using var otherSteps = Begin(steps: [.. PlanSteps, "g3|s3|default|1080p|repeats=3"]);
        var otherStepsPublished = Published;
        using var otherRepeats = Begin(steps: [.. PlanSteps[..2], "g2|s2|default|1080p|repeats=5"]);
        var otherRepeatsPublished = Published;
        using var otherGpu = Begin(system: Machine("Other GPU"));
        var otherGpuPublished = Published;

        Assert.Equal(firstPublished.Plan.Hash, samePublished.Plan.Hash);
        Assert.NotEqual(firstPublished.Plan.Hash, otherStepsPublished.Plan.Hash);
        Assert.NotEqual(firstPublished.Plan.Hash, otherRepeatsPublished.Plan.Hash);
        Assert.Equal(firstPublished.Unit.Hash, samePublished.Unit.Hash);
        Assert.NotEqual(firstPublished.Unit.Hash, otherGpuPublished.Unit.Hash);
        Assert.Equal("Other GPU", otherGpuPublished.Unit.Id);
    }

    [Fact]
    public void TheUnitDescribesTheMachineWithScalarValuesAndLeavesUnknownsNull()
    {
        using var tracker = Begin(simulated: true);

        var details = Published.Unit.Details;
        Assert.Equal("Test GPU", details["gpu"]);
        Assert.Equal(32.0, details["totalRamGb"]);
        Assert.Equal(true, details["simulation"]);
        Assert.Equal(true, details["lhmAvailable"]);
        Assert.Null(details["driverVersion"]);
        Assert.All(details.Values, value => Assert.True(value is null or string or double or bool));
    }

    [Fact]
    public void ABlankGpuNameStillGivesTheUnitAnId()
    {
        using var tracker = Begin(system: Machine(gpu: " "));

        Assert.Equal("unknown-gpu", Published.Unit.Id);
        Assert.Null(Published.Unit.Details["gpu"]);
    }

    [Fact]
    public void ProgressNamesTheStepThePhaseAndTheRepeat()
    {
        using var tracker = Begin();

        tracker.Progress(Progress(cellIndex: 2, phase: "Warm-up"));

        var method = Published;
        Assert.Equal("Game One / Scene One / default / 4K", method.Session.Step);
        Assert.Equal("Warm-up", method.Session.Phase);
        Assert.Equal(2, method.Session.Index);
        Assert.Equal(2, method.Extra["repeat"]);
        Assert.Equal(3, method.Extra["repeats"]);
        Assert.Equal("4K", method.Extra["resolution"]);
        Assert.Equal(false, method.Extra["operatorPromptsTracked"]);
    }

    [Fact]
    public void AFinishedCellLeavesNoStepInProgress()
    {
        using var tracker = Begin();
        tracker.Progress(Progress());

        tracker.Progress(Progress(phase: "Completed", completed: true));

        var session = Published.Session;
        Assert.Null(session.Step);
        Assert.Null(session.Index);
        Assert.Equal("between cells", session.Phase);
        Assert.Equal(MethodState.Running, session.State);
    }

    [Fact]
    public void MeasuredCellsAdvanceTheProgressAndTheProvisionalResults()
    {
        using var tracker = Begin();

        tracker.CellMeasured(Cell("1080p", validRuns: 3), requiredRepeats: 3);
        tracker.CellMeasured(Cell("4K", validRuns: 1), requiredRepeats: 3);

        var method = Published;
        Assert.Equal(new MethodProgress(2, PlanSteps.Length), method.Progress);
        var results = method.Results.Provisional!;
        Assert.Equal(2, results["cellsCompleted"]);
        Assert.Equal(1, results["cellsFullyValid"]);
        Assert.Equal(4, results["validRuns"]);
        Assert.Equal(6, results["totalRuns"]);
        Assert.Equal("g1|s1|default|4K", results["lastCell"]);
        Assert.False(method.Results.Final);
    }

    [Fact]
    public void ACellWithoutAValidRunReportsNoAveragesInsteadOfZero()
    {
        using var tracker = Begin();

        tracker.CellMeasured(Cell(validRuns: 0), requiredRepeats: 3);

        var results = Published.Results.Provisional!;
        Assert.Null(results["lastCellAvgFps"]);
        Assert.Null(results["lastCellP1LowFps"]);
        Assert.Null(results["lastCellAvgGpuPowerW"]);
        Assert.Null(results["lastCellPowerSource"]);
        Assert.Equal(0, results["validRuns"]);
    }

    [Fact]
    public void TheLastRunOfAMeasuredCellIsTheLatestWindow()
    {
        using var tracker = Begin();

        tracker.CellMeasured(Cell(runs: [Run(valid: false, "first problem"), Run(valid: true)]), requiredRepeats: 3);
        Assert.Equal(new MethodWindow(true, null), Published.LastWindow);

        tracker.CellMeasured(Cell(runs: [Run(valid: true), Run(valid: false, " ", "Benchmark start was not detected.")]), requiredRepeats: 3);
        Assert.Equal(new MethodWindow(false, "Benchmark start was not detected."), Published.LastWindow);
    }

    [Fact]
    public void ACellWithoutRunsLeavesTheLatestWindowAlone()
    {
        using var tracker = Begin();
        tracker.CellMeasured(Cell(runs: [Run(valid: true)]), requiredRepeats: 3);

        tracker.CellMeasured(Cell("1080p"), requiredRepeats: 3);

        Assert.Equal(new MethodWindow(true, null), Published.LastWindow);
    }

    [Fact]
    public void EveryRejectedRunRaisesAnIssueNamingTheWholeCell()
    {
        using var tracker = Begin();
        _now = _now.AddSeconds(5);

        tracker.CellMeasured(Cell(runs: [Run(valid: false, "Setup failed."), Run(valid: true), Run(valid: false)]), requiredRepeats: 3);

        var issues = Published.Issues;
        Assert.Equal(2, issues.Count);
        Assert.Equal(new MethodIssue("g1|s1|default|4K", "Setup failed.", _now), issues[0]);
        Assert.Equal("The run verdict was Invalid.", issues[1].Message);
    }

    [Fact]
    public void OnlyTheNewestTenIssuesAreKeptOldestFirst()
    {
        using var tracker = Begin();
        for (int i = 1; i <= 12; i++)
        {
            _now = _now.AddSeconds(1);
            tracker.CellMeasured(Cell(runs: [Run(valid: false, $"problem {i}")]), requiredRepeats: 3);
        }

        Assert.Equal(Enumerable.Range(3, 10).Select(i => $"problem {i}"), Published.Issues.Select(issue => issue.Message));
        Assert.Equal(Published.Issues.OrderBy(issue => issue.Utc), Published.Issues);
    }

    [Fact]
    public void AResumedCellCountsAsFinishedButNothingWasMeasuredOrJudged()
    {
        using var tracker = Begin();

        tracker.CellResumed(Cell(runs: [Run(valid: false, "an earlier session's problem")]), requiredRepeats: 3);

        var method = Published;
        Assert.Equal(1, method.Progress.Completed);
        Assert.Null(method.LastWindow);
        Assert.Empty(method.Issues);
    }

    [Fact]
    public void PathsAndTheUserNameNeverReachTheWindowReasonOrTheIssues()
    {
        using var tracker = Begin();
        string drive = "C" + ":" + "\\"; // built at run time so that no drive path appears literally in this source file
        string leaked = $"Run threw: Could not find file '{drive}Users\\{Environment.UserName}\\My Games\\profile.ini' for {Environment.UserName}.";

        tracker.CellMeasured(Cell(runs: [Run(valid: false, leaked)]), requiredRepeats: 3);

        string[] texts = [Published.LastWindow!.Reason!, Published.Issues.Single().Message];
        Assert.All(texts, text =>
        {
            Assert.DoesNotContain("Users", text);
            Assert.DoesNotContain("My Games", text);
            Assert.Contains("Run threw", text);
        });
    }

    [Fact]
    public void ARunThatFinishedEveryCellIsDoneWithFinalResults()
    {
        using var tracker = Begin();
        tracker.Progress(Progress());
        foreach (string resolution in new[] { "1080p", "4K", "8K" }) tracker.CellMeasured(Cell(resolution), requiredRepeats: 3);

        tracker.Complete();

        var method = Published;
        Assert.Equal(MethodState.Done, method.Session.State);
        Assert.Null(method.Session.Index);
        Assert.True(method.Results.Final);
        Assert.Empty(method.Issues);
    }

    [Fact]
    public void ARunThatEndedBeforeEveryCellFinishedIsSealedAndNotFinal()
    {
        using var tracker = Begin();
        tracker.CellMeasured(Cell("1080p"), requiredRepeats: 3);

        tracker.Complete();

        var method = Published;
        Assert.Equal(MethodState.Aborted, method.Session.State);
        Assert.False(method.Results.Final);
        Assert.Equal(1, method.Progress.Completed);
        Assert.Equal("The run ended after 1 of 3 planned cells.", method.Issues.Single().Message);
    }

    [Fact]
    public void DisposingAfterCompleteKeepsTheOutcome()
    {
        var tracker = Begin(steps: ["only|step"]);
        tracker.CellMeasured(Cell(), requiredRepeats: 3);
        tracker.Complete();

        tracker.Dispose();

        Assert.Equal(MethodState.Done, Published.Session.State);
        Assert.True(Published.Results.Final);
    }

    [Fact]
    public void LeavingWithoutCompleteReportsTheRunAsAbortedAndNotFinal()
    {
        var tracker = Begin();
        tracker.Progress(Progress());
        tracker.CellMeasured(Cell(), requiredRepeats: 3);

        tracker.Dispose();

        var method = Published;
        Assert.Equal(MethodState.Aborted, method.Session.State);
        Assert.Null(method.Session.Index);
        Assert.False(method.Results.Final);
        Assert.Equal(1, method.Progress.Completed);
    }

    [Fact]
    public void ANewRunReplacesThePreviousRunsState()
    {
        using (var old = Begin(steps: ["only|step"]))
        {
            old.CellMeasured(Cell(), requiredRepeats: 3);
            old.Complete();
        }

        using var next = Begin(steps: ["x|y|default|1080p|repeats=3"]);

        var method = Published;
        Assert.Equal(MethodState.Running, method.Session.State);
        Assert.Equal(1, method.Plan.TotalSteps);
        Assert.Null(method.Results.Provisional);
        Assert.Empty(method.Issues);
    }
}
