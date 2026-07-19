using GpuSuite.Core.Diagnostics;
using GpuSuite.Engine.Vision;

namespace GpuSuite.Engine.Display;

/// <summary>
/// Verifies the Elgato is actually delivering the display's frames — and MODE-NUDGES the output until it
/// does. A game's video-settings apply re-inits its swapchain and can drop the card's sync (DOOM, since the
/// Ripatorium update); Windows still reports the same mode, so <see cref="RefreshGuard"/> is blind to it and
/// every capture read is the card's NO-SIGNAL slate. A desktop mode nudge (drop one mode, set the target
/// back) forces the output link to renegotiate and the card re-syncs.
///
/// This guard is CLOSED-LOOP where the previous fix was a single blind nudge (run 21, 2026-07-05, proved
/// that insufficient: the sync FLAPPED — NO-SIGNAL slates interleaved with real frames through the whole
/// nav, likely the windowed FG present toggling composed↔independent flip). Probe FIRST (zero cost when the
/// card is fine), require TWO consecutive real frames so a lucky read during a flap can't pass, nudge only
/// when needed, and give up loudly after a bounded number of rounds so a dead capture environment is
/// attributable instead of a silent blind-nav.
/// </summary>
public static class CaptureSyncGuard
{
    /// <summary>Probe the capture card and nudge the display mode until it returns real frames (or rounds run
    /// out). Returns true when two consecutive probe reads came back as non-slate frames. Never throws on
    /// capture failure — a null read counts as "no signal" and is retried via the nudge, bounded.</summary>
    public static async Task<bool> EnsureSyncedAsync(ScreenReader reader, int maxHz, RunLogger log,
        CancellationToken ct, int maxNudges = 3, string context = "")
    {
        string tag = string.IsNullOrWhiteSpace(context) ? "" : $" [{context}]";
        // Capture the restore target ONCE — if a nudge is interrupted mid-flight, GetCurrent inside the loop
        // would "restore" to the nudge's own 1080p and strand the desktop there.
        var pre = DisplayController.GetCurrent();
        int preW = pre?.Width ?? 3840, preH = pre?.Height ?? 2160;
        // Nudge THROUGH a mode different from the target: setting the current mode again is a no-op for
        // Windows (no link renegotiation), which would make the whole guard a placebo on a 1080p run.
        (int w, int h) via = (preW == 1920 && preH == 1080) ? (1280, 720) : (1920, 1080);

        for (int round = 0; ; round++)
        {
            bool good = await ProbeAsync(reader, ct).ConfigureAwait(false);
            if (good)
            {
                await Task.Delay(1500, ct).ConfigureAwait(false);   // flap detector: one lucky frame must not pass
                good = await ProbeAsync(reader, ct).ConfigureAwait(false);
            }
            if (good)
            {
                if (round > 0) log.Info("CaptureSync", $"Capture card back in sync after {round} nudge(s){tag} (2 consecutive real frames).");
                return true;
            }
            if (round >= maxNudges)
            {
                log.Warn("CaptureSync", $"Capture card still NOT in sync after {maxNudges} display nudge(s){tag} — reads are the NO-SIGNAL slate. Vision-gated nav will go blind; treat downstream nav failures as a capture-environment failure, not a game failure.");
                return false;
            }
            log.Info("CaptureSync", $"Capture card is showing its NO-SIGNAL slate{tag} — mode-nudging the output to renegotiate ({round + 1}/{maxNudges}): {via.w}x{via.h} → {preW}x{preH} at ≤{maxHz}Hz.");
            DisplayController.TrySetResolutionAtCap(via.w, via.h, maxHz);
            await Task.Delay(3000, ct).ConfigureAwait(false);
            DisplayController.TrySetResolutionAtCap(preW, preH, maxHz);
            // The card's renegotiation after a nudge is NOT instant: in the wedged/flapping state the first
            // real frame arrived ~20s after the restore leg (live 2026-07-05 run 22 — nudge at :51, real
            // frame at :15; the original fixed 5s settle declared failure while its own fix was still
            // landing). Poll for the first real frame up to ~25s, then loop back to the 2-consecutive check.
            var settleDeadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < settleDeadline)
            {
                await Task.Delay(2500, ct).ConfigureAwait(false);
                if (await ProbeAsync(reader, ct).ConfigureAwait(false)) break;
            }
            GpuSuite.Core.RunHeartbeat.Ping();   // legitimate recovery work — keep the hang-watchdog calm
        }
    }

    private static async Task<bool> ProbeAsync(ScreenReader reader, CancellationToken ct)
    {
        OcrFrame? f;
        try { f = await reader.ReadAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { f = null; }
        GpuSuite.Core.RunHeartbeat.Ping();
        return f is not null && !f.IsNoSignalSlate;
    }
}
