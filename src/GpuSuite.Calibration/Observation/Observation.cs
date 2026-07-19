using GpuSuite.Calibration.Schema;

namespace GpuSuite.Calibration.Observation;

/// <summary>
/// One captured frame to reason over. A real observer fills <see cref="ImagePath"/> from the capture
/// card with the game as the foreground window; the <see cref="SyntheticOcr"/> field lets a replay/stub
/// observer pre-supply OCR for offline pipeline proofs (the real OCR engine reads the image instead).
/// </summary>
public sealed class CaptureFrame
{
    /// <summary>The nav step / screen label this frame was grabbed for, e.g. "graphics_settings".</summary>
    public string Label { get; set; } = "";
    /// <summary>Path to the captured PNG (may be empty for a pure synthetic frame).</summary>
    public string ImagePath { get; set; } = "";
    /// <summary>True only when the GAME was the foreground window at capture (the contamination guard).</summary>
    public bool GameIsForeground { get; set; } = true;
    public int Width { get; set; }
    public int Height { get; set; }
    public string CapturedAtIso { get; set; } = "";
    /// <summary>
    /// Replay/stub observers pre-supply OCR here so the offline pipeline can run without capture hardware.
    /// Real observers leave this null and the OCR engine reads <see cref="ImagePath"/>. One code path.
    /// </summary>
    public IReadOnlyList<OcrWord>? SyntheticOcr { get; set; }
}

/// <summary>The literal-text read of a frame, with whole-word + substring lookup (the PLAY⊄gameplay fix).</summary>
public sealed class OcrFrame
{
    public List<OcrWord> Words { get; set; } = new();
    public string FullText => string.Join(" ", Words.Select(w => w.Text));

    /// <summary>Substring match (case-insensitive) — the legacy behavior; collision-prone for short anchors.</summary>
    public OcrWord? FindWord(string text)
        => Words.FirstOrDefault(w => w.Text.Contains(text, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whole-word match (case-insensitive) — robust for short anchors (PLAY must not match "gameplay").</summary>
    public OcrWord? FindWordWhole(string text)
        => Words.FirstOrDefault(w => string.Equals(w.Text.Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>A vision-model read of a frame: the desktop-contamination verdict + (optionally) a description.</summary>
public sealed class ContaminationVerdict
{
    public bool IsContaminated { get; set; }
    public string Reason { get; set; } = "";
    public Confidence Confidence { get; set; } = new();
}

public enum VisionRequestKind { Describe, Classify, LocateControls }

/// <summary>What the Vision Engine is being asked to do with a frame.</summary>
public sealed class VisionRequest
{
    public VisionRequestKind Kind { get; set; } = VisionRequestKind.Describe;
    public List<string> KnownScreens { get; set; } = new();
}

/// <summary>The structured output of the Vision Engine. Describe-and-locate only — never an action.</summary>
public sealed class VisionObservation
{
    public SemanticDescription? Description { get; set; }
    public List<DetectedControl> Controls { get; set; } = new();
    public ContaminationVerdict Contamination { get; set; } = new();
    public string Model { get; set; } = "";
    public double LatencyMs { get; set; }
    public Confidence Confidence { get; set; } = new();
}

/// <summary>
/// The capture eye. Acquires a frame for a nav step with the foreground gate asserted. The library ships
/// a stub; the CLI (composition root) supplies the real capture-card-backed observer.
/// </summary>
public interface IScreenObserver
{
    Task<CaptureFrame> ObserveAsync(string stepLabel, CancellationToken ct);
}

/// <summary>The literal eye (module 3). Deterministic, cheap, runs first — escalation, not default, is the model.</summary>
public interface IOcrEngine
{
    Task<OcrFrame> ReadAsync(CaptureFrame frame, CancellationToken ct);
    Task<OcrFrame> ReadRegionAsync(CaptureFrame frame, Box region, CancellationToken ct);
}

/// <summary>The semantic eye (module 2). Describe / locate-controls / contamination — brokers the vision model.</summary>
public interface IVisionEngine
{
    Task<VisionObservation> ObserveAsync(CaptureFrame frame, VisionRequest request, CancellationToken ct);
    Task<IReadOnlyList<DetectedControl>> DetectControlsAsync(CaptureFrame frame, CancellationToken ct);
    Task<ContaminationVerdict> IsContaminatedAsync(CaptureFrame frame, CancellationToken ct);
}

/// <summary>
/// A deterministic, hardware-free observer for the Phase-2 skeleton and offline pipeline proofs. It
/// replays a scripted set of synthetic screens (supplied by a game plugin) so the full
/// observe→OCR→graph→persist→report path can be exercised without launching a game or owning a capture
/// card. The real engine-backed observer (wrapping CaptureCardGrabber/ScreenReader) drops in behind the
/// same <see cref="IScreenObserver"/> interface.
/// </summary>
public sealed class StubScreenObserver : IScreenObserver
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<OcrWord>> _script;
    public StubScreenObserver(IReadOnlyDictionary<string, IReadOnlyList<OcrWord>> screenScript) => _script = screenScript;

    public Task<CaptureFrame> ObserveAsync(string stepLabel, CancellationToken ct)
    {
        _script.TryGetValue(stepLabel, out var words);
        var frame = new CaptureFrame
        {
            Label = stepLabel,
            ImagePath = "",
            GameIsForeground = true,
            Width = 3840,
            Height = 2160,
            CapturedAtIso = "",                       // stamped by the caller (scripts can't read the clock)
            SyntheticOcr = words ?? Array.Empty<OcrWord>()
        };
        return Task.FromResult(frame);
    }
}

/// <summary>
/// OCR engine for the skeleton. Returns the frame's pre-supplied synthetic OCR when present (stub path);
/// a real implementation would invoke the capture-card OCR over <see cref="CaptureFrame.ImagePath"/>.
/// </summary>
public sealed class StubOcrEngine : IOcrEngine
{
    public Task<OcrFrame> ReadAsync(CaptureFrame frame, CancellationToken ct)
        => Task.FromResult(new OcrFrame { Words = (frame.SyntheticOcr ?? Array.Empty<OcrWord>()).ToList() });

    public Task<OcrFrame> ReadRegionAsync(CaptureFrame frame, Box region, CancellationToken ct)
        => ReadAsync(frame, ct);   // the stub does not crop; a real OCR engine would honor the region
}
