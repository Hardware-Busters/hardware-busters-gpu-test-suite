using GpuSuite.Core.Diagnostics;
using GpuSuite.Calibration.Observation;
using GpuSuite.Calibration.Schema;
using EngineVision = GpuSuite.Engine.Vision;
using EngineAuto = GpuSuite.Engine.Automation;

namespace GpuSuite.Cli;

/// <summary>
/// The REAL calibration eye: wires the Engine's capture-card stack (CaptureCardGrabber +
/// ScreenReader/Windows.Media.Ocr) behind the Calibration plane's <see cref="IScreenObserver"/> and
/// <see cref="IOcrEngine"/>. It lives in the CLI composition root because the Calibration library
/// references Core ONLY (not the Engine) by design — so the real, hardware-touching implementation is
/// injected here. With these in place the <c>Gx10Reasoner</c> receives an ACTUAL screenshot (ImagePath)
/// plus real OCR, instead of scripted text.
///
/// Strictly READ-ONLY: it grabs frames and OCRs them; it drives nothing in the game. It captures whatever
/// the capture card currently shows (the operator preps the game/screen).
/// </summary>
internal sealed class CaptureCardObserver : IScreenObserver
{
    private readonly EngineVision.CaptureCardGrabber _grabber;
    private readonly string _shotDir;
    private readonly RunLogger _log;
    private readonly bool _guided;

    public CaptureCardObserver(EngineVision.CaptureCardGrabber grabber, string shotDir, RunLogger log, bool guided = false)
    {
        _grabber = grabber;
        _shotDir = shotDir;
        _log = log;
        _guided = guided;
        try { Directory.CreateDirectory(_shotDir); } catch { }
    }

    public async Task<CaptureFrame> ObserveAsync(string stepLabel, CancellationToken ct)
    {
        // GUIDED: the operator drives the game to each screen and the plane observes it — this is the honest
        // "nav driver" for live MULTI-screen calibration without breaking the sidecar rule (the LLM/plane drives
        // NOTHING; a human navigates). We block for Enter before capturing. Piped/EOF stdin (null) = just proceed,
        // so the path is testable headless; "s" + Enter skips a screen.
        if (_guided)
        {
            Console.WriteLine();
            Console.WriteLine($"  ▶ Navigate the game to screen: '{stepLabel}', then press Enter to capture  (type 's' + Enter to skip).");
            Console.Write("    > ");
            string? key = Console.ReadLine();
            if (key is not null && key.Trim().Equals("s", StringComparison.OrdinalIgnoreCase))
            {
                _log.Info("Calibrate", $"operator skipped screen '{stepLabel}'.");
                return new CaptureFrame { Label = stepLabel ?? "", ImagePath = "", GameIsForeground = false };
            }
        }

        string safe = string.Concat((stepLabel ?? "frame").Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        string png = Path.Combine(_shotDir, $"{safe}_{Guid.NewGuid():N}.png");
        // Full-resolution grab (scaleWidth 0): OCR wants full res; the reasoner downsizes for the model itself.
        bool ok = await _grabber.GrabAsync(png, warmupFrames: 8, ct, scaleWidth: 0).ConfigureAwait(false);
        if (!ok)
        {
            _log.Warn("Calibrate", $"capture-card grab failed for screen '{stepLabel}'.");
            return new CaptureFrame { Label = stepLabel ?? "", ImagePath = "", GameIsForeground = false };
        }
        // GameIsForeground = true: a live calibration assumes the operator has the target screen up (the
        // sidecar drives nothing). A real foreground gate would need the game's process; out of scope here.
        return new CaptureFrame { Label = stepLabel ?? "", ImagePath = png, GameIsForeground = true };
    }
}

/// <summary>
/// HANDS-OFF bot-driven calibration eye: between observations it injects each screen's executable Drive
/// sequence via the deterministic input engine (the ENGINE drives, NOT the LLM — the same separation the
/// benchmark bots use), then grabs. Screens with no Drive are observed as-is (author a Drive or use --guided).
/// In dry mode it LOGS the planned injection without touching the keyboard, so the wiring is offline-safe to test.
/// </summary>
internal sealed class BotDrivenObserver : IScreenObserver
{
    private readonly EngineVision.CaptureCardGrabber _grabber;
    private readonly string _shotDir;
    private readonly RunLogger _log;
    private readonly IReadOnlyDictionary<string, string> _driveFor;   // screenId → key sequence
    private readonly int? _pid;
    private readonly bool _dry;

    public BotDrivenObserver(EngineVision.CaptureCardGrabber grabber, string shotDir, RunLogger log,
        IReadOnlyDictionary<string, string> driveFor, int? pid, bool dry)
    {
        _grabber = grabber; _shotDir = shotDir; _log = log;
        _driveFor = driveFor; _pid = pid; _dry = dry;
        try { Directory.CreateDirectory(_shotDir); } catch { }
    }

    public async Task<CaptureFrame> ObserveAsync(string stepLabel, CancellationToken ct)
    {
        string drive = _driveFor.TryGetValue(stepLabel ?? "", out var d) ? (d ?? "") : "";
        if (!string.IsNullOrWhiteSpace(drive))
        {
            if (_dry)
            {
                _log.Info("Calibrate", $"[dry-drive] would inject to reach '{stepLabel}': {drive}");
            }
            else
            {
                _log.Info("Calibrate", $"driving to '{stepLabel}': {drive}");
                var actions = Program.ParseKeySequence(drive, pad: false);
                var engine = new EngineAuto.InputAutomationEngine(_log, inject: true) { TargetPid = _pid };
                var script = new EngineAuto.BotScript
                {
                    Id = $"calib-{stepLabel}", Loop = false,
                    InputDevice = EngineAuto.BotInputDevice.Keyboard, Actions = actions
                };
                await engine.RunAsync(script, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                await Task.Delay(800, ct).ConfigureAwait(false);   // settle before the grab
            }
        }
        else
        {
            _log.Info("Calibrate", $"no Drive for '{stepLabel}' — observing the current frame (author a Drive sequence or use --guided).");
        }

        string safe = string.Concat((stepLabel ?? "frame").Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        string png = Path.Combine(_shotDir, $"{safe}_{Guid.NewGuid():N}.png");
        bool ok = await _grabber.GrabAsync(png, warmupFrames: 8, ct, scaleWidth: 0).ConfigureAwait(false);
        if (!ok)
        {
            _log.Warn("Calibrate", $"capture-card grab failed for screen '{stepLabel}'.");
            return new CaptureFrame { Label = stepLabel ?? "", ImagePath = "", GameIsForeground = false };
        }
        return new CaptureFrame { Label = stepLabel ?? "", ImagePath = png, GameIsForeground = true };
    }
}

/// <summary>Real OCR for the calibration plane: OCRs the observer's PNG via the Engine ScreenReader and maps
/// the result (lines, in pixels) onto the Calibration plane's word/box model (whole words, normalized 0..1).</summary>
internal sealed class CaptureCardOcrEngine : IOcrEngine
{
    private readonly EngineVision.ScreenReader _reader;

    public CaptureCardOcrEngine(EngineVision.ScreenReader reader) => _reader = reader;

    public async Task<OcrFrame> ReadAsync(CaptureFrame frame, CancellationToken ct)
    {
        // A replay/stub frame may carry pre-supplied OCR — honor it (one code path).
        if (frame.SyntheticOcr is not null) return new OcrFrame { Words = frame.SyntheticOcr.ToList() };
        if (string.IsNullOrWhiteSpace(frame.ImagePath) || !File.Exists(frame.ImagePath)) return new OcrFrame();
        var ef = await _reader.OcrImageAsync(frame.ImagePath, ct).ConfigureAwait(false);
        return ToCalib(ef);
    }

    // The plane does not crop in this skeleton; a real region read would clip to the box first.
    public Task<OcrFrame> ReadRegionAsync(CaptureFrame frame, Box region, CancellationToken ct) => ReadAsync(frame, ct);

    /// <summary>Map the Engine's OCR (lines + pixel boxes) to the Calibration OcrFrame: each line is split into
    /// whole WORDS (so whole-word anchor matching works — PLAY ⊄ gameplay), each carrying the line's box, normalized.</summary>
    internal static OcrFrame ToCalib(EngineVision.OcrFrame? ef)
    {
        var words = new List<OcrWord>();
        if (ef is not null)
        {
            double w = Math.Max(1, ef.Width), h = Math.Max(1, ef.Height);
            foreach (var line in ef.Lines)
            {
                var box = new Box { Xn = line.X / w, Yn = line.Y / h, Wn = line.W / w, Hn = line.H / h };
                foreach (var tok in (line.Text ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    words.Add(new OcrWord { Text = tok, Box = box, Confidence = 0.9 });
            }
        }
        return new OcrFrame { Words = words };
    }
}
