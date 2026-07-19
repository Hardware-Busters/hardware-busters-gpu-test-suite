using System.Diagnostics;
using GpuSuite.Core.Models;
using RJCP.IO.Ports;

namespace GpuSuite.Measurement.Real.Powenetics;

/// <summary>
/// Opens a Powenetics V2 device on a COM port, puts it in streaming mode, and decodes the
/// frame stream into <see cref="PowerSample"/>s. Uses RJCP SerialPortStream at 921600 8N1,
/// exactly as the proven app, which is more reliable at this baud than System.IO.Ports.
/// </summary>
public sealed class PoweneticsSerialClient : IDisposable
{
    private readonly string _port;
    private SerialPortStream? _sp;
    private readonly PoweneticsFrameAssembler _assembler = new();
    private readonly Stopwatch _sw = new();
    private readonly object _lock = new();
    private Action<PowerSample>? _onSample;
    private readonly double _minIntervalSec;   // decimation floor (s) between KEPT samples; 0 = keep every frame
    private double _lastKeptT = double.NegativeInfinity;

    public long FramesParsed => _assembler.FramesParsed;

    /// <summary><paramref name="minIntervalMs"/> decimates the ~1 kHz PMD stream to a target log rate — a sample
    /// is kept only when at least that many ms have elapsed since the last kept one (the average is unbiased; only
    /// sub-interval transients are dropped). 0/negative keeps every frame.</summary>
    public PoweneticsSerialClient(string port, int minIntervalMs = 0)
    {
        _port = port;
        _minIntervalSec = Math.Max(0, minIntervalMs) / 1000.0;
    }

    /// <summary>Decimation predicate (pure + testable): keep a sample at time <paramref name="t"/>s only if it is
    /// the first or at least <paramref name="minIntervalSec"/> past the last kept; updates <paramref name="lastKeptT"/>
    /// when kept. 0 interval keeps everything. Over a window the output rate ≈ min(input rate, 1/minIntervalSec).</summary>
    public static bool KeepSample(double t, ref double lastKeptT, double minIntervalSec)
    {
        // 0.999 tolerance: drop only when CLEARLY under the interval, so a sample landing exactly ON the boundary
        // isn't dropped by floating-point rounding (which otherwise undershoots the rate, e.g. 810/s vs 1000/s).
        if (minIntervalSec > 0 && (t - lastKeptT) < minIntervalSec * 0.999) return false;
        lastKeptT = t;
        return true;
    }

    public void Open(Action<PowerSample> onSample)
    {
        _onSample = onSample;
        _sp = new SerialPortStream(_port, 921600, 8, Parity.None, StopBits.One);
        _sp.DataReceived += OnDataReceived;
        _sp.Open();
        _sw.Restart();
        // Enter streaming mode: Calibration OK, then Stream.
        _sp.Write(PoweneticsProtocol.CalibrationOk, 0, PoweneticsProtocol.CalibrationOk.Length);
        Thread.Sleep(100);
        _sp.Write(PoweneticsProtocol.StreamMode, 0, PoweneticsProtocol.StreamMode.Length);
    }

    private void OnDataReceived(object? sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var sp = _sp;
            if (sp is null) return;
            int n = sp.BytesToRead;
            if (n <= 0) return;
            var buf = new byte[n];
            int read = sp.Read(buf, 0, n);
            if (read <= 0) return;
            if (read != buf.Length) Array.Resize(ref buf, read);

            double t = _sw.Elapsed.TotalSeconds;
            lock (_lock)
            {
                foreach (var payload in _assembler.Push(buf))
                {
                    var sample = PoweneticsProtocol.Decode(payload, t);
                    if (!PoweneticsProtocol.IsPlausible(sample)) continue;
                    if (!KeepSample(t, ref _lastKeptT, _minIntervalSec)) continue;   // decimate to the configured rate
                    _onSample?.Invoke(sample);
                }
            }
        }
        catch { /* keep the serial callback alive */ }
    }

    public void Close()
    {
        try
        {
            if (_sp is not null)
            {
                _sp.DataReceived -= OnDataReceived;
                if (_sp.IsOpen) _sp.Close();
                _sp.Dispose();
            }
        }
        catch { }
        finally { _sp = null; }
    }

    public void Dispose() => Close();

    /// <summary>
    /// Probe a port for a Powenetics device: open, request streaming, and see whether valid
    /// frames arrive within <paramref name="timeoutMs"/>. Returns frame count observed.
    /// </summary>
    public static long ProbePort(string port, int timeoutMs = 600)
    {
        SerialPortStream? sp = null;
        var assembler = new PoweneticsFrameAssembler();
        long frames = 0;
        try
        {
            sp = new SerialPortStream(port, 921600, 8, Parity.None, StopBits.One) { ReadTimeout = 200 };
            sp.Open();
            sp.Write(PoweneticsProtocol.CalibrationOk, 0, 4);
            Thread.Sleep(80);
            sp.Write(PoweneticsProtocol.StreamMode, 0, 4);

            var sw = Stopwatch.StartNew();
            var tmp = new byte[4096];
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                int n = sp.BytesToRead;
                if (n > 0)
                {
                    int read = sp.Read(tmp, 0, Math.Min(n, tmp.Length));
                    if (read > 0)
                    {
                        var slice = read == tmp.Length ? tmp : tmp[..read];
                        foreach (var _ in assembler.Push(slice)) frames++;
                        if (frames >= 5) break; // enough to confirm a real device
                    }
                }
                else Thread.Sleep(20);
            }
        }
        catch { /* not a Powenetics port */ }
        finally { try { sp?.Close(); sp?.Dispose(); } catch { } }
        return frames;
    }
}
