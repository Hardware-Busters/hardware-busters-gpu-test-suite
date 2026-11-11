using System.Text.Json;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Orchestration;
using GpuSuite.Measurement.Monitoring;
using Xunit;

namespace GpuSuite.Tests;

[Collection("Monitoring")]
public sealed class MethodStateTrackerTests : IDisposable
{
    private static readonly PlannedStep[] PlanSteps = [Step("g1", "1080p"), Step("g1", "4K"), Step("g2", "1080p")];

    private static PlannedStep Step(string game, string resolution, int repeats = 3)
    {
        string key = $"{game}|s1|default|{resolution}";
        return new PlannedStep(key, game, $"{key}|repeats={repeats}", PlannedStep.LabelOf($"Game {game}", "Scene One", "default", resolution));
    }

    private DateTime _now = new(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc);

    public MethodStateTrackerTests() => MonitoringState.Method(null);

    public void Dispose() => MonitoringState.Method(null);

    private static MonitoringMethod Published => MonitoringState.ReadMethod()!;

    private static SystemInfo Machine(string gpu = "Test GPU") => new()
    {
        GpuName = gpu, CpuName = "Test CPU", OsVersion = "Test OS", TotalRamGb = 32, LhmAvailable = true
    };

    private MethodStateTracker Begin(IReadOnlyList<PlannedStep>? steps = null, SystemInfo? system = null, bool simulated = false)
        => MethodStateTracker.Begin(system ?? Machine(), steps ?? PlanSteps, simulated, () => _now);

    private static BenchmarkProgress Progress(int cellIndex = 2, string phase = "Running", bool completed = false, string game = "g1", string resolution = "4K")
        => new(game, "Game One", "s1", "Scene One", "default", resolution, 2, 3, cellIndex, PlanSteps.Length, phase, completed);

    private static RunResult Run(bool valid, params string[] issues)
    {
        var run = new RunResult
        {
            GameId = "g1", SceneId = "s1", ResolutionName = "4K", Verdict = valid ? RunVerdict.Valid : RunVerdict.Invalid
        };
        run.ValidationIssues.AddRange(issues);
        return run;
    }

    private static SceneResolutionAggregate Cell(string resolution = "4K", int validRuns = 3, int totalRuns = 3, string game = "g1", params RunResult[] runs)
    {
        var cell = new SceneResolutionAggregate
        {
            GameId = game, SceneId = "s1", ResolutionName = resolution, ValidRuns = validRuns, TotalRuns = totalRuns,
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
        using var otherSteps = Begin(steps: [.. PlanSteps, Step("g3", "1080p")]);
        var otherStepsPublished = Published;
        using var otherRepeats = Begin(steps: [.. PlanSteps[..2], Step("g2", "1080p", repeats: 5)]);
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
        Assert.Equal(PlanSteps[1].Label, method.Session.Step);
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
        tracker.CellMeasured(Cell(), requiredRepeats: 3);

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
        tracker.CellMeasured(Cell("1080p"), requiredRepeats: 3);
        tracker.CellMeasured(Cell(), requiredRepeats: 3);
        tracker.CellMeasured(Cell("1080p", game: "g2"), requiredRepeats: 3);

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
        var tracker = Begin(steps: [Step("g1", "4K")]);
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
        using (var old = Begin(steps: [Step("g1", "4K")]))
        {
            old.CellMeasured(Cell(), requiredRepeats: 3);
            old.Complete();
        }

        using var next = Begin(steps: [Step("x", "1080p")]);

        var method = Published;
        Assert.Equal(MethodState.Running, method.Session.State);
        Assert.Equal(1, method.Plan.TotalSteps);
        Assert.Null(method.Results.Provisional);
        Assert.Empty(method.Issues);
    }

    private MonitoringMethod ValidSnapshot()
    {
        var method = Published;
        var block = JsonSerializer.SerializeToElement(FamilyMethodBlock.Build(method, online: true, _now));
        Assert.Empty(MethodBlockRules.Validate(block));
        Assert.True(block.GetProperty("available").GetBoolean(), block.GetProperty("error").GetString());
        return method;
    }

    private MonitoringMethod AssertSteps(params StepState[] expected)
    {
        var method = ValidSnapshot();
        Assert.NotNull(method.Plan.Steps);
        Assert.Equal(expected, method.Plan.Steps.Select(step => step.State));
        return method;
    }

    [Fact]
    public void TheChecklistStartsWithEveryPlannedLabelInTodoState()
    {
        using var tracker = Begin();
        var method = AssertSteps(StepState.Todo, StepState.Todo, StepState.Todo);
        Assert.Equal(PlanSteps.Select(step => step.Label), method.Plan.Steps!.Select(step => step.Label));
    }

    [Fact]
    public void ProgressMarksThePlanPositionNowAndSkipsEarlierUnfinishedSteps()
    {
        using var tracker = Begin();
        AssertSteps(StepState.Todo, StepState.Todo, StepState.Todo);
        tracker.Progress(Progress(cellIndex: 1, resolution: "1080p"));
        AssertSteps(StepState.Now, StepState.Todo, StepState.Todo);

        // Game g1 never finishes; g2 is the second visit but the third planned step.
        tracker.Progress(Progress(cellIndex: 2, game: "g2", resolution: "1080p"));

        var method = AssertSteps(StepState.Skipped, StepState.Skipped, StepState.Now);
        Assert.Equal(3, method.Session.Index);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeasuredAndResumedCellsBecomeDoneWithoutChangingEarlierDoneSteps(bool resumed)
    {
        using var tracker = Begin();
        AssertSteps(StepState.Todo, StepState.Todo, StepState.Todo);
        tracker.CellMeasured(Cell("1080p"), requiredRepeats: 3);
        AssertSteps(StepState.Done, StepState.Todo, StepState.Todo);
        tracker.Progress(Progress());
        AssertSteps(StepState.Done, StepState.Now, StepState.Todo);

        if (resumed) tracker.CellResumed(Cell(), requiredRepeats: 3);
        else tracker.CellMeasured(Cell(), requiredRepeats: 3);

        AssertSteps(StepState.Done, StepState.Done, StepState.Todo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReachingALaterMeasuredOrResumedCellSkipsAllEarlierUnfinishedSteps(bool resumed)
    {
        using var tracker = Begin();
        AssertSteps(StepState.Todo, StepState.Todo, StepState.Todo);
        tracker.Progress(Progress(cellIndex: 1, resolution: "1080p"));
        AssertSteps(StepState.Now, StepState.Todo, StepState.Todo);
        var laterCell = Cell("1080p", game: "g2");

        if (resumed) tracker.CellResumed(laterCell, requiredRepeats: 3);
        else tracker.CellMeasured(laterCell, requiredRepeats: 3);

        AssertSteps(StepState.Skipped, StepState.Skipped, StepState.Done);
    }

    [Fact]
    public void BetweenTwoMeasuredCellsNoChecklistEntryIsNow()
    {
        using var tracker = Begin();
        AssertSteps(StepState.Todo, StepState.Todo, StepState.Todo);
        tracker.Progress(Progress(cellIndex: 1, resolution: "1080p"));
        AssertSteps(StepState.Now, StepState.Todo, StepState.Todo);
        tracker.CellMeasured(Cell("1080p"), requiredRepeats: 3);
        AssertSteps(StepState.Done, StepState.Todo, StepState.Todo);
        tracker.Progress(Progress(cellIndex: 1, resolution: "1080p", phase: "Completed", completed: true));
        var method = AssertSteps(StepState.Done, StepState.Todo, StepState.Todo);
        Assert.Null(method.Session.Index);
        Assert.Equal("between cells", method.Session.Phase);

        tracker.Progress(Progress());
        AssertSteps(StepState.Done, StepState.Now, StepState.Todo);
    }

    [Fact]
    public void CompleteLeavesUnfinishedStepsForGameEndedAndKeepsFinishedStepsDone()
    {
        using var tracker = Begin();
        AssertSteps(StepState.Todo, StepState.Todo, StepState.Todo);
        tracker.CellMeasured(Cell("1080p"), requiredRepeats: 3);
        AssertSteps(StepState.Done, StepState.Todo, StepState.Todo);
        tracker.GameEnded("g1");
        AssertSteps(StepState.Done, StepState.Skipped, StepState.Todo);

        tracker.Complete();

        var method = AssertSteps(StepState.Done, StepState.Skipped, StepState.Todo);
        Assert.Equal(MethodState.Aborted, method.Session.State);
        Assert.False(method.Results.Final);
        tracker.Dispose();
        AssertSteps(StepState.Done, StepState.Skipped, StepState.Todo);
    }

    [Fact]
    public void CompleteWithEveryPlannedCellFinishedLeavesAllStepsDone()
    {
        using var tracker = Begin();
        AssertSteps(StepState.Todo, StepState.Todo, StepState.Todo);
        tracker.CellMeasured(Cell("1080p"), requiredRepeats: 3);
        AssertSteps(StepState.Done, StepState.Todo, StepState.Todo);
        tracker.CellResumed(Cell(), requiredRepeats: 3);
        AssertSteps(StepState.Done, StepState.Done, StepState.Todo);
        tracker.CellMeasured(Cell("1080p", game: "g2"), requiredRepeats: 3);
        AssertSteps(StepState.Done, StepState.Done, StepState.Done);

        tracker.Complete();

        var method = AssertSteps(StepState.Done, StepState.Done, StepState.Done);
        Assert.Equal(MethodState.Done, method.Session.State);
        Assert.True(method.Results.Final);
        tracker.Dispose();
        AssertSteps(StepState.Done, StepState.Done, StepState.Done);
    }

    [Fact]
    public void DisposeSkipsOnlyTheStepInProgressAndLeavesUnreachedStepsTodo()
    {
        using var tracker = Begin();
        AssertSteps(StepState.Todo, StepState.Todo, StepState.Todo);
        tracker.CellMeasured(Cell("1080p"), requiredRepeats: 3);
        AssertSteps(StepState.Done, StepState.Todo, StepState.Todo);
        tracker.Progress(Progress());
        AssertSteps(StepState.Done, StepState.Now, StepState.Todo);

        tracker.Dispose();

        var method = AssertSteps(StepState.Done, StepState.Skipped, StepState.Todo);
        Assert.Equal(MethodState.Aborted, method.Session.State);
        Assert.Null(method.Session.Index);
    }

    [Fact]
    public void UnknownCellKeysHaveNoPlanIndexAndDoNotInventAStepState()
    {
        using var tracker = Begin();
        AssertSteps(StepState.Todo, StepState.Todo, StepState.Todo);

        tracker.Progress(Progress(game: "unknown"));

        Assert.Null(AssertSteps(StepState.Todo, StepState.Todo, StepState.Todo).Session.Index);
    }

    [Fact]
    public void A300StepPlanListsItsFirst256StepsInPlanOrder()
    {
        var steps = Enumerable.Range(1, 300).Select(i => Step("g1", $"{i}p")).ToArray();
        using var tracker = Begin(steps: steps);
        var method = ValidSnapshot();
        Assert.Equal(300, method.Plan.TotalSteps);
        Assert.Equal(256, method.Plan.Steps!.Count);
        Assert.Equal(steps.Take(256).Select(step => step.Label), method.Plan.Steps.Select(step => step.Label));
        Assert.All(method.Plan.Steps, step => Assert.Equal(StepState.Todo, step.State));

        tracker.Progress(Progress(cellIndex: 1, resolution: "257p"));

        method = ValidSnapshot();
        Assert.Equal(257, method.Session.Index);
        Assert.Equal(256, method.Plan.Steps!.Count);
        Assert.All(method.Plan.Steps, step => Assert.Equal(StepState.Skipped, step.State));
        tracker.Dispose();
        ValidSnapshot();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GameEndedSkipsOnlyItsUnfinishedStepsAndClearsAnInterruptedCell(bool interrupted)
    {
        using var tracker = Begin();
        tracker.CellMeasured(Cell("1080p"), requiredRepeats: 3);
        AssertSteps(StepState.Done, StepState.Todo, StepState.Todo);
        if (interrupted)
        {
            tracker.Progress(Progress());
            AssertSteps(StepState.Done, StepState.Now, StepState.Todo);
        }
        var previousSession = Published.Session;

        tracker.GameEnded("g1");

        var method = AssertSteps(StepState.Done, StepState.Skipped, StepState.Todo);
        Assert.Equal(1, method.Progress.Completed);
        Assert.Equal(MethodState.Running, method.Session.State);
        if (interrupted)
        {
            Assert.Null(method.Session.Step);
            Assert.Null(method.Session.Index);
            Assert.Equal("between cells", method.Session.Phase);
        }
        else Assert.Equal(previousSession, method.Session);
    }

    [Fact]
    public void GameEndedLeavesAnotherGamesActiveStepAndSessionAlone()
    {
        using var tracker = Begin(steps: [Step("g2", "1080p"), Step("g1", "4K")]);
        tracker.Progress(Progress(cellIndex: 1, game: "g2", resolution: "1080p"));
        var session = AssertSteps(StepState.Now, StepState.Todo).Session;

        tracker.GameEnded("g1");

        Assert.Equal(session, AssertSteps(StepState.Now, StepState.Skipped).Session);
    }

    [Fact]
    public void APrecheckSkippedGameEndsBeforeTheNextVisitAndKeepsThePlanIndex()
    {
        using var tracker = Begin();
        AssertSteps(StepState.Todo, StepState.Todo, StepState.Todo);

        tracker.GameEnded("g1");
        AssertSteps(StepState.Skipped, StepState.Skipped, StepState.Todo);
        tracker.Progress(Progress(cellIndex: 1, game: "g2", resolution: "1080p"));

        var method = AssertSteps(StepState.Skipped, StepState.Skipped, StepState.Now);
        Assert.Equal(3, method.Session.Index);
        Assert.Equal(0, method.Progress.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateKeysFinishEachOccurrenceInOrder(bool resumed)
    {
        var first = Step("g1", "4K");
        var second = first with { Label = PlannedStep.LabelOf("Game g1", "Scene One", "default", "3840x2160") };
        using var tracker = Begin(steps: [first, second]);
        AssertSteps(StepState.Todo, StepState.Todo);
        tracker.Progress(Progress());
        Assert.Equal(1, AssertSteps(StepState.Now, StepState.Todo).Session.Index);

        tracker.CellMeasured(Cell(), requiredRepeats: 3);
        AssertSteps(StepState.Done, StepState.Todo);
        tracker.Progress(Progress(phase: "Completed", completed: true));
        AssertSteps(StepState.Done, StepState.Todo);
        tracker.Progress(Progress());
        var method = AssertSteps(StepState.Done, StepState.Now);
        Assert.Equal(2, method.Session.Index);
        Assert.Equal(second.Label, method.Session.Step);

        if (resumed) tracker.CellResumed(Cell(), requiredRepeats: 3);
        else tracker.CellMeasured(Cell(), requiredRepeats: 3);
        AssertSteps(StepState.Done, StepState.Done);
        tracker.Progress(Progress(phase: "Completed", completed: true));
        tracker.GameEnded("g1");
        AssertSteps(StepState.Done, StepState.Done);
        tracker.Complete();

        method = AssertSteps(StepState.Done, StepState.Done);
        Assert.Equal(2, method.Progress.Completed);
        Assert.Equal(MethodState.Done, method.Session.State);
        Assert.True(method.Results.Final);
    }

    [Fact]
    public void LabelsAndSessionStepsNeverPublishTheWindowsAccountName()
    {
        string user = Environment.UserName;
        if (string.IsNullOrWhiteSpace(user)) return;
        string label = PlannedStep.LabelOf($"Game {user}", $"Scene {user}", $"Variant {user}", $"Resolution {user}");
        Assert.Equal("Game [user] / Scene [user] / Variant [user] / Resolution [user]", label);
        using var tracker = Begin(steps: [Step("g1", "4K") with { Label = label }]);
        Assert.Equal(label, AssertSteps(StepState.Todo).Plan.Steps![0].Label);

        var progress = new BenchmarkProgress("g1", $"Game {user}", "s1", $"Scene {user}", "default", "4K", 1, 3, 1, 1, "Running", false);
        tracker.Progress(progress);
        Assert.Equal(label, AssertSteps(StepState.Now).Session.Step);

        tracker.Progress(progress with { GameId = "unknown" });
        var method = AssertSteps(StepState.Skipped);
        Assert.Null(method.Session.Index);
        Assert.Equal("Game [user] / Scene [user] / default / 4K", method.Session.Step);
    }

    [Fact]
    public void AnUnknownKeySkipsTheActiveStepWithoutNamingAPlanIndex()
    {
        using var tracker = Begin();
        tracker.Progress(Progress(cellIndex: 1, resolution: "1080p"));
        AssertSteps(StepState.Now, StepState.Todo, StepState.Todo);

        tracker.Progress(Progress(game: "unknown"));

        var method = AssertSteps(StepState.Skipped, StepState.Todo, StepState.Todo);
        Assert.Null(method.Session.Index);
        Assert.Equal(0, method.Progress.Completed);
    }

    [Fact]
    public void UnplannedMeasuredCellsCannotMakeUnfinishedPlannedCellsFinal()
    {
        using var tracker = Begin();
        tracker.CellMeasured(Cell("1080p"), requiredRepeats: 3);
        AssertSteps(StepState.Done, StepState.Todo, StepState.Todo);
        tracker.CellMeasured(Cell(), requiredRepeats: 3);
        AssertSteps(StepState.Done, StepState.Done, StepState.Todo);
        tracker.CellMeasured(Cell("8K"), requiredRepeats: 3);
        Assert.Null(ValidSnapshot().Plan.Steps);

        tracker.Complete();

        var method = ValidSnapshot();
        Assert.Equal(PlanSteps.Length, method.Progress.Completed);
        Assert.Equal(MethodState.Aborted, method.Session.State);
        Assert.False(method.Results.Final);
    }

    [Fact]
    public void A300StepPlanWithholdsTheListAfterThe257thFinishedCell()
    {
        const int totalSteps = 300;
        const int listedSteps = 256;
        var steps = Enumerable.Range(1, totalSteps).Select(i => Step("g1", $"{i}p")).ToArray();
        using var tracker = Begin(steps: steps);
        ValidSnapshot();
        for (int i = 1; i <= listedSteps; i++)
        {
            tracker.CellMeasured(Cell($"{i}p"), requiredRepeats: 3);
            ValidSnapshot();
        }
        var method = AssertSteps(Enumerable.Repeat(StepState.Done, listedSteps).ToArray());
        Assert.Equal(listedSteps, method.Progress.Completed);
        Assert.Equal(steps.Take(listedSteps).Select(step => step.Label), method.Plan.Steps!.Select(step => step.Label));

        tracker.CellMeasured(Cell($"{listedSteps + 1}p"), requiredRepeats: 3);

        method = ValidSnapshot();
        Assert.Equal(listedSteps + 1, method.Progress.Completed);
        Assert.Equal(totalSteps, method.Progress.Total);
        Assert.Null(method.Plan.Steps);
    }
}
