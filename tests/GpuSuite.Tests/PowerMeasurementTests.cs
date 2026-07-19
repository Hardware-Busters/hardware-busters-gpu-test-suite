using GpuSuite.App.Services;
using GpuSuite.Core.Config;
using GpuSuite.Core.Io;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Aggregation;
using GpuSuite.Reporting;
using Xunit;

namespace GpuSuite.Tests;

public sealed class PowerMeasurementTests : IDisposable
{
    private readonly string _originalCwd = Directory.GetCurrentDirectory();
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "GpuSuitePowerTests", Guid.NewGuid().ToString("N"));
    public PowerMeasurementTests() => Directory.CreateDirectory(_temp);

    [Fact]
    public void OnlyDirectPoweneticsQualifiesForEfficiency()
    {
        var direct = Run(PowerMeasurementKind.PoweneticsDirect, eligible: true);
        var telemetry = Run(PowerMeasurementKind.GpuReportedTelemetry, eligible: false);
        var synthetic = Run(PowerMeasurementKind.Synthetic, eligible: false);
        var replay = Run(PowerMeasurementKind.Replay, eligible: false);
        var unknown = Run(PowerMeasurementKind.Unknown, eligible: false);

        Assert.Equal(1, direct.FpsPerWatt);
        Assert.Null(telemetry.FpsPerWatt);
        Assert.Null(synthetic.FpsPerWatt);
        Assert.Null(replay.FpsPerWatt);
        Assert.Null(unknown.FpsPerWatt);
    }

    [Fact]
    public void ProvenanceUsesTheExactUserFacingLabels()
    {
        Assert.Equal("DIRECT · POWENETICS", PowerProvenance.Label(new PowerMeasurementMetadata { Kind = PowerMeasurementKind.PoweneticsDirect, HasPerRailData = true, HardwareBustersVerifiedPowerEligible = true }));
        Assert.Equal("APPROXIMATE · GPU TELEMETRY", PowerProvenance.Label(new PowerMeasurementMetadata { Kind = PowerMeasurementKind.GpuReportedTelemetry }));
        Assert.Equal("SYNTHETIC · NOT A HARDWARE RESULT", PowerProvenance.Label(new PowerMeasurementMetadata { Kind = PowerMeasurementKind.Synthetic }));
        Assert.Equal("NOT A HARDWARE RESULT", PowerProvenance.Label(new PowerMeasurementMetadata { Kind = PowerMeasurementKind.Unknown }));
    }

    [Fact]
    public void MutableEligibilityFlagCannotQualifyNonDirectOrLegacyPower()
    {
        var spoofedTelemetry = new PowerMeasurementMetadata
        {
            Kind = PowerMeasurementKind.GpuReportedTelemetry,
            HasPerRailData = true,
            HardwareBustersVerifiedPowerEligible = true
        };
        var legacyPowenetics = new PowerMeasurementMetadata
        {
            Kind = PowerMeasurementKind.PoweneticsDirect,
            HasPerRailData = true,
            HardwareBustersVerifiedPowerEligible = true,
            LegacyInferred = true
        };

        Assert.False(PowerProvenance.IsDirectEfficiencyEligible(spoofedTelemetry));
        Assert.False(PowerProvenance.IsDirectEfficiencyEligible(legacyPowenetics));
        Assert.Equal("LEGACY INFERRED · NOT A HARDWARE RESULT", PowerProvenance.Label(legacyPowenetics));
        Assert.False(PowerProvenance.AreCompatible(
            new PowerMeasurementMetadata { Kind = PowerMeasurementKind.PoweneticsDirect, Scope = "PCIe slot + auxiliary GPU rails", HasPerRailData = true, HardwareBustersVerifiedPowerEligible = true },
            new PowerMeasurementMetadata { Kind = PowerMeasurementKind.PoweneticsDirect, Scope = "GPU board power", HasPerRailData = true, HardwareBustersVerifiedPowerEligible = true }));
    }

    [Fact]
    public void SameKindMixedTrustIsIncompatibleAndSuppressedInAggregateAndReport()
    {
        var current = Run(PowerMeasurementKind.PoweneticsDirect, eligible: true);
        current.Power.Measurement.HasPerRailData = true;
        current.Power.Measurement.Scope = "PCIe slot + auxiliary GPU rails";
        var legacy = Run(PowerMeasurementKind.PoweneticsDirect, eligible: true);
        legacy.Power.Measurement.HasPerRailData = true;
        legacy.Power.Measurement.LegacyInferred = true;
        legacy.Power.Measurement.Scope = "PCIe slot + auxiliary GPU rails";

        var aggregate = new ResultAggregator().Aggregate("game", "scene", "direct", "direct", "1080p", [current, legacy]);
        Assert.Equal(PowerMeasurementKind.Unknown, aggregate.PowerMeasurement.Kind);
        Assert.Null(aggregate.AvgGpuPowerW);
        Assert.Null(aggregate.EnergyPerFrameJ);
        Assert.Null(aggregate.FpsPerWatt);

        var directAggregate = new ResultAggregator().Aggregate("game", "scene", "current", "current", "1080p", [current]);
        var legacyAggregate = new ResultAggregator().Aggregate("game", "scene", "legacy", "legacy", "1080p", [legacy]);
        string html = new HtmlReportGenerator().Generate(new SuiteResult { GpuName = "GPU", Aggregates = [directAggregate, legacyAggregate] });
        Assert.Contains("LEGACY INFERRED · NOT A HARDWARE RESULT", html);
        Assert.Contains("Power and efficiency comparison suppressed", html);
    }

    [Fact]
    public void AggregationNeverMixesPowerKinds()
    {
        var direct = Run(PowerMeasurementKind.PoweneticsDirect, eligible: true);
        var telemetry = Run(PowerMeasurementKind.GpuReportedTelemetry, eligible: false);
        var aggregate = new ResultAggregator().Aggregate("g", "s", "", "", "r", [direct, telemetry]);

        Assert.Null(aggregate.AvgGpuPowerW);
        Assert.Null(aggregate.FpsPerWatt);
        Assert.Contains("incompatible", aggregate.PowerComparisonNote!);
    }

    [Fact]
    public void PowerMetadataIsCarriedInJsonAndUsesStableSidecarName()
    {
        var run = Run(PowerMeasurementKind.PoweneticsDirect, eligible: true);
        string json = Json.ToString(run);
        Assert.Contains("measurement", json);
        Assert.Contains("PoweneticsDirect", json);
        Assert.Equal("power_metadata.json", run.PowerMetadataFile);
    }

    [Fact]
    public async Task LegacyResultsAreInferredReadOnlyAndNeverVerified()
    {
        var workspace = new Workspace(new ConfigService());
        workspace.SetRoot(_temp);
        string resultDir = Path.Combine(_temp, "Results", "GPU");
        string resultPath = Path.Combine(resultDir, "suite_result.json");
        var legacy = new SuiteResult { GpuName = "GPU", GeneratedUtc = DateTime.UtcNow, Aggregates = [new SceneResolutionAggregate { EnergyPerFrameJ = 1, FpsPerWatt = 1, Runs = [new RunResult { PowerProviderName = "Powenetics V2", Power = new PowerStats { AvgGpuPowerW = 100, EnergyPerFrameJ = 1 } }] }] };
        Json.Save(resultPath, legacy);
        string before = File.ReadAllText(resultPath);

        var loaded = await new ResultsService(workspace).LoadAllAsync();
        var measurement = Assert.Single(Assert.Single(loaded).Result.Aggregates).Runs.Single().Power.Measurement;
        Assert.Equal(PowerMeasurementKind.PoweneticsDirect, measurement.Kind);
        Assert.True(measurement.LegacyInferred);
        Assert.False(measurement.HardwareBustersVerifiedPowerEligible);
        Assert.Equal("LEGACY INFERRED · NOT A HARDWARE RESULT", PowerProvenance.Label(measurement));
        Assert.Null(Assert.Single(loaded).Result.Aggregates.Single().EnergyPerFrameJ);
        Assert.Equal(before, File.ReadAllText(resultPath));
    }

    [Fact]
    public void ReportShowsProvenanceAndWithholdsIneligibleEfficiency()
    {
        var lhm = Run(PowerMeasurementKind.GpuReportedTelemetry, eligible: false);
        var aggregate = new ResultAggregator().Aggregate("game", "scene", "", "", "1080p", [lhm]);
        string html = new HtmlReportGenerator().Generate(new SuiteResult { GpuName = "GPU", Aggregates = [aggregate] });

        Assert.Contains("APPROXIMATE · GPU TELEMETRY", html);
        Assert.Contains("Energy/frame and FPS/W are withheld unless direct eligible", html);
        Assert.Null(aggregate.EnergyPerFrameJ);
        Assert.Null(aggregate.FpsPerWatt);
    }

    private static RunResult Run(PowerMeasurementKind kind, bool eligible) => new()
    {
        Verdict = RunVerdict.Valid,
        Frames = new FrameStats { AvgFps = 100 },
        Power = new PowerStats
        {
            AvgGpuPowerW = 100,
            Measurement = new PowerMeasurementMetadata
            {
                Kind = kind,
                HasPerRailData = kind == PowerMeasurementKind.PoweneticsDirect && eligible,
                HardwareBustersVerifiedPowerEligible = eligible,
                Scope = kind == PowerMeasurementKind.PoweneticsDirect ? "PCIe slot + auxiliary GPU rails" : "test scope"
            }
        }
    };

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalCwd);
        if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
    }
}
