using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using System.Threading.Channels;
using GpuSuite.Core.Diagnostics;

namespace GpuSuite.Measurement.Monitoring;

/// <summary>Version-one read-only consumer contract. A heartbeat is never a sensor acquisition timestamp.</summary>
public sealed record MonitoringSnapshot(int SchemaVersion, int ProcessId, Guid Instance,
    DateTimeOffset PublishedUtc, bool Online, MonitoringOperation Operation,
    IReadOnlyList<MonitoringChannel> Channels, string ControlMode = "read-only; no command endpoint")
{
    public MonitoringEvent? LastEvent { get; init; }
    public MonitoringEvent? LastFault { get; init; }
    public IReadOnlyDictionary<string, string> Units { get; } = new Dictionary<string, string>
    {
        ["*TimeSec"] = "s (source-relative)", ["*TimeMs"] = "ms", ["*W"] = "W",
        ["*C"] = "degC", ["*Mhz"] = "MHz", ["*Pct"] = "%", ["*Rpm*"] = "RPM",
        ["*Mb"] = "MB", ["*V"] = "V", ["sourceAgeMs"] = "ms", ["noiseDba"] = "dB(A) at 1 m"
    };
}

/// <summary>
/// Single worker owns the mapping. A capacity-one signal coalesces bursts; a signal received while
/// serializing is retained for the next drain. Acquisition callbacks only TryWrite a byte.
/// </summary>
public sealed class MonitoringPool : IDisposable
{
    public const int Capacity = 256 * 1024;
    public const int HeaderSize = 32;
    public const int Magic = 0x4754534D;
    public static string Name(int pid) => $"Local\\HwbGpuSuite.Telemetry.v1.{pid}";
    private readonly Channel<byte> _signals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _heartbeatStop = new();
    private readonly Guid _instance = Guid.NewGuid();
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private readonly Task _worker;
    private readonly Task _heartbeat;
    private long _generation;
    private int _disposed;
    private volatile string? _lastError;
    public string? LastError => _lastError;

    public MonitoringPool()
    {
        _mapping = MemoryMappedFile.CreateNew(Name(Environment.ProcessId), Capacity);
        _view = _mapping.CreateViewAccessor(0, Capacity, MemoryMappedFileAccess.ReadWrite);
        _worker = Task.Run(DrainAsync);
        MonitoringState.Changed += Request;
        _heartbeat = Task.Run(HeartbeatAsync);
        Request();
    }

    private void Request() => _signals.Writer.TryWrite(0);

    private async Task HeartbeatAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            while (await timer.WaitForNextTickAsync(_heartbeatStop.Token).ConfigureAwait(false)) Request();
        }
        catch (OperationCanceledException) { }
    }

    private async Task DrainAsync()
    {
        await foreach (var _ in _signals.Reader.ReadAllAsync().ConfigureAwait(false))
            Publish(online: true);
        // Producers detached, queue completed and drained before the offline record is written.
        Publish(online: false);
    }

    private void Publish(bool online)
    {
        try
        {
            var state = MonitoringState.Read();
            var now = DateTimeOffset.UtcNow;
            var channels = state.Channels.Select(c => c with
            {
                SourceAgeMs = c.ReceivedTicks != 0 ? Stopwatch.GetElapsedTime(c.ReceivedTicks).TotalMilliseconds : null,
                Fresh = online && c.Valid && c.Status == "sampling" && c.ReceivedTicks != 0 &&
                    Stopwatch.GetElapsedTime(c.ReceivedTicks) <= TimeSpan.FromSeconds(2)
            }).ToArray();
            var snapshot = new MonitoringSnapshot(1, Environment.ProcessId, _instance,
                now, online, state.Operation, channels)
                { LastEvent = MonitoringState.LastEvent, LastFault = MonitoringState.LastFault };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Options);
            if (bytes.Length > Capacity - HeaderSize) throw new InvalidOperationException("Monitoring snapshot exceeds pool capacity.");
            long next = _generation + 2;
            _view.Write(0, next - 1); // odd: write in progress, including after a failed prior write
            Thread.MemoryBarrier();
            _view.Write(8, Magic);
            _view.Write(12, 1);
            _view.Write(16, bytes.Length);
            _view.WriteArray(HeaderSize, bytes, 0, bytes.Length);
            Thread.MemoryBarrier();
            _view.Write(0, next); // even: stable
            _generation = next;
            _lastError = null;
        }
        catch (Exception ex) { _lastError = ex.Message; }
    }

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(), new FiniteDoubleConverter() }
    };

    public static MonitoringPool? TryStart()
    {
        try { return new MonitoringPool(); }
        catch (Exception ex) { Trace.TraceWarning("Monitoring pool unavailable: {0}", ex.Message); return null; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        MonitoringState.Changed -= Request;
        _heartbeatStop.Cancel();
        _signals.Writer.TryComplete();
        _heartbeat.GetAwaiter().GetResult();
        _worker.GetAwaiter().GetResult();
        _view.Dispose();
        _mapping.Dispose();
        _heartbeatStop.Dispose();
    }

    /// <summary>Nonblocking, read-only, bounded optimistic read. Never creates a mapping or actuates hardware.</summary>
    public static string? ReadJson(int pid)
    {
        using var mapping = MemoryMappedFile.OpenExisting(Name(pid), MemoryMappedFileRights.Read);
        using var view = mapping.CreateViewAccessor(0, Capacity, MemoryMappedFileAccess.Read);
        for (int retry = 0; retry < 5; retry++)
        {
            long before = view.ReadInt64(0);
            if (before == 0 || (before & 1) != 0) continue;
            Thread.MemoryBarrier();
            if (view.ReadInt32(8) != Magic || view.ReadInt32(12) != 1) return null;
            int length = view.ReadInt32(16);
            if (length < 1 || length > Capacity - HeaderSize) return null;
            byte[] bytes = new byte[length];
            view.ReadArray(HeaderSize, bytes, 0, length);
            Thread.MemoryBarrier();
            if (before != view.ReadInt64(0)) continue;
            using var json = JsonDocument.Parse(bytes);
            return json.RootElement.GetRawText();
        }
        return null;
    }
}

internal sealed class FiniteDoubleConverter : System.Text.Json.Serialization.JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDouble();
    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (double.IsFinite(value)) writer.WriteNumberValue(value);
        else writer.WriteNullValue();
    }
}
