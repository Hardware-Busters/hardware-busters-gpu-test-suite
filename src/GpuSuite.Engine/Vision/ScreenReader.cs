using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GpuSuite.Core.Diagnostics;

namespace GpuSuite.Engine.Vision;

/// <summary>One OCR'd text line with its pixel bounding box (top-left x,y + w,h) and center (cx,cy),
/// in capture-frame coordinates. Emitted by tools/vision/ocr.ps1 (Windows.Media.Ocr).</summary>
public sealed class OcrLine
{
    public string Text { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public int Cx { get; set; }
    public int Cy { get; set; }
    public int Right => X + W;
    public override string ToString() => $"\"{Text}\" @({Cx},{Cy}) [{W}x{H}]";
}

/// <summary>
/// A single OCR'd frame: the words/lines the capture card currently shows, with positions. This is the
/// vision-nav engine's "eye" — the menu applier finds a setting's label line, reads the value sitting to
/// its right (or the trailing token on the same line), actuates input, then re-reads to VERIFY.
/// </summary>
public sealed class OcrFrame
{
    public int Width { get; set; }
    public int Height { get; set; }
    public List<OcrLine> Lines { get; set; } = new();

    private sealed class Dto
    {
        public int width { get; set; }
        public int height { get; set; }
        public List<OcrLine>? lines { get; set; }
        public string? error { get; set; }
    }

    /// <summary>Parse the JSON ocr.ps1 emits ({width,height,lines:[{text,x,y,w,h,cx,cy}]}). Returns null
    /// on an {"error":...} payload or unparseable text.</summary>
    public static OcrFrame? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        // Windows OCR occasionally yields raw control characters (e.g. BEL 0x07) inside recognized text, and
        // PowerShell 5.1's ConvertTo-Json does not escape them, so the JSON is invalid for strict parsers.
        // Drop raw control chars (keeping tab/newline/cr, which appear only as harmless inter-token space).
        if (json.Any(c => c < 0x20 && c != '\t' && c != '\n' && c != '\r'))
            json = new string(json.Where(c => c >= 0x20 || c == '\t' || c == '\n' || c == '\r').ToArray());
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto is null || dto.error is not null) return null;
            return new OcrFrame { Width = dto.width, Height = dto.height, Lines = dto.lines ?? new() };
        }
        catch { return null; }
    }

    private static string Norm(string s) => Regex.Replace(s, "[^a-z0-9]", "", RegexOptions.IgnoreCase).ToLowerInvariant();

    /// <summary>First line whose text contains <paramref name="needle"/> (alphanumeric-insensitive: spacing,
    /// punctuation and case are ignored, so "Ray Tracing" matches "Ray  Tracing:").</summary>
    public OcrLine? Find(string needle) => FindAll(needle).FirstOrDefault();

    /// <summary>All lines containing <paramref name="needle"/>, top-to-bottom then left-to-right.</summary>
    public IEnumerable<OcrLine> FindAll(string needle)
    {
        var n = Norm(needle);
        if (n.Length == 0) return Enumerable.Empty<OcrLine>();
        return Lines.Where(l => Norm(l.Text).Contains(n)).OrderBy(l => l.Cy).ThenBy(l => l.X);
    }

    /// <summary>First line containing <paramref name="needle"/> as a WHOLE WORD/phrase token (not a
    /// substring), top-to-bottom then left-to-right. Use when a substring would false-match a longer word —
    /// e.g. gating on "PLAY" must NOT trip on "gameplay" (Forza's legal-warning splash, which desynced the
    /// whole nav by satisfying the title gate 2-3 minutes early). See <see cref="FindAllWord"/>.</summary>
    public OcrLine? FindWord(string needle) => FindAllWord(needle).FirstOrDefault();

    /// <summary>All lines containing <paramref name="needle"/> as a standalone alphanumeric token (or run of
    /// consecutive tokens that concatenate to it, so multi-word needles like "free play" still match), top-to-
    /// bottom then left-to-right. Word boundaries are the non-alphanumeric gaps in the ORIGINAL OCR text, so
    /// "PLAY" matches "Play" / "PLAY ›" but not "gameplay"; "RACE" matches "RACE" but not "terrace".</summary>
    public IEnumerable<OcrLine> FindAllWord(string needle)
    {
        var n = Norm(needle);
        if (n.Length == 0) return Enumerable.Empty<OcrLine>();
        return Lines.Where(l => ContainsWord(l.Text, n)).OrderBy(l => l.Cy).ThenBy(l => l.X);
    }

    /// <summary>True if <paramref name="normNeedle"/> (already normalized to [a-z0-9]) appears in
    /// <paramref name="text"/> as one standalone alphanumeric token, or as a run of consecutive tokens whose
    /// concatenation equals it. Tokens are the maximal [a-z0-9]+ runs of the original text (case-folded), so
    /// surrounding letters (gamePLAY) never count and punctuation/spacing is ignored.</summary>
    private static bool ContainsWord(string text, string normNeedle)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var tokens = Regex.Matches(text, "[a-z0-9]+", RegexOptions.IgnoreCase);
        for (int i = 0; i < tokens.Count; i++)
        {
            var acc = "";
            for (int j = i; j < tokens.Count; j++)
            {
                acc += tokens[j].Value.ToLowerInvariant();
                if (acc.Length > normNeedle.Length) break;
                if (acc == normNeedle) return true;
                if (!normNeedle.StartsWith(acc, StringComparison.Ordinal)) break;
            }
        }
        return false;
    }

    /// <summary>True when this frame is the Elgato capture card's NO-SIGNAL slate — the card lost sync with
    /// the display output (a game's video-settings apply re-initing the swapchain does this; DOOM 2026-07-04/05).
    /// The slate is a near-empty black card whose ONLY text is "NO SIGNAL" over the small elgato logo, so:
    /// very few lines total AND the SIGNAL token AND a corroborating NO/elgato token. A real game frame that
    /// happens to mention "signal" carries many other lines and never matches; a black loading screen OCRs to
    /// ZERO lines and deliberately does NOT match (blackness is not evidence the card lost sync).</summary>
    public bool IsNoSignalSlate =>
        Lines.Count > 0 && Lines.Count <= 4 &&
        FindWord("signal") is not null &&
        (FindWord("no") is not null || FindWord("elgato") is not null);

    // A line that reads like the RTSS / benchmark On-Screen-Display: an FPS/frametime counter ("120 fps", "8.3 ms"),
    // the suite OSD header ("Pass 1/3"), or a bracketed phase token ("[RUN]"). These are distinctive to the overlay —
    // menu anchors (RESUME / CAREER / PLAY / CONTINUE) never match — so stripping them is safe for anchor matching.
    private static readonly Regex OsdLineRx = new(
        @"\bfps\b|\bpass\s*\d+\s*/\s*\d+\b|\[(run|wait|capture|done|nav)\]|\b\d+(\.\d+)?\s*ms\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// A copy of this frame with RTSS / benchmark OSD lines removed. The suite's own OSD is already blanked during
    /// nav (BenchmarkOsd returns "" while Phase=="WAIT"); this ALSO strips RTSS's NATIVE FPS counter, which the
    /// suite doesn't control, so the top-left overlay can't clutter or false-match the menu OCR. Anchor matching
    /// (WaitForText / PressUntilText / the nav graph) reads the stripped frame; the menu-VALUE applier — which
    /// legitimately reads numbers — uses the raw frame. Returns the same instance when nothing matched.
    /// </summary>
    public OcrFrame WithoutOsdLines()
    {
        if (Lines.Count == 0) return this;
        var kept = Lines.Where(l => !OsdLineRx.IsMatch(l.Text ?? "")).ToList();
        return kept.Count == Lines.Count ? this : new OcrFrame { Width = Width, Height = Height, Lines = kept };
    }

    /// <summary>
    /// Read the VALUE associated with a labelled setting row. Strategy, in order:
    /// (1) a separate OCR line to the RIGHT of the label on the same row (vertical centers within one line
    ///     height) — the common menu layout "Ray Tracing            On"; pick the nearest such line;
    /// (2) failing that, the trailing text of the label's own line after the label words (single-line
    ///     "Ray Tracing  On"). Returns null if the label isn't found.
    /// </summary>
    public string? ValueFor(string labelNeedle, double maxLabelXFrac = 1.0, double maxValueXFrac = 1.0)
    {
        // A robust substring can match several lines: the actual list ROW (label on the LEFT, value beside
        // it) plus a right-side description pane that repeats the setting name (header + body prose, no
        // value). maxLabelXFrac caps how far right a LABEL may be — set it below a right-side description
        // pane so a missed/highlighted row reads null (→ retry) instead of returning the prose. Prefer the
        // LEFTMOST match and return the first that actually yields a value beside it.
        // maxValueXFrac caps how far right the VALUE may sit: when the row's value token fails to OCR (a
        // small highlighted 'TAA' at 1080p), the nearest in-band line to the right is a description-pane
        // wrapped line ('…your GPU.') — cap the value column below the pane so that read returns null
        // (→ retry) instead of prose (live DOOM 1080p, 2026-07-09).
        int maxX = maxLabelXFrac >= 1.0 || Width <= 0 ? int.MaxValue : (int)(maxLabelXFrac * Width);
        foreach (var label in FindAll(labelNeedle).Where(l => l.X <= maxX).OrderBy(l => l.X))
        {
            var v = ValueForLine(label, labelNeedle, maxValueXFrac);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    public string? ValueForLine(OcrLine label, string labelNeedle, double maxValueXFrac = 1.0)
    {
        int tol = Math.Max(12, (int)(label.H * 0.8));
        int maxValueX = maxValueXFrac >= 1.0 || Width <= 0 ? int.MaxValue : (int)(maxValueXFrac * Width);
        var right = Lines
            .Where(l => !ReferenceEquals(l, label) && l.X >= label.X + label.W / 2 && l.X <= maxValueX && Math.Abs(l.Cy - label.Cy) <= tol)
            .OrderBy(l => l.X)
            .FirstOrDefault();
        if (right is not null && right.Text.Trim().Length > 0) return right.Text.Trim();

        // same-line "Label   Value": strip the matched label words off the front.
        var n = Norm(labelNeedle);
        var words = label.Text.Trim();
        var normWords = Norm(words);
        int idx = normWords.IndexOf(n, StringComparison.Ordinal);
        if (idx >= 0)
        {
            // walk the original string skipping characters until we've consumed (idx + n.Length) alnum chars
            int need = idx + n.Length, seen = 0, p = 0;
            for (; p < words.Length && seen < need; p++)
                if (char.IsLetterOrDigit(words[p])) seen++;
            var tail = words.Substring(p).Trim();
            if (tail.Length > 0) return tail;
        }
        return null;
    }

    /// <summary>True if the value read for <paramref name="labelNeedle"/> matches <paramref name="expected"/>
    /// (alphanumeric-insensitive). Use to decide whether a setting already holds the target value.</summary>
    public bool ValueMatches(string labelNeedle, string expected)
    {
        var v = ValueFor(labelNeedle);
        return v is not null && Norm(v).Contains(Norm(expected));
    }

    /// <summary>Concatenated text of all lines whose center falls inside the given rectangle, expressed as
    /// 0..1 fractions of the frame (left-to-right). Used to read a game's "focused setting" header pane.</summary>
    public string TextInRegion(double xMinF, double yMinF, double xMaxF, double yMaxF)
    {
        if (Width <= 0 || Height <= 0) return "";
        int x0 = (int)(xMinF * Width), x1 = (int)(xMaxF * Width);
        int y0 = (int)(yMinF * Height), y1 = (int)(yMaxF * Height);
        return string.Join(" ", Lines
            .Where(l => l.Cx >= x0 && l.Cx <= x1 && l.Cy >= y0 && l.Cy <= y1)
            .OrderBy(l => l.Cx)
            .Select(l => l.Text));
    }

    public string Dump()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"frame {Width}x{Height}, {Lines.Count} line(s):");
        foreach (var l in Lines.OrderBy(l => l.Cy).ThenBy(l => l.X))
            sb.AppendLine($"  {l.Cy,5} {l.X,5}  {l.Text}");
        return sb.ToString();
    }
}

/// <summary>
/// The vision-nav "eye": grabs a clean frame off the capture card (<see cref="CaptureCardGrabber"/>) and
/// runs Windows OCR (tools/vision/ocr.ps1, which needs Windows PowerShell 5.1 for the WinRT projection),
/// returning a positioned <see cref="OcrFrame"/>. Resolution-independent and sees exclusive-fullscreen
/// menus a GDI screenshot can't. Vision only — PresentMon/RTSS remain the FPS path.
/// </summary>
public sealed class ScreenReader
{
    private readonly CaptureCardGrabber _grabber;
    private readonly RunLogger _log;
    private readonly string _ocrScript;

    /// <summary>The underlying capture-card grabber — also the smart-bot Tier-0 MOTION sensor
    /// (<see cref="CaptureCardGrabber.SampleMotionAsync"/>), so a caller with the vision eye gets motion for free.</summary>
    public CaptureCardGrabber Grabber => _grabber;

    public ScreenReader(CaptureCardGrabber grabber, RunLogger log, string? ocrScriptPath = null)
    {
        _grabber = grabber;
        _log = log;
        _ocrScript = ResolveOcrScript(ocrScriptPath);
    }

    public bool OcrScriptResolved => File.Exists(_ocrScript);

    private static string ResolveOcrScript(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        // walk up from cwd and from the binary dir looking for tools/vision/ocr.ps1
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
            {
                var p = Path.Combine(dir.FullName, "tools", "vision", "ocr.ps1");
                if (File.Exists(p)) return Path.GetFullPath(p);
            }
        }
        return Path.GetFullPath(Path.Combine("tools", "vision", "ocr.ps1"));
    }

    private static int _dumpSeq;

    /// <summary>Grab a fresh frame off the capture card and OCR it. Null if the grab or OCR fails.</summary>
    public async Task<OcrFrame?> ReadAsync(CancellationToken ct = default, int warmupFrames = 12)
    {
        var png = Path.Combine(Path.GetTempPath(), $"vision_{Guid.NewGuid():N}.png".Substring(0, 18) + ".png");
        // DEBUG: when GPUSUITE_OCR_DUMP names a directory, keep a copy of every OCR'd frame there
        // (sequence-numbered) so a nav can be reconstructed exactly as the OCR saw it. Off by default.
        var dumpDir = Environment.GetEnvironmentVariable("GPUSUITE_OCR_DUMP");
        try
        {
            if (!await _grabber.GrabAsync(png, warmupFrames, ct).ConfigureAwait(false)) return null;
            var frame = await OcrImageAsync(png, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(dumpDir))
            {
                try
                {
                    Directory.CreateDirectory(dumpDir);
                    int seq = System.Threading.Interlocked.Increment(ref _dumpSeq);
                    File.Copy(png, Path.Combine(dumpDir, $"ocr_{seq:D3}.png"), true);
                }
                catch { }
            }
            return frame;
        }
        finally { try { if (File.Exists(png)) File.Delete(png); } catch { } }
    }

    /// <summary>Frames shorter than this many rows are lanczos-upscaled to it before OCR (#205). Windows OCR
    /// reads 4K menu captures fine but garbles the SAME menu at 1080p (glyphs half the pixel height fall
    /// under its small-text floor) — the exact live failure: ACM's 'Quit to Desktop' gate 0/21 reads and
    /// DOOM's menu-apply value misreads at a 1080p desktop, while both pass at 4K. 2160 also normalizes
    /// 1440p frames, adding margin on a path that already worked.</summary>
    private const int OcrNormalizeHeight = 2160;

    /// <summary>OCR an existing PNG (no capture). Used by the offline image path and `vision ocr --png`.</summary>
    public async Task<OcrFrame?> OcrImageAsync(string pngPath, CancellationToken ct = default)
    {
        if (!File.Exists(pngPath)) { _log.Warn("Vision", $"OCR image not found: {pngPath}"); return null; }
        if (!OcrScriptResolved) { _log.Warn("Vision", $"ocr.ps1 not found at '{_ocrScript}'."); return null; }

        // #205 sub-native OCR normalization: recognize small frames at 2160 rows, then map the returned
        // geometry BACK to the original frame so OcrFrame coordinates remain capture-frame coordinates for
        // every consumer (TextInRegion fractions, ValueForLine row tolerance, any coord-derived actuation).
        var (origW, origH) = CaptureCardGrabber.PngSize(pngPath);
        string ocrPath = pngPath;
        string? upscaledTmp = null;
        if (origH > 0 && origH < OcrNormalizeHeight)
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"visionup_{Guid.NewGuid():N}.png");
            if (await _grabber.ScaleToHeightAsync(pngPath, tmp, OcrNormalizeHeight, ct).ConfigureAwait(false))
            {
                upscaledTmp = tmp;
                ocrPath = tmp;
            }
            else
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                _log.Warn("Vision", $"Sub-native OCR upscale unavailable — OCR'ing the native {origW}x{origH} frame (reads may degrade).");
            }
        }
        try
        {
            return await OcrImageCoreAsync(ocrPath, origW, origH, upscaledTmp is not null, ct).ConfigureAwait(false);
        }
        finally
        {
            if (upscaledTmp is not null) { try { File.Delete(upscaledTmp); } catch { } }
        }
    }

    /// <summary>
    /// Row-zoom value read (#205 follow-up): OCR ONLY the band to the RIGHT of a known label line, cropped
    /// from <paramref name="pngPath"/> and zoomed 4× first. Rescues value tokens that full-frame OCR misses
    /// even after sub-native normalization (DOOM's focused 'TAA' at a 1080p desktop read as the description
    /// pane's '…GPU.' / null). <paramref name="label"/> must come from an OCR pass over the SAME png so its
    /// pixel geometry matches. Returns the concatenated left-to-right text of the zoomed band, or null.
    /// </summary>
    public async Task<string?> OcrRowValueZoomAsync(string pngPath, OcrLine label, int maxValueX, CancellationToken ct = default)
    {
        var (fw, fh) = CaptureCardGrabber.PngSize(pngPath);
        if (fw <= 0 || fh <= 0) return null;
        int x0 = Math.Min(fw - 1, label.Right + 4);
        int x1 = maxValueX <= 0 ? fw : Math.Min(fw, maxValueX);
        int y0 = Math.Max(0, label.Y - label.H / 2);
        int y1 = Math.Min(fh, label.Y + label.H * 2);
        if (x1 - x0 < 16 || y1 - y0 < 8) return null;
        var tmp = Path.Combine(Path.GetTempPath(), $"visionrow_{Guid.NewGuid():N}.png");
        try
        {
            if (!await _grabber.CropScaleAsync(pngPath, tmp, x0, y0, x1 - x0, y1 - y0, 4, ct).ConfigureAwait(false)) return null;
            var f = await OcrImageCoreAsync(tmp, 0, 0, upscaled: false, ct).ConfigureAwait(false);
            if (f is null || f.Lines.Count == 0) return null;
            return string.Join(" ", f.Lines.OrderBy(l => l.Cy).ThenBy(l => l.X).Select(l => l.Text)).Trim();
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    private async Task<OcrFrame?> OcrImageCoreAsync(string pngPath, int origW, int origH, bool upscaled, CancellationToken ct)
    {
        // Windows OCR (Windows.Media.Ocr) projects only under Windows PowerShell 5.1, so shell the script
        // there via an encoded command (avoids execution-policy / param-binding friction — and we never
        // pass -ExecutionPolicy Bypass).
        string script = await File.ReadAllTextAsync(_ocrScript, ct).ConfigureAwait(false);
        string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(b64);
        psi.Environment["OCR_IMG"] = Path.GetFullPath(pngPath);

        using var p = new Process { StartInfo = psi };
        try { if (!p.Start()) { _log.Warn("Vision", "failed to start powershell.exe for OCR"); return null; } }
        catch (Exception ex) { _log.Warn("Vision", "OCR launch error: " + ex.Message); return null; }

        // Read both streams to completion CONCURRENTLY, then await exit. ReadToEndAsync avoids the
        // BeginOutputReadLine/WaitForExitAsync flush race that silently dropped the (single, ~11 KB) JSON
        // line when OCR finished faster than the async handler drained; reading both at once never deadlocks.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(40));
        var outTask = p.StandardOutput.ReadToEndAsync(cts.Token);
        var errTask = p.StandardError.ReadToEndAsync(cts.Token);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } _log.Warn("Vision", "OCR timed out."); return null; }
        string stdout = await outTask.ConfigureAwait(false);
        string stderr = await errTask.ConfigureAwait(false);

        // ocr.ps1 emits exactly one compact JSON object on stdout. Carve it out from the first '{' to the
        // last '}' — robust to a leading BOM, the progress banner, or trailing newlines that broke a
        // per-line StartsWith/EndsWith match.
        int open = stdout.IndexOf('{');
        int close = stdout.LastIndexOf('}');
        string? jsonLine = (open >= 0 && close > open) ? stdout.Substring(open, close - open + 1) : null;
        var frame = OcrFrame.FromJson(jsonLine);
        if (frame is null)
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "vision_ocr_last.txt"), stdout); } catch { }
            _log.Warn("Vision", $"OCR produced no usable result (stdout {stdout.Length} chars, dumped to %TEMP%\\vision_ocr_last.txt). stderr: {stderr.Trim()}");
        }
        else if (upscaled && frame.Width > 0 && frame.Height > 0 && (frame.Width != origW || frame.Height != origH))
        {
            // Map the upscaled-space geometry back to the original capture frame (per-axis, exact).
            double fx = (double)origW / frame.Width, fy = (double)origH / frame.Height;
            foreach (var l in frame.Lines)
            {
                l.X = (int)Math.Round(l.X * fx); l.W = (int)Math.Round(l.W * fx); l.Cx = (int)Math.Round(l.Cx * fx);
                l.Y = (int)Math.Round(l.Y * fy); l.H = (int)Math.Round(l.H * fy); l.Cy = (int)Math.Round(l.Cy * fy);
            }
            _log.Info("Vision", $"OCR read {origW}x{origH} frame via {frame.Width}x{frame.Height} upscale ({frame.Lines.Count} line(s)) — sub-native OCR normalization.");
            frame.Width = origW; frame.Height = origH;
        }
        return frame;
    }
}
