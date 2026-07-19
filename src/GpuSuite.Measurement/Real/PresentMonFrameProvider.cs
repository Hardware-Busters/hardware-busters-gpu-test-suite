using System.Diagnostics;
using System.Globalization;
using System.Threading;
using GpuSuite.Core.Models;

namespace GpuSuite.Measurement.Real;

/// <summary>
/// Real frame capture by driving PresentMon (2.x) and parsing its CSV stream from
/// stdout. Columns are resolved by HEADER NAME (not fixed index) so it tolerates
/// version differences between PresentMon builds. Requires the PresentMon exe and a
/// live target process; otherwise the factory chooses the synthetic provider.
/// </summary>
public sealed class PresentMonFrameProvider : IFrameCaptureProvider
{
    private readonly string _exePath;
    private static int _sessionSeq;   // monotonic suffix → a unique ETW session name per capture (never collides)
    public PresentMonFrameProvider(string exePath) => _exePath = exePath;

    public string Name => "PresentMon";
    public bool IsLive => true;

    public static bool Probe(string exePath, out string detail)
    {
        if (File.Exists(exePath)) { detail = "PresentMon found: " + exePath; return true; }
        detail = "PresentMon exe not found at " + exePath;
        return false;
    }

    public Task<ISampleSession<FrameSample>> StartAsync(FrameCaptureTarget target, CancellationToken ct)
    {
        // A cancelled/killed host can leave its realtime ETW session behind even though PresentMon.exe is
        // gone. Those orphan providers continue flooding ETW; one live repro dropped 91,670 events and made
        // every later capture return zero rows. Reclaim only sessions owned by this process or by a process
        // that no longer exists, never a genuinely concurrent live suite.
        var reclaimed = CleanupStaleSuiteSessions();
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // UTF-8, NOT Unicode: PresentMon 2.4.1 emits stderr as plain ASCII — decoding it as UTF-16LE
            // turned the 0-frame forensics into CJK mojibake (byte pairs 'wa'→U+6177 etc.; live 2026-07-09,
            // the garbled text was actually "warning: PresentMon requires elevated privilege ... short-running").
            // If some build DOES emit UTF-16LE, UTF-8-decoding it yields interleaved NULs — stripped in
            // ReadErrLoop — so the text stays readable under either encoding.
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        psi.ArgumentList.Add("--output_stdout");
        // Give every capture a UNIQUE ETW realtime session name. PresentMon's DEFAULT name is the fixed
        // string "PresentMon"; if any prior PresentMon still owns that session — a killed run's leftover,
        // a crashed instance, or a concurrent capture — a new instance can't (re)create it and the trace
        // fails to start (Windows error 4201) → ZERO frames captured, silently. Worse, --stop_existing_session
        // cannot stop a session owned by a higher-integrity (elevated) instance, so ONE leaked elevated
        // "PresentMon" session makes EVERY later run capture 0 frames (observed live across a whole elevated
        // sweep). A per-instance name can't collide, so the trace always starts on a clean session.
        var sessionName = $"GpuSuite_{Environment.ProcessId}_{Interlocked.Increment(ref _sessionSeq)}";
        psi.ArgumentList.Add("--session_name");
        psi.ArgumentList.Add(sessionName);
        psi.ArgumentList.Add("--stop_existing_session");   // belt-and-suspenders: reclaim our own name if a prior run leaked it
        psi.ArgumentList.Add("--no_console_stats");
        psi.ArgumentList.Add("--no_track_input");          // benchmark FPS never needs input-latency ETW; reduce provider pressure
        // Target by PROCESS NAME, not pid, whenever we know the exe. Games launched through a store
        // front-end (GOG Galaxy here) frequently BOOTSTRAP: the process we launched exits and a NEW
        // same-named process does the actual rendering — most visibly on the FIRST launch after a
        // settings/resolution change. PresentMon told `--process_id <launched pid>` then traces the dead
        // bootstrapper and captures ZERO frames (observed: run 1 of every sweep cell after the first; the
        // game self-reported its fps and wrote its result file, but PresentMon saw nothing). Targeting by
        // name follows the handoff; only the genuinely-presenting instance emits frames, so a lingering or
        // bootstrap twin of the same name is harmless. This is also the proven Ratchet path: its profile
        // forbids RTSS because the hook crashes the game, while name-targeted PresentMon captures it cleanly.
        foreach (var arg in BuildTargetArguments(target)) psi.ArgumentList.Add(arg);

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start PresentMon.");
        string? startup = reclaimed.Count == 0
            ? null
            : "reclaimed stale ETW session(s): " + string.Join(", ", reclaimed);
        return Task.FromResult<ISampleSession<FrameSample>>(new Session(proc, sessionName, startup));
    }

    internal static IReadOnlyList<string> FindStaleSuiteSessions(
        string queryOutput, int currentPid, Func<int, bool> processExists)
    {
        var stale = new List<string>();
        foreach (string raw in queryOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (!token.StartsWith("GpuSuite_", StringComparison.OrdinalIgnoreCase)) continue;
            var pieces = token.Split('_');
            if (pieces.Length != 3 || !int.TryParse(pieces[1], out int ownerPid) ||
                !int.TryParse(pieces[2], out _)) continue;
            if (ownerPid == currentPid || !processExists(ownerPid)) stale.Add(token);
        }
        return stale;
    }

    private static IReadOnlyList<string> CleanupStaleSuiteSessions()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "logman", Arguments = "query -ets", UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var query = Process.Start(psi);
            if (query is null) return Array.Empty<string>();
            string output = query.StandardOutput.ReadToEnd();
            query.WaitForExit(3000);
            var stale = FindStaleSuiteSessions(output, Environment.ProcessId, ProcessExists);
            foreach (string name in stale) StopEtwSession(name);
            return stale;
        }
        catch { return Array.Empty<string>(); } // cleanup is defensive; capture diagnostics still fail safe
    }

    private static bool ProcessExists(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch { return false; }
    }

    public static IReadOnlyList<string> BuildTargetArguments(FrameCaptureTarget target)
    {
        if (!string.IsNullOrEmpty(target.ProcessName))
            return new[] { "--process_name", target.ProcessName };
        if (target.Pid is int pid)
            return new[] { "--process_id", pid.ToString(CultureInfo.InvariantCulture) };
        return Array.Empty<string>();
    }

    private sealed class Session : ISampleSession<FrameSample>
    {
        private readonly Process _proc;
        private readonly string _sessionName;   // our unique ETW realtime session — explicitly stopped on teardown (see StopAsync)
        private readonly string? _startupDiagnostics;
        private readonly List<FrameSample> _frames = new();
        private readonly List<string> _stderr = new();   // PresentMon warnings/errors (session collision, trace-start failure, ...)
        private readonly List<string> _rawHead = new();  // first raw stdout lines, verbatim — 0-frame forensics (see Diagnostics)
        private int _stdoutLines;                        // total stdout lines seen (raw), including ones the parser skipped
        private readonly object _lock = new();
        private readonly Task _reader;
        private readonly Task _errReader;
        private int _iTime = -1, _iFrameTime = -1, _iBetween = -1, _iDisplayed = -1, _iGpu = -1;
        private bool _haveHeader;
        private bool _timeIsMs;   // PresentMon 2.x reports the timestamp column in MILLISECONDS (TimeInMs)
        private int _stopped;

        public Session(Process proc, string sessionName, string? startupDiagnostics)
        {
            _proc = proc;
            _sessionName = sessionName;
            _startupDiagnostics = startupDiagnostics;
            _reader = Task.Run(ReadLoop);
            _errReader = Task.Run(ReadErrLoop);   // drain stderr so it can't block the pipe AND keep it for diagnostics
        }

        public DataSourceMode Mode => DataSourceMode.Live;
        public int SampleCount { get { lock (_lock) return _frames.Count; } }
        public FrameSample? Latest { get { lock (_lock) return _frames.Count > 0 ? _frames[^1] : null; } }

        /// <summary>Stderr, plus — when the capture yielded ZERO frames — raw-stdout forensics: how many
        /// stdout lines PresentMon actually emitted, whether a header was recognized, the first raw lines
        /// verbatim, and the exit state. The 2026-07 all-bench 0-frame regression was undiagnosable because
        /// only the parsed (empty) result survived; this pins WHICH stage died — no output at all (trace
        /// never started / no presents seen), output the parser didn't recognize (schema change), or an
        /// instant process exit. SceneRunner already logs Diagnostics on every 0-frame capture.</summary>
        public string? Diagnostics
        {
            get
            {
                lock (_lock)
                {
                    var parts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(_startupDiagnostics)) parts.Add(_startupDiagnostics);
                    if (_stderr.Count > 0) parts.Add(string.Join(" | ", _stderr));
                    // At startup zero rows is the normal state, not a failure. Emit raw zero-frame forensics
                    // only after teardown (or an unexpected early exit), when SceneRunner is actually
                    // diagnosing a failed capture.
                    bool exited = false;
                    try { exited = _proc.HasExited; } catch { }
                    if (_frames.Count == 0 && (Volatile.Read(ref _stopped) != 0 || exited))
                    {
                        string exit = "running";
                        try { if (_proc.HasExited) exit = $"EXITED code {_proc.ExitCode}"; } catch { exit = "exit state unknown"; }
                        parts.Add($"[PM raw: {_stdoutLines} stdout line(s), header {(_haveHeader ? "found" : "NOT found")}, process {exit}]");
                        if (_rawHead.Count > 0) parts.Add("[first stdout: " + string.Join(" ⏎ ", _rawHead) + "]");
                    }
                    return parts.Count == 0 ? null : string.Join(" | ", parts);
                }
            }
        }

        private void ReadLoop()
        {
            string? line;
            while ((line = _proc.StandardOutput.ReadLine()) is not null)
            {
                lock (_lock)
                {
                    _stdoutLines++;
                    if (_rawHead.Count < 8) _rawHead.Add(line.Length <= 160 ? line : line[..160] + "…");
                }
                if (string.IsNullOrWhiteSpace(line)) continue;
                // The header is the only line containing the literal column name "ProcessID"
                // (data rows hold a numeric pid there). Stable across PresentMon 1.x and 2.x.
                if (!_haveHeader && line.Contains("ProcessID", StringComparison.OrdinalIgnoreCase))
                {
                    ResolveHeader(line.Split(','));
                    _haveHeader = true;
                    continue;
                }
                if (!_haveHeader) continue;

                var c = line.Split(',');
                double t = Get(c, _iTime);
                if (_timeIsMs && !double.IsNaN(t)) t /= 1000.0;   // TimeInMs -> seconds
                double ft = _iFrameTime >= 0 ? Get(c, _iFrameTime) : Get(c, _iBetween);
                if (ft <= 0 || double.IsNaN(ft)) continue;
                var f = new FrameSample(t, ft)
                {
                    DisplayedTimeMs = _iDisplayed >= 0 ? NGet(c, _iDisplayed) : null,
                    GpuBusyMs = _iGpu >= 0 ? NGet(c, _iGpu) : null
                };
                lock (_lock) _frames.Add(f);
                GpuSuite.Core.RunHeartbeat.Ping();   // frames are presenting = the game is alive and rendering
            }
        }

        private void ReadErrLoop()
        {
            string? line;
            while ((line = _proc.StandardError.ReadLine()) is not null)
            {
                var t = line.Replace("\0", "").Trim();   // NUL-strip: keeps a UTF-16LE-emitting build readable under the UTF-8 decode
                if (t.Length == 0) continue;
                lock (_lock) { if (_stderr.Count < 40) _stderr.Add(t); }   // cap: keep the first ~40 lines (the failure is always early)
            }
        }

        private void ResolveHeader(string[] cols)
        {
            // Resolve columns by HEADER NAME, tolerating PresentMon 1.x and 2.x schemas:
            //   timestamp : TimeInSeconds (1.x, sec)        | TimeInMs (2.x, ms)
            //   frametime : FrameTime / msBetweenPresents   | MsBetweenPresents (2.x)
            //   displayed : msUntilDisplayed/...DisplayChange | MsUntilDisplayed / MsBetweenDisplayChange
            //   gpu busy  : GPUBusy / msGPUActive           | MsGPUBusy (2.x)
            for (int i = 0; i < cols.Length; i++)
            {
                var h = cols[i].Trim();
                if (h.Equals("TimeInSeconds", StringComparison.OrdinalIgnoreCase)) { _iTime = i; _timeIsMs = false; }
                else if (h.Equals("TimeInMs", StringComparison.OrdinalIgnoreCase)) { _iTime = i; _timeIsMs = true; }
                else if (h.Equals("FrameTime", StringComparison.OrdinalIgnoreCase) || h.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase)) { if (_iFrameTime < 0) _iFrameTime = i; }
                else if (h.Equals("msUntilDisplayed", StringComparison.OrdinalIgnoreCase) || h.Equals("MsUntilDisplayed", StringComparison.OrdinalIgnoreCase) ||
                         h.Equals("msBetweenDisplayChange", StringComparison.OrdinalIgnoreCase) || h.Equals("MsBetweenDisplayChange", StringComparison.OrdinalIgnoreCase)) { if (_iDisplayed < 0) _iDisplayed = i; }
                else if (h.Equals("GPUBusy", StringComparison.OrdinalIgnoreCase) || h.Equals("msGPUActive", StringComparison.OrdinalIgnoreCase) || h.Equals("MsGPUBusy", StringComparison.OrdinalIgnoreCase)) { if (_iGpu < 0) _iGpu = i; }
            }
            if (_iFrameTime < 0 && _iBetween >= 0) _iFrameTime = _iBetween;
        }

        private static double Get(string[] c, int i) =>
            i >= 0 && i < c.Length && double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
        private static double? NGet(string[] c, int i) =>
            i >= 0 && i < c.Length && double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

        // Killing the PresentMon process ORPHANS its realtime ETW session — the session is a kernel object
        // that outlives its creator process and keeps running with no consumer. Leaked sessions pile up across
        // a sweep and, past a couple, new PresentMon sessions silently stop receiving present events → every
        // later run captures 0 frames (observed live: a single elevated Cyberpunk sweep leaked GpuSuite_*_1..5
        // and capture died after run 2). So explicitly stop our named session on teardown. `logman` is a
        // built-in (System32) and stops a realtime session by name with `-ets`; best-effort, same-integrity so
        // never denied for our own session.
        public async Task<IReadOnlyList<FrameSample>> StopAsync()
        {
            Volatile.Write(ref _stopped, 1);
            // Stop the named realtime ETW session before touching the host process. Hard-killing
            // PresentMon first can strand the kernel session and poison later captures with dropped
            // events. Give it a short graceful-exit window; force termination is only the last resort.
            StopEtwSession(_sessionName);
            try
            {
                if (!_proc.HasExited)
                {
                    using var graceful = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await _proc.WaitForExitAsync(graceful.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            try { if (!_proc.HasExited) _proc.Kill(true); } catch { }
            try { await _reader.ConfigureAwait(false); } catch { }
            try { await _errReader.ConfigureAwait(false); } catch { }
            StopEtwSession(_sessionName);
            lock (_lock) return _frames.ToList();
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            try { _proc.Dispose(); } catch { }
        }
    }

    private static void StopEtwSession(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        try
        {
            var psi = new ProcessStartInfo { FileName = "logman", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("stop"); psi.ArgumentList.Add(name); psi.ArgumentList.Add("-ets");
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch { /* best-effort: a leaked session is recoverable; never let cleanup throw */ }
    }
}
