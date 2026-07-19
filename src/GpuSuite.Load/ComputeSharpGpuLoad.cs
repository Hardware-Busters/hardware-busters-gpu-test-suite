using System.Diagnostics;
using ComputeSharp;

namespace GpuSuite.Load;

/// <summary>
/// LEGACY kernel — transcendental (sin/cos) chain. SFU-bound: the Special Function Unit is a low-throughput
/// path, so the SM stalls on it while the main FP32 array (the dominant power consumer) idles. Tops out
/// well below the card's power limit (~50% TGP on Blackwell). Kept for comparison / GPUSUITE_LOAD_KERNEL=legacy.
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct LoadShaderLegacy(ReadWriteBuffer<float> buffer, int iterations) : IComputeShader
{
    public void Execute()
    {
        int i = ThreadIds.X;
        float x = buffer[i] + 1.0f;
        for (int k = 0; k < iterations; k++)
            x = Hlsl.Sin(x) * 0.99999f + Hlsl.Cos(x * 1.0001f) * 0.0001f;
        buffer[i] = x;
    }
}

/// <summary>
/// FMA power-virus kernel — eight INDEPENDENT logistic-map recurrences per thread. Each map is
/// <c>r·a·(1−a)</c> = pure FP32 multiply-add, bounded in [0,1] (anti-overflow across repeated dispatches),
/// and chaotic (no closed form, so DXC can't constant-fold it). Eight independent chains expose 8-way ILP
/// to fill the FP32 SIMD lanes and hide latency — this is what pushes the card to its real power limit.
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct LoadShaderFma(ReadWriteBuffer<float> buffer, int iterations) : IComputeShader
{
    public void Execute()
    {
        int i = ThreadIds.X;
        float s = buffer[i];
        // Seed eight accumulators independently into [0,1) via frac (cheap, not SFU-heavy).
        float a = Hlsl.Frac(s * 0.131f + 0.11f);
        float b = Hlsl.Frac(s * 0.179f + 0.23f);
        float c = Hlsl.Frac(s * 0.211f + 0.37f);
        float d = Hlsl.Frac(s * 0.249f + 0.41f);
        float e = Hlsl.Frac(s * 0.283f + 0.53f);
        float f = Hlsl.Frac(s * 0.317f + 0.61f);
        float g = Hlsl.Frac(s * 0.359f + 0.71f);
        float h = Hlsl.Frac(s * 0.397f + 0.83f);
        for (int k = 0; k < iterations; k++)
        {
            a = 3.90f * a * (1.0f - a);
            b = 3.80f * b * (1.0f - b);
            c = 3.70f * c * (1.0f - c);
            d = 3.95f * d * (1.0f - d);
            e = 3.85f * e * (1.0f - e);
            f = 3.75f * f * (1.0f - f);
            g = 3.60f * g * (1.0f - g);
            h = 3.93f * h * (1.0f - h);
        }
        buffer[i] = Hlsl.Frac(a + b + c + d + e + f + g + h);   // frac keeps the buffer bounded for the next dispatch
    }
}

/// <summary>
/// MIXED kernel — the FMA power virus PLUS a coalesced streaming sweep of a large auxiliary buffer, so the
/// memory controllers / GDDR draw their share of board power too. Thread <c>i</c> reads <c>aux[i + k·stride]</c>
/// for k=0..memReads-1 (consecutive threads → consecutive addresses → coalesced, near-peak bandwidth),
/// folding the sum into the output so it can't be optimised away.
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct LoadShaderMixed(
    ReadWriteBuffer<float> buffer, ReadWriteBuffer<float> aux, int iterations, int memReads, int stride) : IComputeShader
{
    public void Execute()
    {
        int i = ThreadIds.X;
        float s = buffer[i];
        float a = Hlsl.Frac(s * 0.131f + 0.11f);
        float b = Hlsl.Frac(s * 0.179f + 0.23f);
        float c = Hlsl.Frac(s * 0.211f + 0.37f);
        float d = Hlsl.Frac(s * 0.249f + 0.41f);
        float e = Hlsl.Frac(s * 0.283f + 0.53f);
        float f = Hlsl.Frac(s * 0.317f + 0.61f);
        float g = Hlsl.Frac(s * 0.359f + 0.71f);
        float h = Hlsl.Frac(s * 0.397f + 0.83f);
        for (int k = 0; k < iterations; k++)
        {
            a = 3.90f * a * (1.0f - a);
            b = 3.80f * b * (1.0f - b);
            c = 3.70f * c * (1.0f - c);
            d = 3.95f * d * (1.0f - d);
            e = 3.85f * e * (1.0f - e);
            f = 3.75f * f * (1.0f - f);
            g = 3.60f * g * (1.0f - g);
            h = 3.93f * h * (1.0f - h);
        }
        float m = 0.0f;
        int baseIdx = i;
        for (int k = 0; k < memReads; k++)
            m += aux[baseIdx + k * stride];
        aux[i] = m * 0.5f + a;                                  // write-back → read+write memory traffic
        buffer[i] = Hlsl.Frac(a + b + c + d + e + f + g + h + m * 1e-6f);
    }
}

/// <summary>
/// ComputeSharp (D3D12) implementation of <see cref="IGpuLoad"/>. <see cref="Intensity"/> is a duty
/// cycle: a worker thread dispatches the load shader for that fraction of each short period and idles the
/// rest. Continuous dispatch always pins the GPU into its boost state (~max power, no low range), so duty
/// cycling is what gives a full near-idle→power-limit range. The period is short (~50 ms) so a power meter
/// and the card's thermal mass average out the on/off ripple; the closed-loop controller runs slowly enough
/// that each averaged reading is steady before it adjusts.
///
/// The kernel (legacy / fma / mixed) and its tuning knobs are selectable via environment variables so the
/// power ceiling can be characterised without a recompile:
///   GPUSUITE_LOAD_KERNEL = legacy | fma | mixed   (default: mixed — the highest sustainable power)
///   GPUSUITE_LOAD_ITERS  = inner FMA iterations    (default: 512)
///   GPUSUITE_LOAD_MEMRD   = aux reads per thread (mixed only; default: 24)
///   GPUSUITE_LOAD_BATCH   = dispatches enqueued per GPU sync at full duty (default: 4 — cuts launch bubbles)
/// </summary>
public sealed class ComputeSharpGpuLoad : IGpuLoad
{
    private const int BufferLength = 1 << 20;   // 1M floats — also the streaming stride (coalesced sweep)
    private const double PeriodMs = 50.0;       // ~20 Hz duty period

    private readonly GraphicsDevice _device;
    private readonly ReadWriteBuffer<float> _buffer;
    private readonly ReadWriteBuffer<float>? _aux;
    private readonly int? _lockClockMhz;

    private readonly KernelMode _kernel;
    private readonly int _iterations;
    private readonly int _memReads;
    private readonly int _batch;

    private Thread? _worker;
    private volatile bool _running;
    private bool _clockLocked;
    private double _intensity;

    private enum KernelMode { Legacy, Fma, Mixed }

    public string GpuName => _device.Name;
    public bool Running => _running;
    public bool ClockLocked => _clockLocked;
    public int? LockedClockMhz => _clockLocked ? _lockClockMhz : null;
    public string ClockLockDetail { get; private set; } = "";

    /// <summary>Human-readable description of the active load configuration (for the cooler-load banner).</summary>
    public string LoadDescription =>
        _kernel switch
        {
            KernelMode.Legacy => $"legacy sin/cos ×{_iterations}",
            KernelMode.Fma => $"fma 8-chain ×{_iterations} (batch {_batch})",
            _ => $"mixed fma×{_iterations} + {_memReads} mem-reads ({_memReads * (long)BufferLength * 4 / (1024 * 1024)} MB sweep, batch {_batch})"
        };

    public ComputeSharpGpuLoad(string? preferGpuNameContains = null, int? lockClockMhz = null)
    {
        _device = SelectDevice(preferGpuNameContains);
        _buffer = _device.AllocateReadWriteBuffer<float>(BufferLength);
        _lockClockMhz = lockClockMhz;

        _kernel = (Environment.GetEnvironmentVariable("GPUSUITE_LOAD_KERNEL")?.Trim().ToLowerInvariant()) switch
        {
            "legacy" => KernelMode.Legacy,
            "fma" => KernelMode.Fma,
            _ => KernelMode.Mixed,
        };
        _iterations = EnvInt("GPUSUITE_LOAD_ITERS", 512, 1, 1 << 20);
        _memReads = EnvInt("GPUSUITE_LOAD_MEMRD", 24, 1, 256);
        _batch = EnvInt("GPUSUITE_LOAD_BATCH", 4, 1, 64);

        if (_kernel == KernelMode.Mixed)
        {
            // aux must hold the full coalesced sweep: max index = (BufferLength-1) + (memReads-1)*BufferLength.
            long auxLen = (long)_memReads * BufferLength;
            _aux = _device.AllocateReadWriteBuffer<float>((int)auxLen);
        }
    }

    public double Intensity
    {
        get => Volatile.Read(ref _intensity);
        set => Volatile.Write(ref _intensity, Math.Clamp(value, 0.0, 1.0));
    }

    public void Start()
    {
        if (_running) return;
        if (_lockClockMhz is int mhz)
        {
            _clockLocked = NvidiaClockLock.TryLock(mhz, out var detail);
            ClockLockDetail = detail;
        }
        _running = true;
        _worker = new Thread(Run) { IsBackground = true, Name = "GpuLoad", Priority = ThreadPriority.AboveNormal };
        _worker.Start();
    }

    public void Stop()
    {
        _running = false;
        _worker?.Join(2000);
        _worker = null;
        if (_clockLocked)
        {
            NvidiaClockLock.Reset();
            _clockLocked = false;
        }
    }

    /// <summary>One GPU dispatch of the active kernel.</summary>
    private void Dispatch()
    {
        switch (_kernel)
        {
            case KernelMode.Legacy:
                _device.For(BufferLength, new LoadShaderLegacy(_buffer, _iterations));
                break;
            case KernelMode.Fma:
                _device.For(BufferLength, new LoadShaderFma(_buffer, _iterations));
                break;
            default:
                _device.For(BufferLength, new LoadShaderMixed(_buffer, _aux!, _iterations, _memReads, BufferLength));
                break;
        }
    }

    private void Run()
    {
        var sw = new Stopwatch();
        while (_running)
        {
            double duty = Intensity;
            if (duty <= 0.001)
            {
                Thread.Sleep(15);   // true idle
                continue;
            }
            if (duty >= 0.999)
            {
                // Full duty: enqueue a batch before the implicit per-call sync to cut CPU launch bubbles.
                for (int j = 0; j < _batch && _running; j++) Dispatch();
                continue;
            }

            double onMs = PeriodMs * duty;
            sw.Restart();
            do
            {
                Dispatch();
            }
            while (_running && sw.Elapsed.TotalMilliseconds < onMs);

            double offMs = PeriodMs - sw.Elapsed.TotalMilliseconds;
            if (offMs > 1.0) Thread.Sleep((int)offMs);
        }
    }

    private static int EnvInt(string name, int dflt, int lo, int hi)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return int.TryParse(v, out var n) ? Math.Clamp(n, lo, hi) : dflt;
    }

    /// <summary>
    /// Pick the GPU under test. If a name hint is given, choose the first device whose name contains it;
    /// otherwise the ComputeSharp default device (the highest-performance adapter — the dGPU on a hybrid box).
    /// </summary>
    private static GraphicsDevice SelectDevice(string? nameContains)
    {
        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            foreach (var d in GraphicsDevice.EnumerateDevices())
            {
                if (d.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
        }
        return GraphicsDevice.GetDefault();
    }

    public void Dispose()
    {
        Stop();
        _buffer.Dispose();
        _aux?.Dispose();
    }
}
