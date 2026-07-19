using GpuSuite.Core.Diagnostics;

namespace GpuSuite.Engine.Display;

/// <summary>
/// A background watchdog that protects the 60 Hz-capped capture card while a game runs. It polls the
/// live desktop refresh and, when it exceeds the cap, first tries to RECOVER (force the panel back to the
/// cap), and only if that doesn't stick fires <c>onViolation</c> (the orchestrator wires this to KILL the
/// game) so the panel re-syncs at the desktop's safe refresh within ~two poll intervals.
///
/// Why this exists: two different titles push the scanout above the cap. (1) A true exclusive-fullscreen
/// game OWNS the mode — DOOM: The Dark Ages jumps to 160 Hz at its menu regardless of the desktop being at
/// 60 Hz; the desktop can't override that, so we must kill the game to un-blank the Elgato. (2) A BORDERLESS /
/// flip-model game (Ratchet & Clank: Rift Apart, configured 60 Hz borderless) can momentarily be granted the
/// panel's higher refresh by Windows' windowed-game optimizations even though the desktop is at 60 Hz — and
/// that one CAN be pulled straight back by re-setting the desktop mode, WITHOUT killing the (correctly
/// configured) game. So the guard now RECOVERS first: re-set the desktop to res@cap; only if the panel is
/// STILL over the cap after a couple of recovery attempts (case 1) does it escalate to a kill. This bounds
/// any blank to ~two polls AND stops needlessly killing a borderless game that just needed a mode nudge.
///
/// Re-arms once the refresh returns to ≤ cap, so a single recovered blip doesn't spam kills. Disposing stops
/// the loop promptly.
/// </summary>
public sealed class RefreshGuard : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly Task _loop;
    // How many CONSECUTIVE over-cap polls before we give up recovering and kill. Polls 1..(N-1) each attempt a
    // recover (re-set the desktop to the cap); the N-th still-over-cap poll means recovery didn't take (a true
    // exclusive mode) → kill. 3 ⇒ two recovery attempts, ~2 extra polls of blank at most for the exclusive case.
    private const int KillAfterConsecutiveOverCap = 3;

    private RefreshGuard(CancellationTokenSource cts, Task loop)
    {
        _cts = cts;
        _loop = loop;
    }

    /// <summary>
    /// Start watching the (primary) display refresh. When it exceeds <paramref name="maxHz"/> the guard first
    /// tries to RECOVER by re-setting the desktop to the cap; if it stays over the cap across
    /// <see cref="KillAfterConsecutiveOverCap"/> consecutive polls, <paramref name="onViolation"/> is invoked
    /// with the offending refresh (kill). Polling is light (one EnumDisplaySettings call every
    /// <paramref name="pollMs"/>, clamped ≥250ms).
    ///
    /// When <paramref name="expectedW"/>×<paramref name="expectedH"/> is given (&gt;0), the guard ALSO watches
    /// for the desktop dropping BELOW the run's target resolution and pulls it straight back (recover only,
    /// never kill). Why: a remote-desktop session (TeamViewer "optimize resolution") can silently resize the
    /// host mid-run — caught live 2026-07-02 when a 4K run flipped to 2560×1440 between two log lines and the
    /// number passed as "4K". Only a DOWNGRADE fires (lower pixel count): a legit exclusive-fullscreen mode
    /// transition goes TO the expected resolution, and a desktop that is still at a HIGHER res (e.g. 4K desktop
    /// during a 1080p exclusive run's loading screen) is left alone.
    /// </summary>
    public static RefreshGuard Start(int maxHz, int pollMs, Action<int> onViolation, RunLogger log, CancellationToken outerCt,
        int expectedW = 0, int expectedH = 0)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        int poll = Math.Max(250, pollMs);
        var loop = Task.Run(async () =>
        {
            bool armed = true;          // fire the kill at most once per breach episode; re-arm after refresh returns to safe
            int overCapStreak = 0;      // consecutive over-cap polls in this episode
            int resDriftRecovers = 0;   // resolution-downgrade recoveries this run (log the first few, then every 20th)
            bool guardRes = expectedW > 0 && expectedH > 0;
            long expectedPixels = (long)expectedW * expectedH;
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(poll, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                var cur = DisplayController.GetCurrent();
                if (cur is not DisplayMode m) continue;   // driver couldn't report — skip this tick

                // Resolution-downgrade guard (remote-desktop resize): pull the desktop back to the run's
                // target before the refresh logic — a 1440p mode is "≤cap" and would otherwise look healthy.
                if (guardRes && (long)m.Width * m.Height < expectedPixels)
                {
                    resDriftRecovers++;
                    if (resDriftRecovers <= 3 || resDriftRecovers % 20 == 0)
                        log.Warn("RefreshGuard", $"!! Desktop resolution dropped to {m.Width}x{m.Height} below the run target {expectedW}x{expectedH} (remote-desktop resize?) — forcing it back (recover #{resDriftRecovers}). A remote viewer fighting this will keep flipping; disconnect or disable the client's 'optimize resolution' during runs.");
                    var (rok, rdetail) = DisplayController.TrySetResolutionAtCap(expectedW, expectedH, maxHz);
                    if (!rok && resDriftRecovers <= 3) log.Warn("RefreshGuard", $"Resolution recover failed: {rdetail}");
                    continue;   // re-check next poll; refresh check resumes once the mode is back
                }

                if (m.RefreshHz > maxHz)
                {
                    overCapStreak++;
                    if (overCapStreak < KillAfterConsecutiveOverCap)
                    {
                        // RECOVER: pull the panel back to the cap at the current resolution. This rescues a
                        // borderless / flip-model game (e.g. Ratchet) that Windows momentarily granted a higher
                        // refresh — it keeps running at 60 Hz instead of being killed. Harmless no-op-ish for a
                        // true exclusive game (the game will just re-take its mode, and the streak escalates).
                        log.Warn("RefreshGuard", $"Display refresh {m.RefreshHz}Hz exceeded the {maxHz}Hz cap (poll {overCapStreak}/{KillAfterConsecutiveOverCap}) — forcing {m.Width}x{m.Height}@{maxHz}Hz back before killing (saves a borderless game that just needs a mode nudge).");
                        var (ok, detail) = DisplayController.TrySetResolutionAtCap(m.Width, m.Height, maxHz);
                        if (!ok) log.Warn("RefreshGuard", $"Recover set to {maxHz}Hz failed: {detail}");
                    }
                    else if (armed)
                    {
                        // Still over the cap after the recovery attempts — a real exclusive-fullscreen mode the
                        // desktop can't override (DOOM-style). Protect the Elgato by killing the game.
                        armed = false;
                        log.Error("RefreshGuard", $"!! Display still {m.RefreshHz}Hz after {KillAfterConsecutiveOverCap - 1} recovery attempt(s) — an exclusive mode the {maxHz}Hz cap can't override. Killing the game to un-blank the Elgato.");
                        try { onViolation(m.RefreshHz); } catch (Exception ex) { log.Warn("RefreshGuard", "onViolation threw: " + ex.Message); }
                    }
                }
                else
                {
                    if (overCapStreak > 0)
                        log.Info("RefreshGuard", $"Display back at {m.RefreshHz}Hz (≤{maxHz}Hz cap) — recovered; game kept running.");
                    overCapStreak = 0;
                    armed = true;     // back in spec — re-arm for the next episode
                }
            }
        }, CancellationToken.None);
        return new RefreshGuard(cts, loop);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
