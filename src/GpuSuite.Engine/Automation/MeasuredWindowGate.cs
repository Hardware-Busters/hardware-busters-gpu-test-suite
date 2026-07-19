namespace GpuSuite.Engine.Automation;

/// <summary>
/// A tiny thread-safe flag shared between the bot engine (which OPENS it at MarkStart and CLOSES it at MarkEnd)
/// and the <c>RuntimeHealthMonitor</c> (which only fires its render-health checks — gpu-idle / frames-frozen —
/// while it is OPEN). It exists to stop a FALSE "frozen capture" failure during a scripted-scene bot's NAV:
/// a game's static cold-launch screens (studio logos, a PHOTOSENSITIVITY/disclaimer, a "PRESS TO CONTINUE"
/// prompt, a load screen) legitimately sit GPU-idle, and on a SLOW cold launch one can hold past the monitor's
/// 6s idle grace → the real-time gpu-idle check trips even though the run is navigating fine (diagnosed live
/// on Alan Wake II 2026-06-29: passed warm 3/3, failed cold 0/5 because a disclaimer held idle >6s during nav).
/// Render-health is only meaningful once the MEASURED window has begun (gameplay should be rendering), so the
/// engine gates it to [MarkStart, MarkEnd]. The post-capture GPU-idle FLOOR (the avg-load &lt; 40% validity check)
/// is a SEPARATE guard and still rejects a frozen measured window; process-exit / telemetry / power health stay
/// armed throughout. Default is OPEN, so a builtin-benchmark scene (no bot MarkStart) is watched the whole way,
/// and a route-only --attach scene (preconditioned in-world, no MarkStart) keeps full protection.
/// </summary>
public sealed class MeasuredWindowGate
{
    private volatile bool _open;

    public MeasuredWindowGate(bool initiallyOpen = true) => _open = initiallyOpen;

    /// <summary>True while render-health checks should be armed (the measured window is active, or no bot gates it).</summary>
    public bool IsOpen => _open;

    /// <summary>Arm render-health checks — the measured benchmark window has begun (bot MarkStart).</summary>
    public void Open() => _open = true;

    /// <summary>Disarm render-health checks — we are navigating/loading (pre-MarkStart) or the window has ended (MarkEnd).</summary>
    public void Close() => _open = false;
}
