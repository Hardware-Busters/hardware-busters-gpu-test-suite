using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GpuSuite.Measurement.Monitoring.FamilyTransport;

/// <summary>
/// Publishes read-only telemetry frames in the family wire format without ever waiting for a reader.
/// Everything outside this namespace talks to <see cref="IFamilyFrameWriter"/>, so this writer can be replaced as one unit.
/// </summary>
public sealed class FamilyTelemetryWriter : IFamilyFrameWriter
{
    public const int SchemaVersion = 1;
    public const int HeaderSize = 128;
    public const int MaxPayloadBytes = 1048576;
    public const int PublishIntervalMs = 200;

    private const int SchemaOffset = 8;
    private const int LengthOffset = 12;
    private const int SequenceOffset = 16;
    private const int TicksOffset = 24;
    private const int DigestOffset = 40;

    private static readonly HashSet<string> ProcessLeases = new(StringComparer.Ordinal);
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FGTEL001");
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly object _gate = new();
    private readonly string _appId;
    private readonly string _appVersion;
    private readonly Semaphore? _writerLease;
    private readonly Mutex? _frameMutex;
    private readonly MemoryMappedFile? _mapping;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly bool _ownsProcessLease;
    private readonly bool _ownsWriterLease;
    private bool _disposed;

    public string MapName { get; }
    public TimeSpan PublishInterval => TimeSpan.FromMilliseconds(PublishIntervalMs);
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    public long Sequence { get; private set; }
    public string? LastError { get; private set; }

    public FamilyTelemetryWriter(string appId, string appVersion)
    {
        MapName = MappingName(appId);
        _appId = appId;
        _appVersion = appVersion;
        try
        {
            lock (ProcessLeases) _ownsProcessLease = ProcessLeases.Add(MapName);
            if (!_ownsProcessLease) throw new InvalidOperationException("This process already publishes this application's telemetry.");
            _writerLease = FamilyTelemetryAccess.CreateSemaphore(MapName + ".Writer");
            _ownsWriterLease = _writerLease.WaitOne(0);
            if (!_ownsWriterLease) throw new InvalidOperationException("Another instance already publishes this application's telemetry.");
            _frameMutex = FamilyTelemetryAccess.CreateMutex(FrameMutexName(appId));
            _mapping = FamilyTelemetryAccess.CreateSection(MapName, HeaderSize + MaxPayloadBytes);
            _view = _mapping.CreateViewAccessor(0, HeaderSize + MaxPayloadBytes, MemoryMappedFileAccess.ReadWrite);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static string MappingName(string appId)
    {
        if (appId is null || appId.Length is < 2 or > 24 || appId[0] is < 'a' or > 'z' ||
            appId.Any(c => c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9')))
            throw new ArgumentException("Invalid telemetry application ID.", nameof(appId));
        return $"Local\\Cybenetics.Faganas.{FamilyTelemetryAccess.CurrentUser.Value}.{appId}.Telemetry.v1";
    }

    public static string FrameMutexName(string appId) => MappingName(appId) + ".Frame";

    public bool TryPublish(object data, bool online = true)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                LastError = "Telemetry writer is disposed.";
                return false;
            }
            bool acquired = false;
            try
            {
                long next = checked(Sequence + 1);
                var utc = DateTime.UtcNow;
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = SchemaVersion, appId = _appId, appVersion = _appVersion,
                    pid = Environment.ProcessId, instanceId = InstanceId, sequence = next,
                    publishedUtc = utc.ToString("o", CultureInfo.InvariantCulture),
                    publishIntervalMs = PublishIntervalMs, online, data
                }, Options);
                if (payload.Length > MaxPayloadBytes)
                {
                    LastError = "Telemetry exceeds the 1 MiB frame limit.";
                    return false;
                }
                byte[] digest = SHA256.HashData(payload);
                acquired = FamilyTelemetryAccess.TryEnter(_frameMutex!, 0);
                if (!acquired)
                {
                    LastError = "Telemetry reader busy; frame skipped.";
                    return false;
                }
                Commit(payload, digest, next, utc);
                Sequence = next;
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
            finally
            {
                if (acquired) _frameMutex!.ReleaseMutex();
            }
        }
    }

    private void Commit(byte[] payload, byte[] digest, long sequence, DateTime utc)
    {
        // A zero length marks the frame incomplete until every other field is written; the length is committed last.
        var view = _view!;
        view.Write(LengthOffset, 0);
        view.WriteArray(HeaderSize, payload, 0, payload.Length);
        view.WriteArray(DigestOffset, digest, 0, digest.Length);
        view.WriteArray(0, Magic, 0, Magic.Length);
        view.Write(SchemaOffset, SchemaVersion);
        view.Write(SequenceOffset, sequence);
        view.Write(TicksOffset, utc.Ticks);
        view.Write(LengthOffset, payload.Length);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _view?.Dispose();
                _mapping?.Dispose();
                _frameMutex?.Dispose();
            }
            finally
            {
                try
                {
                    if (_ownsWriterLease) _writerLease!.Release();
                }
                finally
                {
                    _writerLease?.Dispose();
                    if (_ownsProcessLease)
                        lock (ProcessLeases) ProcessLeases.Remove(MapName);
                }
            }
        }
    }
}
