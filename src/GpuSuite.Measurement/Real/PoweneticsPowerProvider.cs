using System.IO.Ports;
using GpuSuite.Core.Models;
using GpuSuite.Measurement.Real.Powenetics;

namespace GpuSuite.Measurement.Real;

/// <summary>
/// Real Powenetics V2 power logging over serial — full PMD decode ported from the proven
/// Powenetics app. <see cref="Probe"/> auto-detects the device (or uses a configured port);
/// when found, <see cref="IsLive"/> flips true and the factory selects this provider. With no
/// device attached it reports not-live and the factory falls back to synthetic — the honest
/// degraded mode, never fake "live" data.
/// </summary>
public sealed class PoweneticsPowerProvider : IPowerProvider
{
    private readonly string _preferredPort;
    private readonly bool _autoDetect;
    private string? _detectedPort;

    public PoweneticsPowerProvider(string preferredPort = "", bool autoDetect = false)
    {
        _preferredPort = preferredPort?.Trim() ?? "";
        _autoDetect = autoDetect;
    }

    public string Name => "Powenetics V2";
    public bool IsLive { get; private set; }
    public PowerMeasurementMetadata Measurement => new()
    {
        Kind = PowerMeasurementKind.PoweneticsDirect,
        Scope = "PCIe slot + auxiliary GPU rails",
        HasPerRailData = true,
        HardwareBustersVerifiedPowerEligible = true,
        QualificationNote = "DIRECT · POWENETICS — GPU power is measured from PCIe slot and auxiliary GPU rails."
    };

    /// <summary>
    /// Detect a Powenetics device. Tries the configured port first, then — only when
    /// <c>poweneticsAutoDetect</c> is on — every remaining COM port, looking for a valid PMD frame
    /// stream. Sets <see cref="IsLive"/> on success.
    /// </summary>
    public bool Probe(out string detail)
    {
        var ports = SerialPort.GetPortNames();
        string portList = ports.Length > 0 ? string.Join(", ", ports.Distinct().OrderBy(p => p)) : "(none)";

        // Safety: only probe a configured port, or every port when auto-detect is explicitly on.
        // Never poke unknown bench equipment by default.
        //
        // The configured port is a HINT, not a limit. The PMD's COM number drifts across reconnects, so with
        // auto-detect on we try the hint first (fast path) and then every remaining port. Until 2026-08-14
        // this was an `else if`: a configured port suppressed the scan entirely, so a STALE poweneticsComPort
        // silently defeated auto-detect and power fell back to LHM board power — a provenance downgrade
        // (DIRECT · POWENETICS → APPROXIMATE) caused purely by a renumbered port. The doc comment above has
        // promised the fallback since this file was written; only now does the code do it.
        var order = new List<string>();
        if (!string.IsNullOrEmpty(_preferredPort)) order.Add(_preferredPort);
        if (_autoDetect) order.AddRange(ports.Where(p => !order.Contains(p, StringComparer.OrdinalIgnoreCase)));

        if (order.Count == 0)
        {
            IsLive = false;
            detail = ports.Length == 0
                ? "No COM ports present. Using synthetic power."
                : $"No port configured (set poweneticsComPort, or poweneticsAutoDetect=true). COM ports: {portList}. Using synthetic power.";
            return false;
        }

        var silentPorts = new List<string>();   // opened fine, handshook, 0 frames — candidate WEDGE
        foreach (var port in order)
        {
            long frames; bool opened = true;
            try { frames = PoweneticsSerialClient.ProbePort(port); }
            catch { frames = 0; opened = false; }
            if (frames > 0)
            {
                _detectedPort = port;
                IsLive = true;
                detail = $"Powenetics detected on {port} ({frames}+ frames). COM ports: {portList}.";
                return true;
            }
            if (opened) silentPorts.Add(port);
        }

        IsLive = false;
        // WEDGE detection (2026-07-02): distinguish "the PMD is unplugged" from "the PMD's MCU is HUNG" —
        // the recurring bench failure is the latter: the board stays USB-enumerated and its port OPENS, but
        // it answers ZERO protocol frames. A host reboot does NOT reset it (USB 5V stays up); only a
        // physical unplug/replug does. Signature: a silent port that is the PMD's own USB device (Microchip
        // VID_04D8, matched via the registry PortName). Without that match a silent port is just "some
        // other serial device", not evidence.
        var pmdPorts = PmdUsbPortNames();
        var wedged = silentPorts.Where(p => pmdPorts.Contains(p)).ToList();
        detail = wedged.Count > 0
            ? $"PMD WEDGED on {string.Join(", ", wedged)} — the Powenetics USB device (VID_04D8) is enumerated and its port opens, but it streams 0 protocol frames. The MCU is hung: physically UNPLUG/REPLUG its USB cable (a host reboot does NOT reset it). COM ports: {portList}."
            : $"No Powenetics device responded. COM ports: {portList}. Using synthetic power.";
        return false;
    }

    /// <summary>Live COM port names that belong to the Powenetics PMD's own USB device (Microchip
    /// VID_04D8), read from the registry Enum tree (readable without elevation). Used to tell a WEDGED
    /// PMD (device present, port opens, zero frames → needs a physical replug) apart from an absent one.
    /// Ghost/past enumerations are filtered out by the caller intersecting with live ports.</summary>
    public static HashSet<string> PmdUsbPortNames()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var usb = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb is null) return found;
            foreach (var dev in usb.GetSubKeyNames().Where(n => n.StartsWith("VID_04D8", StringComparison.OrdinalIgnoreCase)))
            {
                using var devKey = usb.OpenSubKey(dev);
                foreach (var inst in devKey?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var p = devKey!.OpenSubKey(inst + @"\Device Parameters");
                    if (p?.GetValue("PortName") is string pn && !string.IsNullOrWhiteSpace(pn))
                        found.Add(pn.Trim());
                }
            }
        }
        catch { /* registry unavailable — wedge detection degrades to the generic message */ }
        return found;
    }

    public Task<ISampleSession<PowerSample>> StartAsync(int intervalMs, WorkloadHint hint, CancellationToken ct)
    {
        if (!IsLive || _detectedPort is null)
            throw new InvalidOperationException("Powenetics is not live; the factory should select the synthetic provider.");
        // intervalMs (= 1000 / PowerSampleHz) decimates the ~1 kHz PMD stream to the configured log rate so a
        // multi-hour sweep doesn't accumulate millions of rows for no gain (the power average is unbiased).
        return Task.FromResult<ISampleSession<PowerSample>>(new Session(_detectedPort, intervalMs));
    }

    private sealed class Session : ISampleSession<PowerSample>
    {
        private readonly PoweneticsSerialClient _client;
        private readonly List<PowerSample> _samples = new();
        private readonly object _lock = new();

        public Session(string port, int minIntervalMs)
        {
            _client = new PoweneticsSerialClient(port, minIntervalMs);
            _client.Open(s => { lock (_lock) _samples.Add(s); });
        }

        public DataSourceMode Mode => DataSourceMode.Live;
        public int SampleCount { get { lock (_lock) return _samples.Count; } }
        public PowerSample? Latest { get { lock (_lock) return _samples.Count > 0 ? _samples[^1] : null; } }

        public Task<IReadOnlyList<PowerSample>> StopAsync()
        {
            _client.Close();
            lock (_lock) return Task.FromResult<IReadOnlyList<PowerSample>>(_samples.ToList());
        }

        public ValueTask DisposeAsync() { _client.Dispose(); return ValueTask.CompletedTask; }
    }
}
