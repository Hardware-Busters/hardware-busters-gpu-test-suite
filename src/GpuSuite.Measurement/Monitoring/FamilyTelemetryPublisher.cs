using System.Diagnostics;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Measurement.Monitoring.FamilyTransport;

namespace GpuSuite.Measurement.Monitoring;

/// <summary>Publishes cached monitoring state through the read-only family telemetry transport.</summary>
public sealed class FamilyTelemetryPublisher : IDisposable
{
    public const string AppId = "gpu";
    private readonly IFamilyFrameWriter _writer;
    private readonly LatestStatePump _pump;
    private volatile string? _lastError;
    private int _disposed;

    public string? LastError => _lastError;

    internal FamilyTelemetryPublisher(IFamilyFrameWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        try { _pump = new LatestStatePump(Publish, writer.PublishInterval); }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    public static FamilyTelemetryPublisher? TryStart(string appVersion) => TryStart(AppId, appVersion);

    internal static FamilyTelemetryPublisher? TryStart(string appId, string appVersion)
    {
        try { return new FamilyTelemetryPublisher(new FamilyTelemetryWriter(appId, appVersion)); } // swap point: the shared library's writer
        catch (Exception ex)
        {
            Trace.TraceWarning("Family telemetry unavailable: {0}", ex.Message);
            return null;
        }
    }

    private void Publish(bool online)
    {
        try
        {
            var state = MonitoringState.Read();
            var channels = ChannelFreshness.Evaluate(state.Channels, online);
            var method = FamilyMethodBlock.Build(MonitoringState.ReadMethod(), online, DateTime.UtcNow);
            var data = FamilyTelemetryData.Build(state.Operation, channels, MonitoringState.LastEvent, MonitoringState.LastFault, method);
            _writer.TryPublish(data, online);
            _lastError = _writer.LastError;
        }
        catch (Exception ex) { _lastError = ex.Message; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _pump.Dispose(); }
        finally { _writer.Dispose(); }
    }
}
