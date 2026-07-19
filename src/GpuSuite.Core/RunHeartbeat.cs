namespace GpuSuite.Core;

/// <summary>
/// Process-wide liveness beacon for the physical input lock.
///
/// While a run is active the engine <see cref="Ping"/>s this on GENUINE forward progress — frames
/// flowing during the measured window, or the game being read in the FOREGROUND during menu nav, or
/// a launch milestone. The input lock's watchdog reads <see cref="IsStale"/>: if nothing has pinged
/// for the stale window, the game-under-test has crashed (its render/present stopped) or hung (its
/// window never came to the captured display — the classic "no main window yet / capture shows the
/// desktop" loop), and the lock self-releases so the operator gets the keyboard and mouse back
/// WITHOUT having to wait for the whole suite to die.
///
/// Deliberately STATIC + ambient: the suite drives exactly one game at a time in a single process,
/// so one global beacon is the right model and it avoids threading a handle through every frame
/// provider, the bot engine, and the orchestrator (all of which would otherwise need a constructor
/// change on a safety-critical path). Pings are cheap (a single volatile write) so the hot frame
/// loop can call it freely.
///
/// Pinging is gated by <see cref="Begin"/>/<see cref="End"/> so it is inert outside a locked run —
/// <see cref="IsStale"/> returns false unless a run is active, so nothing can trip when idle.
/// </summary>
public static class RunHeartbeat
{
    private static long _lastTick;        // Environment.TickCount64 at last ping (0 = never pinged this run)
    private static volatile bool _active; // true only between Begin() and End()

    /// <summary>Begin watching — call when the input lock arms. Starts fresh (counts "now" as progress).</summary>
    public static void Begin()
    {
        Volatile.Write(ref _lastTick, Environment.TickCount64);
        _active = true;
    }

    /// <summary>Stop watching — call when the lock releases / the suite ends. Pings become no-ops.</summary>
    public static void End()
    {
        _active = false;
        Volatile.Write(ref _lastTick, 0);
    }

    /// <summary>Record genuine forward progress (a captured frame, a foreground OCR grab, a launch milestone).
    /// A no-op unless a run is active, so it is safe to call unconditionally from any component.</summary>
    public static void Ping()
    {
        if (_active) Volatile.Write(ref _lastTick, Environment.TickCount64);
    }

    /// <summary>True only when a run is active AND no progress has been recorded for at least
    /// <paramref name="staleMs"/> milliseconds — the crash/hang signature the input lock releases on.</summary>
    public static bool IsStale(int staleMs)
    {
        if (!_active) return false;
        long t = Volatile.Read(ref _lastTick);
        return t != 0 && Environment.TickCount64 - t >= staleMs;
    }

    /// <summary>Milliseconds since the last ping, or -1 when no run is active. For diagnostics/logging.</summary>
    public static long IdleMs
    {
        get
        {
            long t = Volatile.Read(ref _lastTick);
            return (_active && t != 0) ? Environment.TickCount64 - t : -1;
        }
    }
}
