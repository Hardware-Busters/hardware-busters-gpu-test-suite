using System.Text.Json;
using System.Text.Json.Serialization;

namespace GpuSuite.Engine.Automation;

/// <summary>One primitive in a recorded in-world movement route.</summary>
public enum RouteStepKind
{
    /// <summary>Hold "run forward" for <see cref="RouteStep.DurationMs"/> (pad left-stick up / W key down).</summary>
    Forward,
    /// <summary>Hold "move back" for <see cref="RouteStep.DurationMs"/> (pad left-stick down / S key).</summary>
    Back,
    /// <summary>Hold "strafe left" for <see cref="RouteStep.DurationMs"/> (pad left-stick left / A key).</summary>
    StrafeLeft,
    /// <summary>Hold "strafe right" for <see cref="RouteStep.DurationMs"/> (pad left-stick right / D key).</summary>
    StrafeRight,
    /// <summary>Turn the camera/character <see cref="RouteStep.Deg"/>° (sign = direction). The sweep duration is
    /// derived from the degrees exactly as <c>InputAutomationEngine.TurnAsync</c> clamps it.</summary>
    Turn,
    /// <summary>Jump (a brief Space / pad-A tap).</summary>
    Jump,
    /// <summary>Idle dwell for <see cref="RouteStep.DurationMs"/> (no movement) — a deliberate pause in the path.</summary>
    Wait
}

/// <summary>A single timed movement primitive (forward-hold, turn, or dwell).</summary>
public sealed class RouteStep
{
    public RouteStepKind Kind { get; set; }
    /// <summary>Forward/Wait: hold or dwell milliseconds. Turn: unused (the sweep comes from <see cref="Deg"/>).</summary>
    public int DurationMs { get; set; }
    /// <summary>Turn: signed degrees (sign = direction). 0 for Forward/Wait.</summary>
    public int Deg { get; set; }

    public static RouteStep Fwd(int ms) => new() { Kind = RouteStepKind.Forward, DurationMs = ms };
    public static RouteStep BackStep(int ms) => new() { Kind = RouteStepKind.Back, DurationMs = ms };
    public static RouteStep StrafeL(int ms) => new() { Kind = RouteStepKind.StrafeLeft, DurationMs = ms };
    public static RouteStep StrafeR(int ms) => new() { Kind = RouteStepKind.StrafeRight, DurationMs = ms };
    public static RouteStep TurnBy(int deg) => new() { Kind = RouteStepKind.Turn, Deg = deg };
    public static RouteStep JumpStep(int ms = 80) => new() { Kind = RouteStepKind.Jump, DurationMs = ms };
    public static RouteStep Dwell(int ms) => new() { Kind = RouteStepKind.Wait, DurationMs = ms };
}

/// <summary>
/// A previously-RECORDED in-world traversal route: the ordered forward-holds and unstick-turns a SmartTraverse
/// "discovery" pass emitted while exploring real open space (sensing motion off the capture card, turning to break
/// wall-jams). It is the REPRODUCIBLE half of <b>discover-then-replay</b>: a smart route is discovered ONCE (in the
/// trimmed warm-up, where the capture-card motion sensor / GX10 may drive), serialized here, then REPLAYED bit-for-bit
/// in the measured window by <see cref="BotActionType.ReplayRoute"/> — so the timed window traces the smart (non-idle,
/// non-wall-stuck) path with run-to-run-stable motion and NO sensor or GX10 in the loop. Pure data + JSON; no GPU.
/// </summary>
public sealed class RecordedRoute
{
    public int Version { get; set; } = 1;
    /// <summary>Free-text provenance (e.g. "RE Requiem open-corridor discovery, 2026-06-30").</summary>
    public string Note { get; set; } = "";
    /// <summary>"pad" | "keyboard" — which device recorded it (informational; replay follows the live script's device).</summary>
    public string RecordedDevice { get; set; } = "";
    public List<RouteStep> Steps { get; set; } = new();

    /// <summary>Estimated total duration (ms): forward/wait holds plus each turn's clamped sweep — matches the engine's
    /// <c>TurnAsync</c> clamp of <c>|deg|*7</c> into [150, 900] ms, so a measured window can be sized against it.</summary>
    public int TotalMs => Steps.Sum(s => s.Kind == RouteStepKind.Turn
        ? Math.Clamp(Math.Abs(s.Deg) * 7, 150, 900)
        : s.DurationMs);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }   // serialize Kind as "Forward"/"Turn"/"Wait", not an int
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    /// <summary>Parse a route from JSON. Null on any malformed input (the caller skips rather than wedging a run).</summary>
    public static RecordedRoute? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<RecordedRoute>(json, JsonOpts); }
        catch { return null; }
    }

    /// <summary>Load a route from a file. Null if it's missing or unparseable (replay then no-ops with a warning).</summary>
    public static RecordedRoute? Load(string path)
    {
        try { return File.Exists(path) ? FromJson(File.ReadAllText(path)) : null; }
        catch { return null; }
    }

    /// <summary>Write the route to a file (creating the directory). Throws only on a genuine I/O failure.</summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJson());
    }
}
