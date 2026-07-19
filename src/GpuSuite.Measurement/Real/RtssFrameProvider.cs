using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using GpuSuite.Core.Models;

namespace GpuSuite.Measurement.Real;

/// <summary>
/// Frame capture via RivaTuner Statistics Server (RTSS) shared memory ("RTSSSharedMemoryV2").
/// RTSS continuously publishes a per-application instantaneous frame time (dwFrameTime, microseconds)
/// that we poll by process id — there is no ETW trace-session setup, so unlike PresentMon this stays
/// reliable for SHORT capture windows (PresentMon can lose the first seconds of a brief scene while
/// its realtime session spins up). Requires RTSS to be running and hooking the target.
///
/// Layout reference: RTSS SDK Include/RTSSSharedMemory.h (bundled with Powenetics V2). Field NAMES
/// are authoritative; the header's App/OSD *comments* are famously swapped, so we index by the
/// dwApp* fields exactly as the SDK sample (RTSSSharedMemorySampleDlg.cpp) does.
/// </summary>
public sealed class RtssFrameProvider : IFrameCaptureProvider
{
    public const string SharedMemoryName = "RTSSSharedMemoryV2";
    private const uint Signature = 0x52545353; // C multi-char literal 'RTSS' = ('R'<<24)|('T'<<16)|('S'<<8)|'S'
    private const uint MinVersion = 0x00020000; // v2.0 layout

    // RTSS_SHARED_MEMORY header offsets (DWORDs). Names per the SDK; comments in the header are mislabeled.
    private const int H_SIGNATURE = 0, H_VERSION = 4, H_APP_ENTRY_SIZE = 8, H_APP_ARR_OFFSET = 12, H_APP_ARR_SIZE = 16;
    // RTSS_SHARED_MEMORY_APP_ENTRY field offsets. dwProcessID/dwFrameTime are stable since v2.0;
    // the per-frame ring buffer (dwStatFrameTimeBuf[1024] + dwStatFrameTimeBufPos) sits at a fixed
    // offset from v2.5 onward (later versions only append fields after it).
    private const int A_PROCESS_ID = 0;        // dwProcessID
    private const int A_FRAMETIME = 280;       // dwFrameTime (microseconds, instantaneous) — fallback
    private const int A_FRAMETIME_BUF = 924;   // dwStatFrameTimeBuf[1024] (microseconds, per-frame ring)
    private const int A_FRAMETIME_BUF_POS = 5020; // dwStatFrameTimeBufPos (advances once per frame)
    private const uint FrameTimeBufLen = 1024;
    private const int MaxAppScan = 256;        // arrApp[256]

    public string Name => "RivaTuner (RTSS)";
    public bool IsLive => true;

    /// <summary>True if RTSS is running and its shared memory holds a valid v2.x layout.</summary>
    public static bool Probe(out string detail)
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(SharedMemoryName, MemoryMappedFileRights.Read);
            using var acc = mmf.CreateViewAccessor(0, 64, MemoryMappedFileAccess.Read);
            uint sig = acc.ReadUInt32(H_SIGNATURE), ver = acc.ReadUInt32(H_VERSION);
            if (sig == Signature && ver >= MinVersion) { detail = $"RTSS shared memory v{ver >> 16}.{ver & 0xffff} OK"; return true; }
            detail = "RTSS shared memory present but not initialized (signature/version)";
            return false;
        }
        catch (FileNotFoundException) { detail = "RTSS not running (RTSSSharedMemoryV2 not found)"; return false; }
        catch (Exception ex) { detail = "RTSS probe failed: " + ex.Message; return false; }
    }

    public async Task<ISampleSession<FrameSample>> StartAsync(FrameCaptureTarget target, CancellationToken ct)
    {
        // Pre-warm: a freshly-launched game is not hooked by RTSS for a moment after it starts presenting, so
        // a capture begun immediately records ZERO frames until the hook lands (observed as run-1-of-each-cell
        // 0-frame failures with --frames rtss). Wait until RTSS shows a LIVE, advancing frame-time ring for the
        // target before declaring the session started, so the measured window opens on real frames. Best-effort
        // and bounded — if RTSS never hooks within the timeout, capture proceeds and the 0-frame run fails safe
        // (the orchestrator falls back / auto-repeats).
        await WaitForLiveHookAsync(target, 8000, ct).ConfigureAwait(false);
        return new Session(target.Pid, target.ProcessName);
    }

    /// <summary>Poll RTSS shared memory until the target app entry exists AND its per-frame ring advances
    /// (RTSS is actively recording frames for it), or the timeout elapses. Returns true if a live hook was seen.</summary>
    public static async Task<bool> WaitForLiveHookAsync(FrameCaptureTarget target, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(SharedMemoryName, MemoryMappedFileRights.Read);
            using var acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (acc.ReadUInt32(H_SIGNATURE) != Signature) return false;
            uint entrySize = acc.ReadUInt32(H_APP_ENTRY_SIZE), arrOffset = acc.ReadUInt32(H_APP_ARR_OFFSET);
            uint arrSize = Math.Min(acc.ReadUInt32(H_APP_ARR_SIZE), MaxAppScan);
            if (entrySize == 0 || arrOffset == 0) return false;

            var sw = Stopwatch.StartNew();
            uint firstPos = 0; bool haveFirst = false;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                ct.ThrowIfCancellationRequested();
                long entryBase = FindEntry(acc, arrOffset, entrySize, arrSize, target.Pid, target.ProcessName);
                if (entryBase >= 0)
                {
                    uint pos = acc.ReadUInt32((int)entryBase + A_FRAMETIME_BUF_POS);
                    if (!haveFirst) { firstPos = pos; haveFirst = true; }
                    else if (pos != firstPos) return true; // ring advanced ⇒ RTSS is recording the target's frames
                }
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; } // RTSS closed / layout mismatch — best effort; capture proceeds regardless
    }

    /// <summary>Byte offset of the app entry whose dwProcessID matches the target pid (or, with no pid, its exe name), or -1.</summary>
    private static long FindEntry(MemoryMappedViewAccessor acc, uint arrOffset, uint entrySize, uint count, int? wantPid, string? wantProc)
    {
        string wantName = Path.GetFileName(wantProc ?? "");
        for (uint i = 0; i < count; i++)
        {
            long b = arrOffset + (long)i * entrySize;
            uint pid = acc.ReadUInt32((int)b + A_PROCESS_ID);
            if (pid == 0) continue;
            if (wantPid is int want && pid == (uint)want) return b;
            if (wantPid is null && !string.IsNullOrEmpty(wantName) && EntryNameEquals(acc, b, wantName)) return b;
        }
        return -1;
    }

    private static bool EntryNameEquals(MemoryMappedViewAccessor acc, long entryBase, string wantName)
    {
        // szName[MAX_PATH] at +4, null-terminated ASCII.
        Span<byte> buf = stackalloc byte[260];
        for (int i = 0; i < buf.Length; i++) buf[i] = acc.ReadByte((int)entryBase + 4 + i);
        int len = buf.IndexOf((byte)0); if (len < 0) len = buf.Length;
        var name = System.Text.Encoding.ASCII.GetString(buf[..len]);
        return string.Equals(Path.GetFileName(name), wantName, StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uMilliseconds);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uMilliseconds);

    private sealed class Session : ISampleSession<FrameSample>
    {
        private readonly int? _pid;
        private readonly string _procName;
        private readonly List<FrameSample> _frames = new();
        private readonly object _lock = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _thread;
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        public Session(int? pid, string procName)
        {
            _pid = pid;
            _procName = procName;
            _thread = new Thread(PollLoop) { IsBackground = true, Name = "RTSS-poll", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        public DataSourceMode Mode => DataSourceMode.Live;
        public int SampleCount { get { lock (_lock) return _frames.Count; } }
        public FrameSample? Latest { get { lock (_lock) return _frames.Count > 0 ? _frames[^1] : null; } }

        private void PollLoop()
        {
            MemoryMappedFile? mmf = null;
            MemoryMappedViewAccessor? acc = null;
            bool periodSet = false;
            try
            {
                mmf = MemoryMappedFile.OpenExisting(SharedMemoryName, MemoryMappedFileRights.Read);
                acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                if (acc.ReadUInt32(H_SIGNATURE) != Signature) return;

                uint appEntrySize = acc.ReadUInt32(H_APP_ENTRY_SIZE);
                uint appArrOffset = acc.ReadUInt32(H_APP_ARR_OFFSET);
                uint appArrSize = Math.Min(acc.ReadUInt32(H_APP_ARR_SIZE), MaxAppScan);
                if (appEntrySize == 0 || appArrOffset == 0) return;

                timeBeginPeriod(1); periodSet = true;

                long entryBase = -1;     // cached byte offset of our app's entry once found
                uint lastBufPos = 0;     // last-read ring-buffer write position (per-frame counter)
                bool primed = false;

                while (!_cts.IsCancellationRequested)
                {
                    // (Re)locate the target app entry: its dwProcessID must match; re-scan if it moves/disappears.
                    if (entryBase < 0 || acc.ReadUInt32((int)entryBase + A_PROCESS_ID) != (uint)(_pid ?? 0))
                    {
                        entryBase = FindEntry(acc, appArrOffset, appEntrySize, appArrSize);
                        primed = false;
                    }

                    if (entryBase >= 0)
                    {
                        uint bufPos = acc.ReadUInt32((int)entryBase + A_FRAMETIME_BUF_POS) % FrameTimeBufLen;
                        if (!primed) { lastBufPos = bufPos; primed = true; }
                        else if (bufPos != lastBufPos)
                        {
                            // Drain every new ring-buffer entry since the last poll (each is one presented
                            // frame's time in microseconds), so no frame is missed at high frame rates.
                            uint count = (bufPos + FrameTimeBufLen - lastBufPos) % FrameTimeBufLen;
                            double now = _sw.Elapsed.TotalSeconds;
                            for (uint k = 0; k < count; k++)
                            {
                                uint idx = (lastBufPos + k) % FrameTimeBufLen;
                                uint ftUs = acc.ReadUInt32((int)entryBase + A_FRAMETIME_BUF + (int)idx * 4);
                                if (ftUs > 0 && ftUs < 10_000_000)
                                    lock (_lock) _frames.Add(new FrameSample(now, ftUs / 1000.0));
                            }
                            lastBufPos = bufPos;
                            GpuSuite.Core.RunHeartbeat.Ping();   // frames are presenting = the game is alive and rendering
                        }
                    }

                    Thread.Sleep(1);
                }
            }
            catch { /* RTSS closed / mapping vanished — stop quietly; validator sees the (possibly empty) result */ }
            finally
            {
                if (periodSet) timeEndPeriod(1);
                acc?.Dispose();
                mmf?.Dispose();
            }
        }

        /// <summary>Find the byte offset of the app entry whose dwProcessID matches the target (or its exe name).</summary>
        private long FindEntry(MemoryMappedViewAccessor acc, uint arrOffset, uint entrySize, uint count)
            => RtssFrameProvider.FindEntry(acc, arrOffset, entrySize, count, _pid, _procName);

        public async Task<IReadOnlyList<FrameSample>> StopAsync()
        {
            _cts.Cancel();
            try { await Task.Run(() => _thread.Join(2000)).ConfigureAwait(false); } catch { }
            lock (_lock) return _frames.ToList();
        }

        public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); _cts.Dispose(); }
    }
}
