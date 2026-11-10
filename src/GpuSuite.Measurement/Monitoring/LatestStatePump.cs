using System.Threading.Channels;
using GpuSuite.Core.Diagnostics;

namespace GpuSuite.Measurement.Monitoring;

/// <summary>
/// Calls <c>publish(true)</c> with the latest monitoring state whenever it changes and on every heartbeat, then
/// <c>publish(false)</c> once on dispose. <c>publish</c> must catch its own failures: it runs off the producers' threads,
/// but an exception escaping it would end the drain loop and surface from <see cref="Dispose"/>.
/// </summary>
internal sealed class LatestStatePump : IDisposable
{
    private readonly Channel<byte> _signals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _heartbeatStop = new();
    private readonly Action<bool> _publish;
    private readonly PeriodicTimer _timer;
    private readonly Task _worker;
    private readonly Task _heartbeat;
    private int _disposed;

    internal LatestStatePump(Action<bool> publish, TimeSpan heartbeat)
    {
        ArgumentNullException.ThrowIfNull(publish);
        _timer = new PeriodicTimer(heartbeat);
        _publish = publish;
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
            while (await _timer.WaitForNextTickAsync(_heartbeatStop.Token).ConfigureAwait(false)) Request();
        }
        catch (OperationCanceledException) { }
    }

    private async Task DrainAsync()
    {
        await foreach (var _ in _signals.Reader.ReadAllAsync().ConfigureAwait(false)) _publish(true);
        // Complete and drain the queue before the single offline publication.
        _publish(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        MonitoringState.Changed -= Request;
        _heartbeatStop.Cancel();
        _signals.Writer.TryComplete();
        try
        {
            _heartbeat.GetAwaiter().GetResult();
            _worker.GetAwaiter().GetResult();
        }
        finally
        {
            _timer.Dispose();
            _heartbeatStop.Dispose();
        }
    }
}
