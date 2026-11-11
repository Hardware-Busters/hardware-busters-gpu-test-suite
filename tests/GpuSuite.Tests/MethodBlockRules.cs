using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GpuSuite.Tests;

internal sealed class MethodBlockRules
{
    private readonly List<string> _violations = [];
    private static readonly JsonSerializerOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static IReadOnlyList<string> Validate(JsonElement block)
    {
        var checker = new MethodBlockRules();
        checker.Document(block);
        return checker._violations;
    }

    private void Check(bool valid, int rule, string message)
    {
        if (!valid) _violations.Add($"Rule {rule}: {message}");
    }

    private JsonElement Member(JsonElement parent, string name, int rule)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)) return value;
        Check(false, rule, "Missing " + name);
        return default;
    }

    private static bool IsNull(JsonElement value) => value.ValueKind == JsonValueKind.Null;
    private static string? String(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Text(JsonElement value, int max, bool nullable = false, bool nonempty = false) =>
        nullable && IsNull(value) || String(value) is { } text && text.Length <= max && (!nonempty || text.Length > 0);
    private static bool Word(JsonElement value, params string[] words) => String(value) is { } text && words.Contains(text);
    private static bool Integer(JsonElement value, int min = 0, bool nullable = false) =>
        nullable && IsNull(value) || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number >= min;
    private static bool Timestamp(JsonElement value) => String(value) is { } text &&
        Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$") &&
        DateTime.TryParseExact(text, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
    private static bool Name(string name) => Regex.IsMatch(name, @"\A[A-Za-z][A-Za-z0-9_]{0,31}\z");

    private void Document(JsonElement block)
    {
        Check(block.ValueKind == JsonValueKind.Object &&
            Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(block, WriterOptions)) <= 16_384, 1, "Object or size");
        Check(Word(Member(block, "schema", 2), "faganas-method/v1"), 2, "Schema");
        var freshness = Member(block, "freshness", 3);
        var available = Member(block, "available", 3);
        bool hasState = Word(freshness, "fresh", "stale");
        Check(Word(freshness, "fresh", "stale", "unavailable", "invalid") &&
            available.ValueKind == (hasState ? JsonValueKind.True : JsonValueKind.False), 3, "Freshness and availability");
        var error = Member(block, "error", 4);
        Check(Text(error, 240, nullable: true) && (!hasState || IsNull(error)) &&
            (!Word(freshness, "invalid") || Text(error, 240, nonempty: true)), 4, "Error");
        var refreshed = Member(block, "refreshedUtc", 5);
        Check(Timestamp(refreshed) || IsNull(refreshed) && Word(freshness, "unavailable", "invalid"), 5, "Refreshed timestamp");

        var ruleset = Member(block, "ruleset", 6);
        var unit = Member(block, "unit", 8);
        var plan = Member(block, "plan", 9);
        var session = Member(block, "session", 10);
        var progress = Member(block, "progress", 13);
        var windows = Member(block, "windows", 14);
        var results = Member(block, "results", 15);
        IntegerEncoding(plan, "runs", "totalSteps", "estimatedSeconds");
        IntegerEncoding(session, "index", "count", "remainingSeconds");
        IntegerEncoding(progress, "completed", "total");
        if (hasState) Check(ruleset.ValueKind == JsonValueKind.Object, 6, "Available ruleset");
        else Quiet(ruleset, unit, plan, session, progress, windows, results);
        if (!IsNull(ruleset)) Ruleset(ruleset);
        if (!IsNull(unit)) Unit(unit);
        if (!IsNull(plan)) Plan(plan);
        Session(session, plan);
        Progress(progress);
        Window(Member(windows, "last", 14));
        Results(results);
        Issues(Member(block, "issues", 16));
        var extra = Member(block, "extra", 17);
        Check(extra.ValueKind == JsonValueKind.Object, 17, "Extra object");
        Extra(extra, 0);
        AllValues(block);
    }

    private void Quiet(JsonElement ruleset, JsonElement unit, JsonElement plan, JsonElement session,
        JsonElement progress, JsonElement windows, JsonElement results)
    {
        Check(IsNull(ruleset) && IsNull(unit) && IsNull(plan), 6, "Quiet identities");
        Check(Word(Member(session, "state", 6), "idle"), 6, "Quiet state");
        foreach (string name in new[] { "step", "phase", "index", "count", "remainingSeconds", "personNeeded" })
            Check(IsNull(Member(session, name, 6)), 6, "Quiet session " + name);
        Check(IsNull(Member(progress, "completed", 6)) && IsNull(Member(progress, "total", 6)), 6, "Quiet progress");
        Check(IsNull(Member(windows, "last", 6)), 6, "Quiet window");
        Check(IsNull(Member(results, "provisional", 6)) && Word(Member(results, "status", 6), "none"), 6, "Quiet results");
    }

    private void IntegerEncoding(JsonElement parent, params string[] names)
    {
        if (parent.ValueKind != JsonValueKind.Object) return;
        foreach (string name in names)
            if (parent.TryGetProperty(name, out var value) && !IsNull(value))
                Check(Integer(value) && Regex.IsMatch(value.GetRawText(), @"\A[0-9]+\z"), 18, "Integer encoding " + name);
    }

    private void Ruleset(JsonElement ruleset)
    {
        Check(Text(Member(ruleset, "id", 7), 64, nonempty: true), 7, "Ruleset id");
        Check(Word(Member(ruleset, "status", 7), "draft", "approved"), 7, "Ruleset status");
    }

    private void Unit(JsonElement unit)
    {
        Check(Text(Member(unit, "id", 8), 64, nonempty: true) && Text(Member(unit, "hash", 8), 64, nullable: true) &&
            Text(Member(unit, "version", 8), 32, nullable: true), 8, "Unit identity");
        Scalars(Member(unit, "details", 8), 8);
    }

    private void Plan(JsonElement plan)
    {
        Check(Text(Member(plan, "id", 9), 64, nonempty: true) && Text(Member(plan, "hash", 9), 64, nullable: true), 9, "Plan identity");
        Check(Word(Member(plan, "status", 9), "preview", "sealed", "running", "done"), 9, "Plan status");
        foreach (string name in new[] { "runs", "totalSteps", "estimatedSeconds" })
            Check(Integer(Member(plan, name, 9), nullable: name == "estimatedSeconds"), 9, "Plan " + name);
    }

    private void Session(JsonElement session, JsonElement plan)
    {
        var state = Member(session, "state", 10);
        Check(Word(state, "idle", "running", "paused", "waitingForPerson", "aborted", "done"), 10, "Session state");
        foreach (string name in new[] { "step", "phase" })
            Check(Text(Member(session, name, 10), 96, nullable: true, nonempty: true), 10, "Session " + name);
        var index = Member(session, "index", 10);
        var count = Member(session, "count", 10);
        var remaining = Member(session, "remainingSeconds", 10);
        Check(Integer(index, 1, nullable: true) && Integer(count, nullable: true) && Integer(remaining, nullable: true), 10, "Session counts");
        Check(!Integer(index, 1) || !Integer(count) || index.GetInt32() <= count.GetInt32(), 10, "Index <= count");
        var person = Member(session, "personNeeded", 11);
        Check(IsNull(person) == !Word(state, "waitingForPerson"), 11, "Person state");
        if (!IsNull(person))
            Check(Text(Member(person, "kind", 11), 32, nonempty: true) && Text(Member(person, "text", 11), 240, nonempty: true), 11, "Person text");

        bool active = Word(state, "running", "paused", "waitingForPerson");
        Check(active == (plan.ValueKind == JsonValueKind.Object && Word(Member(plan, "status", 12), "running")), 12, "Running plan");
        if (Word(state, "idle"))
            Check(IsNull(Member(session, "step", 12)) && IsNull(Member(session, "phase", 12)) && IsNull(index) && IsNull(remaining) && IsNull(person), 12, "Idle session");
        if (Word(state, "done", "aborted"))
        {
            Check(IsNull(index) && IsNull(remaining) && IsNull(person), 12, "Terminal session");
            Check(IsNull(plan) || Word(Member(plan, "status", 12), Word(state, "done") ? "done" : "sealed"), 12, "Terminal plan");
        }
    }

    private void Progress(JsonElement progress)
    {
        var completed = Member(progress, "completed", 13);
        var total = Member(progress, "total", 13);
        Check(Integer(completed, nullable: true) && Integer(total, nullable: true) && IsNull(completed) == IsNull(total), 13, "Progress counts");
        Check(!Integer(completed) || !Integer(total) || completed.GetInt32() <= total.GetInt32(), 13, "Completed <= total");
    }

    private void Window(JsonElement window)
    {
        if (IsNull(window)) return;
        var valid = Member(window, "valid", 14);
        var reason = Member(window, "reason", 14);
        Check(valid.ValueKind is JsonValueKind.True or JsonValueKind.False, 14, "Window valid");
        Check(Text(reason, 240, nullable: valid.ValueKind == JsonValueKind.True, nonempty: valid.ValueKind == JsonValueKind.False), 14, "Window reason");
    }

    private void Results(JsonElement results)
    {
        var status = Member(results, "status", 15);
        var provisional = Member(results, "provisional", 15);
        Check(Word(status, "none", "provisional", "final") && Word(status, "none") == IsNull(provisional), 15, "Result status");
        if (!IsNull(provisional)) Scalars(provisional, 15);
    }

    private void Issues(JsonElement issues)
    {
        Check(issues.ValueKind == JsonValueKind.Array && issues.GetArrayLength() <= 10, 16, "Issue list");
        if (issues.ValueKind != JsonValueKind.Array) return;
        string? previous = null;
        foreach (var issue in issues.EnumerateArray())
        {
            Check(Text(Member(issue, "field", 16), 64, nullable: true) && Text(Member(issue, "message", 16), 240, nonempty: true), 16, "Issue text");
            var utc = Member(issue, "utc", 16);
            Check(Timestamp(utc), 16, "Issue timestamp");
            string? current = String(utc);
            Check(previous is null || string.CompareOrdinal(previous, current) <= 0, 16, "Issue order");
            previous = current;
        }
    }

    private void Scalars(JsonElement value, int rule)
    {
        Check(value.ValueKind == JsonValueKind.Object, rule, "Scalar object");
        if (value.ValueKind != JsonValueKind.Object) return;
        Check(value.EnumerateObject().Count() <= 16, rule, "Scalar members");
        foreach (var property in value.EnumerateObject())
            Check(Name(property.Name) && (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number ||
                Text(property.Value, 240)), rule, "Scalar member " + property.Name);
    }

    private void Extra(JsonElement value, int depth)
    {
        Check(depth <= 3, 17, "Extra depth");
        if (value.ValueKind == JsonValueKind.Object)
        {
            Check(value.EnumerateObject().Count() <= 32, 17, "Extra members");
            foreach (var property in value.EnumerateObject())
            {
                Check(Name(property.Name), 17, "Extra name");
                Extra(property.Value, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            Check(value.GetArrayLength() <= 32, 17, "Extra array");
            foreach (var item in value.EnumerateArray()) Extra(item, depth + 1);
        }
        else if (value.ValueKind == JsonValueKind.String) Check(Text(value, 240), 17, "Extra text");
    }

    private void AllValues(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                Check(!property.Name.Any(c => c <= '\u001f'), 18, "Control in member name");
                AllValues(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) AllValues(item);
        else if (value.ValueKind == JsonValueKind.String)
            Check(!value.GetString()!.Any(c => c <= '\u001f'), 18, "Control in string");
        else if (value.ValueKind == JsonValueKind.Number)
            Check(value.TryGetDouble(out double number) && double.IsFinite(number), 18, "Nonfinite number");
    }
}
