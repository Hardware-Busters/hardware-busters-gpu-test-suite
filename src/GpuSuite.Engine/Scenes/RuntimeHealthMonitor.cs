using System.Diagnostics;
using GpuSuite.Core.Config;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Io;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Vision;

namespace GpuSuite.Engine.Scenes;

/// <summary>
/// Runtime health watchdog (Milestone 3). Runs as a background loop ALONGSIDE a measured run, polling the
/// live frame / telemetry / power sessions + the game process. When a condition stays unhealthy past its
/// grace period it records a <see cref="HealthIncident"/> (with a best-effort screenshot + a JSON log) and
/// stops. The caller then invalidates the run, which feeds the deterministic auto-repeat recovery; a
/// persistent failure classifies the game RuntimeHealth and skips it — the roster never stops.
///
/// It is PASSIVE: it observes and records, it does not cancel the capture. The measured window is already
/// bounded (the completion detector's frame-stall + max-timeout, or the fixed window), and the per-game
/// heartbeat watchdog is the hard backstop — so a dead run still ends promptly and is then rejected here
/// with a clear, classified reason + evidence.
/// </summary>
public sealed class RuntimeHealthMonitor
{
    private readonly RuntimeHealthConfig _cfg;
    private readonly SuiteConfig _suite;
    private readonly RunLogger _log;
    private readonly string _runDir;
    private readonly Func<int> _frameCount;
    private readonly Func<double?> _gpuLoad;
    private readonly Func<double?> _power;
    private readonly Func<int> _telCount;
    private readonly int? _pid;
    private readonly double _timeoutSeconds;
    private readonly Func<bool>? _renderArmed;   // gpu-idle / frames-frozen only count while this is true (measured window); null = always
    private readonly double _motionFloor;        // >0 arms the scene-static motion probe (effective per-scene floor)
    private readonly double _motionDelaySeconds; // legitimate static opening after the measured gate first opens
    private readonly string _frameProviderName;
    private readonly CaptureCardGrabber? _motion; // built only when the motion probe is armed + device/ffmpeg available
    private readonly CancellationTokenSource _stop;   // LINKED to the run's token (ct) — see ctor
    private Task? _loop;
    private bool _stopped;

    public HealthIncident? Incident { get; private set; }

    /// <summary>Measured-window motion stats from the scene-static sensor — populated when the loop ends
    /// (read AFTER <see cref="StopAsync"/>). Null when the sensor never armed or no probe succeeded.</summary>
    public MotionStats? Motion { get; private set; }

    private RuntimeHealthMonitor(RuntimeHealthConfig cfg, SuiteConfig suite, RunLogger log, string runDir,
        Func<int> frameCount, Func<double?> gpuLoad, Func<double?> power, Func<int> telCount, int? pid, double timeoutSeconds, CancellationToken ct,
        Func<bool>? renderArmed, double motionFloor, double motionDelaySeconds, string? frameProviderName)
    {
        _cfg = cfg; _suite = suite; _log = log; _runDir = runDir;
        _frameCount = frameCount; _gpuLoad = gpuLoad; _power = power; _telCount = telCount;
        _pid = pid; _timeoutSeconds = timeoutSeconds; _renderArmed = renderArmed;
        _motionFloor = motionFloor;
        _motionDelaySeconds = Math.Max(0, motionDelaySeconds);
        _frameProviderName = string.IsNullOrWhiteSpace(frameProviderName) ? "frame capture" : frameProviderName.Trim();
        if (motionFloor > 0 && !string.IsNullOrWhiteSpace(suite.CaptureCardDevice))
        {
            var g = new CaptureCardGrabber(suite.FfmpegPath, suite.CaptureCardDevice, log);
            if (g.FfmpegResolved) _motion = g;
        }
        // Linked to the run's token so the background loop ALWAYS dies when the run/game/campaign ends —
        // even if StopAsync is missed on an exception path. No background task can survive the run.
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    public static RuntimeHealthMonitor? Start(RuntimeHealthConfig cfg, SuiteConfig suite, RunLogger log, string runDir,
        Func<int> frameCount, Func<double?> gpuLoad, Func<double?> power, Func<int> telCount, int? pid, double timeoutSeconds, CancellationToken ct,
        Func<bool>? renderArmed = null, double motionFloor = 0, double motionDelaySeconds = 0, string? frameProviderName = null)
    {
        if (!cfg.Enabled) return null;
        var m = new RuntimeHealthMonitor(cfg, suite, log, runDir, frameCount, gpuLoad, power, telCount, pid, timeoutSeconds, ct, renderArmed, motionFloor, motionDelaySeconds, frameProviderName);
        m._loop = Task.Run(m.LoopAsync);
        return m;
    }

    /// <summary>Stop the loop and return the incident it found (or null). Idempotent.</summary>
    public async Task<HealthIncident?> StopAsync()
    {
        if (_stopped) return Incident;
        _stopped = true;
        try { _stop.Cancel(); } catch { }
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch { } }
        _stop.Dispose();
        return Incident;
    }

    private async Task LoopAsync()
    {
        var start = DateTime.UtcNow;
        int lastFrames = SafeInt(_frameCount); DateTime? frameStall = null;
        bool providerDeadLogged = false;   // frames-stuck-while-GPU-busy (dead provider) is warned ONCE, not per poll
        int lastTel = SafeInt(_telCount); DateTime? telStall = null;
        DateTime? idleSince = null;
        double? lastPower = null; DateTime? powerFlat = null;
        // grace before believing telemetry/power stalled — must exceed their sampling interval
        double telGrace = Math.Max(_cfg.TelemetryStallGraceSeconds, _suite.TelemetryIntervalMs / 1000.0 * 4);
        // scene-static motion sensing (only while the measured window is armed)
        DateTime lastMotionProbe = DateTime.MinValue; DateTime? staticSince = null;
        DateTime? motionWindowOpened = null;
        int motionProbes = 0, motionLow = 0; double motionMin = double.MaxValue, motionSum = 0;

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(_cfg.PollIntervalMs, _stop.Token).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                double elapsed = (now - start).TotalSeconds;

                // Render-health (gpu-idle / frames-frozen) only counts while the MEASURED window is active — a
                // scripted-scene bot CLOSES the gate during its nav so a static cold-launch screen (disclaimer /
                // "PRESS TO CONTINUE" / load) sitting GPU-idle past the 6s grace can't false-fail the run, then
                // OPENS it at MarkStart. Null gate (builtin benchmark / route-only attach) = always armed.
                bool renderArmed = _renderArmed?.Invoke() ?? true;
                if (renderArmed) motionWindowOpened ??= now;
                else motionWindowOpened = null;
                bool motionArmed = renderArmed && motionWindowOpened is DateTime opened &&
                    (now - opened).TotalSeconds >= _motionDelaySeconds;

                // process exited (reliable; skip for simulated/attach where pid may be null).
                // Dispose the Process handle each poll — otherwise a 24h run leaks one handle per second.
                if (_pid is int pid)
                {
                    bool exited;
                    try { using var proc = Process.GetProcessById(pid); exited = proc.HasExited; }
                    catch { await FireAsync("process-exited", $"game process {pid} is gone (exited/killed) during the measured run", elapsed); return; }
                    if (exited) { await FireAsync("process-exited", $"game process {pid} exited during the measured run", elapsed); return; }
                }

                int cf = SafeInt(_frameCount);
                var gl = Safe(_gpuLoad);

                // LIVENESS → per-game heartbeat (2026-07-05, DOOM dlss-fg run 25): during a measured window the
                // heartbeat is normally fed by the frame provider ("frames are presenting = alive"). When the
                // provider records NOTHING (PresentMon's known 0-frame ETW flake on idTech 8 / Cyberpunk) while
                // the game is demonstrably rendering (GPU busy — the incident screenshot showed the Hebeth
                // flythrough LIVE at 159.7 fps), the game-level hang watchdog starved and KILLED the healthy
                // benchmark at exactly MarkStart+60s — classified 'crash', game skipped, and the attempt-2 RTSS
                // fallback (designed for THIS provider failure) never ran. This monitor is the one component
                // that can tell a dead GAME from a dead PROVIDER, so it feeds the beacon: frames advancing OR
                // GPU visibly busy = the game is alive. The capture failure itself is still recorded
                // (frames-frozen below) and still invalidates the run — but the game survives to its retry.
                if (cf != lastFrames || (gl is double liveGpu && liveGpu > _cfg.IdleGpuLoadPct))
                    GpuSuite.Core.RunHeartbeat.Ping();

                // frozen render / 0 fps — captured frame count not advancing
                if (renderArmed && cf == lastFrames)
                {
                    frameStall ??= now;
                    if ((now - frameStall.Value).TotalSeconds >= _cfg.FrameStallGraceSeconds)
                    {
                        // Frame count stuck but the GPU is BUSY = the capture PROVIDER is dead, not the game
                        // (PresentMon's 0-frame ETW flake — run 25 fired frames-frozen at +7.4s over a Hebeth
                        // flythrough rendering at 159.7 fps, and the loop's exit then re-starved the heartbeat).
                        // Do NOT fire (an incident here misclassifies a healthy game as crashed): log once,
                        // keep watching (the liveness beacon above stays fed), let the completion detector
                        // bound the window — the run is then rejected on MinFrameCount and the NEXT attempt
                        // follows the game's configured capture recovery policy (which may forbid RTSS).
                        if (gl is double busyG && busyG > _cfg.IdleGpuLoadPct)
                        {
                            if (!providerDeadLogged)
                            {
                                providerDeadLogged = true;
                                _log.Warn("Health", $"Frame count stuck at {cf} for {_cfg.FrameStallGraceSeconds:0}s but the GPU is BUSY (~{busyG:0}%) — {_frameProviderName} is not producing frames, so this is a capture-provider failure rather than a frozen game. Letting the measured window complete; this run will be rejected so the next attempt can retry the configured capture path.");
                            }
                        }
                        else
                        { await FireAsync("frames-frozen", $"no new frames for {_cfg.FrameStallGraceSeconds:0}s (frame count stuck at {cf}) — frozen render / 0 fps", elapsed); return; }
                    }
                }
                else { frameStall = null; lastFrames = cf; }

                // GPU stuck near 0% — not rendering
                if (renderArmed && gl is double g && g <= _cfg.IdleGpuLoadPct)
                {
                    idleSince ??= now;
                    if ((now - idleSince.Value).TotalSeconds >= _cfg.IdleGraceSeconds)
                    { await FireAsync("gpu-idle", $"GPU load ~{g:0}% (≤ {_cfg.IdleGpuLoadPct:0}%) for {_cfg.IdleGraceSeconds:0}s — GPU not rendering", elapsed); return; }
                }
                else idleSince = null;

                // scene-static — capture-card MOTION (ffmpeg scene-change, CPU-only/off-bench: the SmartTraverse
                // Tier-0 sensor) near zero while the measured window claims a moving scripted route. Catches what
                // gpu-idle and frames-frozen CANNOT: a wedged/dead character, a paused game, or a static screen
                // that still RENDERS at high GPU with advancing frames (the AW2 #107 failure — two static screens
                // measured as a "valid" run). Polite on the exclusive Elgato: SKIPS the probe (without counting it
                // as evidence either way) while any bot sensor/OCR grab holds the device.
                if (motionArmed && _motion is not null &&
                    (now - lastMotionProbe).TotalSeconds >= _cfg.MotionProbeIntervalSeconds && !CaptureCardGrabber.DeviceBusy)
                {
                    lastMotionProbe = now;
                    double m;
                    using (var probe = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                    {
                        probe.CancelAfter(TimeSpan.FromSeconds(8));   // bound the stall — a hung ffmpeg must not starve the other checks
                        try { m = await _motion.SampleMotionAsync(8, ct: probe.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { m = -1; }
                    }
                    if (m >= 0)
                    {
                        motionProbes++; motionSum += m; if (m < motionMin) motionMin = m;
                        if (m < _motionFloor)
                        {
                            motionLow++;
                            staticSince ??= now;
                            if ((now - staticSince.Value).TotalSeconds >= _cfg.MotionStaticGraceSeconds)
                            { await FireAsync("scene-static", $"capture-card motion ~{m:0.000} (< {_motionFloor:0.000}) for {_cfg.MotionStaticGraceSeconds:0}s — the measured scene is STATIC (wedged/dead character, paused game, or a static screen still rendering)", elapsed); return; }
                        }
                        else staticSince = null;
                    }
                }
                else if (!motionArmed) staticSince = null;   // window closed or in its legitimate static opening

                // telemetry stopped — sensor sample count frozen
                int ct = SafeInt(_telCount);
                if (ct == lastTel)
                {
                    telStall ??= now;
                    if ((now - telStall.Value).TotalSeconds >= telGrace)
                    { await FireAsync("telemetry-stopped", $"no new telemetry for {telGrace:0}s — sensor feed stopped", elapsed); return; }
                }
                else { telStall = null; lastTel = ct; }

                // power flatline — identical reading repeated (frozen PMD/sensor)
                var pw = Safe(_power);
                if (pw is double w && w > 0)
                {
                    if (lastPower is double lp && Math.Abs(w - lp) < 0.01)
                    {
                        powerFlat ??= now;
                        if ((now - powerFlat.Value).TotalSeconds >= _cfg.PowerFlatlineGraceSeconds)
                        { await FireAsync("power-flatline", $"power stuck at {w:0.0} W for {_cfg.PowerFlatlineGraceSeconds:0}s — power sensor/PMD frozen", elapsed); return; }
                    }
                    else powerFlat = null;
                    lastPower = w;
                }

                // benchmark timeout — the measured window ran far past its expected length
                if (_timeoutSeconds > 0 && elapsed > _timeoutSeconds)
                { await FireAsync("benchmark-timeout", $"measured window exceeded {_timeoutSeconds:0}s without completing", elapsed); return; }
            }
        }
        catch (OperationCanceledException) { /* stopped normally */ }
        catch (Exception ex) { _log.Trace("Health", $"health monitor loop error: {ex.Message}"); }
        finally
        {
            if (motionProbes > 0)
            {
                Motion = new MotionStats
                {
                    Probes = motionProbes,
                    MeanScore = Math.Round(motionSum / motionProbes, 3),
                    MinScore = Math.Round(motionMin, 3),
                    FloorScore = _motionFloor,
                    BelowFloor = motionLow
                };
                _log.Trace("Health", $"scene-static sensor: {motionProbes} motion probe(s) in the measured window — mean {motionSum / motionProbes:0.000}, min {motionMin:0.000}, floor {_motionFloor:0.000}{(motionLow > 0 ? $", {motionLow} below-floor" : "")}.");
            }
        }
    }

    private async Task FireAsync(string kind, string detail, double elapsed)
    {
        if (Incident is not null) return;   // first incident wins
        Incident = new HealthIncident { Kind = kind, Detail = detail, AtSeconds = Math.Round(elapsed, 1), DetectedUtc = DateTime.UtcNow.ToString("o") };
        _log.Warn("Health", $"RUNTIME HEALTH incident: {kind} — {detail} (at {elapsed:0.0}s into the window).");
        if (_cfg.CaptureScreenshotOnIncident) Incident.ScreenshotFile = await TryScreenshotAsync().ConfigureAwait(false);
        TryWriteLog();
    }

    /// <summary>Best-effort capture-card screenshot at the incident. Never throws; null when unavailable.</summary>
    private async Task<string?> TryScreenshotAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_suite.CaptureCardDevice)) return null;
            var file = "health_incident.png";
            var path = Path.Combine(_runDir, file);
            var grabber = new CaptureCardGrabber(_suite.FfmpegPath, _suite.CaptureCardDevice, _log);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            bool ok = await grabber.GrabAsync(path, warmupFrames: 4, cts.Token, scaleWidth: 1280).ConfigureAwait(false);
            return ok && File.Exists(path) ? file : null;
        }
        catch (Exception ex) { _log.Trace("Health", $"incident screenshot failed: {ex.Message}"); return null; }
    }

    private void TryWriteLog()
    {
        try { if (Incident is not null) Json.Save(Path.Combine(_runDir, "health_incident.json"), Incident); }
        catch (Exception ex) { _log.Trace("Health", $"incident log write failed: {ex.Message}"); }
    }

    private static int SafeInt(Func<int> f) { try { return f(); } catch { return 0; } }
    private static double? Safe(Func<double?> f) { try { return f(); } catch { return null; } }
}
