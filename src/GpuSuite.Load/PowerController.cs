using System.Diagnostics;

namespace GpuSuite.Load;

/// <summary>One control-loop observation.</summary>
public readonly record struct PowerTick(double ElapsedSec, double? Watts, double Intensity, double TargetW);

/// <summary>
/// Closed-loop (PI) controller that drives an <see cref="IGpuLoad"/> to a target GPU power by reading
/// the live board power each tick and nudging the load's <see cref="IGpuLoad.Intensity"/>. Signal-driven:
/// it watches the power reading rather than sleeping a fixed settle time. Gains are conservative so it
/// converges smoothly without oscillation; tune on the bench against Powenetics.
/// </summary>
public sealed class PowerController
{
    private readonly IGpuLoad _load;
    private readonly Func<double?> _readWatts;

    /// <summary>Proportional gain (intensity change per watt of error).</summary>
    public double Kp { get; init; } = 0.00055;
    /// <summary>Integral gain (kills steady-state offset). Small — heavy integral winds up against the
    /// boost-clock lag and causes a slow limit cycle when the clock is NOT locked.</summary>
    public double Ki { get; init; } = 0.00008;
    /// <summary>No adjustment while |error| is within this band (watts) — stops hunting around target.</summary>
    public double DeadbandW { get; init; } = 8.0;
    /// <summary>Control period. Deliberately slow so the duty-averaged board power settles before each
    /// adjustment — the ~1 Hz, lagged LHM sensor on a dev box needs this to avoid limit-cycle overshoot.
    /// With Powenetics (~1 kHz) on the bench there is effectively no dead time, so this can be shortened.</summary>
    public int IntervalMs { get; init; } = 1400;
    /// <summary>EMA factor for the power feedback (0..1; higher = less smoothing). Tames sensor noise, but
    /// too much smoothing lags the true power during a ramp and causes overshoot — the LHM board-power
    /// sensor is responsive enough that 0.6 tracks cleanly.</summary>
    public double FeedbackEma { get; init; } = 0.60;
    /// <summary>Max |intensity| change applied per step — slew limit that prevents end-to-end slamming.</summary>
    public double SlewMax { get; init; } = 0.05;

    public PowerController(IGpuLoad load, Func<double?> readWatts)
    {
        _load = load;
        _readWatts = readWatts;
    }

    /// <summary>
    /// Hold <paramref name="targetW"/> for <paramref name="duration"/> (or until cancelled), calling
    /// <paramref name="onTick"/> each interval. Returns the observed ticks (for settle/soak analysis).
    /// </summary>
    public async Task<IReadOnlyList<PowerTick>> RunAsync(
        double targetW, TimeSpan duration, Action<PowerTick>? onTick, CancellationToken ct)
    {
        var ticks = new List<PowerTick>();
        double integral = 0;
        double integralClampW = 0.30 / Math.Max(1e-9, Ki);   // cap the integral's contribution at ~0.30 intensity
        double ema = 0; bool haveEma = false;
        var sw = Stopwatch.StartNew();

        while (!ct.IsCancellationRequested && sw.Elapsed < duration)
        {
            double? w = _readWatts();
            if (w is double cur && cur > 0)
            {
                ema = haveEma ? ema + FeedbackEma * (cur - ema) : cur;
                haveEma = true;

                double err = targetW - ema;                 // watts
                if (Math.Abs(err) > DeadbandW)
                {
                    double pTerm = Kp * err;
                    // Anti-windup (conditional integration): only accumulate the integral once inside the
                    // proportional band — i.e. when P alone is no longer demanding a full-slew step. During
                    // the long max-slew ramp to a high target the integral is frozen (and any residual bled
                    // off), so it can't wind up and slam the load past the target once power finally arrives.
                    if (Math.Abs(pTerm) < SlewMax)
                        integral = Math.Clamp(integral + err, -integralClampW, integralClampW);
                    else
                        integral *= 0.85;
                    double delta = Math.Clamp(pTerm + Ki * integral, -SlewMax, SlewMax);
                    _load.Intensity = Math.Clamp(_load.Intensity + delta, 0.0, 1.0);
                }
            }

            var tick = new PowerTick(sw.Elapsed.TotalSeconds, w, _load.Intensity, targetW);
            ticks.Add(tick);
            onTick?.Invoke(tick);

            try { await Task.Delay(IntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        return ticks;
    }

    /// <summary>
    /// Ramp intensity to find the maximum sustainable power (the card's effective power limit under this
    /// load). Increases intensity until the measured power stops rising, then holds. Returns the plateau W.
    /// </summary>
    public async Task<double> FindMaxAsync(TimeSpan rampDuration, Action<PowerTick>? onTick, CancellationToken ct)
    {
        _load.Intensity = 1.0;   // full duty; the card's own power limit caps it
        double peak = 0;
        var sw = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested && sw.Elapsed < rampDuration)
        {
            double? w = _readWatts();
            if (w is double cur && cur > peak) peak = cur;
            onTick?.Invoke(new PowerTick(sw.Elapsed.TotalSeconds, w, _load.Intensity, peak));
            try { await Task.Delay(IntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        return peak;
    }
}
