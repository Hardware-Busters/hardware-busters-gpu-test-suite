namespace GpuSuite.Calibration.Schema;

/// <summary>One screen in the per-game menu graph: its name, the controls on it, and a reference shot.</summary>
public sealed class MenuNode
{
    /// <summary>Stable screen id, e.g. "main_menu", "graphics_settings", "benchmark_launcher".</summary>
    public string Id { get; set; } = "";
    /// <summary>The GX10's semantic description of the screen (advisory; recorded for the reviewer).</summary>
    public string SemanticDescription { get; set; } = "";
    public List<DetectedControl> Controls { get; set; } = new();
    /// <summary>Screenshot path (relative to the game's screenshots/ folder).</summary>
    public string? Screenshot { get; set; }
    public Confidence Confidence { get; set; } = new();
}

/// <summary>A directed transition between two screens: the input that produced it and whether it reproduced.</summary>
public sealed class MenuEdge
{
    public string From { get; set; } = "";
    /// <summary>The input that drives this transition, e.g. "Down x3 + A", "PressUntilText 'RACE'".</summary>
    public string Input { get; set; } = "";
    public string To { get; set; } = "";
    /// <summary>Did the same input reproduce the same transition on re-observation?</summary>
    public bool Reproducible { get; set; }
    public Confidence Confidence { get; set; } = new();
}

/// <summary>
/// The per-game menu map — the canonical calibration artifact. Nodes are named screens, edges are input
/// transitions. A bot route is a path through this graph; drift is a diff of two graphs; route validation
/// is checking a path still exists. Versioned by <see cref="Env"/>.
/// </summary>
public sealed class MenuGraph
{
    public EnvFingerprint Env { get; set; } = new();
    public List<MenuNode> Nodes { get; set; } = new();
    public List<MenuEdge> Edges { get; set; } = new();
    /// <summary>Derived version tag (Env.Key()) used in the persisted filename.</summary>
    public string Version { get; set; } = "";

    public MenuNode? FindNode(string id) => Nodes.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The per-screen unit of drift comparison: the literal OCR, the detected controls, the semantic
/// description, and a reference shot. Two snapshots of the same screen across versions are what the
/// Layout Comparison Engine diffs.
/// </summary>
public sealed class LayoutSnapshot
{
    public string Screen { get; set; } = "";
    public EnvFingerprint Env { get; set; } = new();
    public List<OcrWord> Ocr { get; set; } = new();
    public List<DetectedControl> Controls { get; set; } = new();
    public string SemanticDescription { get; set; } = "";
    public string? Screenshot { get; set; }
}

/// <summary>One step in a route: the input to inject and the screen it is expected to land on.</summary>
public sealed class RouteStep
{
    public string Input { get; set; } = "";
    public string? ExpectScreen { get; set; }
    /// <summary>Optional whole-word OCR guard learned from a recording; the input remains graph-verifiable.</summary>
    public string? GuardAnchor { get; set; }
}

/// <summary>A validated path through the menu graph to a goal screen — what a bot route replays.</summary>
public sealed class RouteRecord
{
    public string Goal { get; set; } = "";
    public EnvFingerprint Env { get; set; } = new();
    public List<RouteStep> Steps { get; set; } = new();
    public string? LastValidated { get; set; }
    public Confidence Confidence { get; set; } = new();
}

/// <summary>
/// A DRAFT bot route synthesized/repaired by the Bot Calibration Engine. It is never executed by the
/// deterministic engine until a human promotes it into profiles/bots/ (principle P7). The draft state
/// is intrinsic — there is no "live" flag here on purpose.
/// </summary>
public sealed class BotDraft
{
    public string Goal { get; set; } = "";
    public EnvFingerprint Env { get; set; } = new();
    public List<RouteStep> Steps { get; set; } = new();
    public string Rationale { get; set; } = "";
    /// <summary>"graph" | "recording" | "repair".</summary>
    public string CreatedFrom { get; set; } = "graph";
    /// <summary>For repairs: a human-readable diff against the stale route.</summary>
    public string? DiffAgainst { get; set; }
    public Confidence Confidence { get; set; } = new();
}

/// <summary>One human-driven navigation observation used to calibrate a cleaner graph-aligned route.</summary>
public sealed class RecordedNavStep
{
    public string Input { get; set; } = "";
    public string ObservedScreen { get; set; } = "";
    public List<string> OcrAnchors { get; set; } = new();
}

/// <summary>A human navigation recording; Phase 3 aligns it to known graph edges and emits a draft only.</summary>
public sealed class NavRecording
{
    public string StartScreen { get; set; } = "";
    public string Goal { get; set; } = "";
    public List<RecordedNavStep> Steps { get; set; } = new();
    public EnvFingerprint Env { get; set; } = new();
}

/// <summary>The game's profile-of-record in the calibration database (game.json).</summary>
public sealed class GameCalibration
{
    /// <summary>Stable game id (matches the suite's GameProfile.Id where possible), e.g. "cyberpunk-2077".</summary>
    public string Game { get; set; } = "";
    public string Name { get; set; } = "";
    public EnvFingerprint Env { get; set; } = new();
    /// <summary>Reference to the current menu graph file under menus/.</summary>
    public string? CurrentGraphRef { get; set; }
    public List<string> Screens { get; set; } = new();
    public string? Notes { get; set; }
}

/// <summary>A recorded validation outcome (route / gameplay / settings) for the history.</summary>
public sealed class ValidationEvent
{
    public string Time { get; set; } = "";
    /// <summary>"route" | "gameplay" | "settings".</summary>
    public string Kind { get; set; } = "";
    /// <summary>"pass" | "warn" | "fail".</summary>
    public string Verdict { get; set; } = "";
    public Confidence Confidence { get; set; } = new();
    public string Rationale { get; set; } = "";
    public Evidence Evidence { get; set; } = new();
}

/// <summary>One change between a baseline and a current layout, with its downstream impact.</summary>
public sealed class LayoutChange
{
    public string Screen { get; set; } = "";
    /// <summary>What changed, e.g. "inserted control 'Path Tracing' above 'Frame Generation'".</summary>
    public string Change { get; set; } = "";
    /// <summary>The impact on routes/thresholds, e.g. "benchmark_launcher route shifted Down x7 → x8".</summary>
    public string Impact { get; set; } = "";
    public Confidence Confidence { get; set; } = new();
}

/// <summary>A recorded drift detection between two graph/layout versions, with the proposed fix reference.</summary>
public sealed class DriftEvent
{
    public string Time { get; set; } = "";
    public string? BaselineRef { get; set; }
    public string? CurrentRef { get; set; }
    public List<LayoutChange> Changes { get; set; } = new();
    /// <summary>Reference to the draft fix (e.g. a bots/…_draft_….json) — advisory, human-approved.</summary>
    public string? Recommendation { get; set; }
    public Confidence Confidence { get; set; } = new();
}

/// <summary>The deterministic result of comparing two layouts (narration is delegated to the LLM).</summary>
public sealed class LayoutDiff
{
    public List<LayoutChange> Changes { get; set; } = new();
    public bool AnyChange => Changes.Count > 0;
}
