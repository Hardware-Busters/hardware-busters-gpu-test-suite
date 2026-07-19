using GpuSuite.Core.Models;

namespace GpuSuite.Measurement.Real;

/// <summary>
/// Power provider backed by LibreHardwareMonitor's GPU board-power sensor — a no-extra-hardware
/// alternative to the Powenetics V2 PMD that works on ANY GPU LHM can read. It shares the session's
/// <see cref="LhmTelemetryProvider"/> (one LHM Computer, one pinned device-under-test GPU) so it reads
/// the same card telemetry does. Emits <see cref="PowerSample.GpuTotalW"/> (a single board figure — no
/// per-rail breakdown, which is Powenetics-only); the report/aggregation read GpuTotalW the same way.
/// Use via SuiteConfig.PowerSource = "lhm" (force) or "auto" (PMD first, LHM fallback).
/// </summary>
public sealed class LhmPowerProvider : IPowerProvider
{
    private readonly LhmTelemetryProvider _lhm;
    public LhmPowerProvider(LhmTelemetryProvider lhm) { _lhm = lhm; }

    public string Name => "LHM GPU board power";
    public bool IsLive { get; private set; }
    public PowerMeasurementMetadata Measurement => new()
    {
        Kind = PowerMeasurementKind.GpuReportedTelemetry,
        Scope = "GPU-reported board-power telemetry",
        HasPerRailData = false,
        HardwareBustersVerifiedPowerEligible = false,
        QualificationNote = "APPROXIMATE · GPU TELEMETRY — board power is reported by the GPU through LibreHardwareMonitor."
    };

    /// <summary>True if LHM exposes a usable GPU board-power sensor right now.</summary>
    public bool Probe(out string detail)
    {
        IsLive = _lhm.TryReadGpuBoardPowerW(out var w);
        detail = IsLive
            ? $"LHM GPU board power readable (~{w:0} W)"
            : "LHM exposes no GPU board-power sensor on this card";
        return IsLive;
    }

    public Task<ISampleSession<PowerSample>> StartAsync(int intervalMs, WorkloadHint hint, CancellationToken ct)
        => Task.FromResult<ISampleSession<PowerSample>>(new Session(_lhm, intervalMs));

    private sealed class Session : ISampleSession<PowerSample>
    {
        private readonly LhmTelemetryProvider _lhm;
        private readonly List<PowerSample> _samples = new();
        private readonly object _lock = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _task;
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

        public Session(LhmTelemetryProvider lhm, int intervalMs)
        {
            _lhm = lhm;
            // LHM's board-power sensor updates ~1-2 Hz, so polling at the Powenetics 10ms cadence would
            // just repeat values and hammer gpu.Update(). Clamp to >=100ms (10 Hz) — still hundreds of
            // samples over a benchmark run (clears the >=50-sample validity floor) at a fraction of the cost.
            int delay = Math.Max(100, intervalMs);
            _task = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var s = new PowerSample { TimeSec = _sw.Elapsed.TotalSeconds };
                        if (_lhm.TryReadGpuBoardPowerW(out var w)) s.GpuTotalW = w;
                        lock (_lock) _samples.Add(s);
                        await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        public DataSourceMode Mode => DataSourceMode.Live;
        public int SampleCount { get { lock (_lock) return _samples.Count; } }
        public PowerSample? Latest { get { lock (_lock) return _samples.Count > 0 ? _samples[^1] : null; } }

        public async Task<IReadOnlyList<PowerSample>> StopAsync()
        {
            _cts.Cancel();
            try { await _task.ConfigureAwait(false); } catch (OperationCanceledException) { }
            lock (_lock) return _samples.ToList();
        }

        public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); _cts.Dispose(); }
    }
}
