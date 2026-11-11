using System.Globalization;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Models;

namespace GpuSuite.Measurement.Monitoring;

internal static class FamilyTelemetryData
{
    private const string SimulationMarker = "(simulation)";
    private const string ApproximateMarker = "(approximate)";

    internal static Dictionary<string, object?> Build(MonitoringOperation operation,
        IReadOnlyList<MonitoringChannel> channels, MonitoringEvent? lastEvent, MonitoringEvent? lastFault,
        IReadOnlyDictionary<string, object?> method)
    {
        var power = channels.FirstOrDefault(c => c.Kind == "power");
        var telemetry = channels.FirstOrDefault(c => c.Kind == "telemetry");
        var frames = channels.FirstOrDefault(c => c.Kind == "frames");
        var powerSample = power?.Sample as PowerSample;
        var telemetrySample = telemetry?.Sample as TelemetrySample;
        var frameSample = frames?.Sample as FrameSample;
        var readings = new Dictionary<string, object?>();

        Add("gpuPowerW", "W", power, powerSample?.GpuTotalW);
        Add("pcieSlot12vW", "W", power, powerSample?.PcieSlot12vW);
        Add("pcieSlot3v3W", "W", power, powerSample?.PcieSlot3v3W);
        Add("pcie8pin1W", "W", power, powerSample?.Pcie8pin1W);
        Add("pcie8pin2W", "W", power, powerSample?.Pcie8pin2W);
        Add("pcie8pin3W", "W", power, powerSample?.Pcie8pin3W);
        Add("cpuTotalW", "W", power, powerSample?.CpuTotalW);
        Add("systemTotalW", "W", power, powerSample?.SystemTotalW);
        Add("gpuTempC", "degC", telemetry, telemetrySample?.GpuTempC);
        Add("gpuHotspotC", "degC", telemetry, telemetrySample?.GpuHotspotC);
        Add("gpuVramTempC", "degC", telemetry, telemetrySample?.GpuVramTempC);
        Add("gpuCoreClockMhz", "MHz", telemetry, telemetrySample?.GpuCoreClockMhz);
        Add("gpuMemClockMhz", "MHz", telemetry, telemetrySample?.GpuMemClockMhz);
        Add("gpuLoadPct", "%", telemetry, telemetrySample?.GpuLoadPct);
        Add("gpuVoltageV", "V", telemetry, telemetrySample?.GpuVoltageV);
        Add("gpuBoardPowerW", "W", telemetry, telemetrySample?.GpuBoardPowerW, approximate: true);
        Add("vramUsedMb", "MB", telemetry, telemetrySample?.VramUsedMb);
        Add("fanRpm1", "RPM", telemetry, telemetrySample?.FanRpm1);
        Add("fanRpm2", "RPM", telemetry, telemetrySample?.FanRpm2);
        Add("fanRpm3", "RPM", telemetry, telemetrySample?.FanRpm3);
        Add("fanRpm4", "RPM", telemetry, telemetrySample?.FanRpm4);
        Add("fanPct1", "%", telemetry, telemetrySample?.FanPct1);
        Add("cpuTempC", "degC", telemetry, telemetrySample?.CpuTempC);
        Add("cpuLoadPct", "%", telemetry, telemetrySample?.CpuLoadPct);
        Add("cpuPowerW", "W", telemetry, telemetrySample?.CpuPowerW);
        Add("cpuClockMhz", "MHz", telemetry, telemetrySample?.CpuClockMhz);
        Add("frameTimeMs", "ms", frames, frameSample?.FrameTimeMs);
        Add("displayedTimeMs", "ms", frames, frameSample?.DisplayedTimeMs);
        Add("gpuBusyMs", "ms", frames, frameSample?.GpuBusyMs);

        var sources = new Dictionary<string, object?>();
        foreach (var channel in channels)
        {
            sources[channel.Kind] = new
            {
                source = channel.Source, mode = channel.Mode.ToString(), simulation = IsSimulation(channel),
                status = channel.Status, sequence = channel.Sequence, sourceTimeSec = Finite(channel.SourceTimeSec),
                receivedUtc = channel.ReceivedUtc?.ToString("o", CultureInfo.InvariantCulture),
                sourceAgeMs = Finite(channel.SourceAgeMs), fresh = channel.Fresh
            };
        }

        return new Dictionary<string, object?>
        {
            ["readings"] = readings,
            ["sources"] = sources,
            ["outputs"] = Array.Empty<object>(),
            ["test"] = new
            {
                state = operation.State, game = operation.Game, scene = operation.Scene, resolution = operation.Resolution,
                repeat = operation.Repeat, totalRepeats = operation.TotalRepeats,
                targetPowerW = Finite(operation.TargetPowerW), noiseDba = Finite(operation.NoiseDba),
                verdict = operation.Verdict, fault = operation.Fault
            },
            ["control"] = new { mode = "read-only; no command endpoint" },
            ["extension"] = new { schema = "gpu-suite/v1", lastEvent = EventData(lastEvent), lastFault = EventData(lastFault), method }
        };

        void Add(string key, string unit, MonitoringChannel? channel, double? sampleValue, bool approximate = false)
        {
            double? value = Finite(sampleValue);
            readings[key] = new
            {
                value, unit,
                source = ReadingSource(channel, approximate),
                acquiredUtc = (string?)null,
                freshness = channel?.Sample is null || value is null ? "unavailable"
                    : channel.Status == "stopped" ? "retained" : channel.Fresh ? "fresh" : "stale"
            };
        }
    }

    // Replayed data is as unverified as synthetic data, so both are labelled; the mode field still says which one it is.
    private static bool IsSimulation(MonitoringChannel channel) =>
        channel.Mode is DataSourceMode.Synthetic or DataSourceMode.Replay;

    private static string? ReadingSource(MonitoringChannel? channel, bool approximate)
    {
        if (channel is null) return null;
        string source = channel.Source;
        if (approximate && !source.Contains(ApproximateMarker, StringComparison.Ordinal)) source += " " + ApproximateMarker;
        return IsSimulation(channel) ? $"{source} {SimulationMarker}" : source;
    }

    private static double? Finite(double? value) => value is double number && double.IsFinite(number) ? number : null;

    private static object? EventData(MonitoringEvent? value) => value is null ? null : new
    {
        utc = value.Utc.ToString("o", CultureInfo.InvariantCulture), stage = value.Stage, severity = value.Severity
    };
}
