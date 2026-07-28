using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using GpuSuite.Core.Diagnostics;

namespace GpuSuite.Engine.Vision;

/// <summary>
/// Grabs a single CLEAN still frame from a capture card (e.g. Elgato 4K Pro) via ffmpeg's DirectShow
/// input — the operator-prep VISION path. It yields a resolution-independent, pixel-exact picture of what
/// the bench display is actually rendering (and sees exclusive-fullscreen, which a GDI CopyFromScreen
/// screenshot returns black for), replacing fragile desktop screenshots for reading game menus/settings.
///
/// Headless by design (no preview window) so a single-PC capture loopback produces no video-feedback
/// recursion. This is NOT a frame-timing source — PresentMon/RTSS remain the FPS path; this is vision only.
/// Shells out to ffmpeg (no image-library dependency: PNG size is read straight from the IHDR header).
/// </summary>
public sealed class CaptureCardGrabber
{
    /// <summary>The capture card is EXCLUSIVE — one in-process ffmpeg open at a time. Serializes grabs/probes
    /// from EVERY grabber instance (bot vision, OCR gates, the health monitor's motion probe) so two ffmpeg's
    /// never fight over the DirectShow device. Polite low-priority callers (the runtime-health scene-static
    /// probe) check <see cref="DeviceBusy"/> and SKIP their sample instead of queueing behind a bot's sensor.</summary>
    private static readonly SemaphoreSlim s_device = new(1, 1);

    /// <summary>True while another in-process grab/probe holds the capture device.</summary>
    public static bool DeviceBusy => s_device.CurrentCount == 0;

    private readonly string _ffmpeg;
    private readonly string _device;
    private readonly RunLogger _log;

    public CaptureCardGrabber(string? ffmpegPath, string? deviceName, RunLogger log)
    {
        var ff = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : Environment.ExpandEnvironmentVariables(ffmpegPath.Trim());
        // Pin to an absolute path when the configured file exists, so it resolves regardless of cwd.
        _ffmpeg = (ff != "ffmpeg" && File.Exists(ff)) ? Path.GetFullPath(ff) : ff;
        _device = deviceName?.Trim() ?? "";
        _log = log;
    }

    /// <summary>ffmpeg resolvable? (a bare "ffmpeg" we trust PATH to find, or an existing file path).</summary>
    public bool FfmpegResolved => _ffmpeg == "ffmpeg" || File.Exists(_ffmpeg);

    /// <summary>
    /// Opens the configured DirectShow video stream for a bounded number of frames and discards them to
    /// ffmpeg's null muxer. This is the full pre-flight transport probe: it never writes, retains, or OCRs
    /// an image, and it does not launch a game.
    /// </summary>
    public async Task<bool> ProbeVideoAsync(int frames = 2, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_device)) { _log.Warn("Vision", "No capture-card device set for the stream probe."); return false; }
        if (!FfmpegResolved) { _log.Warn("Vision", $"ffmpeg not found at '{_ffmpeg}' (set settings.ffmpegPath)."); return false; }

        string[] args =
        {
            "-hide_banner", "-loglevel", "error", "-f", "dshow", "-rtbufsize", "200M", "-i", $"video={_device}",
            "-frames:v", Math.Max(1, frames).ToString(), "-an", "-f", "null", "-"
        };
        bool ok; string stderr;
        await s_device.WaitAsync(ct).ConfigureAwait(false);
        try { (ok, stderr) = await RunAsync(_ffmpeg, args, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false); }
        finally { s_device.Release(); }
        if (ok) _log.Info("Vision", $"Capture-card stream probe passed for \"{_device}\"; no image retained.");
        else _log.Warn("Vision", $"Capture-card stream probe failed for \"{_device}\": {stderr.Trim()}");
        return ok;
    }

    /// <summary>
    /// Grab one frame to <paramref name="outPng"/>. Grabs <paramref name="warmupFrames"/> frames and keeps
    /// the LAST (ffmpeg -update 1 overwrites the same file each frame) to skip the capture device's warm-up
    /// — the first frame off a card is frequently black. Returns true only when a non-trivial PNG was written.
    /// </summary>
    public async Task<bool> GrabAsync(string outPng, int warmupFrames = 12, CancellationToken ct = default, int scaleWidth = 0)
    {
        if (string.IsNullOrWhiteSpace(_device)) { _log.Warn("Vision", "No capture-card device set (settings.captureCardDevice or --device)."); return false; }
        if (!FfmpegResolved) { _log.Warn("Vision", $"ffmpeg not found at '{_ffmpeg}' (set settings.ffmpegPath)."); return false; }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPng))!);
        try { if (File.Exists(outPng)) File.Delete(outPng); } catch { /* will be overwritten */ }

        int n = Math.Max(1, warmupFrames);
        // Optional downscale (scaleWidth>0): the vision navigator wants a small frame — fast GPU inference and under
        // the model's request-size limit — while OCR still grabs full-res. -2 keeps the aspect with an even height.
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-f", "dshow", "-rtbufsize", "200M", "-i", $"video={_device}" };
        if (scaleWidth > 0) { args.Add("-vf"); args.Add($"scale={scaleWidth}:-2"); }
        args.AddRange(new[] { "-frames:v", n.ToString(), "-update", "1", "-y", outPng });
        bool ok; string stderr;
        await s_device.WaitAsync(ct).ConfigureAwait(false);
        try { (ok, stderr) = await RunAsync(_ffmpeg, args, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
        finally { s_device.Release(); }
        bool produced = File.Exists(outPng) && new FileInfo(outPng).Length > 1024;
        if (ok && produced)
        {
            var (w, h) = PngSize(outPng);
            _log.Info("Vision", $"Grabbed capture-card frame → {outPng} ({w}x{h}) from \"{_device}\".");
            return true;
        }
        _log.Warn("Vision", $"Capture-card grab failed (device \"{_device}\"). {stderr.Trim()}");
        return false;
    }

    /// <summary>
    /// Sample in-world MOTION off the capture card WITHOUT touching the benchmark GPU — the Tier-0 "stuck
    /// reflex" sensor for smart bots. Captures a short burst and runs ffmpeg's scene-change detector (scdet):
    /// the returned MEAN scene score is ≈0 when the scene barely changes between frames (the character is
    /// wall-stuck / not translating) and rises as the whole frame shifts (real movement THROUGH space). The
    /// Elgato read is raw ffmpeg (doesn't perturb the RTSS frame capture) and the work is CPU/ffmpeg only —
    /// no local-GPU cost, so it's safe to run during the measured window. Returns -1 when unavailable
    /// (no device / no ffmpeg / no scores parsed) so the caller can fall back to a blind route.
    /// Threshold is calibrated live via `gpusuite motion-probe` (translating ≫ stuck).
    /// </summary>
    public async Task<double> SampleMotionAsync(int frames = 12, int scaleWidth = 320, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_device) || !FfmpegResolved) return -1;
        int n = Math.Max(2, frames);
        // scdet emits a per-frame "lavfi.scd.score"; metadata=print dumps it to stderr where RunAsync collects it.
        // A small grayscale scale keeps it cheap and motion-focused (color/exposure flicker doesn't inflate the score).
        string[] args =
        {
            "-hide_banner", "-loglevel", "info", "-f", "dshow", "-rtbufsize", "200M", "-i", $"video={_device}",
            "-frames:v", n.ToString(), "-vf", $"format=gray,scale={scaleWidth}:-2,scdet=threshold=0,metadata=mode=print",
            "-an", "-f", "null", "-"
        };
        string stderr;
        await s_device.WaitAsync(ct).ConfigureAwait(false);
        try { (_, stderr) = await RunAsync(_ffmpeg, args, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false); }
        finally { s_device.Release(); }
        var scores = new List<double>();
        foreach (Match m in Regex.Matches(stderr, @"scd\.score[=:]\s*([0-9]+(?:\.[0-9]+)?)"))
            if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s))
                scores.Add(s);
        if (scores.Count == 0) { _log.Trace("Vision", "Motion probe parsed no scdet scores (older ffmpeg? device busy?) — motion unknown."); return -1; }
        double mean = scores.Average();
        _log.Trace("Vision", $"Motion probe: mean scene-change {mean:0.00} over {scores.Count} frame(s) (low ≈ stuck, high ≈ moving through space).");
        return mean;
    }

    /// <summary>
    /// Rescale an existing PNG to <paramref name="targetHeight"/> rows (lanczos, aspect preserved) — the
    /// sub-native OCR normalization step (#205). Windows.Media.Ocr has a hard small-text floor: menu-row
    /// text that reads perfectly off a 3840x2160 capture garbles or vanishes off the SAME menu captured at
    /// 1920x1080 (ACM 'Quit to Desktop', DOOM menu-apply values), because at 1080p the glyphs are half the
    /// pixel height. Upscaling the frame before recognition restores the reads (offline A/B/C-proven: the
    /// re-upscale found a SUPERSET of the 4K original's lines). Pure file transcode — no capture device
    /// access, so no <see cref="s_device"/> hold. Returns true when the output PNG was produced.
    /// </summary>
    public async Task<bool> ScaleToHeightAsync(string inPng, string outPng, int targetHeight, CancellationToken ct = default)
    {
        if (!FfmpegResolved || !File.Exists(inPng)) return false;
        string[] args =
        {
            "-hide_banner", "-loglevel", "error", "-y", "-i", inPng,
            "-vf", $"scale=-2:{targetHeight}:flags=lanczos", outPng
        };
        var (ok, stderr) = await RunAsync(_ffmpeg, args, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (!ok) _log.Warn("Vision", $"PNG upscale to {targetHeight} rows failed: {stderr.Trim()}");
        return ok && File.Exists(outPng) && new FileInfo(outPng).Length > 1024;
    }

    /// <summary>
    /// Crop a region out of an existing PNG and upscale it <paramref name="scale"/>× (lanczos) — the
    /// row-zoom OCR fallback (#205 follow-up). A focused menu row's small value token ('TAA' at a 1080p
    /// desktop) can be missed by full-frame OCR even after normalization; a 4× zoom of just the value band
    /// puts the glyphs far above Windows OCR's small-text floor. Pure file transcode — no device hold.
    /// </summary>
    public async Task<bool> CropScaleAsync(string inPng, string outPng, int x, int y, int w, int h, int scale, CancellationToken ct = default)
    {
        if (!FfmpegResolved || !File.Exists(inPng) || w <= 0 || h <= 0) return false;
        string[] args =
        {
            "-hide_banner", "-loglevel", "error", "-y", "-i", inPng,
            "-vf", $"crop={w}:{h}:{Math.Max(0, x)}:{Math.Max(0, y)},scale=iw*{scale}:ih*{scale}:flags=lanczos", outPng
        };
        var (ok, stderr) = await RunAsync(_ffmpeg, args, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (!ok) _log.Warn("Vision", $"PNG crop+zoom failed: {stderr.Trim()}");
        return ok && File.Exists(outPng) && new FileInfo(outPng).Length > 256;
    }

    /// <summary>List the DirectShow VIDEO device names ffmpeg can see — for discovery / picking the card.</summary>
    public async Task<IReadOnlyList<string>> ListVideoDevicesAsync(CancellationToken ct = default)
    {
        if (!FfmpegResolved) { _log.Warn("Vision", $"ffmpeg not found at '{_ffmpeg}' (set settings.ffmpegPath)."); return Array.Empty<string>(); }
        // ffmpeg prints the device list to stderr and exits non-zero (the "dummy" input fails by design).
        string[] args = { "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy" };
        var (_, stderr) = await RunAsync(_ffmpeg, args, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        var names = new List<string>();
        foreach (Match m in Regex.Matches(stderr, "\"([^\"]+)\"\\s+\\(video\\)"))
            names.Add(m.Groups[1].Value);
        return names;
    }

    private static async Task<(bool ok, string stderr)> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);   // ArgumentList quotes each item (handles the spaces in "Elgato 4K Pro")

        using var p = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
        try { if (!p.Start()) return (false, "failed to start ffmpeg"); }
        catch (Exception ex) { return (false, ex.Message); }
        p.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (false, sb + "\n(ffmpeg timed out)"); }
        return (p.ExitCode == 0, sb.ToString());
    }

    /// <summary>Read PNG width/height from the IHDR header (bytes 16..23, big-endian) — no image library.
    /// Internal so <see cref="ScreenReader"/> can size a frame before deciding to OCR-normalize it (#205).</summary>
    internal static (int w, int h) PngSize(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> b = stackalloc byte[24];
            int read = 0;
            while (read < 24) { int r = fs.Read(b.Slice(read)); if (r <= 0) break; read += r; }
            if (read < 24) return (0, 0);
            int w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
            int h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
            return (w, h);
        }
        catch { return (0, 0); }
    }
}
