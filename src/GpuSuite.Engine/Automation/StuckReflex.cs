namespace GpuSuite.Engine.Automation;

/// <summary>The outcome of one stuck-reflex observation: whether to FORCE a turn now, by how many degrees
/// (signed: + = right, − = left), and a short hint to fold into the next vision prompt so the model cooperates.</summary>
public readonly record struct StuckVerdict(bool ForceTurn, int TurnDeg, string? Hint)
{
    public static readonly StuckVerdict None = new(false, 0, null);
}

/// <summary>
/// CPU frame-diff STUCK-REFLEX for the Tier-1 in-world navigator — the deterministic safety net under the
/// (single-frame, fallible) GX10 vision driver. It is fed the mean capture-card scene-change score
/// (<see cref="GpuSuite.Engine.Vision.CaptureCardGrabber.SampleMotionAsync"/>, ffmpeg scdet, CPU/off-bench) sampled
/// while a translation is HELD. A run of low scores means the character is wall-grinding (pressing FORWARD into
/// debris — the exact failure seen driving Ratchet) — so it FORCES a committed turn to break free instead of
/// trusting another FORWARD from the model. Successive forced turns ALTERNATE direction and ESCALATE in angle so a
/// character wedged in a corner actually escapes. Pure logic, no I/O — unit-tested offline with synthetic scores.
/// A negative score means "sensor unavailable" and is a no-op (a missing sensor must never manufacture a turn).
/// </summary>
public sealed class StuckReflex
{
    private readonly double _threshold;
    private readonly int _trigger;
    private int _lowStreak;   // consecutive low-motion samples while moving
    private int _fires;       // forced turns so far — drives alternation + escalation

    public StuckReflex(double threshold = 0.5, int triggerCount = 2)
    {
        _threshold = threshold > 0 ? threshold : 0.5;
        _trigger = Math.Max(1, triggerCount);
    }

    /// <summary>True once the reflex is meaningfully armed (threshold &gt; 0 came from config).</summary>
    public double Threshold => _threshold;
    public int LowStreak => _lowStreak;
    public int Fires => _fires;

    /// <summary>Feed one motion sample taken while <paramref name="translating"/> a held direction.
    /// <paramref name="motionScore"/> &lt; 0 = sensor unavailable (no-op, streak preserved). At/above the threshold =
    /// real movement through space → reset the streak. Below it → grow the streak; once it reaches the trigger count,
    /// emit a forced turn (and reset the streak so the next jam re-arms).</summary>
    public StuckVerdict Observe(bool translating, double motionScore)
    {
        if (!translating) { _lowStreak = 0; return StuckVerdict.None; }   // not trying to move ⇒ stillness isn't "stuck"
        if (motionScore < 0) return StuckVerdict.None;                     // unknown ⇒ no info, no punishment
        if (motionScore >= _threshold) { _lowStreak = 0; return StuckVerdict.None; }  // moving through space ⇒ all good

        _lowStreak++;
        if (_lowStreak < _trigger) return StuckVerdict.None;

        _lowStreak = 0;
        int deg = NextTurn(_fires);
        _fires++;
        return new StuckVerdict(true, deg,
            "NOTE: you appear STUCK — the view is barely changing even though the character is moving, so you are " +
            "grinding against a wall or obstacle. Do NOT choose FORWARD; pick TURNLEFT or TURNRIGHT toward open space.");
    }

    /// <summary>Clear the low-motion streak (e.g. the model itself just chose a turn, which already breaks the jam).
    /// Keeps the fire count so forced-turn alternation/escalation stays sensible across the whole run.</summary>
    public void NotePivot() => _lowStreak = 0;

    /// <summary>Signed turn for the n-th forced escape: alternate right/left and escalate 90°→120°→150°→170° so a
    /// corner-wedged character sweeps progressively wider until it finds an opening.</summary>
    private static int NextTurn(int fires)
    {
        int mag = Math.Min(170, 90 + 30 * fires);   // 90, 120, 150, 170 (capped)…
        return (fires % 2 == 0) ? mag : -mag;       // right, left, right, …
    }
}
