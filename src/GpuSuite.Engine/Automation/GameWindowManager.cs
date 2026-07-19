using System.Diagnostics;
using System.Runtime.InteropServices;
using GpuSuite.Core.Diagnostics;

namespace GpuSuite.Engine.Automation;

/// <summary>
/// Forces a game's top-level window into a borderless window that fills the whole primary screen.
/// Used for titles we must run WINDOWED (so the GPU stays at the 60 Hz desktop refresh and an exclusive
/// >60 Hz mode can never blank the Elgato capture card — e.g. DOOM: The Dark Ages on idTech 8). The default
/// windowed box only fills part of the 4K capture, leaving the desktop visible; that (a) feeds the
/// capture-card OCR nav stray desktop text (WaitForText false-positives off the suite's own on-screen log)
/// and (b) under-renders. Stripping the frame and sizing the window to the desktop keeps it DWM-composited
/// at 60 Hz (Elgato-safe) while giving the OCR eye a clean, full-screen game and a full-res render.
/// Best-effort: every failure logs and returns false; it never throws into the run.
/// </summary>
public static class GameWindowManager
{
    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_BORDER = 0x00800000, WS_DLGFRAME = 0x00400000;
    private const uint SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040, SWP_NOZORDER = 0x0004;
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// Poll up to <paramref name="waitForWindowSeconds"/> for the process main window to exist, then strip its
    /// border and size it to the primary screen at (0,0). Returns true if the window was forced borderless.
    /// </summary>
    public static async Task<bool> ForceBorderlessFullscreenAsync(int pid, RunLogger log, CancellationToken ct, int waitForWindowSeconds = 20)
    {
        try
        {
            IntPtr h = IntPtr.Zero;
            var deadline = DateTime.UtcNow.AddSeconds(waitForWindowSeconds);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    p.Refresh();
                    h = p.MainWindowHandle;
                }
                catch (ArgumentException) { log.Trace("Window", $"pid {pid} not running yet for borderless-fullscreen."); }
                if (h != IntPtr.Zero) break;
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            if (h == IntPtr.Zero) { log.Warn("Window", $"Borderless-fullscreen skipped: pid {pid} has no main window after {waitForWindowSeconds}s."); return false; }

            int sw = GetSystemMetrics(SM_CXSCREEN), sh = GetSystemMetrics(SM_CYSCREEN);
            if (sw <= 0 || sh <= 0) { log.Warn("Window", "Borderless-fullscreen skipped: could not read primary screen size."); return false; }

            GetWindowRect(h, out var before);
            int bw = before.Right - before.Left, bh = before.Bottom - before.Top;
            if (IsIconic(h)) ShowWindow(h, SW_RESTORE);

            int style = GetWindowLong(h, GWL_STYLE);
            int stripped = style & ~(WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_DLGFRAME);
            if (stripped != style) SetWindowLong(h, GWL_STYLE, stripped);
            SetWindowPos(h, IntPtr.Zero, 0, 0, sw, sh, SWP_FRAMECHANGED | SWP_SHOWWINDOW | SWP_NOZORDER);
            SetForegroundWindow(h);

            GetWindowRect(h, out var after);
            int aw = after.Right - after.Left, ah = after.Bottom - after.Top;
            bool filled = aw >= sw - 4 && ah >= sh - 4 && after.Left <= 2 && after.Top <= 2;
            log.Info("Window", $"Forced game window (pid {pid}) borderless-fullscreen: {bw}x{bh} → {aw}x{ah} at ({after.Left},{after.Top}) " +
                $"[screen {sw}x{sh}] — {(filled ? "fills screen (clean OCR + full-res render)" : "did NOT fill screen; check display")}.");
            return filled;
        }
        catch (Exception ex) { log.Warn("Window", $"Borderless-fullscreen failed ({ex.GetType().Name}: {ex.Message})."); return false; }
    }

    /// <summary>
    /// Restore a game window's GEOMETRY to fill the primary screen WITHOUT touching its style. A desktop mode
    /// nudge (<c>CaptureSyncGuard</c>) makes Windows shrink any window larger than the interim mode — DOOM's
    /// own 3840x2160 windowed box came back at half size on the restored 4K desktop (live 2026-07-05 run 21),
    /// which silently under-renders (a "4K" number measured at 1080p — Ratchet-bombshell class) and re-exposes
    /// desktop text to the OCR eye. This puts the window back over the whole screen while leaving the style
    /// exactly as the game created it: for post-Ripatorium DOOM the bordered, DWM-composited window is the
    /// PROVEN Elgato-safe present path, so unlike <see cref="ForceBorderlessFullscreenAsync"/> this never
    /// changes the window's flip eligibility. The size comes from GetSystemMetrics — the SAME (possibly
    /// DPI-virtualized) coordinate space SetWindowPos/GetWindowRect use in this process — NOT from the run's
    /// physical resolution: this process is not per-monitor-DPI-aware, so at the 4K desktop's 150% scaling a
    /// physical 3840x2160 request reads back as a "2577x1457" clamp and the success check misfires (run 22).
    /// Best-effort: failures log and return false, never throw.
    /// </summary>
    public static async Task<bool> RestoreWindowRectAsync(int pid, RunLogger log, CancellationToken ct, int waitForWindowSeconds = 10)
    {
        try
        {
            IntPtr h = IntPtr.Zero;
            var deadline = DateTime.UtcNow.AddSeconds(waitForWindowSeconds);
            while (DateTime.UtcNow < deadline)
            {
                try { using var p = Process.GetProcessById(pid); p.Refresh(); h = p.MainWindowHandle; }
                catch (ArgumentException) { log.Warn("Window", $"Window-rect restore skipped: pid {pid} is not running."); return false; }
                if (h != IntPtr.Zero) break;
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            if (h == IntPtr.Zero) { log.Warn("Window", $"Window-rect restore skipped: pid {pid} has no main window after {waitForWindowSeconds}s."); return false; }

            int sw = GetSystemMetrics(SM_CXSCREEN), sh = GetSystemMetrics(SM_CYSCREEN);
            if (sw <= 0 || sh <= 0) { log.Warn("Window", "Window-rect restore skipped: could not read primary screen size."); return false; }

            GetWindowRect(h, out var before);
            int bw = before.Right - before.Left, bh = before.Bottom - before.Top;
            if (bw >= sw - 4 && bh >= sh - 4 && before.Left <= 2 && before.Top <= 2)
            {
                log.Trace("Window", $"Window-rect restore: pid {pid} already {bw}x{bh} at ({before.Left},{before.Top}) — no-op.");
                return true;
            }
            if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
            SetWindowPos(h, IntPtr.Zero, 0, 0, sw, sh, SWP_SHOWWINDOW | SWP_NOZORDER);
            GetWindowRect(h, out var after);
            int aw = after.Right - after.Left, ah = after.Bottom - after.Top;
            bool filled = aw >= sw - 4 && ah >= sh - 4;
            log.Info("Window", $"Restored game window (pid {pid}) geometry after display nudge: {bw}x{bh} → {aw}x{ah} [screen {sw}x{sh}, style untouched] — {(filled ? "fills screen" : "did NOT fill screen")}.");
            return filled;
        }
        catch (Exception ex) { log.Warn("Window", $"Window-rect restore failed ({ex.GetType().Name}: {ex.Message})."); return false; }
    }

    /// <summary>
    /// Hold the window borderless-fullscreen for the life of <paramref name="ct"/>. A one-shot force is not
    /// enough: idTech 8 (DOOM) re-applies its OWN windowed geometry on menu transitions a few seconds after
    /// launch — shrinking the window back to a partial box that re-exposes the desktop (which re-pollutes the
    /// capture-card OCR nav and under-renders). This polls every <paramref name="periodMs"/> and re-asserts
    /// ONLY when the window isn't already filling the screen, so it snaps DOOM back within one period but is a
    /// no-op during the steady-state flythrough (no resize, no focus-steal, no perf disturbance while
    /// measuring). Best-effort; swallows transient failures; exits when the process dies or ct cancels.
    /// </summary>
    public static async Task KeepBorderlessFullscreenAsync(int pid, RunLogger log, CancellationToken ct, int periodMs = 750)
    {
        int reasserts = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    p.Refresh();
                    IntPtr h = p.MainWindowHandle;
                    if (h != IntPtr.Zero)
                    {
                        int sw = GetSystemMetrics(SM_CXSCREEN), sh = GetSystemMetrics(SM_CYSCREEN);
                        GetWindowRect(h, out var r);
                        int w = r.Right - r.Left, hh = r.Bottom - r.Top;
                        bool filled = w >= sw - 4 && hh >= sh - 4 && r.Left <= 2 && r.Top <= 2;
                        if (!filled && sw > 0 && sh > 0)
                        {
                            if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                            int style = GetWindowLong(h, GWL_STYLE);
                            int stripped = style & ~(WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_DLGFRAME);
                            if (stripped != style) SetWindowLong(h, GWL_STYLE, stripped);
                            SetWindowPos(h, IntPtr.Zero, 0, 0, sw, sh, SWP_FRAMECHANGED | SWP_SHOWWINDOW | SWP_NOZORDER);
                            reasserts++;
                        }
                    }
                }
                catch (ArgumentException) { break; }   // process exited
                catch { /* transient: ignore and retry next period */ }
                await Task.Delay(periodMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        if (reasserts > 0) log.Info("Window", $"Borderless-fullscreen keeper re-asserted the window {reasserts} time(s) (the game kept shrinking it back).");
    }
}
