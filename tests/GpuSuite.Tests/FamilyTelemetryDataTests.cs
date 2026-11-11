using System.Diagnostics;
using System.Text.Json;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Models;
using GpuSuite.Measurement.Monitoring;
using Xunit;

namespace GpuSuite.Tests;

public sealed class FamilyTelemetryDataTests
{
    private static readonly Dictionary<string, string> ExpectedUnits = new()
    {
        ["gpuPowerW"] = "W", ["pcieSlot12vW"] = "W", ["pcieSlot3v3W"] = "W", ["pcie8pin1W"] = "W", ["pcie8pin2W"] = "W",
        ["pcie8pin3W"] = "W", ["cpuTotalW"] = "W", ["systemTotalW"] = "W",
        ["gpuTempC"] = "degC", ["gpuHotspotC"] = "degC", ["gpuVramTempC"] = "degC", ["gpuCoreClockMhz"] = "MHz",
        ["gpuMemClockMhz"] = "MHz", ["gpuLoadPct"] = "%", ["gpuVoltageV"] = "V", ["gpuBoardPowerW"] = "W", ["vramUsedMb"] = "MB",
        ["fanRpm1"] = "RPM", ["fanRpm2"] = "RPM", ["fanRpm3"] = "RPM", ["fanRpm4"] = "RPM", ["fanPct1"] = "%",
        ["cpuTempC"] = "degC", ["cpuLoadPct"] = "%", ["cpuPowerW"] = "W", ["cpuClockMhz"] = "MHz",
        ["frameTimeMs"] = "ms", ["displayedTimeMs"] = "ms", ["gpuBusyMs"] = "ms"
    };

    private static MonitoringChannel Channel(string kind, object sample, string status = "sampling",
        DataSourceMode mode = DataSourceMode.Live, bool fresh = true) =>
        new(kind, Guid.NewGuid(), "test source", mode, status, 7, DateTimeOffset.Parse("2026-11-10T10:00:00Z"), 1.5, sample)
        { Fresh = fresh, SourceAgeMs = 25 };

    private static JsonElement Project(MonitoringChannel[]? channels = null, MonitoringOperation? operation = null,
        MonitoringEvent? lastEvent = null, MonitoringEvent? lastFault = null) =>
        JsonSerializer.SerializeToElement(FamilyTelemetryData.Build(
            operation ?? new MonitoringOperation("idle"), channels ?? [], lastEvent, lastFault,
            FamilyMethodBlock.Build(null, true, default)));

    private static JsonElement Reading(JsonElement data, string key) => data.GetProperty("readings").GetProperty(key);

    [Fact]
    public void EveryReadingIsListedAsUnavailableNullWhenNothingIsRegistered()
    {
        var data = Project();

        var readings = data.GetProperty("readings").EnumerateObject().ToList();
        Assert.Equal(ExpectedUnits.Keys.Order(), readings.Select(r => r.Name).Order());
        foreach (var reading in readings)
        {
            Assert.Equal(JsonValueKind.Null, reading.Value.GetProperty("value").ValueKind);
            Assert.Equal(ExpectedUnits[reading.Name], reading.Value.GetProperty("unit").GetString());
            Assert.Equal(JsonValueKind.Null, reading.Value.GetProperty("source").ValueKind);
            Assert.Equal(JsonValueKind.Null, reading.Value.GetProperty("acquiredUtc").ValueKind);
            Assert.Equal("unavailable", reading.Value.GetProperty("freshness").GetString());
        }
    }

    [Fact]
    public void EmptyGroupsAndFixedGroupsAreAlwaysPresent()
    {
        var data = Project();

        Assert.Empty(data.GetProperty("sources").EnumerateObject());
        Assert.Equal(0, data.GetProperty("outputs").GetArrayLength());
        Assert.Equal("read-only; no command endpoint", data.GetProperty("control").GetProperty("mode").GetString());
        var extension = data.GetProperty("extension");
        Assert.Equal("gpu-suite/v1", extension.GetProperty("schema").GetString());
        Assert.Equal(JsonValueKind.Null, extension.GetProperty("lastEvent").ValueKind);
        Assert.Equal(JsonValueKind.Null, extension.GetProperty("lastFault").ValueKind);
    }

    [Fact]
    public void FreshLiveChannelPublishesValueUnitSourceAndFreshness()
    {
        var data = Project([Channel("power", new PowerSample { GpuTotalW = 123.5 })]);

        var reading = Reading(data, "gpuPowerW");
        Assert.Equal(123.5, reading.GetProperty("value").GetDouble());
        Assert.Equal("W", reading.GetProperty("unit").GetString());
        Assert.Equal("test source", reading.GetProperty("source").GetString());
        Assert.Equal("fresh", reading.GetProperty("freshness").GetString());
    }

    [Fact]
    public void AnUnsetPropertyStaysNullEvenWhenItsSiblingsAreFresh()
    {
        var data = Project([Channel("power", new PowerSample { GpuTotalW = 123.5 })]);

        var unset = Reading(data, "pcie8pin1W");
        Assert.Equal(JsonValueKind.Null, unset.GetProperty("value").ValueKind);
        Assert.Equal("unavailable", unset.GetProperty("freshness").GetString());
    }

    [Fact]
    public void NonFiniteValuesAreNullNeverZero()
    {
        var sample = new TelemetrySample { GpuTempC = double.NaN, GpuLoadPct = double.PositiveInfinity, CpuTempC = 61 };
        var data = Project([Channel("telemetry", sample)]);

        Assert.Equal(JsonValueKind.Null, Reading(data, "gpuTempC").GetProperty("value").ValueKind);
        Assert.Equal("unavailable", Reading(data, "gpuTempC").GetProperty("freshness").GetString());
        Assert.Equal(JsonValueKind.Null, Reading(data, "gpuLoadPct").GetProperty("value").ValueKind);
        Assert.Equal(61, Reading(data, "cpuTempC").GetProperty("value").GetDouble());
    }

    [Theory]
    [InlineData("sampling", true, "fresh")]
    [InlineData("sampling", false, "stale")]
    [InlineData("stopped", true, "retained")]
    [InlineData("stopped", false, "retained")]
    public void FreshnessFollowsTheChannelStatus(string status, bool fresh, string expected)
    {
        var data = Project([Channel("power", new PowerSample { GpuTotalW = 100 }, status, fresh: fresh)]);

        var reading = Reading(data, "gpuPowerW");
        Assert.Equal(expected, reading.GetProperty("freshness").GetString());
        Assert.Equal(100, reading.GetProperty("value").GetDouble());
    }

    [Fact]
    public void ASimulatedChannelIsLabelledInTheReadingAndItsSource()
    {
        var data = Project([Channel("power", new PowerSample { GpuTotalW = 50 }, mode: DataSourceMode.Synthetic)]);

        Assert.Equal("test source (simulation)", Reading(data, "gpuPowerW").GetProperty("source").GetString());
        var source = data.GetProperty("sources").GetProperty("power");
        Assert.True(source.GetProperty("simulation").GetBoolean());
        Assert.Equal("Synthetic", source.GetProperty("mode").GetString());
    }

    [Fact]
    public void AReplayedChannelIsLabelledAsSimulationAndKeepsItsMode()
    {
        var data = Project([Channel("power", new PowerSample { GpuTotalW = 50 }, mode: DataSourceMode.Replay)]);

        Assert.Equal("test source (simulation)", Reading(data, "gpuPowerW").GetProperty("source").GetString());
        var source = data.GetProperty("sources").GetProperty("power");
        Assert.True(source.GetProperty("simulation").GetBoolean());
        Assert.Equal("Replay", source.GetProperty("mode").GetString());
    }

    [Fact]
    public void BoardPowerFromTheTelemetryChannelIsLabelledApproximateAndNothingElseIs()
    {
        var sample = new TelemetrySample { GpuBoardPowerW = 180, GpuTempC = 60 };
        var data = Project([Channel("telemetry", sample)]);

        Assert.Equal("test source (approximate)", Reading(data, "gpuBoardPowerW").GetProperty("source").GetString());
        Assert.Equal("test source", Reading(data, "gpuTempC").GetProperty("source").GetString());
    }

    [Fact]
    public void ABoardPowerSourceThatIsAlreadyApproximateIsNotLabelledTwice()
    {
        var channel = Channel("telemetry", new TelemetrySample { GpuBoardPowerW = 180 }, mode: DataSourceMode.Synthetic)
            with { Source = "LibreHardwareMonitor (approximate)" };

        var source = Reading(Project([channel]), "gpuBoardPowerW").GetProperty("source").GetString();

        Assert.Equal("LibreHardwareMonitor (approximate) (simulation)", source);
    }

    [Fact]
    public void ASourceEntryDescribesTheChannelWithoutItsSample()
    {
        var data = Project([Channel("frames", new FrameSample { FrameTimeMs = 16.6 })]);

        var source = data.GetProperty("sources").GetProperty("frames");
        Assert.Equal(
            ["fresh", "mode", "receivedUtc", "sequence", "simulation", "source", "sourceAgeMs", "sourceTimeSec", "status"],
            source.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("Live", source.GetProperty("mode").GetString());
        Assert.False(source.GetProperty("simulation").GetBoolean());
        Assert.Equal(7, source.GetProperty("sequence").GetInt64());
        Assert.Equal(1.5, source.GetProperty("sourceTimeSec").GetDouble());
        Assert.Equal(25, source.GetProperty("sourceAgeMs").GetDouble());
        Assert.True(source.GetProperty("fresh").GetBoolean());
        Assert.Equal(DateTimeOffset.Parse("2026-11-10T10:00:00Z"), source.GetProperty("receivedUtc").GetDateTimeOffset());
    }

    [Fact]
    public void OnlyRegisteredChannelsAppearAsSources()
    {
        var data = Project([Channel("power", new PowerSample { GpuTotalW = 1 })]);

        Assert.Equal(["power"], data.GetProperty("sources").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void AcquisitionTimeIsNeverInventedForAnyReading()
    {
        var channels = new[]
        {
            Channel("power", new PowerSample { GpuTotalW = 1 }),
            Channel("telemetry", new TelemetrySample { GpuTempC = 2 }),
            Channel("frames", new FrameSample { FrameTimeMs = 3 })
        };

        foreach (var reading in Project(channels).GetProperty("readings").EnumerateObject())
            Assert.Equal(JsonValueKind.Null, reading.Value.GetProperty("acquiredUtc").ValueKind);
    }

    [Fact]
    public void TheOperationMapsOntoTheTestGroup()
    {
        var operation = new MonitoringOperation("capturing", "game-x", "scene-y", "4K", 2, 3, 250.5, 35.5, "valid", "none");

        var test = Project(operation: operation).GetProperty("test");

        Assert.Equal("capturing", test.GetProperty("state").GetString());
        Assert.Equal("game-x", test.GetProperty("game").GetString());
        Assert.Equal("scene-y", test.GetProperty("scene").GetString());
        Assert.Equal("4K", test.GetProperty("resolution").GetString());
        Assert.Equal(2, test.GetProperty("repeat").GetInt32());
        Assert.Equal(3, test.GetProperty("totalRepeats").GetInt32());
        Assert.Equal(250.5, test.GetProperty("targetPowerW").GetDouble());
        Assert.Equal(35.5, test.GetProperty("noiseDba").GetDouble());
        Assert.Equal("valid", test.GetProperty("verdict").GetString());
        Assert.Equal("none", test.GetProperty("fault").GetString());
    }

    [Fact]
    public void UnsetOperationFieldsStayNull()
    {
        var test = Project().GetProperty("test");

        Assert.Equal("idle", test.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, test.GetProperty("game").ValueKind);
        Assert.Equal(JsonValueKind.Null, test.GetProperty("repeat").ValueKind);
    }

    [Fact]
    public void EventsCarryOnlyTheTimeStageAndSeverity()
    {
        var logged = new MonitoringEvent(DateTime.Parse("2026-11-10T10:00:00Z").ToUniversalTime(), "capture", "Info");
        var faulted = new MonitoringEvent(DateTime.Parse("2026-11-10T10:00:05Z").ToUniversalTime(), "launch", "Error");

        var extension = Project(lastEvent: logged, lastFault: faulted).GetProperty("extension");

        foreach (var (name, expected) in new[] { ("lastEvent", logged), ("lastFault", faulted) })
        {
            var entry = extension.GetProperty(name);
            Assert.Equal(["severity", "stage", "utc"], entry.EnumerateObject().Select(p => p.Name).Order());
            Assert.Equal(expected.Stage, entry.GetProperty("stage").GetString());
            Assert.Equal(expected.Severity, entry.GetProperty("severity").GetString());
            Assert.Equal(expected.Utc, entry.GetProperty("utc").GetDateTime().ToUniversalTime());
        }
    }

    [Fact]
    public void TheProjectionIsValidJsonWithoutNonFiniteNumbers()
    {
        var sample = new TelemetrySample { GpuTempC = double.NaN, GpuBoardPowerW = double.NegativeInfinity };

        string json = JsonSerializer.Serialize(FamilyTelemetryData.Build(
            new MonitoringOperation("capturing"), [Channel("telemetry", sample)], null, null,
            FamilyMethodBlock.Build(null, true, default)));

        using var parsed = JsonDocument.Parse(json);
        Assert.DoesNotContain("NaN", json);
        Assert.DoesNotContain("Infinity", json);
    }

    [Fact]
    public void FreshnessIsOnlyAssertedWhileOnlineValidAndRecent()
    {
        MonitoringChannel Sampled(long receivedTicks, double? sourceTime = 1) =>
            Channel("power", new PowerSample { GpuTotalW = 100 }) with { ReceivedTicks = receivedTicks, SourceTimeSec = sourceTime };
        bool IsFresh(MonitoringChannel channel, bool online) => ChannelFreshness.Evaluate([channel], online).Single().Fresh;

        Assert.True(IsFresh(Sampled(Stopwatch.GetTimestamp()), online: true));
        Assert.False(IsFresh(Sampled(Stopwatch.GetTimestamp()), online: false));
        Assert.False(IsFresh(Sampled(Stopwatch.GetTimestamp() - 3 * Stopwatch.Frequency), online: true));
        Assert.False(IsFresh(Sampled(Stopwatch.GetTimestamp(), sourceTime: null), online: true));
    }

    [Fact]
    public void ASourceHasNoAgeUntilSomethingWasReceived()
    {
        var evaluated = ChannelFreshness.Evaluate(
            [Channel("power", new PowerSample { GpuTotalW = 100 }) with { ReceivedTicks = 0 }], online: true).Single();

        Assert.Null(evaluated.SourceAgeMs);
        Assert.False(evaluated.Fresh);
    }

    [Fact]
    public void EvaluatingFreshnessLeavesTheInputUntouched()
    {
        var original = Channel("power", new PowerSample { GpuTotalW = 100 }, fresh: false) with { ReceivedTicks = Stopwatch.GetTimestamp() };

        _ = ChannelFreshness.Evaluate([original], online: true);

        Assert.False(original.Fresh);
        Assert.Equal(25, original.SourceAgeMs);
    }
}
