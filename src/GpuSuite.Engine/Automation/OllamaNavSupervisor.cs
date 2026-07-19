using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GpuSuite.Core.Diagnostics;

namespace GpuSuite.Engine.Automation;

/// <summary>
/// LLM fallback for the reactive <see cref="NavGraph"/> runner, backed by a LOCAL model via Ollama — free, private,
/// no API key. When the deterministic graph is LOST on a screen not in its map, the runner calls this with the
/// screen's OCR lines + the goal; the model picks the single next gamepad button to press toward the goal.
///
/// Runs on CPU (<c>num_gpu = 0</c> in the request) so it never competes with — or destabilizes — the benchmark
/// GPU. Low temperature for near-deterministic choices. Any failure (server down, timeout, unparseable reply)
/// returns null, so the runner falls back to its recovery press / abort and a flaky model can never wedge a run.
/// </summary>
public sealed class OllamaNavSupervisor : INavSupervisor
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };
    // Buttons the runner can press (must match the ViGEm pad names the engine accepts).
    private static readonly string[] ValidKeys = { "Up", "Down", "Left", "Right", "LB", "RB", "Start", "Back", "A", "B", "X", "Y" };

    private readonly string _endpoint;
    private readonly string _model;
    private readonly RunLogger _log;

    public OllamaNavSupervisor(string endpoint, string model, RunLogger log)
    {
        _endpoint = (string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434" : endpoint).TrimEnd('/');
        _model = string.IsNullOrWhiteSpace(model) ? "qwen2.5:7b" : model;
        _log = log;
    }

    public string Endpoint => _endpoint;
    public string Model => _model;

    public async Task<string?> SuggestKeyAsync(IReadOnlyList<string> screenText, string goal, CancellationToken ct)
    {
        var lines = string.Join("\n", screenText.Where(s => !string.IsNullOrWhiteSpace(s)).Take(40));
        var prompt = BuildPrompt(lines, goal);
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                model = _model,
                prompt,
                stream = false,
                options = new { num_gpu = 0, temperature = 0.1 }   // CPU-only (keep off the benchmark GPU), near-deterministic
            });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync($"{_endpoint}/api/generate", content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { _log.Trace("Bot", $"LLM supervisor HTTP {(int)resp.StatusCode}."); return null; }
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var text = doc.RootElement.TryGetProperty("response", out var r) ? r.GetString() ?? "" : "";
            var key = ExtractKey(text);
            _log.Info("Bot", $"LLM supervisor ({_model}) → '{key ?? "(no valid key)"}' (raw: \"{text.Trim().Replace("\n", " ")}\").");
            return key;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.Trace("Bot", $"LLM supervisor error: {ex.Message}"); return null; }
    }

    /// <summary>Compose the menu-decision prompt (also used by the selftest).</summary>
    public static string BuildPrompt(string ocrLines, string goal) =>
        "You navigate a video-game menu with an Xbox gamepad to reach a goal. Decide the SINGLE next button to press.\n" +
        $"GOAL: {goal}\n" +
        "Text currently on screen (OCR):\n----\n" + ocrLines + "\n----\n" +
        "Buttons: A=confirm/select, B=back/cancel, Up/Down/Left/Right=move the highlight, LB/RB=switch tab left/right, Start.\n" +
        "Answer with ONLY one button name (e.g. \"Down\" or \"A\"). No other words.";

    /// <summary>Pull the first valid button name out of the model's reply (tolerant of extra words/punctuation).
    /// Prefers multi-letter tokens (Down/Right/RB…) before the single letters A/B/X/Y so a stray "A" in prose
    /// doesn't beat an explicit direction.</summary>
    public static string? ExtractKey(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var tokens = Regex.Matches(text, "[A-Za-z]+").Select(m => m.Value).ToList();
        // pass 1: multi-letter button words
        foreach (var tok in tokens.Where(t => t.Length > 1))
        {
            var hit = ValidKeys.FirstOrDefault(k => k.Length > 1 && string.Equals(k, tok, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        // pass 2: single-letter buttons (A/B/X/Y)
        foreach (var tok in tokens.Where(t => t.Length == 1))
        {
            var hit = ValidKeys.FirstOrDefault(k => k.Length == 1 && string.Equals(k, tok, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return null;
    }
}
