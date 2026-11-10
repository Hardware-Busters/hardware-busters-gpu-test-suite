using System.Text.Encodings.Web;
using System.Text.Json;
using GpuSuite.Core.Diagnostics;
using static GpuSuite.Measurement.Monitoring.FamilyMethodValues;

namespace GpuSuite.Measurement.Monitoring;

internal static class FamilyMethodBlock
{
    private const string Schema = "faganas-method/v1";
    private const int MaxBlockBytes = 16_384;
    private const int PlanRuns = 1;
    private const string InvalidStateReason = "Invalid benchmark method state.";
    private static readonly JsonSerializerOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static IReadOnlyDictionary<string, object?> Build(MonitoringMethod? method, bool online, DateTime nowUtc)
    {
        if (method is null)
            return Quiet("unavailable", "No benchmark run has started in this application.", null);

        string refreshedUtc = Timestamp(online ? nowUtc : method.UpdatedUtc);
        try
        {
            Validate(method);
            var block = Document(method, online, refreshedUtc);
            return Shrink(block) ? block : Quiet("invalid", "Method block too large.", refreshedUtc);
        }
        catch (MethodStateException ex)
        {
            return Quiet("invalid", ex.Message, refreshedUtc);
        }
        catch (Exception)
        {
            // Any other exception's text could carry anything, so only the fixed reason is published.
            return Quiet("invalid", InvalidStateReason, refreshedUtc);
        }
    }

    // A state that breaks the contract is reported as invalid, never repaired into a plausible-looking number.
    private static void Validate(MonitoringMethod method)
    {
        RequireId(method.RulesetId, "Ruleset");
        RequireId(method.Unit.Id, "Unit");
        RequireId(method.Plan.Id, "Plan");
        if (!Enum.IsDefined(method.Session.State)) throw new MethodStateException("Unknown method session state.");
        if (method.Plan.TotalSteps < 0) throw new MethodStateException("Method step total must not be negative.");
        if (method.Progress.Total != method.Plan.TotalSteps) throw new MethodStateException("Progress total differs from the plan's steps.");
        if (method.Progress.Completed < 0 || method.Progress.Completed > method.Progress.Total)
            throw new MethodStateException("Completed steps are outside the plan.");
        if (method.Session.State == MethodState.Running && method.Session.Index is { } index && (index < 1 || index > method.Plan.TotalSteps))
            throw new MethodStateException("The step in progress is outside the plan.");
    }

    private static void RequireId(string? id, string name)
    {
        if (Text(id, IdentityLimit) is null) throw new MethodStateException(name + " id is required.");
    }

    private static Dictionary<string, object?> Document(MonitoringMethod method, bool online, string refreshedUtc)
    {
        string state = method.Session.State switch
        {
            MethodState.Running => "running",
            MethodState.Done => "done",
            _ => "aborted"
        };
        var provisional = method.Results.Provisional is null ? null : Scalars(method.Results.Provisional, ScalarMemberLimit);
        if (provisional?.Count == 0) provisional = null;
        return new Dictionary<string, object?>
        {
            ["schema"] = Schema,
            ["freshness"] = online ? "fresh" : "stale",
            ["available"] = true,
            ["error"] = null,
            ["refreshedUtc"] = refreshedUtc,
            ["ruleset"] = new Dictionary<string, object?>
            {
                ["id"] = Text(method.RulesetId, IdentityLimit), ["status"] = "draft"
            },
            ["unit"] = new Dictionary<string, object?>
            {
                ["id"] = Text(method.Unit.Id, IdentityLimit), ["hash"] = Text(method.Unit.Hash, IdentityLimit), ["version"] = null,
                ["details"] = Scalars(method.Unit.Details, ScalarMemberLimit)
            },
            ["plan"] = new Dictionary<string, object?>
            {
                ["id"] = Text(method.Plan.Id, IdentityLimit), ["hash"] = Text(method.Plan.Hash, IdentityLimit),
                ["status"] = state == "aborted" ? "sealed" : state,
                ["runs"] = PlanRuns, ["totalSteps"] = method.Plan.TotalSteps, ["estimatedSeconds"] = null
            },
            ["session"] = Session(state, Text(method.Session.Step, LabelLimit), Text(method.Session.Phase, LabelLimit),
                method.Session.State == MethodState.Running ? method.Session.Index : null, method.Plan.TotalSteps),
            ["progress"] = new Dictionary<string, object?>
            {
                ["completed"] = method.Progress.Completed, ["total"] = method.Progress.Total
            },
            ["windows"] = new Dictionary<string, object?> { ["last"] = Window(method.LastWindow) },
            ["results"] = new Dictionary<string, object?>
            {
                ["provisional"] = provisional,
                ["status"] = provisional is null ? "none" : method.Results.Final ? "final" : "provisional"
            },
            ["issues"] = Issues(method.Issues),
            ["extra"] = Scalars(method.Extra, ExtraMemberLimit)
        };
    }

    private static Dictionary<string, object?>? Window(MethodWindow? window) => window is null ? null : new()
    {
        ["valid"] = window.Valid,
        ["reason"] = Text(window.Reason, TextLimit) ?? (window.Valid ? null : "The run was not valid.")
    };

    private static List<object?> Issues(IReadOnlyList<MethodIssue> issues) => issues
        .Select(issue => new { Field = Text(issue.Field, IdentityLimit), Message = Text(issue.Message, TextLimit), Utc = Timestamp(issue.Utc) })
        .Where(issue => issue.Message is not null)
        .OrderBy(issue => issue.Utc, StringComparer.Ordinal)
        .TakeLast(MonitoringMethod.MaxIssues)
        .Select(issue => (object?)new Dictionary<string, object?>
        {
            ["field"] = issue.Field, ["message"] = issue.Message, ["utc"] = issue.Utc
        }).ToList();

    private static Dictionary<string, object?> Session(string state, string? step = null, string? phase = null,
        int? index = null, int? count = null) => new()
    {
        ["state"] = state, ["step"] = step, ["phase"] = phase, ["index"] = index, ["count"] = count,
        ["remainingSeconds"] = null, ["personNeeded"] = null
    };

    private static Dictionary<string, object?> Quiet(string freshness, string error, string? refreshedUtc) => new()
    {
        ["schema"] = Schema, ["freshness"] = freshness, ["available"] = false,
        ["error"] = error, ["refreshedUtc"] = refreshedUtc,
        ["ruleset"] = null, ["unit"] = null, ["plan"] = null, ["session"] = Session("idle"),
        ["progress"] = new Dictionary<string, object?> { ["completed"] = null, ["total"] = null },
        ["windows"] = new Dictionary<string, object?> { ["last"] = null },
        ["results"] = new Dictionary<string, object?> { ["provisional"] = null, ["status"] = "none" },
        ["issues"] = new List<object?>(), ["extra"] = new Dictionary<string, object?>()
    };

    private static bool Shrink(Dictionary<string, object?> block)
    {
        var extra = (Dictionary<string, object?>)block["extra"]!;
        var issues = (List<object?>)block["issues"]!;
        var unit = (Dictionary<string, object?>)block["unit"]!;
        var details = (Dictionary<string, object?>)unit["details"]!;
        while (JsonSerializer.SerializeToUtf8Bytes(block, WriterOptions).Length > MaxBlockBytes)
        {
            if (extra.Count > 0) extra.Remove(extra.Keys.Last());
            else if (issues.Count > 0) issues.RemoveAt(0);
            else if (details.Count > 0) details.Remove(details.Keys.Last());
            else return false;
        }
        return true;
    }

    private sealed class MethodStateException(string message) : Exception(message);
}
