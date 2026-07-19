using System.Diagnostics;
using GpuSuite.Core.Models;

namespace GpuSuite.Measurement.Synthetic;

internal static class Rng
{
    /// <summary>Box-Muller gaussian.</summary>
    public static double Gauss(Random r, double mean, double sd)
    {
        double u1 = 1.0 - r.NextDouble();
        double u2 = 1.0 - r.NextDouble();
        double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return mean + z * sd;
    }
}

/// <summary>
/// Background loop that ticks until stopped, calling <paramref name="onTick"/> with the
/// elapsed seconds. Used by every synthetic session so generated data spans the REAL
/// scene runtime — keeping capture-duration validation honest in degraded mode.
/// </summary>
internal sealed class TickLoop : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _task;
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public TickLoop(int intervalMs, Action<double, double> onTick)
    {
        _task = Task.Run(async () =>
        {
            double last = 0;
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await Task.Delay(intervalMs, _cts.Token).ConfigureAwait(false);
                    double now = _sw.Elapsed.TotalSeconds;
                    onTick(now, now - last);
                    last = now;
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        try { await _task.ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}

public sealed class SyntheticFrameProvider : IFrameCaptureProvider
{
    public string Name => "Synthetic-Frames";
    public bool IsLive => false;

    public Task<ISampleSession<FrameSample>> StartAsync(FrameCaptureTarget target, CancellationToken ct)
        => Task.FromResult<ISampleSession<FrameSample>>(new Session(target.Hint));

    private sealed class Session : ISampleSession<FrameSample>
    {
        private readonly List<FrameSample> _frames = new();
        private readonly object _lock = new();
        private readonly Random _r;
        private readonly double _targetFps;
        private readonly double _benchDuration;
        private readonly TickLoop _loop;
        private double _accumTime;
        private int _ticks;

        public Session(WorkloadHint hint)
        {
            _r = new Random(hint.Seed ^ 0x5f3759df);
            _targetFps = Math.Max(15, hint.BaseFps);
            _benchDuration = hint.BenchDurationSec;
            _loop = new TickLoop(33, OnTick);
        }

        public DataSourceMode Mode => DataSourceMode.Synthetic;
        public int SampleCount { get { lock (_lock) return _frames.Count; } }
        public FrameSample? Latest { get { lock (_lock) return _frames.Count > 0 ? _frames[^1] : null; } }

        private void OnTick(double now, double dt)
        {
            if (dt <= 0) return;
            // Simulated benchmark: frames stop after the bench length so activity-based
            // finish detection can be exercised without a real game.
            if (_benchDuration > 0 && now > _benchDuration) return;
            // slight load-ramp: first ~1.5s the engine is still warming, lower fps
            double warm = now < 1.5 ? 0.75 + 0.25 * (now / 1.5) : 1.0;
            double meanFt = 1000.0 / (_targetFps * warm);
            int count = Math.Max(1, (int)Math.Round(_targetFps * warm * dt));
            lock (_lock)
            {
                for (int i = 0; i < count; i++)
                {
                    double ft = Math.Max(1.0, Rng.Gauss(_r, meanFt, meanFt * 0.08));
                    // ~0.4% of frames are stutters (2.5x-6x)
                    if (_r.NextDouble() < 0.004) ft *= 2.5 + _r.NextDouble() * 3.5;
                    _accumTime += ft / 1000.0;
                    _frames.Add(new FrameSample(_accumTime, ft)
                    {
                        GpuBusyMs = ft * (0.7 + _r.NextDouble() * 0.2)
                    });
                }
                _ticks++;
            }
        }

        public async Task<IReadOnlyList<FrameSample>> StopAsync()
        {
            await _loop.StopAsync().ConfigureAwait(false);
            lock (_lock) return _frames.ToList();
        }

        public ValueTask DisposeAsync() => _loop.DisposeAsync();
    }
}

public sealed class SyntheticTelemetryProvider : ITelemetryProvider
{
    public string Name => "Synthetic-Telemetry";

    public Task<ISampleSession<TelemetrySample>> StartAsync(int intervalMs, WorkloadHint hint, CancellationToken ct)
        => Task.FromResult<ISampleSession<TelemetrySample>>(new Session(intervalMs, hint));

    private sealed class Session : ISampleSession<TelemetrySample>
    {
        private readonly List<TelemetrySample> _s = new();
        private readonly object _lock = new();
        private readonly Random _r;
        private readonly WorkloadHint _h;
        private readonly TickLoop _loop;

        public Session(int intervalMs, WorkloadHint hint)
        {
            _h = hint;
            _r = new Random(hint.Seed ^ 0x1234abcd);
            _loop = new TickLoop(Math.Max(50, intervalMs), OnTick);
        }

        public DataSourceMode Mode => DataSourceMode.Synthetic;
        public int SampleCount { get { lock (_lock) return _s.Count; } }
        public TelemetrySample? Latest { get { lock (_lock) return _s.Count > 0 ? _s[^1] : null; } }

        private void OnTick(double now, double dt)
        {
            // Temps asymptote toward a steady state with a ~25s thermal time-constant.
            double k = 1.0 - Math.Exp(-now / 25.0);
            double core = _h.BaseGpuTempC + (78 - _h.BaseGpuTempC) * k + Rng.Gauss(_r, 0, 0.4);
            double hot = core + 12 + Rng.Gauss(_r, 0, 0.6);
            double clk = 2600 - 120 * k + Rng.Gauss(_r, 0, 8);
            double fan = 1100 + 900 * k + Rng.Gauss(_r, 0, 20);
            var s = new TelemetrySample
            {
                TimeSec = now,
                GpuTempC = Math.Round(core, 1),
                GpuHotspotC = Math.Round(hot, 1),
                GpuVramTempC = Math.Round(core + 8 + Rng.Gauss(_r, 0, 0.5), 1),
                GpuCoreClockMhz = Math.Round(clk, 0),
                GpuMemClockMhz = 10500,
                GpuLoadPct = Math.Round(Math.Clamp(97 + Rng.Gauss(_r, 0, 1.5), 0, 100), 1),
                GpuVoltageV = Math.Round(0.95 + Rng.Gauss(_r, 0, 0.01), 3),
                GpuBoardPowerW = Math.Round(_h.BaseGpuPowerW + Rng.Gauss(_r, 0, 6), 1),
                VramUsedMb = 9000 + Rng.Gauss(_r, 0, 50),
                FanRpm1 = Math.Round(fan, 0),
                FanPct1 = Math.Round(45 + 35 * k, 0),
                CpuTempC = Math.Round(58 + 10 * k + Rng.Gauss(_r, 0, 0.6), 1),
                CpuLoadPct = Math.Round(Math.Clamp(35 + Rng.Gauss(_r, 0, 5), 0, 100), 1),
                CpuPowerW = Math.Round(85 + Rng.Gauss(_r, 0, 4), 1),
                CpuClockMhz = 4800
            };
            lock (_lock) _s.Add(s);
        }

        public async Task<IReadOnlyList<TelemetrySample>> StopAsync()
        {
            await _loop.StopAsync().ConfigureAwait(false);
            lock (_lock) return _s.ToList();
        }

        public ValueTask DisposeAsync() => _loop.DisposeAsync();
    }
}

public sealed class SyntheticPowerProvider : IPowerProvider
{
    public string Name => "Synthetic-Powenetics";
    public bool IsLive => false;
    public PowerMeasurementMetadata Measurement => new()
    {
        Kind = PowerMeasurementKind.Synthetic,
        Scope = "Synthetic workload model",
        HasPerRailData = false,
        HardwareBustersVerifiedPowerEligible = false,
        QualificationNote = "SYNTHETIC · NOT A HARDWARE RESULT — generated data is for workflow testing only."
    };

    public Task<ISampleSession<PowerSample>> StartAsync(int intervalMs, WorkloadHint hint, CancellationToken ct)
        => Task.FromResult<ISampleSession<PowerSample>>(new Session(intervalMs, hint));

    private sealed class Session : ISampleSession<PowerSample>
    {
        private readonly List<PowerSample> _s = new();
        private readonly object _lock = new();
        private readonly Random _r;
        private readonly WorkloadHint _h;
        private readonly TickLoop _loop;

        public Session(int intervalMs, WorkloadHint hint)
        {
            _h = hint;
            _r = new Random(hint.Seed ^ 0x77777777);
            _loop = new TickLoop(Math.Max(5, intervalMs), OnTick);
        }

        public DataSourceMode Mode => DataSourceMode.Synthetic;
        public int SampleCount { get { lock (_lock) return _s.Count; } }
        public PowerSample? Latest { get { lock (_lock) return _s.Count > 0 ? _s[^1] : null; } }

        private void OnTick(double now, double dt)
        {
            double total = Math.Max(0, _h.BaseGpuPowerW + Rng.Gauss(_r, 0, _h.BaseGpuPowerW * 0.05));
            // split realistically: ~30% slot+nothing, rest over connectors
            double slot = Math.Min(66, total * 0.18);
            double rest = total - slot;
            double p1 = rest * 0.5, p2 = rest * 0.5;
            var s = new PowerSample
            {
                TimeSec = now,
                GpuTotalW = Math.Round(total, 2),
                PcieSlot12vW = Math.Round(slot, 2),
                PcieSlot3v3W = Math.Round(1.5 + Rng.Gauss(_r, 0, 0.2), 2),
                Pcie8pin1W = Math.Round(p1, 2),
                Pcie8pin2W = Math.Round(p2, 2),
                CpuTotalW = Math.Round(90 + Rng.Gauss(_r, 0, 5), 2),
                Eps1W = Math.Round(90 + Rng.Gauss(_r, 0, 5), 2),
                SystemTotalW = Math.Round(total + 130 + Rng.Gauss(_r, 0, 8), 2)
            };
            lock (_lock) _s.Add(s);
        }

        public async Task<IReadOnlyList<PowerSample>> StopAsync()
        {
            await _loop.StopAsync().ConfigureAwait(false);
            lock (_lock) return _s.ToList();
        }

        public ValueTask DisposeAsync() => _loop.DisposeAsync();
    }
}
