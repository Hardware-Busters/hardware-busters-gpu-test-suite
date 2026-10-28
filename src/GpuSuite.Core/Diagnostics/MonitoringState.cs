using GpuSuite.Core.Models;

namespace GpuSuite.Core.Diagnostics;

/// <summary>Process-local latest state only. Producers commit cached values and signal; never serialize or wait for readers.</summary>
public static class MonitoringState
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, MonitoringChannel> Channels = new();
    private static MonitoringOperation _operation = new("idle");
    public static MonitoringEvent? LastEvent { get; private set; }
    public static MonitoringEvent? LastFault { get; private set; }
    public static event Action? Changed;

    internal static void Commit(MonitoringChannel channel)
    {
        lock (Gate)
        {
            if (channel.Sequence != 0 && Channels.TryGetValue(channel.Kind, out var old) && old.Session != channel.Session)
                return; // A superseded capture cannot overwrite the next session's state.
            Channels[channel.Kind] = channel;
        }
        Signal();
    }

    internal static void Stop(string kind, Guid session)
    {
        lock (Gate)
            if (Channels.TryGetValue(kind, out var old) && old.Session == session)
                Channels[kind] = old with { Status = "stopped" };
        Signal();
    }

    public static void Operation(MonitoringOperation operation)
    {
        lock (Gate) _operation = operation;
        Signal();
    }

    public static void Logged(TimelineEntry entry)
    {
        // Deliberately omit raw log messages: timeline text may contain private launch arguments/paths.
        lock (Gate)
        {
            LastEvent = new(entry.Time.ToUniversalTime(), entry.Stage, entry.Level.ToString());
            if (entry.Level == LogLevel.Error) LastFault = LastEvent;
        }
        Signal();
    }

    public static (MonitoringOperation Operation, MonitoringChannel[] Channels) Read()
    {
        lock (Gate) return (_operation, Channels.Values.ToArray());
    }

    private static void Signal()
    {
        var observers = Changed;
        if (observers is null) return;
        foreach (Action observer in observers.GetInvocationList())
            try { observer(); } catch { /* Monitoring cannot fail acquisition or control. */ }
    }
}

public sealed record MonitoringOperation(string State, string? Game = null, string? Scene = null,
    string? Resolution = null, int? Repeat = null, int? TotalRepeats = null,
    double? TargetPowerW = null, double? NoiseDba = null, string? Verdict = null,
    string? Fault = null);

public sealed record MonitoringChannel(string Kind, Guid Session, string Source, DataSourceMode Mode,
    string Status, long Sequence, DateTimeOffset? ReceivedUtc, double? SourceTimeSec, object? Sample)
{
    // No UTC acquisition timestamp is invented from a relative source clock.
    public DateTimeOffset? AcquiredUtc => null;
    public bool Simulation => Mode == DataSourceMode.Synthetic;
    public double? SourceAgeMs { get; init; }
    public bool Fresh { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public long ReceivedTicks { get; init; }
    public bool Valid => SourceTimeSec is double t && double.IsFinite(t) && t >= 0 && Sample switch
    {
        FrameSample f => double.IsFinite(f.FrameTimeMs) && f.FrameTimeMs > 0,
        PowerSample p => p.GpuTotalW is double w && double.IsFinite(w) && w >= 0,
        TelemetrySample s => new[] { s.GpuTempC, s.GpuLoadPct, s.GpuBoardPowerW, s.CpuTempC }
            .Any(v => v is double x && double.IsFinite(x)),
        _ => false
    };
}

public sealed record MonitoringEvent(DateTime Utc, string Stage, string Severity);

/// <summary>An uncommitted/exceptional exit must not appear as a successful test.</summary>
public sealed class MonitoringOperationScope : IDisposable
{
    private MonitoringOperation _operation;
    private bool _completed;
    public MonitoringOperationScope(MonitoringOperation operation)
    {
        _operation = operation;
        MonitoringState.Operation(operation);
    }
    public void State(string state) => MonitoringState.Operation(_operation = _operation with { State = state });
    public void Complete(string verdict)
    {
        _completed = true;
        MonitoringState.Operation(_operation with { State = "completed", Verdict = verdict });
    }
    public void Dispose()
    {
        if (!_completed) MonitoringState.Operation(_operation with { State = "interrupted", Verdict = null,
            Fault = "Operation exited without a committed result; inspect the durable timeline." });
    }
}

/// <summary>One source lifetime. Stop closes publication before the native capture is torn down.</summary>
public sealed class SampleMonitor : IDisposable
{
    private readonly object _gate = new();
    private MonitoringChannel _state;
    private bool _stopped;

    public SampleMonitor(string kind, string source, DataSourceMode mode)
    {
        if (kind is not ("frames" or "power" or "telemetry")) throw new ArgumentOutOfRangeException(nameof(kind));
        _state = new(kind, Guid.NewGuid(), source, mode, "waiting", 0, null, null, null);
        MonitoringState.Commit(_state);
    }

    public void Commit(object sample, double sourceTimeSec)
    {
        lock (_gate)
        {
            if (_stopped) return;
            _state = _state with { Status = "sampling", Sequence = _state.Sequence + 1,
                ReceivedUtc = DateTimeOffset.UtcNow, SourceTimeSec = sourceTimeSec, Sample = sample,
                ReceivedTicks = System.Diagnostics.Stopwatch.GetTimestamp() };
            MonitoringState.Commit(_state);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopped = true;
            MonitoringState.Stop(_state.Kind, _state.Session);
        }
    }
}
