using System.Diagnostics;
using GpuSuite.Core.Models;

namespace GpuSuite.Measurement.Real;

/// <summary>
/// RTSS capture whose global render hook exists only for the measured window. Some titles (notably
/// Ratchet &amp; Clank: Rift Apart) are stable while RTSS is absent during launch/menu/navigation but can
/// still be measured through RTSS once the scene is already warm and in-world. The wrapped session
/// starts RTSS immediately before capture and tears down RTSS plus its hook loaders immediately after.
/// </summary>
public sealed class LateRtssFrameProvider : IFrameCaptureProvider
{
    private readonly string _rtssExePath;

    public LateRtssFrameProvider(string rtssExePath) => _rtssExePath = rtssExePath;

    public string Name => "RivaTuner (measured-window only)";
    public bool IsLive => File.Exists(_rtssExePath);

    public async Task<ISampleSession<FrameSample>> StartAsync(FrameCaptureTarget target, CancellationToken ct)
    {
        StopRtss();
        var process = StartRtss();

        await WaitForSharedMemoryAsync(ct).ConfigureAwait(false);
        bool hooked = await RtssFrameProvider.WaitForLiveHookAsync(target, 12_000, ct).ConfigureAwait(false);
        bool restarted = false;
        if (!hooked)
        {
            // Live Ratchet validation exposed a real RTSS state where the shared-memory server started but the
            // game's frame ring never appeared; three four-minute attempts then produced zero frames. A clean
            // second RTSS process while the already-warm game is presenting reliably recovered the hook. Pay
            // this bounded retry before MarkStart instead of consuming whole benchmark attempts.
            restarted = true;
            try { process.Dispose(); } catch { }
            StopRtss();
            await Task.Delay(500, ct).ConfigureAwait(false);
            process = StartRtss();
            await WaitForSharedMemoryAsync(ct).ConfigureAwait(false);
            hooked = await RtssFrameProvider.WaitForLiveHookAsync(target, 15_000, ct).ConfigureAwait(false);
        }

        // The hook is already proven when possible. StartAsync repeats an 8s bounded proof, which also covers
        // the rare race where the ring begins advancing immediately after the recovery deadline.
        var inner = await new RtssFrameProvider().StartAsync(target, ct).ConfigureAwait(false);
        string startup = hooked
            ? restarted ? "late RTSS hook recovered after one clean restart" : "late RTSS live hook verified"
            : restarted ? "late RTSS hook absent after clean restart; capture will fail safe if it does not attach" : "late RTSS hook not yet visible";
        return new Session(inner, process, startup);
    }

    private Process StartRtss() => Process.Start(new ProcessStartInfo(_rtssExePath)
    {
        UseShellExecute = true,
        WindowStyle = ProcessWindowStyle.Minimized,
        WorkingDirectory = Path.GetDirectoryName(_rtssExePath) ?? ""
    }) ?? throw new InvalidOperationException("Failed to start RTSS for the measured window.");

    private static async Task WaitForSharedMemoryAsync(CancellationToken ct)
    {
        // Wait for RTSSSharedMemoryV2 before asking the normal provider to wait for the game's live hook.
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && !RtssFrameProvider.Probe(out _))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
    }

    public static void StopRtss()
    {
        foreach (var name in new[] { "RTSS", "RTSSHooksLoader", "RTSSHooksLoader64", "EncoderServer" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    if (name == "RTSS") process.CloseMainWindow();
                    if (!process.WaitForExit(800)) process.Kill(entireProcessTree: true);
                }
                catch { /* teardown is best-effort; the next run retries from a clean process scan */ }
                finally { process.Dispose(); }
            }
        }
    }

    private sealed class Session : ISampleSession<FrameSample>
    {
        private readonly ISampleSession<FrameSample> _inner;
        private readonly Process _rtssProcess;
        private readonly string _startupDiagnostics;
        private int _stopped;

        public Session(ISampleSession<FrameSample> inner, Process rtssProcess, string startupDiagnostics)
        {
            _inner = inner;
            _rtssProcess = rtssProcess;
            _startupDiagnostics = startupDiagnostics;
        }

        public DataSourceMode Mode => _inner.Mode;
        public int SampleCount => _inner.SampleCount;
        public FrameSample? Latest => _inner.Latest;
        public string? Diagnostics => string.IsNullOrWhiteSpace(_inner.Diagnostics)
            ? _startupDiagnostics
            : _startupDiagnostics + "; " + _inner.Diagnostics;

        public async Task<IReadOnlyList<FrameSample>> StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return Array.Empty<FrameSample>();
            IReadOnlyList<FrameSample> frames;
            try { frames = await _inner.StopAsync().ConfigureAwait(false); }
            finally
            {
                try { _rtssProcess.Dispose(); } catch { }
                StopRtss();
            }
            return frames;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            try { await _inner.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }
}
