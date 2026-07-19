using System.Diagnostics;
using System.Text;
using GpuSuite.Core.Config;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Models;
using GpuSuite.Measurement;
using GpuSuite.Measurement.Real;

namespace GpuSuite.Engine.Scenes;

/// <summary>
/// Live benchmark On-Screen Display. Composes a compact multi-line status block — current test pass,
/// resolution, phase, live FPS / frame-time, GPU &amp; system power, GPU/CPU temperatures and load —
/// and pushes it to the RTSS OSD a few times a second so it renders over the running game.
///
/// Entirely best-effort: if RTSS / the OSD shared memory is unavailable, <see cref="Start"/> still
/// returns a valid but disabled instance, so callers can <c>await using</c> it unconditionally without
/// guarding. Reads are non-destructive (SampleCount + Latest peek), so the OSD never perturbs capture.
/// </summary>
public sealed class BenchmarkOsd : IAsyncDisposable
{
    /// <summary>Per-run metadata shown on the overlay (pass number, resolution, capture backend).</summary>
    public sealed class Meta
    {
        public string GameName = "";
        public string ResolutionName = "";
        public int Width, Height;
        public int Pass, TotalPasses;
        public string FrameSource = "";
        public string PowerProvenance = "NOT A HARDWARE RESULT";
    }

    private readonly RtssOsdWriter _writer;
    private readonly ISampleSession<FrameSample> _frames;
    private readonly ISampleSession<PowerSample> _power;
    private readonly ISampleSession<TelemetrySample> _tel;
    private readonly Meta _meta;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private double _emaFtMs;   // EMA-smoothed frame time (ms); FPS is derived from this so the two stay consistent

    /// <summary>Short phase token shown in brackets, e.g. WAIT / RUN / CAPTURE / DONE. Settable live.</summary>
    public string Phase { get; set; } = "WAIT";
    /// <summary>True when the OSD is actually driving the RTSS overlay.</summary>
    public bool Enabled { get; }

    private BenchmarkOsd(RtssOsdWriter writer, bool enabled,
        ISampleSession<FrameSample> frames, ISampleSession<PowerSample> power, ISampleSession<TelemetrySample> tel,
        Meta meta, int intervalMs)
    {
        _writer = writer; Enabled = enabled;
        _frames = frames; _power = power; _tel = tel; _meta = meta;
        _loop = enabled ? Task.Run(() => LoopAsync(Math.Max(100, intervalMs))) : Task.CompletedTask;
    }

    /// <summary>
    /// Build and start the OSD for one run. Honors <c>cfg.Osd</c>; opens the RTSS OSD shared memory and,
    /// on failure, returns a disabled instance (logged at trace). Never throws.
    /// </summary>
    public static BenchmarkOsd Start(SuiteConfig cfg, Meta meta,
        ISampleSession<FrameSample> frames, ISampleSession<PowerSample> power, ISampleSession<TelemetrySample> tel,
        RunLogger log)
    {
        var writer = new RtssOsdWriter();
        bool enabled = cfg.Osd && writer.Open();
        if (cfg.Osd && !enabled) log.Trace("OSD", "RTSS OSD unavailable (RTSS not running or no writable slot) — overlay disabled for this run.");
        else if (enabled) log.Info("OSD", $"Live benchmark OSD active via RTSS — pass {meta.Pass}/{meta.TotalPasses} {meta.ResolutionName}.");
        return new BenchmarkOsd(writer, enabled, frames, power, tel, meta, cfg.OsdIntervalMs);
    }

    private async Task LoopAsync(int intervalMs)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                try { _writer.Update(Compose()); } catch { /* shared memory vanished mid-run; stop quietly */ }
                await Task.Delay(intervalMs, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private string Compose()
    {
        // During the NAV phase (WAIT) the bot is reading the game's menus off the capture card via OCR. The
        // RTSS OSD renders top-left and OCCLUDES menu header text there — observed to defeat vision-nav gates
        // (MSFS 'Activities'/'Airbus H125', and a general risk for every menu-nav game). The OSD has no value
        // during nav (it's for the human watching the measured run), so render it BLANK until the benchmark
        // actually starts (phase flips to RUN/CAPTURE). This clears the overlay so OCR sees the clean menu.
        if (Phase == "WAIT") return string.Empty;

        // FPS is derived from the (EMA-smoothed) recent FRAME TIME, not a sample-count delta over
        // wall-clock. PresentMon delivers frames in bursts whose interval is longer than the OSD tick, so
        // the old count-delta rate saw 0 new samples on most ticks and a spike on burst ticks — averaging
        // far too low (it showed ~11 fps while the frame time said 18ms ≈ 55 fps, and disagreed with the
        // benchmark's own report). Tying FPS to the displayed frame time keeps the two consistent with
        // each other AND with the benchmark's frame-time-based average.
        int count = _frames.SampleCount;
        double ftRaw = _frames.Latest?.FrameTimeMs ?? 0;
        if (ftRaw > 0)
            _emaFtMs = _emaFtMs <= 0 ? ftRaw : _emaFtMs * 0.8 + ftRaw * 0.2;
        double ft = _emaFtMs;
        double fps = ft > 0 ? 1000.0 / ft : 0;

        var tel = _tel.Latest;
        var pwr = _power.Latest;

        var sb = new StringBuilder(220);
        sb.Append("GpuTestSuite | ").Append(Trunc(_meta.GameName, 28)).Append('\n');
        sb.Append($"Pass {_meta.Pass}/{_meta.TotalPasses}  {_meta.ResolutionName} {_meta.Width}x{_meta.Height}  [{Phase}]\n");
        sb.Append($"FPS {(fps > 0 ? fps.ToString("0.0") : "--")}  ft {ft:0.0}ms  frames {count}  ({_meta.FrameSource})\n");
        if (tel is not null)
            sb.Append($"GPU {Fmt(tel.GpuTempC)}C hot {Fmt(tel.GpuHotspotC)}C  load {Fmt(tel.GpuLoadPct)}%  {Fmt(tel.GpuCoreClockMhz)}MHz\n");
        // Power: Powenetics is the authoritative source; otherwise show the GPU's own board-power telemetry.
        if (pwr is not null && (pwr.GpuTotalW is not null || pwr.SystemTotalW is not null))
            sb.Append($"PWR gpu {Fmt(pwr.GpuTotalW)}W  sys {Fmt(pwr.SystemTotalW)}W  [{_meta.PowerProvenance}]\n");
        else if (tel?.GpuBoardPowerW is not null)
            sb.Append($"PWR gpu {Fmt(tel.GpuBoardPowerW)}W  [APPROXIMATE · GPU TELEMETRY]\n");
        if (tel is not null)
        {
            sb.Append($"CPU {Fmt(tel.CpuTempC)}C  load {Fmt(tel.CpuLoadPct)}%");
            // CPU power: the Powenetics PMD measures it (EPS rails) WITHOUT elevation; LHM's SMU package
            // power (CpuPowerW) needs admin — same as CPU temp, which ONLY comes from LHM with elevation.
            double? cpuW = pwr?.CpuTotalW ?? tel.CpuPowerW;
            if (cpuW is > 0) sb.Append($"  pwr {Fmt(cpuW)}W");
        }
        return sb.ToString();
    }

    private static string Fmt(double? v) => v is double d ? d.ToString("0") : "--";
    private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n]);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop.ConfigureAwait(false); } catch { }
        try { _writer.Dispose(); } catch { }
        _cts.Dispose();
    }
}
