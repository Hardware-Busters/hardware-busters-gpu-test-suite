using GpuSuite.App.Services;
using GpuSuite.App.ViewModels;
using GpuSuite.Core.Config;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Diagnostics;
using GpuSuite.Engine.Launch;
using GpuSuite.Load;
using GpuSuite.Reporting;
using Xunit;

namespace GpuSuite.Tests;

/// <summary>Regression locks for the 2026-09-21 code-review honesty fixes: a failed launch must fail
/// honestly (never implicit synthetic), the UI compare must gate FPS on verified settings, a cooler
/// step without measured power must neither settle nor interpolate, and the cooler power badge must
/// be fail-closed.</summary>
public sealed class ReviewFixRegressionTests : IDisposable
{
    private readonly string _originalCwd = Directory.GetCurrentDirectory();

    public void Dispose()
    {
        try { Directory.SetCurrentDirectory(_originalCwd); } catch { }
    }

    private static GameProfile StandaloneProfile(string target) => new()
    {
        Id = "regression-test-game",
        Name = "Regression Test Game",
        CaptureProcessName = "gpusuite-regression-test-proc",
        StartupGraceSeconds = 1,
        Launch = new LaunchSpec { Store = "Standalone", Target = target }
    };

    private static string NonexistentExecutable() => Path.Combine(
        Path.GetTempPath(), "gpusuite-regression-" + Guid.NewGuid().ToString("N"), "game.exe");

    [Fact]
    public void StandaloneLaunchFailureIsHonest()
    {
        using var log = new RunLogger(null, echoToConsole: false);
        var launcher = new GameLauncher(log, simulateOnFailure: false);
        var result = launcher.Launch(StandaloneProfile(NonexistentExecutable()));

        Assert.False(result.Launched);
        Assert.False(result.Simulated);
    }

    [Fact]
    public void StandaloneLaunchSimulatesOnlyWhenExplicitlyOptedIn()
    {
        using var log = new RunLogger(null, echoToConsole: false);
        var launcher = new GameLauncher(log, simulateOnFailure: true);
        var result = launcher.Launch(StandaloneProfile(NonexistentExecutable()));

        Assert.False(result.Launched);
        Assert.True(result.Simulated);
    }

    private static SceneResolutionAggregate Cell(
        string scene, double fps, double low, Dictionary<string, string>? fingerprint) => new()
    {
        GameId = "g",
        SceneId = scene,
        ResolutionName = "1080p",
        AvgFps = fps,
        P1LowFps = low,
        Runs = [new RunResult { Verdict = RunVerdict.Valid, SettingsFingerprint = fingerprint }]
    };

    private static ResultsViewModel ViewModel() =>
        new(new ResultsService(new Workspace(new ConfigService())));

    private static SuiteResultEntry Entry(string name, params SceneResolutionAggregate[] cells) =>
        new(name, name, name, name, new SuiteResult { GpuName = name, Aggregates = [.. cells] });

    [Fact]
    public void UiCompareGatesFpsOnVerifiedSettings()
    {
        var vm = ViewModel();
        vm.ComparisonBaseline = Entry("base",
            Cell("mismatch", 100, 80, new() { ["Quality"] = "Ultra" }),
            Cell("match", 100, 80, new() { ["Quality"] = "Ultra" }));
        vm.ComparisonTarget = Entry("target",
            Cell("mismatch", 110, 88, new() { ["Quality"] = "High" }),
            Cell("match", 110, 88, new() { ["Quality"] = "Ultra" }));

        Assert.Equal(2, vm.ComparisonRows.Count);

        var gated = vm.ComparisonRows.Single(r => r.Label.Contains("· mismatch ·"));
        Assert.Null(gated.FpsDeltaPct);
        Assert.Null(gated.LowDeltaPct);
        Assert.NotNull(gated.SettingsComparisonReason);

        var open = vm.ComparisonRows.Single(r => r.Label.Contains("· match ·"));
        Assert.Equal(10.0, open.FpsDeltaPct!.Value, 6);
        Assert.Equal(10.0, open.LowDeltaPct!.Value, 6);
        Assert.Null(open.SettingsComparisonReason);

        Assert.Contains("suppressed", vm.ComparisonSummary);
    }

    private sealed class FakeLoad : IGpuLoad
    {
        public string GpuName => "Regression Test GPU";
        public double Intensity { get; set; }
        public bool Running { get; private set; }
        public void Start() => Running = true;
        public void Stop() => Running = false;
        public void Dispose() { }
    }

    [Fact]
    public async Task MissingPowerNeverSettlesAndNeverInterpolates()
    {
        double t = 0;
        TelemetrySample? Telemetry()
        {
            t += 0.5;
            return new TelemetrySample { TimeSec = t, GpuTempC = 55.0 };
        }

        using var load = new FakeLoad();
        var runner = new CoolerTestRunner(load, readWatts: () => null, readTelemetry: Telemetry);
        var opt = new CoolerSweepOptions
        {
            FromW = 100,
            ToW = 100,
            StepW = 25,
            ReferencePowerW = 100,
            NoiseLevels = [new CoolerNoiseSetting(30, 50)],
            MinSoak = TimeSpan.Zero,
            MaxSoak = TimeSpan.FromSeconds(3),
            SettleWindow = TimeSpan.FromSeconds(2),
        };

        var result = await runner.RunAsync(opt, onTick: null, CancellationToken.None);

        var level = Assert.Single(result.Levels);
        var step = Assert.Single(level.Steps);
        Assert.False(step.Settled);
        Assert.Null(step.AchievedW);
        Assert.Null(level.RefGpuTempC);
    }

    [Fact]
    public void CoolerPowerBadgeIsFailClosed()
    {
        var gen = new CoolerReportGenerator();
        string empty = gen.Generate(new CoolerSweepResult
        {
            GpuName = "T", Vendor = "V", ReferencePowerW = 250, PowerSource = ""
        });
        Assert.Contains("class=\"badge synth\"", empty);
        Assert.DoesNotContain("badge live", empty);

        string live = gen.Generate(new CoolerSweepResult
        {
            GpuName = "T", Vendor = "V", ReferencePowerW = 250, PowerSource = "Powenetics V2"
        });
        Assert.Contains("class=\"badge live\"", live);
    }

    [Fact]
    public void ExplicitPipelineSimulationDoesNotRequireCaptureProcess()
    {
        var game = new GameProfile
        {
            Id = "synthetic-load",
            Name = "Synthetic Pipeline Test",
            Enabled = true,
            Repeats = 3,
            Launch = new LaunchSpec { Store = "Standalone" },
            Scenes = [new SceneProfile { Id = "fixed-window", Name = "Fixed Window" }]
        };

        var result = new GameReadinessChecker(Path.GetTempPath()).CheckOne(game, new GameCatalog());

        Assert.DoesNotContain(result.Checks,
            check => check.Name == "Capture target" && check.Status == CheckStatus.Blocker);
    }

}
