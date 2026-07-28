using System.Security.Principal;
using System.Text;
using GpuSuite.Core.Config;
using GpuSuite.Core.Models;
using GpuSuite.Measurement.Real;
using GpuSuite.Measurement.Synthetic;
using LibreHardwareMonitor.Hardware;

namespace GpuSuite.Measurement;

/// <summary>Resolves the per-game capture pin before the machine-wide default.</summary>
public static class FrameProviderPolicy
{
    /// <summary>
    /// Initial source probes must not arm RTSS for a resolved roster. RTSS is a global render hook, so
    /// profile-aware runs start it only from <see cref="MeasurementFactory.PrepareFrameBackend"/> at the
    /// compatible game boundary. A null plan is retained for legacy stand-alone diagnostics.
    /// </summary>
    public static bool ShouldStartRtssDuringInitialProbe(
        string? globalProvider,
        bool presentMonLive,
        bool? planUsesRtss,
        bool allowRtssStartup = true)
    {
        if (!allowRtssStartup || planUsesRtss.HasValue) return false;
        string global = (globalProvider ?? "auto").Trim().ToLowerInvariant();
        return global == "rtss" || (global == "auto" && !presentMonLive);
    }

    public static string Resolve(string? globalProvider, string? profileProvider, bool presentMonLive, bool forbidRtss = false)
    {
        if (forbidRtss) return "presentmon";
        string global = (globalProvider ?? "auto").Trim().ToLowerInvariant();
        string profile = profileProvider?.Trim().ToLowerInvariant() ?? "";
        // An explicit UI/CLI global RTSS selection is an intentional run-wide override. A per-game RTSS pin
        // still wins over global PresentMon for AppContainer games. Safety forbidRtss wins over both.
        string requested = global == "rtss" || profile == "rtss"
            ? "rtss"
            : profile == "presentmon" ? "presentmon" : global;
        return requested switch
        {
            "rtss" => "rtss",
            "presentmon" => "presentmon",
            "auto" => presentMonLive ? "presentmon" : "rtss",
            _ => presentMonLive ? "presentmon" : "rtss"
        };
    }

    public static bool AllowsRtssFallback(string? globalProvider, string? profileProvider, bool forbidRtss = false)
    {
        if (forbidRtss) return false;
        string global = globalProvider?.Trim() ?? "auto";
        return global.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
               global.Equals("rtss", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The providers selected for a session, plus a human-readable availability report.</summary>
public sealed class ProviderSet
{
    public required IFrameCaptureProvider Frames { get; init; }
    public required IPowerProvider Power { get; init; }
    public required ITelemetryProvider Telemetry { get; init; }
    public DataSourceMode FrameMode => Frames.IsLive ? DataSourceMode.Live : DataSourceMode.Synthetic;
    public DataSourceMode PowerMode => Power.IsLive ? DataSourceMode.Live : DataSourceMode.Synthetic;
    public DataSourceMode TelemetryMode { get; init; }
    public List<string> Report { get; } = new();
}

/// <summary>
/// Chooses real vs synthetic providers based on what is actually available right now.
/// This is the seam that makes the whole suite run in degraded mode: each source is
/// probed independently, so you can have real LHM telemetry alongside synthetic
/// PresentMon/Powenetics, or any mix.
/// </summary>
public sealed class MeasurementFactory : IDisposable
{
    private readonly SuiteConfig _cfg;
    private readonly LhmTelemetryProvider _lhm = new();
    private readonly PoweneticsPowerProvider _powenetics;
    private readonly LhmPowerProvider _lhmPower;
    private bool _lhmLive;
    private bool _powerLive;        // Powenetics PMD streaming
    private bool _lhmPowerLive;     // LHM GPU board-power sensor usable
    private bool _rtssLive;
    public string DetectedGpuName { get; private set; } = "Unknown GPU";
    public string DetectedCpuName { get; private set; } = "Unknown CPU";

    // Live-source status from the last ProbeAll() — read by the hardware-validation layer so it doesn't
    // re-probe each source per game.
    public bool LhmLive => _lhmLive;
    public bool PoweneticsLive => _powerLive;
    public bool RtssLive => _rtssLive;
    public bool PresentMonLive { get; private set; }

    public MeasurementFactory(SuiteConfig cfg)
    {
        _cfg = cfg;
        _lhm.PreferredGpuNameContains = string.IsNullOrWhiteSpace(cfg.PreferredGpu) ? null : cfg.PreferredGpu;
        _powenetics = new PoweneticsPowerProvider(cfg.PoweneticsComPort, cfg.PoweneticsAutoDetect);
        _lhmPower = new LhmPowerProvider(_lhm);   // shares the one LHM Computer / pinned GPU
    }

    /// <summary>Probe all sources once up front; fills DetectedGpuName/CpuName.
    /// <paramref name="planUsesRtss"/>: whether the resolved run plan contains at least one game whose
    /// profile forces <c>frameProvider:"rtss"</c> — RTSS (the global D3D hook that crashes Ratchet, see
    /// HANDOVER §9.2) is started LAZILY based on this. Pass <c>false</c> for an all-PresentMon plan so
    /// neither the frame probe NOR the OSD drags RTSS in; <c>null</c> (callers with no plan — the probe
    /// verb, cooler path) keeps the legacy eager behavior. <paramref name="allowRtssStartup"/> is false for
    /// full pre-flight: it may inspect an already-running RTSS shared-memory mapping, but never starts the
    /// global hook merely to check a later game.</summary>
    public List<string> ProbeAll(bool? planUsesRtss = null, bool allowRtssStartup = true)
    {
        var report = new List<string>();

        if (!_cfg.ForceSyntheticTelemetry && _lhm.Probe(out var lhmDetail, out var gpu))
        {
            _lhmLive = true;
            DetectedGpuName = string.IsNullOrWhiteSpace(gpu) ? _lhm.DetectGpuName() : gpu;
            DetectedCpuName = _lhm.DetectCpuName();
            report.Add($"[Telemetry] LIVE  — {lhmDetail}");
            // CPU temp/power come from the SMU via LHM's Ring0 driver, which needs admin. Surface a clear
            // hint when they read 0 (non-elevated) so the operator knows to elevate — same elevation the
            // Xbox/Game Pass (AppContainer) games need for PresentMon capture.
            if (!_lhm.CpuSmuReadable(out var cpuDetail))
                report.Add($"[Telemetry]   ! {cpuDetail}");
        }
        else
        {
            report.Add($"[Telemetry] SYNTH — {(_cfg.ForceSyntheticTelemetry ? "forced by config" : "LHM unavailable")}");
        }

        var pmPath = ResolvePresentMon();
        string pmDetail = "";
        bool pmOk = !_cfg.ForceSyntheticFrames && PresentMonFrameProvider.Probe(pmPath, out pmDetail);
        PresentMonLive = pmOk;
        string fp = (_cfg.FrameProvider ?? "presentmon").Trim().ToLowerInvariant();

        // RTSS is a global D3D hook that can crash Ratchet. A resolved roster NEVER starts it here;
        // PrepareFrameBackend owns that transition at each compatible game boundary. A null plan keeps the
        // legacy stand-alone diagnostic behavior only.
        bool rtssForFrames = !_cfg.ForceSyntheticFrames &&
                             FrameProviderPolicy.ShouldStartRtssDuringInitialProbe(fp, pmOk, planUsesRtss, allowRtssStartup);
        bool rtssForOsd = _cfg.Osd && !string.IsNullOrWhiteSpace(_cfg.RtssExePath) && rtssForFrames;
        if (_cfg.Osd && !rtssForOsd && !rtssForFrames)
            report.Add("[OSD]       skipped — no RTSS game in this run plan, so RTSS (the global hook that crashes Ratchet) stays closed; the overlay renders only in RTSS-group sweeps.");
        if ((rtssForFrames || rtssForOsd) && allowRtssStartup)
            EnsureRtssRunning();
        // Probe the RTSS frame backend whenever RTSS is running — for the global rtss/auto provider AND so a
        // PER-GAME frameProvider="rtss" override (AppContainer titles) can actually use it. Without this,
        // _rtssLive stayed false when the global provider was "presentmon", and the per-game override silently
        // fell through to PresentMon (which can't trace a sandboxed game's exclusive-fullscreen benchmark).
        if ((rtssForFrames || rtssForOsd) && (allowRtssStartup || IsRtssRunning()))
        {
            string rtssDetail = "synthetic frames forced";
            _rtssLive = !_cfg.ForceSyntheticFrames && RtssFrameProvider.Probe(out rtssDetail);
            if (rtssForFrames)
                report.Add($"[Frames]    RTSS {(_rtssLive ? "LIVE" : "unavailable")} — {rtssDetail}");
            else
                report.Add($"[OSD]       RTSS started for the live overlay (frames via {fp}; RTSS frame backend {(_rtssLive ? "available" : "unavailable")} for games that force it).");
        }
        else if (planUsesRtss == true)
        {
            report.Add("[Frames]    RTSS required by the enabled roster but intentionally not started during full pre-flight; installed/path readiness is reported separately and runtime starts it only at a compatible game boundary.");
        }

        if (pmOk)
        {
            // PresentMon captures same-user, already-running processes (targeted by --process_id)
            // WITHOUT elevation; admin is only needed to target processes started by another account
            // or very short-lived ones. So this is informational, not a blocker.
            string elev = IsElevated() ? " (elevated)" : " (not elevated — fine for same-user games)";
            report.Add($"[Frames]    PresentMon LIVE-capable — {pmDetail}{elev}");
        }
        else if (fp != "rtss")
            report.Add($"[Frames]    SYNTH — {(_cfg.ForceSyntheticFrames ? "forced by config" : "PresentMon not found")}");

        if (_cfg.ForceSyntheticPower)
        {
            report.Add("[Power]     SYNTH — forced by config");
        }
        else
        {
            // Source policy: "auto" = Powenetics PMD first, then LHM GPU board power, then synthetic;
            // "powenetics" = PMD only; "lhm" = LHM only (skip the PMD probe entirely — e.g. while the PMD
            // is disconnected/flaky); "synthetic" = synthetic. LHM works on any GPU with no extra hardware.
            string src = (_cfg.PowerSource ?? "auto").Trim().ToLowerInvariant();
            bool tryPmd = src is "auto" or "powenetics";
            bool tryLhm = src is "auto" or "lhm";

            if (tryPmd)
            {
                _powenetics.Probe(out var pwDetail);
                _powerLive = _powenetics.IsLive;
                if (_powerLive) report.Add($"[Power]     LIVE — Powenetics PMD: {pwDetail}");
                // Surface the failure detail LOUDLY (it used to be discarded): the provider distinguishes
                // a WEDGED PMD (device enumerated, port opens, 0 protocol frames → operator must physically
                // replug its USB; a host reboot does NOT reset the MCU) from an absent one.
                else report.Add($"[Power]     ! Powenetics: {pwDetail}");
            }
            if (!_powerLive && tryLhm)
            {
                _lhmPower.Probe(out var lhmPwDetail);
                _lhmPowerLive = _lhmPower.IsLive;
                if (_lhmPowerLive) report.Add($"[Power]     LIVE — {lhmPwDetail} (LHM GPU sensor; no PMD)");
            }
            if (!_powerLive && !_lhmPowerLive)
            {
                string why = src switch
                {
                    "lhm" => "LHM GPU board-power sensor unavailable on this card",
                    "powenetics" => "no Powenetics PMD streaming",
                    "synthetic" => "forced by powerSource=synthetic",
                    _ => "no Powenetics PMD and no LHM GPU board-power sensor"
                };
                report.Add($"[Power]     SYNTH — {why}");
            }
        }

        return report;
    }

    /// <summary>Build the provider set for one run, given a workload hint and capture target.
    /// <paramref name="preferRtssOverride"/> forces the RTSS backend (when it is live and the target is
    /// running) regardless of the configured frame provider — used by the orchestrator to retry a run
    /// that hit a PresentMon capture discontinuity with the more robust short-window backend.</summary>
    public ProviderSet CreateFor(
        FrameCaptureTarget target,
        bool preferRtssOverride = false,
        bool preferPresentMonOverride = false,
        bool lateRtssOverride = false)
    {
        // Telemetry
        ITelemetryProvider tel;
        DataSourceMode telMode;
        if (_lhmLive && !_cfg.ForceSyntheticTelemetry) { tel = _lhm; telMode = DataSourceMode.Live; }
        else { tel = new SyntheticTelemetryProvider(); telMode = DataSourceMode.Synthetic; }

        // Frames: live only if a backend is available AND the target process is actually running.
        IFrameCaptureProvider frames;
        var pmPath = ResolvePresentMon();
        bool targetRunning = TargetProcessRunning(target);
        bool pmOk = !_cfg.ForceSyntheticFrames && PresentMonFrameProvider.Probe(pmPath, out _);
        string fp = (_cfg.FrameProvider ?? "presentmon").Trim().ToLowerInvariant();
        bool preferRtss = !preferPresentMonOverride &&
                          (preferRtssOverride || fp == "rtss" || (fp == "auto" && !pmOk));
        // A per-game frameProvider="rtss" override (preferRtssOverride) must work even when the GLOBAL provider
        // is presentmon AND the OSD is off — in that case ProbeAll never started/probed RTSS, so _rtssLive is
        // false and the override would silently fall through to PresentMon (0 frames on a sandboxed title).
        // Start + probe RTSS on demand here so the override is self-sufficient.
        if (preferRtssOverride && !_rtssLive && !_cfg.ForceSyntheticFrames && targetRunning)
        {
            EnsureRtssRunning();
            // A forced retry must never silently fall back to PresentMon just because RTSS's shared-memory
            // mapping is still initializing before the newly launched game hooks. RtssFrameProvider.StartAsync
            // has its own bounded hook wait and a missing hook then fails honestly as zero frames.
            _rtssLive = IsRtssRunning();
        }
        string rtssPath = ResolveRtss();
        if (!_cfg.ForceSyntheticFrames && targetRunning && lateRtssOverride && File.Exists(rtssPath))
            frames = new LateRtssFrameProvider(rtssPath);
        else if (!_cfg.ForceSyntheticFrames && targetRunning && preferRtss && (preferRtssOverride || _rtssLive))
            frames = new RtssFrameProvider();
        else if (pmOk && targetRunning)
            frames = new PresentMonFrameProvider(pmPath);
        else
            frames = new SyntheticFrameProvider();

        // Power: Powenetics PMD if it was detected, else LHM GPU board power if usable, else synthetic.
        // (Which of PMD/LHM was probed is governed by PowerSource in ProbeAll, so honoring the live flags
        // here applies that policy without re-checking the source string.)
        IPowerProvider power;
        if (_cfg.ForceSyntheticPower) power = new SyntheticPowerProvider();
        else if (_powerLive) power = _powenetics;
        else if (_lhmPowerLive) power = _lhmPower;
        else power = new SyntheticPowerProvider();

        return new ProviderSet { Frames = frames, Power = power, Telemetry = tel, TelemetryMode = telMode };
    }

    /// <summary>
    /// Establish the effective backend before each game. RTSS is a global render hook, so leaving it running
    /// while a PresentMon-pinned title launches is not harmless. This method starts it only for RTSS games and
    /// closes it (plus its hook loaders) for PresentMon games.
    /// </summary>
    public bool PrepareFrameBackend(string? profileProvider, bool forbidRtss, bool lateRtss, out string detail)
    {
        if (lateRtss)
        {
            // The whole point of this backend is that no RTSS hook exists until frame StartAsync, which the
            // scene runner calls only after navigation and warm-up. Clear a user-started copy now as well.
            LateRtssFrameProvider.StopRtss();
            _rtssLive = false;
            string path = ResolveRtss();
            bool available = !_cfg.ForceSyntheticFrames && File.Exists(path);
            detail = available
                ? "late RTSS staged: hook remains OFF until the measured window, then is stopped immediately."
                : "late RTSS requested but RTSS.exe was not found: " + path;
            return available || _cfg.ForceSyntheticFrames;
        }

        string effective = FrameProviderPolicy.Resolve(_cfg.FrameProvider, profileProvider, PresentMonLive, forbidRtss);
        if (effective == "rtss")
        {
            EnsureRtssRunning();
            string probe = _cfg.ForceSyntheticFrames ? "synthetic frames forced" : "not probed";
            _rtssLive = !_cfg.ForceSyntheticFrames && RtssFrameProvider.Probe(out probe);
            detail = _rtssLive ? "RTSS started/probed for this game." : "RTSS requested but unavailable: " + probe;
            return _rtssLive;
        }

        var stopped = new List<string>();
        foreach (string processName in new[] { "RTSS", "RTSSHooksLoader", "RTSSHooksLoader64" })
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(processName))
            {
                try
                {
                    if (processName == "RTSS") process.CloseMainWindow();
                    if (!process.WaitForExit(1200)) process.Kill(entireProcessTree: true);
                    stopped.Add($"{processName}:{process.Id}");
                }
                catch { /* a concurrently exiting helper is already the desired state */ }
                finally { process.Dispose(); }
            }
        }
        _rtssLive = false;
        detail = stopped.Count == 0
            ? "PresentMon pinned; RTSS was already closed."
            : "PresentMon pinned; closed RTSS global hook (" + string.Join(", ", stopped) + ").";
        return PresentMonLive || _cfg.ForceSyntheticFrames;
    }

    /// <summary>
    /// The live power provider chosen by the same policy as <see cref="CreateFor"/> (Powenetics PMD →
    /// LHM GPU board power → synthetic), without frame capture. Call <see cref="ProbeAll"/> first.
    /// Used by the cooler-evaluation path, whose closed-loop controller reads <c>provider.Latest</c>.
    /// </summary>
    public IPowerProvider SelectPowerProvider()
    {
        if (_cfg.ForceSyntheticPower) return new SyntheticPowerProvider();
        if (_powerLive) return _powenetics;
        if (_lhmPowerLive) return _lhmPower;
        return new SyntheticPowerProvider();
    }

    /// <summary>The live telemetry provider (LHM if usable, else synthetic) — temps/fan/clocks for the
    /// cooler path. Call <see cref="ProbeAll"/> first.</summary>
    public ITelemetryProvider SelectTelemetryProvider()
        => _lhmLive && !_cfg.ForceSyntheticTelemetry ? _lhm : new SyntheticTelemetryProvider();

    private static bool TargetProcessRunning(FrameCaptureTarget t)
    {
        try
        {
            if (t.Pid is int pid)
                return System.Diagnostics.Process.GetProcesses().Any(p => p.Id == pid);
            var baseName = Path.GetFileNameWithoutExtension(t.ProcessName);
            return !string.IsNullOrEmpty(baseName) &&
                   System.Diagnostics.Process.GetProcessesByName(baseName).Length > 0;
        }
        catch { return false; }
    }

    /// <summary>Launch RTSS from RtssExePath if it is configured and not already running, so RTSS
    /// shared-memory capture works out of the box. No-op when the path is empty or RTSS is up.</summary>
    private void EnsureRtssRunning()
    {
        if (string.IsNullOrWhiteSpace(_cfg.RtssExePath)) return;
        try
        {
            if (System.Diagnostics.Process.GetProcessesByName("RTSS").Length > 0) return;
            var path = Environment.ExpandEnvironmentVariables(_cfg.RtssExePath);
            if (!File.Exists(path)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Minimized,
                WorkingDirectory = Path.GetDirectoryName(path) ?? ""
            });
            System.Threading.Thread.Sleep(2000); // give RTSS time to initialize its shared memory
        }
        catch { /* best-effort; Probe will report RTSS as unavailable */ }
    }

    private static bool IsRtssRunning()
    {
        try { return System.Diagnostics.Process.GetProcessesByName("RTSS").Any(); }
        catch { return false; }
    }

    /// <summary>True if the current process is elevated (Administrator) — required for PresentMon ETW capture.</summary>
    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private string ResolvePresentMon()
    {
        var p = _cfg.PresentMonPath;
        if (Path.IsPathRooted(p)) return p;
        // Prefer the working directory (where settings.json + tools/ live), then fall back
        // to the app's base directory (in case tools were copied next to the exe).
        var cwd = Path.Combine(Directory.GetCurrentDirectory(), p);
        if (File.Exists(cwd)) return cwd;
        return Path.Combine(AppContext.BaseDirectory, p);
    }

    private string ResolveRtss()
    {
        var p = Environment.ExpandEnvironmentVariables(_cfg.RtssExePath ?? "");
        if (Path.IsPathRooted(p)) return p;
        var cwd = Path.Combine(Directory.GetCurrentDirectory(), p);
        if (File.Exists(cwd)) return cwd;
        return Path.Combine(AppContext.BaseDirectory, p);
    }

    /// <summary>
    /// Dump every sensor LHM exposes — the "sensor inventory" diagnostic. Helps confirm
    /// which GPU/CPU sensors are actually available on this machine (hotspot, VRAM temp,
    /// per-rail power, fans) so profiles can be tuned.
    /// </summary>
    public string DumpSensorInventory()
    {
        var sb = new StringBuilder();
        try
        {
            var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true, IsMotherboardEnabled = true };
            computer.Open();
            foreach (var hw in computer.Hardware)
            {
                hw.Update();
                sb.AppendLine($"# {hw.HardwareType}: {hw.Name}");
                foreach (var s in hw.Sensors.OrderBy(s => s.SensorType.ToString()))
                    sb.AppendLine($"    [{s.SensorType,-12}] {s.Name,-32} = {(s.Value?.ToString("0.###") ?? "null")}");
                foreach (var sub in hw.SubHardware)
                {
                    sub.Update();
                    sb.AppendLine($"  ## {sub.HardwareType}: {sub.Name}");
                    foreach (var s in sub.Sensors.OrderBy(s => s.SensorType.ToString()))
                        sb.AppendLine($"      [{s.SensorType,-12}] {s.Name,-32} = {(s.Value?.ToString("0.###") ?? "null")}");
                }
            }
            computer.Close();
        }
        catch (Exception ex) { sb.AppendLine("Sensor inventory failed: " + ex.Message); }
        return sb.ToString();
    }

    public void Dispose() => _lhm.Dispose();
}
