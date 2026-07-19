using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GpuSuite.Core.Config;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Remote;
using GpuSuite.Core.Security;
using GpuSuite.Engine.Vision;

namespace GpuSuite.Engine.Automation;

/// <summary>
/// A MULTIMODAL nav driver: it LOOKS at the captured screen (Qwen2.5-VL or similar, via Ollama) and goal-seeks the
/// next menu/desktop action — the robust, self-adapting replacement for the brittle OCR-signature graph on the
/// fragile cold-launch→in-world path of the no-built-in-benchmark games. Each <see cref="NextStepAsync"/> grabs a
/// fresh, DOWNSCALED frame off the capture card and asks the model for one device-independent <see cref="NavAction"/>
/// (or DONE when the goal state is on screen).
///
/// Runs ON THE GPU — which is free because no benchmark renders during menus — so it is fast and sees the real UI
/// (robust to our own RTSS OSD and to layout changes a text-OCR graph trips over). The engine calls
/// <see cref="UnloadAsync"/> at the nav→measured-route hand-off so the model leaves VRAM and the benchmark owns the
/// GPU 100%; the in-game measured motion then runs as a FIXED deterministic route (no LLM), keeping it reproducible.
/// Any failure (server down, grab fails, timeout, unparseable reply) returns null so the engine can wait + re-observe
/// or abort the run — a flaky model can never wedge or falsely pass a benchmark.
/// </summary>
public sealed class VisionNavSupervisor : INavSupervisor, IVisionNavigator
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private readonly string _endpoint;
    private readonly string _model;
    private readonly CaptureCardGrabber _grabber;
    private readonly int _scaleWidth;
    private readonly bool _gpu;
    private readonly RunLogger _log;
    private readonly VisionApiProvider _provider;
    private readonly string? _apiKey;
    private readonly string _effort;

    public VisionNavSupervisor(string endpoint, string model, CaptureCardGrabber grabber, int scaleWidth, bool gpu, RunLogger log,
                               VisionApiProvider provider = VisionApiProvider.Ollama, string? apiKey = null, string? effort = null)
    {
        _endpoint = (string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434" : endpoint).TrimEnd('/');
        _model = string.IsNullOrWhiteSpace(model) ? "qwen2.5vl:7b" : model;
        _grabber = grabber;
        _scaleWidth = scaleWidth > 0 ? scaleWidth : 1344;
        _gpu = gpu;
        _log = log;
        _provider = provider;
        _apiKey = apiKey;
        _effort = NormalizeEffort(effort);
    }

    public string Model => _model;
    public bool Gpu => _gpu;

    /// <summary>
    /// Decide whether the vision model runs on the GPU. "on"/"off" force it; "auto" (default): a REMOTE endpoint
    /// (e.g. the lab GX10 / GB10 box) runs on its OWN GPU so the bench is never burdened → GPU; otherwise GPU only
    /// if the LOCAL device GPU has at least <paramref name="minGpuGb"/> GB VRAM (enough to hold the model alongside
    /// a game menu), else CPU (num_gpu=0) so a small card (6 GB) never thrashes. Menus aren't timed, so CPU is fine;
    /// for full speed on a small card, run the model on the remote endpoint instead. Logs the decision + reason.
    /// </summary>
    public static bool ResolvePlacement(string? endpoint, string? mode, int minGpuGb, RunLogger log,
                                        string? fallbackEndpoint, out string effectiveEndpoint)
    {
        effectiveEndpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434" : endpoint.TrimEnd('/');
        var m = (mode ?? "auto").Trim().ToLowerInvariant();
        if (m == "on") { log.Info("Bot", "Vision LLM placement: GPU (navSupervisorVisionGpu=on)."); return true; }
        if (m == "off") { log.Info("Bot", "Vision LLM placement: CPU / num_gpu=0 (navSupervisorVisionGpu=off)."); return false; }
        if (IsRemoteEndpoint(effectiveEndpoint)) { log.Info("Bot", $"Vision LLM placement: GPU on the REMOTE endpoint {effectiveEndpoint} — off-bench, safe for ANY local card (incl. 6 GB)."); return true; }
        int min = minGpuGb > 0 ? minGpuGb : 10;
        int vramGb = DetectLocalGpuVramGb(log);
        if (vramGb >= min) { log.Info("Bot", $"Vision LLM placement: GPU — local VRAM {vramGb} GB >= {min} GB threshold (unloaded before the benchmark window)."); return true; }
        // Local card below threshold (e.g. 6 GB): prefer a configured REMOTE fallback (the GX10) over slow local CPU.
        if (IsRemoteEndpoint(fallbackEndpoint))
        {
            effectiveEndpoint = fallbackEndpoint!.TrimEnd('/');
            log.Info("Bot", $"Vision LLM placement: local VRAM {(vramGb > 0 ? vramGb + " GB" : "unknown")} < {min} GB → routing nav to the REMOTE fallback {effectiveEndpoint} (off-bench GPU; the 6 GB-card path) instead of slow local CPU.");
            return true;
        }
        log.Info("Bot", $"Vision LLM placement: CPU / num_gpu=0 — local VRAM {(vramGb > 0 ? vramGb + " GB" : "unknown")} < {min} GB; runs on CPU so it never contends with the game menu. Set navSupervisorRemoteFallbackEndpoint to a remote box (e.g. the GX10) for full-speed nav on a small card.");
        return false;
    }

    /// <summary>Where the vision model ended up running, for logging + the App's status readout.</summary>
    public enum VisionComputeSource { LocalGpu, LocalCpu, Gx10, CustomLocal, OpenAi, Anthropic }
    public enum VisionApiProvider { Ollama, OpenAi, Anthropic }
    public sealed record VisionBackend(VisionComputeSource Source, VisionApiProvider Provider, string Endpoint, string Model, string? ApiKey, string Effort, bool Gpu);

    /// <summary>Resolve the configured vision provider. Auto prefers the private GX10, then an explicitly
    /// provisioned cloud provider, then local compute. Cloud keys are read from Windows Credential Manager (with
    /// environment variables as a headless fallback), so settings.json and exported reports never contain a secret.</summary>
    public static VisionBackend ResolveBackend(SuiteConfig cfg, RunLogger log)
    {
        var compute = (cfg.VisionCompute ?? "auto").Trim().ToLowerInvariant();
        string gx10 = Gx10Client.Normalize(cfg.Gx10Endpoint);
        string? openAiKey = ApiKeyStore.ReadOrEnvironment("openai", "OPENAI_API_KEY");
        string? anthropicKey = ApiKeyStore.ReadOrEnvironment("anthropic", "ANTHROPIC_API_KEY");

        VisionBackend Cloud(VisionApiProvider provider) => provider == VisionApiProvider.OpenAi
            ? new(VisionComputeSource.OpenAi, provider, "https://api.openai.com/v1", cfg.OpenAiVisionModel, openAiKey, cfg.OpenAiVisionEffort, true)
            : new(VisionComputeSource.Anthropic, provider, "https://api.anthropic.com/v1", cfg.AnthropicVisionModel, anthropicKey, cfg.AnthropicVisionEffort, true);

        if (compute == "custom")
        {
            string customEndpoint = Gx10Client.Normalize(cfg.CustomVisionEndpoint);
            if (!string.IsNullOrWhiteSpace(cfg.CustomVisionEndpoint) && Gx10Client.IsReachable(customEndpoint))
            {
                string model = string.IsNullOrWhiteSpace(cfg.CustomVisionModel) ? cfg.NavSupervisorVisionModel : cfg.CustomVisionModel;
                log.Info("Bot", $"Vision compute: custom local Ollama server ({customEndpoint}), model '{model}' — off-bench.");
                return new(VisionComputeSource.CustomLocal, VisionApiProvider.Ollama, customEndpoint, model, null, "none", true);
            }
            log.Warn("Bot", "Vision compute: custom local server requested but it is not reachable → falling back safely.");
        }

        if (compute is "gx10" or "auto")
        {
            if (Gx10Client.IsReachable(gx10))
            {
                log.Info("Bot", $"Vision compute: Custom AI ({gx10}) — reachable; the vision model runs off-bench on its configured GPU.");
                return new(VisionComputeSource.Gx10, VisionApiProvider.Ollama, gx10, cfg.NavSupervisorVisionModel, null, "none", true);
            }
            if (compute == "gx10")
                log.Warn("Bot", $"Vision compute: Custom AI requested but {gx10} is NOT reachable → falling back safely.");
        }

        bool wantsOpenAi = compute == "openai" || (compute == "auto" && !string.IsNullOrWhiteSpace(openAiKey));
        if (wantsOpenAi && !string.IsNullOrWhiteSpace(openAiKey))
        {
            log.Info("Bot", "Vision compute: OpenAI API — configured from OPENAI_API_KEY; screen frames are sent only during menu navigation, never the measured window.");
            return Cloud(VisionApiProvider.OpenAi);
        }
        if (compute == "openai") log.Warn("Bot", "Vision compute: OpenAI requested but OPENAI_API_KEY is not set → falling back safely.");

        bool wantsAnthropic = compute == "anthropic" || (compute == "auto" && string.IsNullOrWhiteSpace(openAiKey) && !string.IsNullOrWhiteSpace(anthropicKey));
        if (wantsAnthropic && !string.IsNullOrWhiteSpace(anthropicKey))
        {
            log.Info("Bot", "Vision compute: Anthropic API — configured from ANTHROPIC_API_KEY; screen frames are sent only during menu navigation, never the measured window.");
            return Cloud(VisionApiProvider.Anthropic);
        }
        if (compute == "anthropic") log.Warn("Bot", "Vision compute: Anthropic requested but ANTHROPIC_API_KEY is not set → falling back safely.");

        bool gpu = ResolvePlacement(cfg.NavSupervisorEndpoint, cfg.NavSupervisorVisionGpu, cfg.NavSupervisorVisionMinGpuGb,
                                    log, cfg.NavSupervisorRemoteFallbackEndpoint, out var endpoint);
        bool routedRemote = IsRemoteEndpoint(endpoint);
        return new(routedRemote ? VisionComputeSource.Gx10 : (gpu ? VisionComputeSource.LocalGpu : VisionComputeSource.LocalCpu),
                   VisionApiProvider.Ollama, endpoint, cfg.NavSupervisorVisionModel, null, "none", gpu);
    }

    /// <summary>
    /// The HIGH-LEVEL placement selector the operator controls via <see cref="SuiteConfig.VisionCompute"/>
    /// ("auto" | "gpu" | "gx10"). The GX10 is chosen ONLY when a live probe confirms it is reachable:
    /// <list type="bullet">
    /// <item><b>gx10</b> — use the GX10 if reachable; if not, log a warning and fall back to the local GPU.</item>
    /// <item><b>auto</b> — use the GX10 if reachable, else the local GPU (silent, expected fallback).</item>
    /// <item><b>gpu</b> — never probe the box; place on the local card per <see cref="ResolvePlacement"/>.</item>
    /// </list>
    /// Returns whether the model runs on a GPU (true) vs CPU (false), and yields the effective endpoint + the
    /// source label. This is the single decision point the run path and the App share, so "GX10 if it exists,
    /// else GPU" behaves identically everywhere.
    /// </summary>
    public static bool ResolveCompute(SuiteConfig cfg, RunLogger log, out string endpoint, out VisionComputeSource source)
    {
        var backend = ResolveBackend(cfg, log);
        endpoint = backend.Endpoint;
        source = backend.Source;
        return backend.Gpu;
    }

    private static bool IsRemoteEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        try { var h = new Uri(endpoint).Host.ToLowerInvariant(); return h is not ("localhost" or "127.0.0.1" or "::1" or "0.0.0.0"); }
        catch { return false; }
    }

    /// <summary>Total VRAM (GB) of the largest local NVIDIA GPU via nvidia-smi; 0 if unreadable (→ auto picks CPU, the safe default).</summary>
    private static int DetectLocalGpuVramGb(RunLogger log)
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.total --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) return 0;
            string outp = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return 0; }
            int bestMb = 0;
            foreach (var line in outp.Split('\n'))
                if (int.TryParse(line.Trim(), out var mb)) bestMb = Math.Max(bestMb, mb);
            return bestMb > 0 ? (int)Math.Round(bestMb / 1024.0) : 0;
        }
        catch (Exception ex) { log.Trace("Bot", $"VRAM probe (nvidia-smi) failed: {ex.Message}"); return 0; }
    }

    /// <summary>Grab a downscaled screen, ask the vision model for the next action toward <paramref name="goal"/>.
    /// Null on any failure (grab/HTTP/parse) — the caller waits and re-observes (never a false pass).</summary>
    public async Task<VisionStep?> NextStepAsync(string goal, CancellationToken ct)
    {
        var png = Path.Combine(Path.GetTempPath(), $"visionnav_{Guid.NewGuid():N}.png");
        try
        {
            if (!await _grabber.GrabAsync(png, warmupFrames: 6, ct, scaleWidth: _scaleWidth).ConfigureAwait(false))
            { _log.Trace("Bot", "VisionNav: capture-card grab failed."); return null; }

            string b64 = Convert.ToBase64String(await File.ReadAllBytesAsync(png, ct).ConfigureAwait(false));
            // GPU by default; force CPU (num_gpu=0) on a small-VRAM bench so the model never contends with the game
            // menu's VRAM. A REMOTE endpoint (e.g. the GX10) runs on ITS own GPU — _gpu stays true there.
            var text = await RequestDecisionAsync(BuildPrompt(goal), b64, ct).ConfigureAwait(false);
            if (text is null) return null;
            var (action, done) = Parse(text);
            _log.Info("Bot", $"VisionNav ({_model}) → {(done ? "DONE" : action?.ToString() ?? "(no action)")}  «{Compact(text)}»");
            return new VisionStep(action, done, text.Trim());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.Trace("Bot", $"VisionNav error: {ex.Message}"); return null; }
        finally { try { if (File.Exists(png)) File.Delete(png); } catch { } }
    }

    /// <summary>Legacy LOST-fallback string API (<see cref="INavSupervisor"/>): gamepad-oriented mapping. The engine's
    /// vision-nav loop uses <see cref="NextStepAsync"/> with device-aware mapping instead; this exists only so a vision
    /// model can also serve as a graph supervisor. An explicit WAIT is preserved as an out-of-band decision so a
    /// legitimate long loading screen does not consume blind recoveries; null still means no usable decision.</summary>
    public async Task<string?> SuggestKeyAsync(IReadOnlyList<string> screenText, string goal, CancellationToken ct)
    {
        var step = await NextStepAsync(goal, ct).ConfigureAwait(false);
        return ToLegacyKey(step);
    }

    internal static string? ToLegacyKey(VisionStep? step)
    {
        if (step is null || step.Done || step.Action is null) return null;
        if (step.Action == NavAction.Wait) return NavSupervisorDecision.Wait;
        return step.Action switch
        {
            NavAction.Up => "Up", NavAction.Down => "Down", NavAction.Left => "Left", NavAction.Right => "Right",
            NavAction.Confirm => "A", NavAction.Back => "B", NavAction.TabLeft => "LB", NavAction.TabRight => "RB",
            NavAction.Start => "Start", _ => "A"
        };
    }

    /// <summary>Free the model from VRAM before the measured window: a keep_alive:0 generate unloads after responding.
    /// Best-effort — never throws (the worst case is Ollama's own idle timeout eventually unloading it).</summary>
    public async Task UnloadAsync(CancellationToken ct)
    {
        if (_provider != VisionApiProvider.Ollama)
        {
            _log.Info("Bot", "VisionNav: cloud request completed — no local model is resident, so there is nothing to unload before measurement.");
            return;
        }
        try
        {
            var body = JsonSerializer.Serialize(new { model = _model, prompt = "", keep_alive = 0, stream = false });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync($"{_endpoint}/api/generate", content, ct).ConfigureAwait(false);
            _log.Info("Bot", $"VisionNav: requested unload of '{_model}' off the GPU (keep_alive=0) — benchmark window owns the GPU now.");

            // LOCAL GPU only: the unload EVICTS ~6GB of model VRAM while the 4K game holds most of the card — the
            // game hitches for seconds and DROPS pad presses fired during the churn (Black Myth 2026-07-02: the
            // handoff pressed A 2ms after this request → 'Continue Journey' eaten on 4/5 runs; on GX10 vision the
            // unload is remote and this never happens). Block until Ollama reports the model gone, + a short settle
            // for the driver to finish paging the game's buffers back in, THEN hand off to the measured route.
            if (_endpoint.Contains("localhost", StringComparison.OrdinalIgnoreCase) || _endpoint.Contains("127.0.0.1"))
            {
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    try
                    {
                        using var ps = await Http.GetAsync($"{_endpoint}/api/ps", ct).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(await ps.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                        bool stillLoaded = doc.RootElement.TryGetProperty("models", out var models)
                            && models.ValueKind == JsonValueKind.Array && models.GetArrayLength() > 0;
                        if (!stillLoaded) break;
                    }
                    catch { break; }   // /api/ps unavailable — fall through to the settle delay
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
                await Task.Delay(3000, ct).ConfigureAwait(false);
                _log.Info("Bot", "VisionNav: local-GPU VRAM unload settled — safe to inject.");
            }
        }
        catch (OperationCanceledException) { /* shutting down — leave it to Ollama's idle timeout */ }
        catch (Exception ex) { _log.Trace("Bot", $"VisionNav unload error: {ex.Message}"); }
    }

    private async Task<string?> RequestDecisionAsync(string prompt, string imageB64, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _provider switch
        {
            VisionApiProvider.OpenAi => $"{_endpoint}/chat/completions",
            VisionApiProvider.Anthropic => $"{_endpoint}/messages",
            _ => $"{_endpoint}/api/generate"
        });

        if (_provider == VisionApiProvider.OpenAi)
        {
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            var body = new Dictionary<string, object>
            {
                ["model"] = _model,
                ["max_tokens"] = 32,
                ["temperature"] = 0.1,
                ["messages"] = new[]
                {
                    new { role = "user", content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new { type = "image_url", image_url = new { url = "data:image/png;base64," + imageB64 } }
                    }}
                }
            };
            if (_effort != "none" && SupportsOpenAiEffort(_model)) body["reasoning_effort"] = _effort;
            else if (_effort != "none") _log.Trace("Bot", $"VisionNav: OpenAI effort '{_effort}' is unavailable for model '{_model}', so it was not sent.");
            req.Content = JsonContent(body);
        }
        else if (_provider == VisionApiProvider.Anthropic)
        {
            req.Headers.Add("x-api-key", _apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            var body = new Dictionary<string, object>
            {
                ["model"] = _model,
                ["max_tokens"] = 32,
                ["messages"] = new[]
                {
                    new { role = "user", content = new object[]
                    {
                        new { type = "image", source = new { type = "base64", media_type = "image/png", data = imageB64 } },
                        new { type = "text", text = prompt }
                    }}
                }
            };
            if (_effort == "none") body["temperature"] = 0.1;
            else body["thinking"] = new { type = "enabled", budget_tokens = ThinkingBudget(_effort) };
            req.Content = JsonContent(body);
        }
        else
        {
            var options = new Dictionary<string, object> { ["temperature"] = 0.1 };
            if (!_gpu) options["num_gpu"] = 0;
            req.Content = JsonContent(new { model = _model, prompt, images = new[] { imageB64 }, stream = false, options });
        }

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) { _log.Trace("Bot", $"VisionNav {_provider} HTTP {(int)resp.StatusCode}."); return null; }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        return _provider switch
        {
            VisionApiProvider.OpenAi => root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString(),
            VisionApiProvider.Anthropic => root.GetProperty("content")[0].GetProperty("text").GetString(),
            _ => root.TryGetProperty("response", out var response) ? response.GetString() : null
        };
    }

    private static StringContent JsonContent(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static string NormalizeEffort(string? effort) => (effort ?? "none").Trim().ToLowerInvariant() switch
    {
        "low" => "low", "medium" => "medium", "high" => "high", _ => "none"
    };
    private static bool SupportsOpenAiEffort(string model) => model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("o", StringComparison.OrdinalIgnoreCase);
    private static int ThinkingBudget(string effort) => effort switch { "low" => 1024, "medium" => 4096, "high" => 8192, _ => 0 };

    /// <summary>The decision prompt (also used by the offline selftest). Asks for ONE device-independent token.</summary>
    public static string BuildPrompt(string goal) =>
        "You are an automation agent navigating a video game by LOOKING at the current screen (a live capture of the game).\n" +
        "You move a highlight and confirm/cancel to reach a goal. Decide the SINGLE next action to progress toward it.\n" +
        $"GOAL: {goal}\n" +
        "Reply with EXACTLY ONE token from this list and nothing else:\n" +
        "  UP DOWN LEFT RIGHT   move the menu highlight / selection\n" +
        "  CONFIRM              select / accept the highlighted item, or advance a prompt\n" +
        "  BACK                 cancel / go back one screen\n" +
        "  TABLEFT TABRIGHT     switch to the previous / next tab or category\n" +
        "  START                press the Start / Menu button\n" +
        "  WAIT                 the screen is loading, or a studio logo / intro video is playing — do nothing, look again\n" +
        "  DONE                 the GOAL state is ALREADY clearly visible on screen right now\n" +
        "Answer DONE only when the goal is clearly already true. While a loading screen or intro video shows, answer WAIT.";

    /// <summary>Pull the first recognized token out of the model's reply (tolerant of stray words/punctuation).</summary>
    public static (NavAction? action, bool done) Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, false);
        foreach (var t in Regex.Matches(text.ToUpperInvariant(), "[A-Z]+").Select(m => m.Value))
        {
            switch (t)
            {
                case "DONE": return (null, true);
                case "UP": return (NavAction.Up, false);
                case "DOWN": return (NavAction.Down, false);
                case "LEFT": return (NavAction.Left, false);
                case "RIGHT": return (NavAction.Right, false);
                case "CONFIRM": case "SELECT": case "ACCEPT": return (NavAction.Confirm, false);
                case "BACK": case "CANCEL": return (NavAction.Back, false);
                case "TABLEFT": return (NavAction.TabLeft, false);
                case "TABRIGHT": return (NavAction.TabRight, false);
                case "START": case "MENU": return (NavAction.Start, false);
                case "WAIT": return (NavAction.Wait, false);
            }
        }
        return (null, false);
    }

    private static string Compact(string s) => s.Trim().Replace("\r", " ").Replace("\n", " ");
}
