namespace GpuSuite.Calibration.Schema;

/// <summary>A rectangle on the capture frame as 0..1 fractions of width/height (resolution-independent).</summary>
public sealed class Box
{
    public double Xn { get; set; }
    public double Yn { get; set; }
    public double Wn { get; set; }
    public double Hn { get; set; }
}

/// <summary>One literal word read by the OCR engine, with its location and recognizer confidence.</summary>
public sealed class OcrWord
{
    public string Text { get; set; } = "";
    public Box? Box { get; set; }
    /// <summary>Recognizer confidence in [0,1].</summary>
    public double Confidence { get; set; }
}

/// <summary>
/// A control detected on a screen (a dropdown, toggle, slider, button, or value). Carries its own
/// confidence and the sources that corroborated it (e.g. ["ocr","vision"]) — never an action to take.
/// </summary>
public sealed class DetectedControl
{
    public string Label { get; set; } = "";
    /// <summary>"dropdown" | "toggle" | "slider" | "button" | "value" | "unknown".</summary>
    public string Type { get; set; } = "unknown";
    /// <summary>The control's currently-shown value, if read (e.g. "Quality", "On", "3840x2160").</summary>
    public string? Value { get; set; }
    public Box? Box { get; set; }
    public Confidence Confidence { get; set; } = new();
    /// <summary>Which detectors agreed this control is here, e.g. ["ocr","vision"].</summary>
    public List<string> Sources { get; set; } = new();
}

/// <summary>
/// The evidence behind any observation or recommendation: the frames, OCR, and prompt id it was
/// derived from. Mandatory for explainability (P6) — a reviewer can always reconstruct "why".
/// </summary>
public sealed class Evidence
{
    /// <summary>Screenshot file paths (relative to the game's screenshots/ folder).</summary>
    public List<string> Screenshots { get; set; } = new();
    /// <summary>OCR snippets / words that supported the claim.</summary>
    public List<string> Ocr { get; set; } = new();
    /// <summary>The GX10 prompt id, when an LLM produced the claim.</summary>
    public string? PromptId { get; set; }
    public string? Note { get; set; }

    public static Evidence None => new();
    public static Evidence FromShots(params string[] shots) => new() { Screenshots = shots.ToList() };
}

/// <summary>
/// The full environment fingerprint stamped on every versioned calibration artifact, so drift is
/// attributable and the engine can refuse a map calibrated under a materially different environment.
/// </summary>
public sealed class EnvFingerprint
{
    public string GameVersion { get; set; } = "";
    public string SuiteVersion { get; set; } = "";
    public string Gpu { get; set; } = "";
    public string DriverVersion { get; set; } = "";
    public string WindowsVersion { get; set; } = "";
    public string Resolution { get; set; } = "";
    public string Preset { get; set; } = "";
    public string Language { get; set; } = "";
    /// <summary>ISO-8601 capture timestamp (string so the record is stable/serializable).</summary>
    public string Timestamp { get; set; } = "";

    /// <summary>A short, filesystem-safe key for versioning artifacts: gameVersion_gpu_driver.</summary>
    public string Key()
    {
        string Slug(string s) => new string((s ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-').ToArray());
        var parts = new[] { GameVersion, Gpu, DriverVersion }.Select(Slug).Where(p => p.Length > 0);
        var key = string.Join("_", parts);
        return key.Length > 0 ? key : "v0";
    }
}
