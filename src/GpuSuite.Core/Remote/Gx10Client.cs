using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GpuSuite.Core.Diagnostics;

namespace GpuSuite.Core.Remote;

/// <summary>One model installed on the box (from <c>/api/tags</c>).</summary>
public sealed class Gx10Model
{
    public string Name { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Family { get; set; } = "";
    public string ParameterSize { get; set; } = "";
    public string Quantization { get; set; } = "";

    /// <summary>Heuristic: a multimodal/vision model (qwen2.5vl, llava, llama4, gemma3-vision, …).</summary>
    public bool IsVision =>
        Name.Contains("vl", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("vision", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("llava", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("llama4", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("scout", StringComparison.OrdinalIgnoreCase)
        || Family.Contains("clip", StringComparison.OrdinalIgnoreCase);

    public double SizeGb => SizeBytes > 0 ? SizeBytes / 1_000_000_000.0 : 0;
}

/// <summary>One model currently resident in memory on the box (from <c>/api/ps</c>).</summary>
public sealed class Gx10LoadedModel
{
    public string Name { get; set; } = "";
    public long SizeBytes { get; set; }
    public long VramBytes { get; set; }
    public string ExpiresAt { get; set; } = "";

    public double VramGb => VramBytes > 0 ? VramBytes / 1_000_000_000.0 : 0;
    /// <summary>Fraction of the resident model held in VRAM (1.0 = fully on the GPU, 0 = pure CPU).</summary>
    public double GpuFraction => SizeBytes > 0 ? Math.Clamp(VramBytes / (double)SizeBytes, 0, 1) : 0;
}

/// <summary>
/// The result of probing the GX10 (or any Ollama endpoint): is it reachable, what is it serving, and how
/// fast did it answer. This is exactly the "statistics to check before testing" surface — the App's GX10
/// panel, the <c>gpusuite gx10</c> verb, and the doctor line all render one of these. Read-only: a probe
/// never loads, pulls, or unloads a model; it only asks what is already there.
/// </summary>
public sealed class Gx10Status
{
    public string Endpoint { get; set; } = "";
    public bool Reachable { get; set; }
    public string? Version { get; set; }
    /// <summary>Round-trip latency of the version ping, in milliseconds (0 when unreachable).</summary>
    public double LatencyMs { get; set; }
    public List<Gx10Model> Models { get; set; } = new();
    public List<Gx10LoadedModel> Loaded { get; set; } = new();
    /// <summary>Why the probe failed (connection refused, DNS, timeout, …); null when reachable.</summary>
    public string? Error { get; set; }

    public IEnumerable<Gx10Model> VisionModels => Models.Where(m => m.IsVision);

    /// <summary>True if a model whose name matches <paramref name="model"/> is installed (tolerant of the :latest tag).</summary>
    public bool HasModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        string wantBase = model.Split(':')[0];
        return Models.Any(m =>
            m.Name.Equals(model, StringComparison.OrdinalIgnoreCase)
            || (!model.Contains(':') && m.Name.Split(':')[0].Equals(wantBase, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>A one-line summary for logs / the doctor line.</summary>
    public string Summary()
    {
        if (!Reachable) return $"unreachable ({Error ?? "no response"})";
        var loaded = Loaded.Count == 0 ? "none loaded" : $"{Loaded.Count} loaded ({string.Join(", ", Loaded.Select(l => $"{l.Name} {l.VramGb:0.0}GB VRAM"))})";
        return $"online v{Version} · {LatencyMs:0} ms · {Models.Count} model(s), {VisionModels.Count()} vision · {loaded}";
    }
}

/// <summary>
/// A thin client for the lab GX10 / GB10 box's Ollama (OpenAI-style) server — or any Ollama endpoint.
/// Two jobs: (1) <see cref="ProbeAsync"/> reports the box's live statistics so the operator can confirm it
/// is up and serving the right models BEFORE a test run; (2) <see cref="GenerateAsync"/> runs a single
/// (optionally multimodal, optionally JSON-constrained) completion — used by the AI Calibration Engineer's
/// GX10 reasoner. Read-only probing; every call is bounded and never throws (failures surface as a
/// non-reachable status or a null generation, so a flaky box can never wedge the caller).
/// </summary>
public sealed class Gx10Client
{
    // One shared client; per-call timeouts are enforced with a linked CTS, not HttpClient.Timeout.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly string _endpoint;
    private readonly RunLogger? _log;

    public Gx10Client(string endpoint, RunLogger? log = null)
    {
        _endpoint = Normalize(endpoint);
        _log = log;
    }

    public string Endpoint => _endpoint;

    /// <summary>Canonical endpoint form: default to the lab GX10, ensure a scheme, strip a trailing slash.</summary>
    public static string Normalize(string? endpoint)
    {
        var e = string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434" : endpoint.Trim();
        if (!e.Contains("://", StringComparison.Ordinal)) e = "http://" + e;
        return e.TrimEnd('/');
    }

    /// <summary>
    /// Fast, synchronous best-effort reachability check (used to decide vision-model placement at run start,
    /// and to grey-in the GX10 option). Returns false on any error/timeout — never throws. The default timeout
    /// matches <see cref="ProbeAsync"/> (5 s): the box's COLD first response includes mDNS resolution of the
    /// <c>*.local</c> host plus a wake-from-idle ping and was measured ~2.9 s — a tighter 2.5 s budget wrongly
    /// reported an online box as unreachable and silently fell back to the local GPU (caught by the in-world
    /// smoke-test, 2026-06-30).
    /// </summary>
    public static bool IsReachable(string endpoint, int timeoutMs = 5000)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{Normalize(endpoint)}/api/version");
            using var resp = Http.Send(req, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>Probe the box: version + latency, installed models, and currently-loaded models (with VRAM).</summary>
    public async Task<Gx10Status> ProbeAsync(CancellationToken ct, int timeoutMs = 5000)
    {
        var status = new Gx10Status { Endpoint = _endpoint };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        // /api/version — also the reachability + latency probe.
        try
        {
            var sw = Stopwatch.StartNew();
            using var resp = await Http.GetAsync($"{_endpoint}/api/version", cts.Token).ConfigureAwait(false);
            sw.Stop();
            status.LatencyMs = sw.Elapsed.TotalMilliseconds;
            if (!resp.IsSuccessStatusCode)
            {
                status.Error = $"HTTP {(int)resp.StatusCode} from /api/version";
                _log?.Trace("Gx10", $"probe {_endpoint}: {status.Error}");
                return status;
            }
            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            status.Version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            status.Reachable = true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            status.Error = $"timed out after {timeoutMs} ms";
            _log?.Trace("Gx10", $"probe {_endpoint}: {status.Error}");
            return status;
        }
        catch (Exception ex)
        {
            status.Error = ex.Message;
            _log?.Trace("Gx10", $"probe {_endpoint}: {ex.Message}");
            return status;
        }

        // Installed models (/api/tags) and resident models (/api/ps) — best-effort; absence isn't fatal.
        try { status.Models = await GetModelsAsync(cts.Token).ConfigureAwait(false); }
        catch (Exception ex) { _log?.Trace("Gx10", $"/api/tags failed: {ex.Message}"); }
        try { status.Loaded = await GetLoadedAsync(cts.Token).ConfigureAwait(false); }
        catch (Exception ex) { _log?.Trace("Gx10", $"/api/ps failed: {ex.Message}"); }

        _log?.Trace("Gx10", $"probe {_endpoint}: {status.Summary()}");
        return status;
    }

    private async Task<List<Gx10Model>> GetModelsAsync(CancellationToken ct)
    {
        var list = new List<Gx10Model>();
        using var resp = await Http.GetAsync($"{_endpoint}/api/tags", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return list;
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return list;
        foreach (var m in models.EnumerateArray())
        {
            var item = new Gx10Model
            {
                Name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                SizeBytes = m.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0
            };
            if (m.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                item.Family = d.TryGetProperty("family", out var f) ? f.GetString() ?? "" : "";
                item.ParameterSize = d.TryGetProperty("parameter_size", out var p) ? p.GetString() ?? "" : "";
                item.Quantization = d.TryGetProperty("quantization_level", out var q) ? q.GetString() ?? "" : "";
            }
            if (!string.IsNullOrWhiteSpace(item.Name)) list.Add(item);
        }
        return list.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<List<Gx10LoadedModel>> GetLoadedAsync(CancellationToken ct)
    {
        var list = new List<Gx10LoadedModel>();
        using var resp = await Http.GetAsync($"{_endpoint}/api/ps", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return list;
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return list;
        foreach (var m in models.EnumerateArray())
        {
            list.Add(new Gx10LoadedModel
            {
                Name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                SizeBytes = m.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0,
                VramBytes = m.TryGetProperty("size_vram", out var vr) && vr.TryGetInt64(out var vv) ? vv : 0,
                ExpiresAt = m.TryGetProperty("expires_at", out var e) ? e.GetString() ?? "" : ""
            });
        }
        return list;
    }

    /// <summary>
    /// Run one completion on the box. <paramref name="imagesBase64"/> attaches images for a multimodal model;
    /// <paramref name="jsonFormat"/> asks Ollama to constrain the output to valid JSON (the structured-output
    /// anti-hallucination requirement). Returns the model's text, or null on any failure (the caller abstains).
    /// </summary>
    public async Task<string?> GenerateAsync(
        string model, string prompt,
        IReadOnlyList<string>? imagesBase64 = null,
        IReadOnlyDictionary<string, object>? options = null,
        bool jsonFormat = false,
        int timeoutMs = 120_000,
        CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            var payload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["prompt"] = prompt,
                ["stream"] = false
            };
            if (imagesBase64 is { Count: > 0 }) payload["images"] = imagesBase64;
            if (jsonFormat) payload["format"] = "json";
            if (options is { Count: > 0 }) payload["options"] = options;

            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync($"{_endpoint}/api/generate", content, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.Trace("Gx10", $"generate HTTP {(int)resp.StatusCode}");
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("response", out var r) ? r.GetString() : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log?.Trace("Gx10", $"generate error: {ex.Message}"); return null; }
    }

    /// <summary>Best-effort unload of a model off the box (keep_alive=0). Never throws.</summary>
    public async Task UnloadAsync(string model, CancellationToken ct = default)
    {
        try
        {
            var payload = new { model, prompt = "", keep_alive = 0, stream = false };
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(15_000);
            using var resp = await Http.PostAsync($"{_endpoint}/api/generate", content, cts.Token).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
    }
}
