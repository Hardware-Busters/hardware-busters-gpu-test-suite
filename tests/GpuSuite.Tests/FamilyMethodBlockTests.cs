using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Measurement.Monitoring;
using Xunit;

namespace GpuSuite.Tests;

public sealed class FamilyMethodBlockTests
{
    private const int MaxBlockBytes = 16_384;
    private static readonly DateTime Now = new(2026, 11, 10, 9, 41, 37, 500, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static MonitoringMethod Running() => new("gpu-benchmark-v1",
        new MethodUnit("gpu-run-1", new string('a', 64), new Dictionary<string, object?> { ["model"] = "Example GPU" }),
        new MethodPlan("plan-1", new string('b', 64), 1450),
        new MethodSession(MethodState.Running, "benchmark cell 412 of 1450", "capture", 412),
        new MethodProgress(411, 1450), new MethodWindow(true, null),
        new MethodResults(new Dictionary<string, object?> { ["completedCells"] = 411, ["averageFps"] = 91.84 }, false),
        [], new Dictionary<string, object?>(), Now.AddSeconds(-30));

    private static JsonElement Build(MonitoringMethod? method, bool online = true)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(FamilyMethodBlock.Build(method, online, Now), WriterOptions);
        Assert.True(json.Length <= MaxBlockBytes);
        using var parsed = JsonDocument.Parse(json);
        var block = parsed.RootElement.Clone();
        Assert.Empty(MethodBlockRules.Validate(block));
        return block;
    }

    private static void Quiet(JsonElement block, string freshness)
    {
        Assert.Equal(freshness, block.GetProperty("freshness").GetString());
        Assert.False(block.GetProperty("available").GetBoolean());
        foreach (string name in new[] { "ruleset", "unit", "plan" }) Assert.Equal(JsonValueKind.Null, block.GetProperty(name).ValueKind);
        var session = block.GetProperty("session");
        Assert.Equal("idle", session.GetProperty("state").GetString());
        Assert.All(session.EnumerateObject().Where(p => p.Name != "state"), p => Assert.Equal(JsonValueKind.Null, p.Value.ValueKind));
        Assert.All(block.GetProperty("progress").EnumerateObject(), p => Assert.Equal(JsonValueKind.Null, p.Value.ValueKind));
        Assert.Equal(JsonValueKind.Null, block.GetProperty("windows").GetProperty("last").ValueKind);
        Assert.Equal(JsonValueKind.Null, block.GetProperty("results").GetProperty("provisional").ValueKind);
        Assert.Equal("none", block.GetProperty("results").GetProperty("status").GetString());
        Assert.Empty(block.GetProperty("issues").EnumerateArray());
        Assert.Empty(block.GetProperty("extra").EnumerateObject());
    }

    [Fact]
    public void NoRunPublishesTheUnavailableQuietForm()
    {
        var block = Build(null);
        Quiet(block, "unavailable");
        Assert.Equal("No benchmark run has started in this application.", block.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, block.GetProperty("refreshedUtc").ValueKind);
    }

    [Fact]
    public void RunningStateHasTheMidRunContractStructure()
    {
        var block = Build(Running());
        Assert.Equal("faganas-method/v1", block.GetProperty("schema").GetString());
        Assert.True(block.GetProperty("available").GetBoolean());
        Assert.Equal("draft", block.GetProperty("ruleset").GetProperty("status").GetString());
        Assert.Equal("gpu-run-1", block.GetProperty("unit").GetProperty("id").GetString());
        Assert.Equal("Example GPU", block.GetProperty("unit").GetProperty("details").GetProperty("model").GetString());
        Assert.Equal("running", block.GetProperty("plan").GetProperty("status").GetString());
        Assert.Equal(1, block.GetProperty("plan").GetProperty("runs").GetInt32());
        Assert.Equal(1450, block.GetProperty("plan").GetProperty("totalSteps").GetInt32());
        var session = block.GetProperty("session");
        Assert.Equal("running", session.GetProperty("state").GetString());
        Assert.Equal("benchmark cell 412 of 1450", session.GetProperty("step").GetString());
        Assert.Equal("capture", session.GetProperty("phase").GetString());
        Assert.Equal(412, session.GetProperty("index").GetInt32());
        Assert.Equal(1450, session.GetProperty("count").GetInt32());
        Assert.Equal(411, block.GetProperty("progress").GetProperty("completed").GetInt32());
        Assert.Equal(1450, block.GetProperty("progress").GetProperty("total").GetInt32());
        Assert.True(block.GetProperty("windows").GetProperty("last").GetProperty("valid").GetBoolean());
        Assert.Equal("provisional", block.GetProperty("results").GetProperty("status").GetString());
        Assert.Equal(91.84, block.GetProperty("results").GetProperty("provisional").GetProperty("averageFps").GetDouble());
    }

    [Theory]
    [InlineData(MethodState.Done, "done", "done")]
    [InlineData(MethodState.Aborted, "aborted", "sealed")]
    public void TerminalStatesHaveNoActiveIndex(MethodState state, string expectedSession, string expectedPlan)
    {
        var method = Running();
        var block = Build(method with
        {
            Session = method.Session with { State = state }, Results = method.Results with { Final = state == MethodState.Done }
        });
        Assert.Equal(expectedSession, block.GetProperty("session").GetProperty("state").GetString());
        Assert.Equal(expectedPlan, block.GetProperty("plan").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, block.GetProperty("session").GetProperty("index").ValueKind);
        Assert.Equal(state == MethodState.Done ? "final" : "provisional", block.GetProperty("results").GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(true, "fresh")]
    [InlineData(false, "stale")]
    public void FreshnessUsesTheCorrectTimestamp(bool online, string expected)
    {
        var method = Running();
        var block = Build(method, online);
        Assert.Equal(expected, block.GetProperty("freshness").GetString());
        Assert.Equal((online ? Now : method.UpdatedUtc).ToString("o", CultureInfo.InvariantCulture), block.GetProperty("refreshedUtc").GetString());
        Assert.Equal(JsonValueKind.Null, block.GetProperty("error").ValueKind);
    }

    [Fact]
    public void TextIsClippedAtUtf16BoundariesWithoutSplittingSurrogates()
    {
        var method = Running();
        string atLimit = new('a', 64);
        var block = Build(method with
        {
            RulesetId = atLimit,
            Unit = method.Unit with { Id = new string('b', 65) },
            Session = method.Session with { Step = new string('s', 94) + "\ud83d\ude00x", Phase = new string('p', 97) },
            LastWindow = new MethodWindow(false, new string('r', 241))
        });
        Assert.Equal(atLimit, block.GetProperty("ruleset").GetProperty("id").GetString());
        Assert.Equal(new string('b', 63) + "\u2026", block.GetProperty("unit").GetProperty("id").GetString());
        Assert.Equal(new string('s', 94) + "\u2026", block.GetProperty("session").GetProperty("step").GetString());
        Assert.Equal(new string('p', 95) + "\u2026", block.GetProperty("session").GetProperty("phase").GetString());
        Assert.Equal(new string('r', 239) + "\u2026", block.GetProperty("windows").GetProperty("last").GetProperty("reason").GetString());
    }

    [Fact]
    public void EveryControlCharacterBecomesASpace()
    {
        var method = Running();
        string controls = string.Concat(Enumerable.Range(0, 32).Select(i => (char)i));
        string text = "before" + controls + "after";
        string expected = "before" + new string(' ', 32) + "after";
        var block = Build(method with
        {
            RulesetId = text, Session = method.Session with { Step = text, Phase = text },
            Issues = [new MethodIssue(text, text, Now)],
            Extra = new Dictionary<string, object?> { ["note"] = text },
            Unit = method.Unit with { Details = new Dictionary<string, object?> { ["note"] = text } }
        });
        Assert.Equal(expected, block.GetProperty("ruleset").GetProperty("id").GetString());
        Assert.Equal(expected, block.GetProperty("session").GetProperty("step").GetString());
        Assert.Equal(expected, block.GetProperty("issues")[0].GetProperty("message").GetString());
        Assert.Equal(expected, block.GetProperty("extra").GetProperty("note").GetString());
        Assert.Equal(expected, block.GetProperty("unit").GetProperty("details").GetProperty("note").GetString());
    }

    [Fact]
    public void IssuesKeepTheNewestTenInStableChronologicalOrder()
    {
        var issues = Enumerable.Range(0, 14).Reverse().Select(i => new MethodIssue("cell", "issue" + i, Now.AddSeconds(i / 2))).ToList();
        issues.Add(new MethodIssue(null, "", Now.AddDays(1)));
        var block = Build(Running() with { Issues = issues });
        var actual = block.GetProperty("issues").EnumerateArray().ToArray();
        Assert.Equal(10, actual.Length);
        Assert.Equal(new[] { 5, 4, 7, 6, 9, 8, 11, 10, 13, 12 }.Select(i => "issue" + i), actual.Select(i => i.GetProperty("message").GetString()));
    }

    [Fact]
    public void ScalarObjectsFilterNamesTypesAndNonfiniteNumbersAndClipText()
    {
        var values = new Dictionary<string, object?>
        {
            ["bad-name"] = 1, ["1bad"] = 2, [new string('a', 33)] = 3, ["NonAscii\u00e9"] = 4,
            ["text"] = new string('t', 241), ["nan"] = double.NaN, ["positiveInfinity"] = double.PositiveInfinity,
            ["negativeInfinity"] = float.NegativeInfinity, ["nested"] = new { hidden = 42 }, ["array"] = new[] { 1 },
            ["flag"] = true, ["unknown"] = null, ["count"] = 0, ["number"] = 1.25m,
            ["A_1"] = 9, [new string('z', 32)] = 10
        };
        foreach (int i in Enumerable.Range(0, 20)) values["field" + i] = i;
        var method = Running();
        var block = Build(method with { Unit = method.Unit with { Details = values }, Results = new MethodResults(values, false) });
        foreach (var actual in new[] { block.GetProperty("unit").GetProperty("details"), block.GetProperty("results").GetProperty("provisional") })
        {
            Assert.Equal(16, actual.EnumerateObject().Count());
            Assert.False(actual.TryGetProperty("bad-name", out _));
            Assert.False(actual.TryGetProperty("1bad", out _));
            Assert.False(actual.TryGetProperty("NonAscii\u00e9", out _));
            Assert.Equal(new string('t', 239) + "\u2026", actual.GetProperty("text").GetString());
            foreach (string name in new[] { "nan", "positiveInfinity", "negativeInfinity", "nested", "array", "unknown" })
                Assert.Equal(JsonValueKind.Null, actual.GetProperty(name).ValueKind);
            Assert.True(actual.GetProperty("flag").GetBoolean());
            Assert.Equal(0, actual.GetProperty("count").GetInt32());
            Assert.Equal(1.25m, actual.GetProperty("number").GetDecimal());
            Assert.True(actual.TryGetProperty("A_1", out _));
            Assert.True(actual.TryGetProperty(new string('z', 32), out _));
            Assert.False(actual.TryGetProperty("field4", out _));
        }
    }

    [Fact]
    public void ExtraIsScalarOnlyAndKeepsAtMostThirtyTwoValidNames()
    {
        var extra = new Dictionary<string, object?> { ["bad-name"] = 1, ["nested"] = new { hidden = 1 }, ["nan"] = float.NaN };
        foreach (int i in Enumerable.Range(0, 40)) extra["field" + i] = new string('x', 241);
        var actual = Build(Running() with { Extra = extra }).GetProperty("extra");
        Assert.Equal(32, actual.EnumerateObject().Count());
        Assert.False(actual.TryGetProperty("bad-name", out _));
        Assert.Equal(JsonValueKind.Null, actual.GetProperty("nested").ValueKind);
        Assert.Equal(JsonValueKind.Null, actual.GetProperty("nan").ValueKind);
        Assert.Equal(new string('x', 239) + "\u2026", actual.GetProperty("field0").GetString());
        Assert.True(actual.TryGetProperty("field29", out _));
        Assert.False(actual.TryGetProperty("field30", out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1451)]
    public void AnIndexOutsideThePlanMakesTheStateInvalid(int index)
    {
        var method = Running();
        Quiet(Build(method with { Session = method.Session with { Index = index } }), "invalid");
    }

    [Fact]
    public void OnlyARunningSessionPublishesAnIndex()
    {
        var method = Running();
        var block = Build(method with { Session = method.Session with { State = MethodState.Done, Index = 5 } });
        Assert.Equal(JsonValueKind.Null, block.GetProperty("session").GetProperty("index").ValueKind);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1451)]
    public void CompletedStepsOutsideThePlanMakeTheStateInvalid(int completed)
    {
        Quiet(Build(Running() with { Progress = new MethodProgress(completed, 1450) }), "invalid");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalWithoutProvisionalValuesIsNone(bool empty)
    {
        var block = Build(Running() with { Results = new MethodResults(empty ? new Dictionary<string, object?>() : null, true) });
        Assert.Equal("none", block.GetProperty("results").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, block.GetProperty("results").GetProperty("provisional").ValueKind);
    }

    [Fact]
    public void UnknownNumbersAndOptionalValuesStayNull()
    {
        var method = Running();
        var block = Build(method with
        {
            Unit = method.Unit with { Hash = null }, Plan = method.Plan with { Hash = null },
            Session = method.Session with { Step = "", Phase = null, Index = null }, LastWindow = null,
            Results = new MethodResults(new Dictionary<string, object?> { ["unknown"] = double.NaN }, false)
        });
        foreach (string name in new[] { "hash", "version" }) Assert.Equal(JsonValueKind.Null, block.GetProperty("unit").GetProperty(name).ValueKind);
        foreach (string name in new[] { "hash", "estimatedSeconds" }) Assert.Equal(JsonValueKind.Null, block.GetProperty("plan").GetProperty(name).ValueKind);
        foreach (string name in new[] { "step", "phase", "index", "remainingSeconds", "personNeeded" })
            Assert.Equal(JsonValueKind.Null, block.GetProperty("session").GetProperty(name).ValueKind);
        Assert.Equal(JsonValueKind.Null, block.GetProperty("windows").GetProperty("last").ValueKind);
        Assert.Equal(JsonValueKind.Null, block.GetProperty("results").GetProperty("provisional").GetProperty("unknown").ValueKind);
    }

    [Fact]
    public void AnOverlongHashIsClippedLikeAnyOtherText()
    {
        var method = Running();
        var block = Build(method with { Unit = method.Unit with { Hash = new string('h', 65) } });
        Assert.Equal(new string('h', 63) + "…", block.GetProperty("unit").GetProperty("hash").GetString());
    }

    [Fact]
    public void TextThatIsOnlyControlCharactersIsNotPublishedAsABlankString()
    {
        var method = Running();
        var block = Build(method with
        {
            Session = method.Session with { Step = "\u0001\u0002" },
            Issues = [new MethodIssue("\u0001", "\u0001", Now), new MethodIssue(null, "kept", Now)]
        });
        Assert.Equal(JsonValueKind.Null, block.GetProperty("session").GetProperty("step").ValueKind);
        Assert.Equal(["kept"], block.GetProperty("issues").EnumerateArray().Select(issue => issue.GetProperty("message").GetString()));
    }

    [Fact]
    public void InvalidWindowWithoutAReasonGetsAnExplanation()
    {
        var window = Build(Running() with { LastWindow = new MethodWindow(false, "") }).GetProperty("windows").GetProperty("last");
        Assert.False(window.GetProperty("valid").GetBoolean());
        Assert.Equal("The run was not valid.", window.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("ruleset")]
    [InlineData("unit")]
    [InlineData("plan")]
    [InlineData("planTotal")]
    [InlineData("progressTotal")]
    [InlineData("totalsDiffer")]
    [InlineData("state")]
    [InlineData("nullUnit")]
    public void BrokenStateBecomesInvalidQuietForm(string broken)
    {
        var method = Running();
        method = broken switch
        {
            "ruleset" => method with { RulesetId = "" },
            "unit" => method with { Unit = method.Unit with { Id = "" } },
            "plan" => method with { Plan = method.Plan with { Id = "" } },
            "planTotal" => method with { Plan = method.Plan with { TotalSteps = -1 } },
            "progressTotal" => method with { Progress = new MethodProgress(0, -1) },
            "totalsDiffer" => method with { Progress = new MethodProgress(0, 10) },
            "state" => method with { Session = method.Session with { State = (MethodState)99 } },
            _ => method with { Unit = null! }
        };
        var block = Build(method);
        Quiet(block, "invalid");
        Assert.NotEmpty(block.GetProperty("error").GetString()!);
    }

    [Fact]
    public void UnreadableStateIsReportedWithoutTheTextOfItsException()
    {
        var block = Build(Running() with { Extra = new UnreadableValues() });
        Quiet(block, "invalid");
        Assert.Equal("Invalid benchmark method state.", block.GetProperty("error").GetString());
    }

    [Fact]
    public void CountedZeroStepsStayZeroWithoutInventingAnActiveStep()
    {
        var method = Running();
        var block = Build(method with
        {
            Plan = method.Plan with { TotalSteps = 0 }, Progress = new MethodProgress(0, 0), Session = method.Session with { Index = null }
        });
        Assert.Equal(0, block.GetProperty("plan").GetProperty("totalSteps").GetInt32());
        Assert.Equal(0, block.GetProperty("session").GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Null, block.GetProperty("session").GetProperty("index").ValueKind);
        Assert.Equal(0, block.GetProperty("progress").GetProperty("completed").GetInt32());
        Assert.Equal(0, block.GetProperty("progress").GetProperty("total").GetInt32());
    }

    private static Dictionary<string, object?> Fields(int count, string text) =>
        Enumerable.Range(0, count).ToDictionary(i => "field" + i, _ => (object?)text);

    private static MonitoringMethod Large(int resultFields, int detailFields, int issueCount, int extraFields)
    {
        var method = Running();
        string wide = new('\u4e00', 240);
        return method with
        {
            Unit = method.Unit with { Details = Fields(detailFields, wide) },
            Results = new MethodResults(Fields(resultFields, wide), false), Extra = Fields(extraFields, wide),
            Issues = Enumerable.Range(0, issueCount).Select(i => new MethodIssue("issue" + i, wide, Now.AddSeconds(i))).ToArray()
        };
    }

    [Fact]
    public void SizeReductionDropsExtraFromTheEndBeforeIssuesOrDetails()
    {
        var block = Build(Large(8, 4, 3, 32));
        var names = block.GetProperty("extra").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.InRange(names.Length, 1, 31);
        Assert.Equal(Enumerable.Range(0, names.Length).Select(i => "field" + i), names);
        Assert.Equal(3, block.GetProperty("issues").GetArrayLength());
        Assert.Equal(4, block.GetProperty("unit").GetProperty("details").EnumerateObject().Count());
    }

    [Fact]
    public void SizeReductionDropsOldestIssuesBeforeDetails()
    {
        var block = Build(Large(10, 4, 10, 1));
        Assert.Empty(block.GetProperty("extra").EnumerateObject());
        var issues = block.GetProperty("issues").EnumerateArray().ToArray();
        Assert.InRange(issues.Length, 1, 9);
        Assert.Equal(Enumerable.Range(10 - issues.Length, issues.Length).Select(i => "issue" + i), issues.Select(i => i.GetProperty("field").GetString()));
        Assert.Equal(4, block.GetProperty("unit").GetProperty("details").EnumerateObject().Count());
    }

    [Fact]
    public void SizeReductionFinallyDropsDetailsFromTheEnd()
    {
        var block = Build(Large(16, 16, 10, 1));
        Assert.Empty(block.GetProperty("extra").EnumerateObject());
        Assert.Empty(block.GetProperty("issues").EnumerateArray());
        var names = block.GetProperty("unit").GetProperty("details").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.InRange(names.Length, 1, 15);
        Assert.Equal(Enumerable.Range(0, names.Length).Select(i => "field" + i), names);
        Assert.Equal(16, block.GetProperty("results").GetProperty("provisional").EnumerateObject().Count());
    }

    [Fact]
    public void IrreducibleOversizePublishesTheRequiredInvalidError()
    {
        var method = Large(16, 16, 10, 32);
        // An unassigned code point is written as a six-byte \uXXXX escape, so the values alone outgrow the limit.
        string escaped = new('\uffff', 240);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(escaped, WriterOptions).Length > 6 * escaped.Length);
        var block = Build(method with { Results = new MethodResults(Fields(16, escaped), false) });
        Quiet(block, "invalid");
        Assert.Equal("Method block too large.", block.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    public void IndependentCheckerDetectsEachContractRule(int rule)
    {
        var node = JsonNode.Parse(Build(Running()).GetRawText())!;
        switch (rule)
        {
            case 1: node["unknown"] = new string('x', MaxBlockBytes); break;
            case 2: node["schema"] = "wrong"; break;
            case 3: node["available"] = false; break;
            case 4: node["error"] = "not null"; break;
            case 5: node["refreshedUtc"] = "2026-11-10T09:41:37Z"; break;
            case 6: node["ruleset"] = null; break;
            case 7: node["ruleset"]!["id"] = ""; break;
            case 8: node["unit"]!["details"]!["nested"] = new JsonObject(); break;
            case 9: node["plan"]!["runs"] = -1; break;
            case 10: node["session"]!["index"] = 1451; break;
            case 11: node["session"]!["personNeeded"] = new JsonObject { ["kind"] = "confirm", ["text"] = "Check" }; break;
            case 12: node["plan"]!["status"] = "sealed"; break;
            case 13: node["progress"]!["completed"] = 1451; break;
            case 14: node["windows"]!["last"] = new JsonObject { ["valid"] = false, ["reason"] = null }; break;
            case 15: node["results"]!["status"] = "none"; break;
            case 16: node["issues"] = new JsonArray(new JsonObject { ["field"] = null, ["message"] = "", ["utc"] = "wrong" }); break;
            case 17: node["extra"]!["bad-name"] = true; break;
            case 18: node["unknown"] = "control\ncharacter"; break;
        }
        using var parsed = JsonDocument.Parse(node.ToJsonString(WriterOptions));
        Assert.Contains(MethodBlockRules.Validate(parsed.RootElement), violation => violation.StartsWith($"Rule {rule}:", StringComparison.Ordinal));
    }

    private sealed class UnreadableValues : IReadOnlyDictionary<string, object?>
    {
        public int Count => 1;
        public IEnumerable<string> Keys => ["field"];
        public IEnumerable<object?> Values => [null];
        public object? this[string key] => null;
        public bool ContainsKey(string key) => key == "field";
        public bool TryGetValue(string key, out object? value) { value = null; return ContainsKey(key); }
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
            throw new InvalidOperationException("Failure\n" + new string('x', 300));
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
