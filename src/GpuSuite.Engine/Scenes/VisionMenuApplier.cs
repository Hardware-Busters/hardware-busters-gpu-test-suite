using System.Text.RegularExpressions;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Automation;
using GpuSuite.Engine.Launch;
using GpuSuite.Engine.Vision;

namespace GpuSuite.Engine.Scenes;

/// <summary>Outcome of applying a variant's menu-method knobs by vision. <see cref="Ok"/> is false unless
/// EVERY menu knob was set AND re-read as the target value off the capture card — so a menu model can never
/// silently mislabel data, exactly like the config-file path.</summary>
public sealed class MenuApplyResult
{
    public bool Ok { get; set; } = true;
    public List<string> Applied { get; } = new();
    public List<string> Failed { get; } = new();
    public string Detail { get; set; } = "";
}

/// <summary>
/// The vision-nav menu APPLIER (the actuator half of the keystone): for each menu-method graphics knob a
/// variant selects, it injects the game's <see cref="MenuMap"/> open sequence, homes + focuses the control's
/// row, reads the current value off the capture card (<see cref="ScreenReader"/>), cycles it to the target,
/// re-reads to VERIFY, then injects the back sequence. Reuses the proven <see cref="InputAutomationEngine"/>
/// for injection (keyboard scancodes / ViGEm pad) and only ever injects into the real game window.
/// </summary>
public sealed class VisionMenuApplier
{
    private readonly ScreenReader _reader;
    private readonly RunLogger _log;
    // The engine driving the CURRENT ApplyAsync — used by ReadFrameAsync to foreground the game before every
    // OCR read (so a window that stole focus over a windowed game can't contaminate the read). Not re-entrant:
    // one apply at a time per applier instance.
    private InputAutomationEngine? _activeEngine;

    /// <summary>Optional self-heal hook, wired by the orchestrator: invoked when a read comes back as the
    /// capture card's NO-SIGNAL slate (<see cref="OcrFrame.IsNoSignalSlate"/>). The game's video-settings
    /// APPLY drops the Elgato's sync at the EXACT moment the applier starts its Back/exit reads (idTech 8,
    /// live 2026-07-05 run 21): the exit hunt then read slates, its blind tapIfNoText re-entered Settings,
    /// and the following benchmark bot walked the settings list instead of the main menu. The hook (a display
    /// mode-nudge + window re-assert) restores real frames IN PLACE so the exit sequence keeps its eyes.
    /// Bounded per apply session — a dead capture environment fails loudly instead of nudging forever.</summary>
    public Func<CancellationToken, Task<bool>>? ResyncOnNoSignal { get; set; }
    private int _resyncRounds;
    private const int MaxResyncRounds = 3;

    public VisionMenuApplier(ScreenReader reader, RunLogger log)
    {
        _reader = reader;
        _log = log;
    }

    private static string Norm(string s) => Regex.Replace(s ?? "", "[^a-z0-9]", "", RegexOptions.IgnoreCase).ToLowerInvariant();
    internal static bool Matches(string? a, string b)
    {
        if (a is null) return false;
        string na = Norm(a), nb = Norm(b);
        // Empty OCR rows are common for disabled/blank menu entries. string.Contains("") is true,
        // so the old matcher treated the first blank row as EVERY requested control (live DOOM DLSS/FG).
        return na.Length > 0 && nb.Length > 0 && (na.Contains(nb) || nb.Contains(na));
    }

    /// <summary>Apply every menu-method knob the variant selects, set+verify each via OCR. Returns Ok only if
    /// all of them were verified. With inject=false (no real game) it logs the plan and returns a non-applied
    /// pass so the orchestrator's degraded path stays honest.</summary>
    public async Task<MenuApplyResult> ApplyAsync(GameProfile game, GameVariant? variant, int? pid, bool inject, CancellationToken ct, bool skipOpen = false)
    {
        var r = new MenuApplyResult();
        var targets = game.Settings
            .Where(s => string.Equals(s.Apply.Method, "menu", StringComparison.OrdinalIgnoreCase))
            .Select(s => (setting: s, value: GameLauncher.EffectiveValue(s, variant)))
            .Where(t => !string.IsNullOrWhiteSpace(t.value))
            .ToList();

        if (targets.Count == 0) { r.Detail = "no menu-method knobs for this variant"; return r; }

        if (!inject)
        {
            foreach (var (s, v) in targets) r.Applied.Add($"{s.Key}={v} (dry-run, not injected)");
            r.Detail = "dry-run (no game) — menu knobs not injected";
            return r;
        }

        var map = game.MenuMap;
        if (map is null)
        {
            r.Ok = false;
            r.Detail = "no menuMap calibrated for this game; cannot set menu knobs (fail-safe).";
            foreach (var (s, v) in targets) r.Failed.Add($"{s.Key}={v}: no menuMap");
            _log.Warn("MenuApply", r.Detail);
            return r;
        }

        var device = string.Equals(map.InputDevice, "gamepad", StringComparison.OrdinalIgnoreCase)
            ? BotInputDevice.Gamepad : BotInputDevice.Keyboard;
        // KeepPadAlive: ONE virtual pad for the whole menu session (engine reuses a connected pad across the
        // per-step RunAsync calls; disposed in the finally below). The old per-step connect→dispose churn popped
        // the Windows touch keyboard over DOOM and throws controller-disconnect modals in CP/F1-class games.
        // StrictForeground: a window-resolve/focus miss ABORTS the step instead of injecting into the shell
        // (the other half of the 2026-07-03 DOOM block) — surfaced here as a failed, never half-applied, variant.
        using var engine = new InputAutomationEngine(_log, inject: true)
        {
            TargetPid = pid,
            KeepPadAlive = true,
            StrictForeground = true,
        };
        _activeEngine = engine;   // ReadFrameAsync foregrounds the game via this before each OCR read
        _resyncRounds = 0;        // NO-SIGNAL self-heal budget is per apply session
        try
        {

            // 1) open the settings page (skipped during calibration when the operator already has it open)
            if (skipOpen)
                _log.Info("MenuApply", $"Skipping the open sequence (operator has the settings page open). variant '{variant?.Id ?? "default"}', {targets.Count} knob(s), device={device}.");
            else
            {
                _log.Info("MenuApply", $"Opening settings menu for '{game.Id}' (variant '{variant?.Id ?? "default"}', {targets.Count} knob(s), device={device}).");
                await RunOpenAsync(engine, device, map.Open, ct).ConfigureAwait(false);
                await Settle(map, ct).ConfigureAwait(false);
            }

            // 2) confirm we reached the settings page; if an AnchorNavKey is set, OCR-guide the category tabs to it
            if (!string.IsNullOrWhiteSpace(map.AnchorLabel))
            {
                async Task<bool> AnchorVisibleAsync()
                {
                    var f = await ReadFrameAsync(ct).ConfigureAwait(false);
                    return f?.Find(map.AnchorLabel!) is not null;
                }
                bool found = await AnchorVisibleAsync().ConfigureAwait(false);
                if (!found && !string.IsNullOrWhiteSpace(map.AnchorNavKey))
                {
                    for (int i = 0; i < Math.Max(1, map.AnchorNavMax) && !found; i++)
                    {
                        _log.Info("MenuApply", $"Anchor '{map.AnchorLabel}' not visible yet — pressing {map.AnchorNavKey} to switch category ({i + 1}/{map.AnchorNavMax}).");
                        await RunStepsAsync(engine, device, Tap1(device, map.AnchorNavKey!), ct).ConfigureAwait(false);
                        await Settle(map, ct).ConfigureAwait(false);
                        found = await AnchorVisibleAsync().ConfigureAwait(false);
                    }
                }
                if (!found)
                {
                    r.Ok = false;
                    r.Detail = $"did not reach the settings page (anchor '{map.AnchorLabel}' not visible) — aborting before any change.";
                    foreach (var (s, v) in targets) r.Failed.Add($"{s.Key}={v}: page anchor missing");
                    _log.Warn("MenuApply", r.Detail);
                    await RunOpenAsync(engine, device, map.Back, ct).ConfigureAwait(false);   // gate-aware: honors waitText + conditional taps
                    return r;
                }
                _log.Info("MenuApply", $"On settings page (anchor '{map.AnchorLabel}' visible).");
            }

            // 3) set + verify each knob
            foreach (var (s, value) in targets)
            {
                var control = map.Controls.FirstOrDefault(c => string.Equals(c.SettingKey, s.Key, StringComparison.OrdinalIgnoreCase));
                if (control is null)
                {
                    r.Failed.Add($"{s.Key}={value}: no MenuControl in menuMap");
                    _log.Warn("MenuApply", $"No MenuControl for '{s.Key}' — cannot set (fail-safe).");
                    continue;
                }
                var (ok, detail) = await SetControlAsync(engine, device, map, control, value, ct).ConfigureAwait(false);
                if (ok) { r.Applied.Add($"{s.Key}={value} ({detail})"); _log.Info("MenuApply", $"{s.Key} → {value}: {detail}"); }
                else { r.Failed.Add($"{s.Key}={value}: {detail}"); _log.Warn("MenuApply", $"{s.Key} → {value} FAILED: {detail}"); }
            }

            // 4) apply + exit
            await RunOpenAsync(engine, device, map.Back, ct).ConfigureAwait(false);   // gate-aware: honors waitText + conditional taps

            // 4b) confirm the Back actually returned to the neutral screen (BackAnchor). DOOM's multi-level
            // Settings needs a VARIABLE number of Back presses to reach the main menu, so a fixed Back list left
            // the game deep in Settings and a following benchmark-start bot navigated the settings list blind
            // (walked its Down-presses to Display Calibration — live 2026-07-05). Hunt like the open anchor:
            // press BackNavKey until the anchor is visible (bounded). Best-effort (a menu model is still
            // set+verified even if the exit hunt can't confirm) — but it logs loudly so a stuck exit is visible.
            if (!string.IsNullOrWhiteSpace(map.BackAnchor))
            {
                async Task<bool> BackAnchorVisibleAsync()
                {
                    var f = await ReadFrameAsync(ct).ConfigureAwait(false);
                    return f?.Find(map.BackAnchor!) is not null;
                }
                bool at = await BackAnchorVisibleAsync().ConfigureAwait(false);
                if (!at && !string.IsNullOrWhiteSpace(map.BackNavKey))
                {
                    for (int i = 0; i < Math.Max(1, map.BackNavMax) && !at; i++)
                    {
                        _log.Info("MenuApply", $"Back anchor '{map.BackAnchor}' not visible yet — pressing {map.BackNavKey} to back out ({i + 1}/{map.BackNavMax}).");
                        await RunStepsAsync(engine, device, Tap1(device, map.BackNavKey!), ct).ConfigureAwait(false);
                        await Settle(map, ct).ConfigureAwait(false);
                        at = await BackAnchorVisibleAsync().ConfigureAwait(false);
                    }
                }
                if (at) _log.Info("MenuApply", $"Returned to '{map.BackAnchor}' — settings menu fully exited.");
                else _log.Warn("MenuApply", $"Back anchor '{map.BackAnchor}' never confirmed after {map.BackNavMax} {map.BackNavKey} press(es) — the game may still be in a submenu (a following start bot must recover).");
            }

            // 4c) restore the neutral screen to its COLD-BOOT state (the applier's exit contract). The main
            // menu keeps FOCUS on the row the applier exited from (DOOM: Settings, 3 below Campaign), while a
            // following benchmark-start bot's deterministic walk is calibrated from the cold-boot focus — so
            // its 'open Extras' re-entered Settings and its Downs walked to Display Calibration (live
            // 2026-07-05 runs 21+23). PostBack inverts the open's main-menu walk (DOOM: Up×3; the menu WRAPS
            // so blind-homing is not safe). Best-effort like the rest of the exit path — but gate-aware.
            if (map.PostBack.Count > 0)
            {
                _log.Info("MenuApply", $"Running {map.PostBack.Count} postBack step(s) — restoring the neutral screen's cold-boot focus for the start bot.");
                await RunOpenAsync(engine, device, map.PostBack, ct).ConfigureAwait(false);
            }

            r.Ok = r.Failed.Count == 0;
            r.Detail = r.Ok
                ? $"set+verified {r.Applied.Count} menu knob(s): {string.Join(", ", r.Applied)}"
                : $"{r.Failed.Count} menu knob(s) failed: {string.Join("; ", r.Failed)}";
            return r;
        }
        catch (MenuApplyAbortedException ex)
        {
            // Strict-foreground abort: the game window vanished or could not be focused mid-apply. Nothing was
            // injected into a non-game window (that's the point) — fail the variant loudly instead of leaving it
            // half-applied. No Back steps: they would need the same unfocusable window.
            r.Ok = false;
            var done = new HashSet<string>(r.Applied.Select(a => a.Split('=')[0]), StringComparer.OrdinalIgnoreCase);
            foreach (var (s, v) in targets)
                if (!done.Contains(s.Key) && !r.Failed.Any(f => f.StartsWith(s.Key + "=", StringComparison.OrdinalIgnoreCase)))
                    r.Failed.Add($"{s.Key}={v}: aborted ({ex.Message})");
            r.Detail = $"menu apply ABORTED mid-session: {ex.Message}";
            _log.Warn("MenuApply", r.Detail);
            return r;
        }
    }

    /// <summary>Raised when a step's input session aborted (the engine's strict-foreground refusal) so the
    /// applier fails the variant instead of continuing half-applied against a window it cannot reach.</summary>
    private sealed class MenuApplyAbortedException : Exception
    {
        public MenuApplyAbortedException(string reason) : base(reason) { }
    }

    /// <summary>Focus a control's row, read its value, actuate to the target (cycle or dropdown), then verify.</summary>
    private async Task<(bool ok, string detail)> SetControlAsync(
        InputAutomationEngine engine, BotInputDevice device, MenuMap map, MenuControl c, string value, CancellationToken ct)
    {
        string targetText = c.OptionLabels.TryGetValue(value, out var t) ? t : value;
        string rowLabel = string.IsNullOrWhiteSpace(c.RowValueLabel) ? c.Label : c.RowValueLabel!;

        var (focusOk, focusDetail) = await NavigateToControlAsync(engine, device, map, c, ct).ConfigureAwait(false);
        if (!focusOk) return (false, focusDetail);

        string? current = await ReadValueAsync(map, rowLabel, 8, ct).ConfigureAwait(false);
        if (Matches(current, targetText)) return (true, $"already '{current}', verified ({focusDetail})");

        return c.Actuation.Equals("dropdown", StringComparison.OrdinalIgnoreCase)
            ? await SetDropdownAsync(engine, device, map, c, value, targetText, rowLabel, current, ct).ConfigureAwait(false)
            : await SetCycleAsync(engine, device, map, c, targetText, rowLabel, ct).ConfigureAwait(false);
    }

    /// <summary>One capture-card OCR read that also PINGS the run heartbeat. Every read the applier makes is
    /// real pre-benchmark progress (perception + actuation, same convention as the nav engine's pings) — without
    /// this, a long legitimate menu-apply (DOOM: splash gate ~15-40s + RB category hunt + an ~18-row guided walk
    /// at ~2.4s/step × 3 knobs) exceeded the 60s no-progress hang-watchdog, which killed the game MID-NAVIGATION
    /// (live 2026-07-04). Safe: the applier is bounded (anchor hunt ≤ AnchorNavMax, guided steps ≤ MaxSteps,
    /// dropdown cycles ≤ MaxCycles, each script ≤ 120s) and StrictForeground aborts if the game window dies, so
    /// pinging during active menu-apply can never wedge a run open forever.</summary>
    private async Task<OcrFrame?> ReadFrameAsync(CancellationToken ct)
    {
        // Foreground CONTAMINATION note (2026-07-05): a windowed game can lose focus to a window on the bench
        // display (the agent's own chat window), and the read then captures that window's text — the applier
        // read the chat's 'VIDEO'/'Ripatorium' and false-matched it. The FIX is operational (keep the bench
        // display clean + never run foreground-stealing commands during a run), NOT a per-read SetForegroundWindow:
        // that flips a WINDOWED game's present mid-frame and the grab catches a transition → null value reads
        // (proven: reads that worked clean without it failed 3/3 with it, run 19). _activeEngine.TryForegroundTargetForRead()
        // stays available for a future, settled foreground pre-step, but is intentionally NOT called per read.
        var f = await _reader.ReadAsync(ct).ConfigureAwait(false);
        GpuSuite.Core.RunHeartbeat.Ping();
        // NO-SIGNAL self-heal (2026-07-05 run 21): every applier read funnels through here, so this one
        // check protects the open gates, the knob verifies, the Back gates AND the back-anchor exit hunt.
        // When the frame is the card's slate, ask the orchestrator to re-sync the output and re-read once;
        // the next caller read re-triggers if the sync is still down, up to MaxResyncRounds per session.
        if (f?.IsNoSignalSlate == true && ResyncOnNoSignal is not null && _resyncRounds < MaxResyncRounds)
        {
            _resyncRounds++;
            _log.Warn("MenuApply", $"Read returned the capture card's NO-SIGNAL slate — invoking display re-sync ({_resyncRounds}/{MaxResyncRounds}).");
            try { await ResyncOnNoSignal(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.Warn("MenuApply", "Display re-sync hook failed: " + ex.Message); }
            f = await _reader.ReadAsync(ct).ConfigureAwait(false);
            GpuSuite.Core.RunHeartbeat.Ping();
        }
        return f;
    }

    /// <summary>Read a control row's current on-screen value, retrying a few times (the menu can be mid-reflow
    /// right after a focus change or a dropdown close, so the first OCR sometimes misses the row).</summary>
    private async Task<string?> ReadValueAsync(MenuMap map, string rowLabel, int tries, CancellationToken ct)
    {
        for (int i = 0; i < Math.Max(1, tries); i++)
        {
            var frame = await ReadFrameAsync(ct).ConfigureAwait(false);
            // Constrain the LABEL to the left ~55% so a missed/highlighted row reads null (→ retry) rather
            // than falling through to a right-side description pane that repeats the setting name. The VALUE
            // is capped by the map's ValueMaxXFrac for the same reason (pane prose must never read as a value).
            var v = frame?.ValueFor(rowLabel, 0.55, map.ValueMaxXFrac);
            if (!string.IsNullOrWhiteSpace(v)) return v;
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        return await ReadValueZoomFallbackAsync(map, rowLabel, ct).ConfigureAwait(false);
    }

    /// <summary>Row-zoom OCR fallback (#205 follow-up, live DOOM 2026-07-09): full-frame OCR can miss a
    /// focused row's small value token entirely at a sub-native desktop ('TAA' at 1080p failed 8/8 reads
    /// while 'DLSS' on the same row read fine) — the whole dropdown then fails 'could not read the current
    /// value'. Rescue: grab one frame to disk, locate the label on the full frame, then crop the band right
    /// of the label and OCR it at 4× zoom, which puts the token far above Windows OCR's small-text floor.
    /// Only runs after every normal read try failed, so it adds no cost to the healthy path.</summary>
    private async Task<string?> ReadValueZoomFallbackAsync(MenuMap map, string rowLabel, CancellationToken ct)
    {
        var png = Path.Combine(Path.GetTempPath(), $"visionval_{Guid.NewGuid():N}.png");
        try
        {
            if (!await _reader.Grabber.GrabAsync(png, 12, ct).ConfigureAwait(false)) return null;
            var frame = await _reader.OcrImageAsync(png, ct).ConfigureAwait(false);
            GpuSuite.Core.RunHeartbeat.Ping();
            if (frame is null || frame.Width <= 0) return null;
            var label = frame.FindAll(rowLabel).Where(l => l.X <= (int)(0.55 * frame.Width)).OrderBy(l => l.X).FirstOrDefault();
            if (label is null) return null;
            int maxValueX = map.ValueMaxXFrac >= 1.0 ? frame.Width : (int)(map.ValueMaxXFrac * frame.Width);
            var zoom = await _reader.OcrRowValueZoomAsync(png, label, maxValueX, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(zoom))
                _log.Info("MenuApply", $"Row-zoom OCR fallback read '{zoom}' for '{rowLabel}' (full-frame reads missed the value).");
            return string.IsNullOrWhiteSpace(zoom) ? null : zoom;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.Warn("MenuApply", "Row-zoom OCR fallback failed: " + ex.Message); return null; }
        finally { try { if (File.Exists(png)) File.Delete(png); } catch { } }
    }

    /// <summary>Focus the control's row. GUIDED (read the FocusHeader region, step Down re-reading until it
    /// names the control — handles long/scrolling/WRAPPING lists) when a FocusHeader is configured; else the
    /// home-up + down-from-top count fallback.</summary>
    private async Task<(bool ok, string detail)> NavigateToControlAsync(
        InputAutomationEngine engine, BotInputDevice device, MenuMap map, MenuControl c, CancellationToken ct)
    {
        if (map.FocusHeader is { } h)
        {
            string last = "";
            for (int i = 0; i <= c.MaxSteps; i++)
            {
                var frame = await ReadFrameAsync(ct).ConfigureAwait(false);
                // OSD-STRIPPED read (#200 hardening): the FocusHeader read is ANCHOR-class (label matching,
                // never numeric values), and the design convention is that anchor matching reads the frame
                // with RTSS/OSD lines removed (WithoutOsdLines) — this read predated the convention. In a
                // campaign the RTSS group runs with the native fps counter on-screen during menu-apply;
                // an OSD line inside the header region would garble every header read ('anchor unreachable').
                last = frame?.WithoutOsdLines().TextInRegion(h.XMin, h.YMin, h.XMax, h.YMax) ?? "";
                _log.Info("MenuApply", $"guided step {i}/{c.MaxSteps} toward '{c.Label}': header '{last}'");
                if (Matches(last, c.Label))
                {
                    // Focus is on the row, but the game can leave the LIST scrolled away from the focused row
                    // (e.g. focus carried over while the view sits at the top), so the row's value is
                    // off-screen. Nudge Down then Up (net-zero focus) to force the view to track it into view.
                    await RunStepsAsync(engine, device, Tap1(device, "Down"), ct).ConfigureAwait(false);
                    await RunStepsAsync(engine, device, Tap1(device, "Up"), ct).ConfigureAwait(false);
                    await Settle(map, ct).ConfigureAwait(false);
                    return (true, $"focused after {i} step(s)");
                }
                await RunStepsAsync(engine, device, Tap1(device, "Down"), ct).ConfigureAwait(false);
                await Settle(map, ct).ConfigureAwait(false);
            }
            return (false, $"could not focus '{c.Label}' within {c.MaxSteps} guided steps (last header '{last}')");
        }

        var focus = new List<BotAction>();
        for (int i = 0; i < Math.Max(0, c.HomeUpCount); i++) { focus.Add(Step(device, "Up", 35)); focus.Add(BotAction.Pause(35)); }
        for (int i = 0; i < Math.Max(0, c.DownFromTop); i++) { focus.Add(Step(device, "Down", 35)); focus.Add(BotAction.Pause(45)); }
        await RunStepsAsync(engine, device, focus, ct).ConfigureAwait(false);
        await Settle(map, ct).ConfigureAwait(false);
        return (true, "focused by count");
    }

    /// <summary>Resolve an on-screen value text to an option index, EXACT-normalized-equality FIRST (case,
    /// spacing and punctuation ignored), containment only as a fallback for partial OCR reads. The old
    /// containment-only Matches() mis-resolved values whose label is a SUBSTRING of a sibling option — live
    /// 2026-07-04 on DOOM: the row read 'Performance' but FindIndex hit 'Ultra Performance' first
    /// ('ultraperformance'.Contains('performance')), so the dropdown stepped 0→3 instead of 1→3 and landed on
    /// DLAA. Same hazard family: FM 'Car reflections' vs 'Car reflections + RTAO'. In the fallback pass the
    /// SHORTEST matching label wins (the minimal claim); a wrong pick still fails the strict verify below, so
    /// it can never silently mislabel.</summary>
    private static int OptionIndexFor(string? text, MenuControl c)
    {
        if (string.IsNullOrWhiteSpace(text)) return -1;
        string nt = Norm(text);
        if (nt.Length == 0) return -1;
        for (int i = 0; i < c.OptionOrder.Count; i++)
        {
            var o = c.OptionOrder[i];
            var label = c.OptionLabels.TryGetValue(o, out var ol) ? ol : o;
            if (Norm(label) == nt || Norm(o) == nt) return i;
        }
        int best = -1, bestLen = int.MaxValue;
        for (int i = 0; i < c.OptionOrder.Count; i++)
        {
            var o = c.OptionOrder[i];
            var label = c.OptionLabels.TryGetValue(o, out var ol) ? ol : o;
            int len = Norm(label).Length;
            if (Matches(text, label) && len < bestLen) { best = i; bestLen = len; }
        }
        return best;
    }

    /// <summary>Dropdown actuation. The list opens with the CURRENT value highlighted, so we step by the
    /// index DELTA (it may wrap, which defeats home-to-top), confirm, then re-read to VERIFY. The indices
    /// come from the declared OptionOrder, so it is robust to OCR noise inside the open dropdown (e.g. "DLSS"
    /// mis-read as "DESS"). currentOnScreen is the value read off the row before opening. Both the current
    /// read AND the verify resolve through <see cref="OptionIndexFor"/> (exact-first), so a substring sibling
    /// (Performance vs Ultra Performance) can neither mis-step the delta nor false-verify the result.</summary>
    private async Task<(bool ok, string detail)> SetDropdownAsync(
        InputAutomationEngine engine, BotInputDevice device, MenuMap map, MenuControl c,
        string value, string targetText, string rowLabel, string? currentOnScreen, CancellationToken ct)
    {
        int targetIdx = c.OptionOrder.FindIndex(o => string.Equals(o, value, StringComparison.OrdinalIgnoreCase));
        if (targetIdx < 0) return (false, $"value '{value}' not in OptionOrder [{string.Join(",", c.OptionOrder)}]");
        int currentIdx = OptionIndexFor(currentOnScreen, c);
        if (currentIdx < 0) return (false, $"could not read the current value to step the dropdown (read '{currentOnScreen ?? "null"}')");

        string sel = device == BotInputDevice.Gamepad ? "A" : "Enter";
        int delta = targetIdx - currentIdx;
        string dir = delta >= 0 ? "Down" : "Up";
        // Drive open -> step(s) -> confirm as ONE input session so the per-call virtual pad does not
        // disconnect mid-dropdown (a disconnect can close the popup or drop the highlight).
        var seq = new List<BotAction> { Step(device, sel, 110), BotAction.Pause(800) }; // open; highlights current
        for (int i = 0; i < Math.Abs(delta); i++) { seq.Add(Step(device, dir, 110)); seq.Add(BotAction.Pause(550)); }
        seq.Add(Step(device, sel, 110)); seq.Add(BotAction.Pause(900));                 // confirm
        await RunStepsAsync(engine, device, seq, ct).ConfigureAwait(false);
        await Settle(map, ct).ConfigureAwait(false);
        await Task.Delay(400, ct).ConfigureAwait(false); // the dropdown closes and the list reflows

        var verify = await ReadValueAsync(map, rowLabel, 8, ct).ConfigureAwait(false);
        // STRICT verify: the re-read must resolve (exact-first) to the TARGET option — Matches() containment
        // alone would accept 'Ultra Performance' for a 'Performance' target (silent mislabel).
        return OptionIndexFor(verify, c) == targetIdx
            ? (true, $"dropdown {dir} {Math.Abs(delta)}× ({currentIdx}->{targetIdx}), verified '{verify}'")
            : (false, $"dropdown {dir} {Math.Abs(delta)}× but row reads '{verify ?? "?"}' (wanted '{targetText}')");
    }

    /// <summary>Cycle actuation: press Right (then Left), re-reading each step until the row value matches.
    /// When the control declares an OptionOrder, the match resolves exact-first through
    /// <see cref="OptionIndexFor"/> so a substring sibling can't stop the cycle early (e.g. an FM target of
    /// 'Car reflections' must not accept the 'Car reflections + RTAO' rung); without one it keeps the legacy
    /// containment match.</summary>
    private async Task<(bool ok, string detail)> SetCycleAsync(
        InputAutomationEngine engine, BotInputDevice device, MenuMap map, MenuControl c,
        string targetText, string rowLabel, CancellationToken ct)
    {
        int targetIdx = c.OptionOrder.Count > 0 ? OptionIndexFor(targetText, c) : -1;
        bool Hit(string? read) => c.OptionOrder.Count > 0 && targetIdx >= 0
            ? OptionIndexFor(read, c) == targetIdx
            : Matches(read, targetText);
        string? current = null;
        foreach (var (key, dir) in new[] { (c.CycleRight, "right"), (c.CycleLeft, "left") })
        {
            for (int i = 0; i < c.MaxCycles; i++)
            {
                await RunStepsAsync(engine, device, Tap1(device, key), ct).ConfigureAwait(false);
                await Settle(map, ct).ConfigureAwait(false);
                var frame = await ReadFrameAsync(ct).ConfigureAwait(false);
                current = frame?.ValueFor(rowLabel, 1.0, map.ValueMaxXFrac);
                if (Hit(current)) return (true, $"cycled {dir} {i + 1}× → verified '{current}'");
            }
        }
        return (false, $"could not reach '{targetText}' (last read '{current ?? "?"}') within {c.MaxCycles} cycles each way");
    }

    private static BotAction Step(BotInputDevice device, string key, int ms)
        => device == BotInputDevice.Gamepad ? BotAction.PadTap(key, ms) : BotAction.Tap(key, ms);

    /// <summary>One key/pad tap plus a short trailing dwell. The dwell matters for the gamepad: each one-shot
    /// step creates and then disposes the ViGEm pad, and without a dwell the pad disconnects before the game
    /// registers the press (so steps silently drop). The dwell keeps it connected just long enough.</summary>
    // 200ms tap + 300ms dwell (was 80/150): DOOM's 2026-07 'Ripatorium 2.0' update started dropping the old
    // 80ms taps — live 2026-07-03 the anchor RB-cycle pressed 8x with the tab never moving, while manual
    // 150ms+ taps switched tabs fine. 200ms matches the proven cross-game pad floor (Black Myth drops <200ms)
    // and the longer dwell widens the create-tap-dispose window described above. Menu taps aren't
    // timing-critical; the extra ~270ms/step is noise.
    private static List<BotAction> Tap1(BotInputDevice device, string key)
        => new() { Step(device, key, 200), BotAction.Pause(300) };

    private Task Settle(MenuMap map, CancellationToken ct) => Task.Delay(Math.Max(120, map.SettleMs), ct);

    /// <summary>Run a one-shot (non-looping) sequence of menu actions through the input engine.</summary>
    private static Task RunStepsAsync(InputAutomationEngine engine, BotInputDevice device, List<MenuAction> actions, CancellationToken ct)
        => RunStepsAsync(engine, device, actions.Select(ToBotAction).ToList(), ct);

    /// <summary>Run an open/back sequence, honoring inline VISION-GATED steps. <c>waitText</c> (Key or Text =
    /// the text, Ms = timeout) polls the capture card until that text is on screen before the following taps
    /// fire — so a COLD launch that hasn't reached its interactive splash yet doesn't eat the open taps (the
    /// DOOM cold-launch failure). <c>tapIfText</c>/<c>tapIfNoText</c> (Key = button, Text = OCR text) press
    /// ONLY when the text is / is not visible — a CONDITIONAL confirm: DOOM's back sequence must press A on
    /// the apply NOTICE, but a verify-only apply (all knobs already correct) raises NO notice, and the old
    /// blind A then selected the main menu's focused CAMPAIGN row — the benchmark bot inherited a campaign
    /// screen and every one of its gates timed out (live 2026-07-04, runs 10-11: 10/10 'finish not detected').
    /// Non-gate steps between gates are batched into one contiguous input script (as before).</summary>
    private async Task RunOpenAsync(InputAutomationEngine engine, BotInputDevice device, List<MenuAction> steps, CancellationToken ct)
    {
        var batch = new List<MenuAction>();
        async Task FlushAsync()
        {
            if (batch.Count > 0) { await RunStepsAsync(engine, device, batch, ct).ConfigureAwait(false); batch = new(); }
        }
        foreach (var step in steps)
        {
            var type = (step.Type ?? "tap").ToLowerInvariant();
            if (type == "waittext")
            {
                await FlushAsync().ConfigureAwait(false);
                await WaitForTextGateAsync(step.Text ?? step.Key, step.Ms > 0 ? step.Ms : 60000, ct).ConfigureAwait(false);
            }
            else if (type is "tapiftext" or "tapifnotext")
            {
                await FlushAsync().ConfigureAwait(false);
                var f = await ReadFrameAsync(ct).ConfigureAwait(false);
                bool visible = f?.Find(step.Text ?? "") is not null;
                bool press = type == "tapiftext" ? visible : !visible;
                _log.Info("MenuApply", $"Conditional tap '{step.Key}': '{step.Text}' {(visible ? "visible" : "not visible")} → {(press ? "PRESSING" : "skipping")}.{(step.Note is null ? "" : " — " + step.Note)}");
                if (press)
                    await RunStepsAsync(engine, device, Tap1(device, step.Key ?? "A"), ct).ConfigureAwait(false);
            }
            else batch.Add(step);
        }
        await FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Poll the capture card until <paramref name="needle"/> is on screen (substring, case/spacing
    /// insensitive) or <paramref name="timeoutMs"/> elapses. On timeout it proceeds best-effort (returns false)
    /// rather than failing — the downstream anchor check still fail-safes if the menu genuinely never opened.</summary>
    private async Task<bool> WaitForTextGateAsync(string? needle, int timeoutMs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(needle)) return true;
        _log.Info("MenuApply", $"Gate: waiting up to {timeoutMs}ms for '{needle}' on screen before driving the menu…");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int reads = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var f = await ReadFrameAsync(ct).ConfigureAwait(false);
            reads++;
            if (f?.Find(needle) is not null)
            {
                _log.Info("MenuApply", $"Gate: '{needle}' visible after {sw.ElapsedMilliseconds}ms ({reads} read(s)) — proceeding.");
                return true;
            }
            await Task.Delay(1200, ct).ConfigureAwait(false);
        }
        _log.Warn("MenuApply", $"Gate: '{needle}' not seen within {timeoutMs}ms ({reads} read(s)) — proceeding best-effort (the anchor check fail-safes if the menu never opened).");
        return false;
    }

    private static async Task RunStepsAsync(InputAutomationEngine engine, BotInputDevice device, List<BotAction> actions, CancellationToken ct)
    {
        if (actions.Count == 0) return;
        var script = new BotScript { Id = "menu-apply-step", Loop = false, InputDevice = device, Actions = actions };
        // Non-looping: the engine exits right after the last action; the window is just an upper bound.
        var res = await engine.RunAsync(script, TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
        // A strict-foreground refusal surfaces as Aborted — stop the whole apply session (caught in ApplyAsync)
        // rather than press on with later steps against a window the engine could not reach.
        if (res.Aborted) throw new MenuApplyAbortedException(res.AbortReason ?? "input session aborted");
    }

    private static BotAction ToBotAction(MenuAction a) => (a.Type ?? "tap").ToLowerInvariant() switch
    {
        "wait" => BotAction.Pause(a.Ms),
        "waittext" => BotAction.Pause(1),   // vision gate — handled in RunOpenAsync; a no-op if it ever reaches here
        "padtap" => BotAction.PadTap(a.Key ?? "A", a.Ms <= 0 ? 200 : a.Ms),
        "moveabs" => BotAction.MoveAbs(a.X, a.Y, a.Ms <= 0 ? 60 : a.Ms),
        "click" => BotAction.Click(a.Key ?? "left", a.Ms <= 0 ? 40 : a.Ms),
        _ => BotAction.Tap(a.Key ?? "Enter", a.Ms <= 0 ? 45 : a.Ms),
    };
}
