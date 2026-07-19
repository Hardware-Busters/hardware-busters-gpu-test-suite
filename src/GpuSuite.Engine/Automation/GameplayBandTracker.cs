namespace GpuSuite.Engine.Automation;

/// <summary>
/// Decides when a live capture's frame-rate has settled into the GAMEPLAY band (the load→gameplay transition),
/// the gate behind <see cref="BotActionType.WaitForGameplayBand"/>. Pure + deterministic (no clock/IO) so it is
/// unit-testable: feed it one poll at a time (frames seen over an elapsed interval) and it reports when settled.
///
/// HITCH-TOLERANT (the Ratchet 0/5 fix). The original gate required CONTINUOUS in-band time and RESET to zero on
/// any out-of-band poll — but a severe-hitch game (Ratchet: ~23 fps 4K gameplay) injects 0-frame STALL polls and
/// brief catch-up SPIKES above the ceiling into otherwise-in-band gameplay, so the continuous accumulator never
/// reached the sustain window and the band "never settled". This instead asks: over the trailing
/// <c>sustainMs</c> of frame-producing time, was at least <c>bandFraction</c> of it IN-BAND?
/// <list type="bullet">
/// <item>A 0-frame poll is NEUTRAL — a hitch/stall is still gameplay, not the wrong screen, so it neither counts
///   for nor against the band (it's excluded from the window). A genuinely frozen state is still caught by the
///   caller's REQUIRED timeout.</item>
/// <item>A transient out-of-band spike is outvoted by the surrounding in-band majority → still settles.</item>
/// <item>A SUSTAINED out-of-band run (a still-rendering ~60 fps load above the ceiling, or a sub-floor black
///   load) keeps the in-band fraction below the threshold → never settles, so the window still can't open
///   mid-load. This preserves the load-rejection the gate exists for.</item>
/// </list>
/// Well-behaved games (stable in-band gameplay, sustained-high menus) are unaffected: their fraction is ~1.0 in
/// gameplay and ~0.0 on the menu/load, exactly as before.
/// </summary>
public sealed class GameplayBandTracker
{
    private readonly double _ceiling;
    private readonly double _floor;
    private readonly double _sustainMs;
    private readonly double _bandFraction;

    // Trailing window of frame-producing polls: each is (durationMs, kind) where kind = +1 in-band, -1 out-of-band.
    // Neutral (0-frame) polls are NOT stored — they neither fill nor reset the window.
    private readonly Queue<(double ms, int kind)> _window = new();
    private double _spannedMs;   // total in+out ms currently in the window
    private double _inBandMs;    // in-band ms currently in the window

    /// <param name="ceiling">Upper fps bound of the gameplay band (e.g. 40 for Ratchet — gameplay ≤40, load ~60).</param>
    /// <param name="floor">Lower fps bound, or ≤0 for a one-sided ceiling-only band.</param>
    /// <param name="sustainMs">Length of the trailing window the fraction is measured over.</param>
    /// <param name="bandFraction">Settle when at least this fraction (0..1) of the window was in-band. Default caller-supplied (0.7).</param>
    public GameplayBandTracker(double ceiling, double floor, double sustainMs, double bandFraction)
    {
        _ceiling = ceiling;
        _floor = floor;
        _sustainMs = sustainMs <= 0 ? 3000 : sustainMs;
        _bandFraction = bandFraction is > 0 and <= 1 ? bandFraction : 0.7;
    }

    /// <summary>Most recent poll's instantaneous fps (for logging).</summary>
    public double LastFps { get; private set; }
    /// <summary>Current in-band fraction of the trailing window (0..1).</summary>
    public double InBandFraction => _spannedMs > 0 ? _inBandMs / _spannedMs : 0;

    /// <summary>Feed one poll: <paramref name="frames"/> rendered over <paramref name="elapsedMs"/>. Returns true
    /// once the band has settled (window full AND in-band fraction ≥ threshold).</summary>
    public bool Feed(int frames, double elapsedMs)
    {
        if (elapsedMs <= 0) return Settled;
        double fps = frames / (elapsedMs / 1000.0);
        LastFps = fps;

        // 0 (or negative) frames over the interval = a hard hitch/stall → NEUTRAL: drop it, leave the window as-is.
        if (frames <= 0) return Settled;

        int kind = (fps <= _ceiling && (_floor <= 0 || fps >= _floor)) ? 1 : -1;
        _window.Enqueue((elapsedMs, kind));
        _spannedMs += elapsedMs;
        if (kind == 1) _inBandMs += elapsedMs;

        // Trim to the trailing sustain window (keep ≥ sustainMs so the fraction is measured over a full window).
        while (_spannedMs - _window.Peek().ms >= _sustainMs && _window.Count > 1)
        {
            var (oldMs, oldKind) = _window.Dequeue();
            _spannedMs -= oldMs;
            if (oldKind == 1) _inBandMs -= oldMs;
        }

        return Settled;
    }

    private bool Settled => _spannedMs >= _sustainMs && InBandFraction >= _bandFraction;
}
