using System.Text.RegularExpressions;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Remote;
using GpuSuite.Engine.Vision;

namespace GpuSuite.Engine.Automation;

/// <summary>One in-world movement primitive the navigator can choose.</summary>
public enum MoveAction
{
    Forward, Back, StrafeLeft, StrafeRight, TurnLeft, TurnRight, Jump, Stop, Wait
}

/// <summary>One decision: the chosen <see cref="MoveAction"/> (null when DONE or unparseable), the DONE flag,
/// and the raw model text (for the timeline log).</summary>
public sealed record MoveStep(MoveAction? Action, bool Done, string Raw);

/// <summary>
/// TIER-1 in-world navigator — the analog of <see cref="VisionNavSupervisor"/>, but for IN-GAME MOVEMENT
/// rather than menu actions. It streams a downscaled capture-card frame to the GX10's vision model over the
/// LAN and asks for the SINGLE next movement primitive toward a plain-language goal ("explore the open area
/// ahead, don't grind walls"). Perception runs OFF-BENCH on the GX10's own GPU, so the local benchmark GPU is
/// never touched — the number stays clean. Any failure (box down, grab fails, unparseable reply) returns null,
/// so the caller falls back to the Tier-0 capture-card stuck-reflex (<c>SmartTraverse</c>) — a flaky box can
/// never wedge or false-pass a run.
///
/// Pairs with discover-then-replay (<see cref="RecordedRoute"/>): drive during the trimmed WARM-UP to discover
/// a good traversal, record the route, then replay it DETERMINISTICALLY in the measured window — so the GX10
/// never touches the timed window and the run stays reproducible.
/// </summary>
public sealed class Gx10InWorldNavigator
{
    private readonly Gx10Client _client;
    private readonly string _model;
    private readonly CaptureCardGrabber _grabber;
    private readonly int _scaleWidth;
    private readonly RunLogger _log;
    private readonly int _timeoutMs;

    public Gx10InWorldNavigator(string endpoint, string model, CaptureCardGrabber grabber, int scaleWidth, RunLogger log, int timeoutMs = 60_000)
    {
        _client = new Gx10Client(endpoint, log);
        _model = string.IsNullOrWhiteSpace(model) ? "qwen2.5vl:7b" : model;
        _grabber = grabber;
        _scaleWidth = scaleWidth > 0 ? scaleWidth : 1024;
        _log = log;
        _timeoutMs = timeoutMs;
    }

    public string Model => _model;
    public string Endpoint => _client.Endpoint;

    /// <summary>Stuck-reflex tuning (read by the engine's continuous-hold driver). <see cref="StuckThreshold"/> is the
    /// mean scene-change below which a held translation counts as wall-grinding; 0 disables the reflex. The driver
    /// samples motion no more often than <see cref="StuckCheckMs"/> and forces a turn after <see cref="StuckTriggerCount"/>
    /// consecutive low samples. Defaults are sane; the run path overrides <see cref="StuckThreshold"/> from config.</summary>
    public double StuckThreshold { get; set; } = 0.5;
    public int StuckTriggerCount { get; set; } = 2;
    public int StuckCheckMs { get; set; } = 3500;

    /// <summary>Sample in-world MOTION off the capture card (ffmpeg scdet, CPU/off-bench — no benchmark-GPU cost) so
    /// the engine's stuck-reflex can tell real translation-through-space from a wall jam. −1 when unavailable.</summary>
    public Task<double> SampleMotionAsync(CancellationToken ct, int frames = 10, int scaleWidth = 320)
        => _grabber.SampleMotionAsync(frames, scaleWidth, ct);

    /// <summary>A 1×1 PNG to force the vision model into VRAM without a capture-card grab.</summary>
    private const string WarmFrameB64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    /// <summary>Force the vision model into VRAM with one throwaway decision so the one-time COLD load (~40s on the
    /// GX10) is paid HERE — during warm-up / before the timed window — instead of stealing the first ~40s of driving.
    /// Best-effort: any failure is swallowed (the first real decision just pays the load instead). Returns load ms.</summary>
    public async Task<long> WarmupAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await DecideAsync(WarmFrameB64, "warm up", ct).ConfigureAwait(false);
            _log.Info("Bot", $"Gx10InWorld: vision model warmed in {sw.ElapsedMilliseconds} ms — driving decisions now stay warm (~12s, not ~40s cold).");
        }
        catch (Exception ex) { _log.Trace("Bot", $"Gx10InWorld warmup skipped: {ex.Message}"); }
        return sw.ElapsedMilliseconds;
    }

    /// <summary>Grab a downscaled capture-card frame and ask the GX10 for the next movement toward
    /// <paramref name="goal"/>. Null on ANY failure (grab / HTTP / parse) — the caller then falls back to the
    /// Tier-0 reflex, so a flaky box never wedges or false-passes.</summary>
    public Task<MoveStep?> NextMoveAsync(string goal, CancellationToken ct) => NextMoveAsync(goal, null, ct);

    /// <summary>As <see cref="NextMoveAsync(string,CancellationToken)"/> but folds an extra <paramref name="hint"/>
    /// (e.g. the stuck-reflex's "you're grinding a wall, TURN") into the prompt so the model cooperates.</summary>
    public async Task<MoveStep?> NextMoveAsync(string goal, string? hint, CancellationToken ct)
    {
        var png = Path.Combine(Path.GetTempPath(), $"inworld_{Guid.NewGuid():N}.png");
        try
        {
            if (!await _grabber.GrabAsync(png, warmupFrames: 4, ct, scaleWidth: _scaleWidth).ConfigureAwait(false))
            { _log.Trace("Bot", "Gx10InWorld: capture-card grab failed."); return null; }
            string b64 = Convert.ToBase64String(await File.ReadAllBytesAsync(png, ct).ConfigureAwait(false));
            return await DecideAsync(b64, goal, hint, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.Trace("Bot", $"Gx10InWorld error: {ex.Message}"); return null; }
        finally { try { if (File.Exists(png)) File.Delete(png); } catch { } }
    }

    public Task<MoveStep?> DecideAsync(string imageBase64, string goal, CancellationToken ct) => DecideAsync(imageBase64, goal, null, ct);

    /// <summary>Ask the GX10 for one movement decision given an already-encoded frame (shared by the live grab
    /// path and the smoke-test). <paramref name="hint"/> is appended to the prompt when present. Null on no
    /// response / unparseable.</summary>
    public async Task<MoveStep?> DecideAsync(string imageBase64, string goal, string? hint, CancellationToken ct)
    {
        var options = new Dictionary<string, object> { ["temperature"] = 0.1 };
        var text = await _client.GenerateAsync(_model, BuildPrompt(goal, hint), new[] { imageBase64 }, options, jsonFormat: false, _timeoutMs, ct).ConfigureAwait(false);
        if (text is null) { _log.Trace("Bot", "Gx10InWorld: no response from the box."); return null; }
        var (action, done) = Parse(text);
        _log.Info("Bot", $"Gx10InWorld ({_model}) → {(done ? "DONE" : action?.ToString() ?? "(none)")}  «{Compact(text)}»{(hint is null ? "" : "  [stuck-hint sent]")}");
        return new MoveStep(action, done, text.Trim());
    }

    /// <summary>The decision prompt (also used by the offline selftest). Asks for ONE movement token.</summary>
    public static string BuildPrompt(string goal) => BuildPrompt(goal, null);

    /// <summary>The decision prompt with an optional extra <paramref name="hint"/> line (stuck-reflex feedback).</summary>
    public static string BuildPrompt(string goal, string? hint) =>
        "You are steering a video-game character by LOOKING at the current on-screen view (a live game capture).\n" +
        "Decide the SINGLE next movement to make progress toward the goal, favouring OPEN, traversable space and\n" +
        "avoiding walls and obstacles.\n" +
        $"GOAL: {(string.IsNullOrWhiteSpace(goal) ? "explore the open area ahead; keep moving through traversable space; do not grind against walls" : goal)}\n" +
        (string.IsNullOrWhiteSpace(hint) ? "" : hint!.Trim() + "\n") +
        "Reply with EXACTLY ONE token from this list and nothing else:\n" +
        "  FORWARD                walk / run straight ahead\n" +
        "  BACK                   back away\n" +
        "  LEFT  RIGHT            strafe (side-step) left / right\n" +
        "  TURNLEFT  TURNRIGHT    rotate the camera/view left / right\n" +
        "  JUMP                   jump to clear a small obstacle or gap\n" +
        "  STOP                   hold position\n" +
        "  WAIT                   the view is a loading screen / cutscene / menu — do nothing, look again\n" +
        "  DONE                   the goal is clearly already achieved\n" +
        "Prefer FORWARD when open space is ahead; TURN when a wall or obstacle blocks the path.";

    /// <summary>Pull the first recognized movement token out of the model's reply (tolerant of stray words/
    /// punctuation). Handles "TURN LEFT" / "TURN RIGHT" written with a space as a turn, not a strafe.</summary>
    public static (MoveAction? action, bool done) Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, false);
        var u = text.ToUpperInvariant();
        // Multi-word turns first, so "TURN RIGHT" isn't read as the "RIGHT" strafe token.
        if (Regex.IsMatch(u, @"\bTURN\s*RIGHT\b")) return (MoveAction.TurnRight, false);
        if (Regex.IsMatch(u, @"\bTURN\s*LEFT\b")) return (MoveAction.TurnLeft, false);
        foreach (var t in Regex.Matches(u, "[A-Z]+").Select(m => m.Value))
        {
            switch (t)
            {
                case "DONE": case "ARRIVED": return (null, true);
                case "FORWARD": case "FORWARDS": case "AHEAD": case "ADVANCE": return (MoveAction.Forward, false);
                case "BACK": case "BACKWARD": case "BACKWARDS": case "BACKUP": case "RETREAT": return (MoveAction.Back, false);
                case "TURNLEFT": return (MoveAction.TurnLeft, false);
                case "TURNRIGHT": return (MoveAction.TurnRight, false);
                case "LEFT": return (MoveAction.StrafeLeft, false);
                case "RIGHT": return (MoveAction.StrafeRight, false);
                case "JUMP": case "HOP": return (MoveAction.Jump, false);
                case "STOP": case "HOLD": case "STAY": return (MoveAction.Stop, false);
                case "WAIT": case "LOADING": case "CUTSCENE": return (MoveAction.Wait, false);
            }
        }
        return (null, false);
    }

    private static string Compact(string s) => s.Trim().Replace("\r", " ").Replace("\n", " ");
}
