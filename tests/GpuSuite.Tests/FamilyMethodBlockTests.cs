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
    private const int MaxBlockBytes = 32_768;
    private static readonly DateTime Now = new(2026, 11, 10, 9, 41, 37, 500, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static MonitoringMethod Running() => new("gpu-benchmark-v1",
        new MethodUnit("gpu-run-1", new string('a', 64), new Dictionary<string, object?> { ["model"] = "Example GPU" }),
        new MethodPlan("plan-1", new string('b', 64), 1450),
        new MethodSession(MethodState.Running, "benchmark cell 412 of 1450", "capture", 412),
        new MethodProgress(411, 1450), new MethodWindow(true, null),
        new MethodResults(new Dictionary<string, object?> { ["completedCells"] = 411, ["averageFps"] = 91.84 }, false),
        [], new Dictionary<string, object?>(), Now.AddSeconds(-30));

    private static JsonElement Build(MonitoringMethod? method, bool online = true, int maxBlockBytes = MaxBlockBytes)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(FamilyMethodBlock.Build(method, online, Now, maxBlockBytes), WriterOptions);
        Assert.True(json.Length <= MaxBlockBytes);
        using var parsed = JsonDocument.Parse(json);
        var block = parsed.RootElement.Clone();
        Assert.Empty(MethodBlockRules.Validate(block));
        if (block.GetProperty("available").GetBoolean()) Assert.True(json.Length <= maxBlockBytes);
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
        Assert.Equal(new string('h', 63) + "\u2026", block.GetProperty("unit").GetProperty("hash").GetString());
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

    [Fact]
    public void PlanStepsArePublishedInPlanOrderWithAllFourStateWords()
    {
        var method = Running();
        MethodStep[] steps = [new("first", StepState.Done), new("second", StepState.Now), new("third", StepState.Todo), new("fourth", StepState.Skipped)];
        var block = Build(method with
        {
            Plan = method.Plan with { Steps = steps }, Session = method.Session with { Index = 2 },
            Progress = new MethodProgress(1, method.Plan.TotalSteps)
        });
        var actual = block.GetProperty("plan").GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal(steps.Select(step => step.Label), actual.Select(step => step.GetProperty("label").GetString()));
        Assert.Equal(["done", "now", "todo", "skipped"], actual.Select(step => step.GetProperty("state").GetString()));
    }

    [Fact]
    public void PlanStepsAreNullWhenThePlanHasNoList()
    {
        Assert.Equal(JsonValueKind.Null, Build(Running()).GetProperty("plan").GetProperty("steps").ValueKind);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(97)]
    public void StepLabelsAreCleanedAndClippedLikeTheSessionStep(int length)
    {
        var method = Running();
        string label = "before\n\t" + new string('x', length - 8);
        var block = Build(method with
        {
            Plan = method.Plan with { Steps = [new(label, StepState.Todo)] },
            Session = method.Session with { Step = label }, Progress = new MethodProgress(0, method.Plan.TotalSteps)
        });
        string expected = "before  " + new string('x', Math.Min(length, 96) - 8 - (length > 96 ? 1 : 0)) + (length > 96 ? "\u2026" : "");
        Assert.Equal(expected, block.GetProperty("plan").GetProperty("steps")[0].GetProperty("label").GetString());
        Assert.Equal(expected, block.GetProperty("session").GetProperty("step").GetString());
    }

    [Fact]
    public void StepLabelsAreClippedWithoutSplittingASurrogatePair()
    {
        var method = Running();
        string label = new string('x', 94) + "\ud83d\ude00x";
        var block = Build(method with
        {
            Plan = method.Plan with { Steps = [new(label, StepState.Todo)] }, Progress = new MethodProgress(0, method.Plan.TotalSteps)
        });
        Assert.Equal(new string('x', 94) + "\u2026", block.GetProperty("plan").GetProperty("steps")[0].GetProperty("label").GetString());
    }

    [Theory]
    [InlineData("tooLong", "The step list is longer than the plan.")]
    [InlineData("emptyLabel", "A plan step has no label or an unknown state.")]
    [InlineData("blankLabel", "A plan step has no label or an unknown state.")]
    [InlineData("controlLabel", "A plan step has no label or an unknown state.")]
    [InlineData("unknownState", "A plan step has no label or an unknown state.")]
    [InlineData("twoNow", "More than one plan step is in progress.")]
    [InlineData("doneNow", "A plan step is in progress although the run is not.")]
    [InlineData("doneTodo", "A finished run still has a plan step waiting.")]
    [InlineData("abortedNow", "A plan step is in progress although the run is not.")]
    [InlineData("wrongIndex", "The step in progress is not the one the session names.")]
    public void InvalidStepsPublishTheQuietFormWithASpecificError(string failure, string expectedError)
    {
        var method = Running();
        method = method with
        {
            Plan = method.Plan with { Steps = [new("first", StepState.Now)] }, Session = method.Session with { Index = 1 },
            Progress = new MethodProgress(0, method.Plan.TotalSteps)
        };
        method = failure switch
        {
            "tooLong" => method with { Plan = method.Plan with { TotalSteps = 0 }, Progress = new MethodProgress(0, 0), Session = method.Session with { Index = null } },
            "emptyLabel" => method with { Plan = method.Plan with { Steps = [new("", StepState.Todo)] } },
            "blankLabel" => method with { Plan = method.Plan with { Steps = [new("   ", StepState.Todo)] } },
            "controlLabel" => method with { Plan = method.Plan with { Steps = [new("\n\t", StepState.Todo)] } },
            "unknownState" => method with { Plan = method.Plan with { Steps = [new("first", (StepState)99)] } },
            "twoNow" => method with { Plan = method.Plan with { Steps = [new("first", StepState.Now), new("second", StepState.Now)] } },
            "doneNow" => method with { Session = method.Session with { State = MethodState.Done } },
            "doneTodo" => method with
            {
                Plan = method.Plan with { Steps = [new("first", StepState.Todo)] },
                Session = method.Session with { State = MethodState.Done }
            },
            "abortedNow" => method with { Session = method.Session with { State = MethodState.Aborted } },
            "wrongIndex" => method with { Session = method.Session with { Index = 2 } },
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };
        var block = Build(method);
        Quiet(block, "invalid");
        Assert.NotEmpty(block.GetProperty("error").GetString()!);
        Assert.Equal(expectedError, block.GetProperty("error").GetString());
    }

    [Fact]
    public void ALongPlanPublishesOnlyItsFirst256Steps()
    {
        var method = Running();
        var steps = Enumerable.Range(1, 300).Select(i => new MethodStep("step " + i, StepState.Todo)).ToArray();
        var actual = Build(method with
        {
            Plan = method.Plan with { Steps = steps }, Progress = new MethodProgress(0, method.Plan.TotalSteps)
        }).GetProperty("plan").GetProperty("steps");
        Assert.Equal(256, actual.GetArrayLength());
        Assert.Equal(steps.Take(256).Select(step => step.Label), actual.EnumerateArray().Select(step => step.GetProperty("label").GetString()));
    }

    [Fact]
    public void InvalidStepsBeyondThePublishedPrefixAreStillRejected()
    {
        var method = Running();
        var steps = Enumerable.Range(1, 257).Select(i => new MethodStep("step " + i, StepState.Todo)).ToArray();
        steps[^1] = new MethodStep("", StepState.Todo);
        Quiet(Build(method with
        {
            Plan = method.Plan with { Steps = steps }, Progress = new MethodProgress(0, method.Plan.TotalSteps)
        }), "invalid");
    }

    private static Dictionary<string, object?> Fields(int count, string text) =>
        Enumerable.Range(0, count).ToDictionary(i => "field" + i, _ => (object?)text);

    private static MonitoringMethod ShrinkFixture()
    {
        var method = Running();
        string text = new('x', 120);
        return method with
        {
            Plan = method.Plan with { Steps = [new("finished", StepState.Done), new("next", StepState.Todo), new("last", StepState.Todo)] },
            Progress = new MethodProgress(1, method.Plan.TotalSteps),
            Unit = method.Unit with { Details = Fields(3, text) },
            Extra = Fields(3, text),
            Issues = Enumerable.Range(0, 3).Select(i => new MethodIssue("issue" + i, text, Now.AddSeconds(i))).ToArray()
        };
    }

    // Derive an exact byte budget from the expected document, so only the requested reductions can fit.
    private static JsonElement ShrinkTo(MonitoringMethod method, Action<JsonNode> reduce)
    {
        var expected = JsonNode.Parse(Build(method).GetRawText())!;
        reduce(expected);
        string json = expected.ToJsonString(WriterOptions);
        int limit = System.Text.Encoding.UTF8.GetByteCount(json);
        var actual = Build(method, maxBlockBytes: limit);
        Assert.Equal(json, actual.GetRawText());
        return actual;
    }

    [Fact]
    public void SizeReductionDropsExtraFromTheEndBeforeIssuesOrDetails()
    {
        var block = ShrinkTo(ShrinkFixture(), expected =>
        {
            expected["plan"]!["steps"] = null;
            expected["extra"]!.AsObject().Remove("field2");
        });
        Assert.Equal(JsonValueKind.Null, block.GetProperty("plan").GetProperty("steps").ValueKind);
        Assert.Equal(["field0", "field1"], block.GetProperty("extra").EnumerateObject().Select(p => p.Name));
        Assert.Equal(3, block.GetProperty("issues").GetArrayLength());
        Assert.Equal(3, block.GetProperty("unit").GetProperty("details").EnumerateObject().Count());
    }

    [Fact]
    public void SizeReductionDropsOldestIssuesBeforeDetails()
    {
        var block = ShrinkTo(ShrinkFixture(), expected =>
        {
            expected["plan"]!["steps"] = null;
            expected["extra"] = new JsonObject();
            expected["issues"]!.AsArray().RemoveAt(0);
        });
        Assert.Empty(block.GetProperty("extra").EnumerateObject());
        Assert.Equal(["issue1", "issue2"], block.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("field").GetString()));
        Assert.Equal(3, block.GetProperty("unit").GetProperty("details").EnumerateObject().Count());
    }

    [Fact]
    public void SizeReductionFinallyDropsDetailsFromTheEnd()
    {
        var block = ShrinkTo(ShrinkFixture(), expected =>
        {
            expected["plan"]!["steps"] = null;
            expected["extra"] = new JsonObject();
            expected["issues"] = new JsonArray();
            expected["unit"]!["details"]!.AsObject().Remove("field2");
        });
        Assert.Empty(block.GetProperty("extra").EnumerateObject());
        Assert.Empty(block.GetProperty("issues").EnumerateArray());
        Assert.Equal(["field0", "field1"], block.GetProperty("unit").GetProperty("details").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void IrreducibleOversizePublishesTheRequiredInvalidError()
    {
        var block = Build(ShrinkFixture(), maxBlockBytes: 1);
        Quiet(block, "invalid");
        Assert.Equal("Method block too large.", block.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SizeReductionDropsTrailingTodoEntriesOneAtATimeKeepingAPrefix(int retained)
    {
        var method = ShrinkFixture();
        var block = ShrinkTo(method, expected =>
        {
            var steps = expected["plan"]!["steps"]!.AsArray();
            while (steps.Count > retained) steps.RemoveAt(steps.Count - 1);
        });
        Assert.Equal(method.Plan.Steps!.Take(retained).Select(step => step.Label),
            block.GetProperty("plan").GetProperty("steps").EnumerateArray().Select(step => step.GetProperty("label").GetString()));
    }

    [Theory]
    [InlineData(StepState.Done)]
    [InlineData(StepState.Now)]
    [InlineData(StepState.Skipped)]
    public void SizeReductionDropsTheWholeListWhenItsLastEntryIsNotTodo(StepState lastState)
    {
        var method = ShrinkFixture();
        method = method with
        {
            Plan = method.Plan with { Steps = [new("earlier todo", StepState.Todo), new("last", lastState)] },
            Session = method.Session with { Index = 2 },
            Progress = new MethodProgress(lastState == StepState.Done ? 1 : 0, method.Plan.TotalSteps)
        };
        var block = ShrinkTo(method, expected => expected["plan"]!["steps"] = null);
        Assert.Equal(JsonValueKind.Null, block.GetProperty("plan").GetProperty("steps").ValueKind);
    }

    [Fact]
    public void SizeReductionDropsAnEmptyListBeforeExtra()
    {
        var method = ShrinkFixture();
        method = method with { Plan = method.Plan with { Steps = [] }, Progress = new MethodProgress(0, method.Plan.TotalSteps) };
        // [] is smaller than null; a smaller budget must also drop the final extra member.
        ShrinkTo(method, expected =>
        {
            expected["plan"]!["steps"] = null;
            expected["extra"]!.AsObject().Remove("field2");
        });
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
    [InlineData(19)]
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
            case 19: node["plan"]!["steps"] = new JsonArray(new JsonObject { ["label"] = "", ["state"] = "todo" }); break;
        }
        using var parsed = JsonDocument.Parse(node.ToJsonString(WriterOptions));
        Assert.Contains(MethodBlockRules.Validate(parsed.RootElement), violation => violation.StartsWith($"Rule {rule}:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("notArray")]
    [InlineData("tooMany")]
    [InlineData("longerThanPlan")]
    [InlineData("notObject")]
    [InlineData("missingLabel")]
    [InlineData("emptyLabel")]
    [InlineData("longLabel")]
    [InlineData("nonTextLabel")]
    [InlineData("missingState")]
    [InlineData("unknownState")]
    [InlineData("twoNow")]
    [InlineData("inactiveNow")]
    [InlineData("wrongIndex")]
    [InlineData("doneTodo")]
    [InlineData("fewerCompletedThanDone")]
    [InlineData("moreCompletedThanDoneAndSkipped")]
    public void IndependentCheckerRejectsEveryStepRuleViolation(string failure)
    {
        var node = JsonNode.Parse(Build(Running()).GetRawText())!;
        node["plan"]!["steps"] = new JsonArray(new JsonObject { ["label"] = "first", ["state"] = "todo" });
        node["progress"]!["completed"] = 0;
        var steps = node["plan"]!["steps"]!.AsArray();
        switch (failure)
        {
            case "notArray": node["plan"]!["steps"] = new JsonObject(); break;
            case "tooMany":
                while (steps.Count <= 256) steps.Add(new JsonObject { ["label"] = "step", ["state"] = "todo" });
                break;
            case "longerThanPlan": node["plan"]!["totalSteps"] = 0; break;
            case "notObject": steps[0] = "step"; break;
            case "missingLabel": steps[0]!.AsObject().Remove("label"); break;
            case "emptyLabel": steps[0]!["label"] = ""; break;
            case "longLabel": steps[0]!["label"] = new string('x', 97); break;
            case "nonTextLabel": steps[0]!["label"] = 1; break;
            case "missingState": steps[0]!.AsObject().Remove("state"); break;
            case "unknownState": steps[0]!["state"] = "pending"; break;
            case "twoNow":
                steps[0]!["state"] = "now";
                steps.Add(new JsonObject { ["label"] = "second", ["state"] = "now" });
                break;
            case "inactiveNow": steps[0]!["state"] = "now"; node["session"]!["state"] = "done"; break;
            case "wrongIndex": steps[0]!["state"] = "now"; break;
            case "doneTodo": node["session"]!["state"] = "done"; break;
            case "fewerCompletedThanDone": steps[0]!["state"] = "done"; break;
            case "moreCompletedThanDoneAndSkipped": node["progress"]!["completed"] = 1; break;
            default: throw new ArgumentOutOfRangeException(nameof(failure));
        }
        using var parsed = JsonDocument.Parse(node.ToJsonString(WriterOptions));
        Assert.Contains(MethodBlockRules.Validate(parsed.RootElement), violation => violation.StartsWith("Rule 19:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("prefix")]
    [InlineData("maximum")]
    [InlineData("labelLimit")]
    [InlineData("running")]
    [InlineData("paused")]
    [InlineData("waitingForPerson")]
    [InlineData("unnumbered")]
    [InlineData("noPlan")]
    public void IndependentCheckerAcceptsOptionalListsPrefixesAndActiveStates(string scenario)
    {
        var node = JsonNode.Parse(Build(Running()).GetRawText())!;
        node["plan"]!["steps"] = new JsonArray(new JsonObject { ["label"] = "first", ["state"] = "todo" });
        node["progress"]!["completed"] = 0;
        switch (scenario)
        {
            case "absent": node["plan"]!.AsObject().Remove("steps"); break;
            case "null": node["plan"]!["steps"] = null; break;
            case "empty": node["plan"]!["steps"] = new JsonArray(); break;
            case "prefix": break;
            case "maximum":
                var steps = node["plan"]!["steps"]!.AsArray();
                while (steps.Count < 256) steps.Add(new JsonObject { ["label"] = "step", ["state"] = "todo" });
                break;
            case "labelLimit": node["plan"]!["steps"]![0]!["label"] = new string('x', 96); break;
            case "noPlan":
                node["plan"] = null;
                node["session"]!["state"] = "done";
                node["session"]!["index"] = null;
                break;
            default:
                node["plan"]!["steps"]![0]!["state"] = "now";
                node["session"]!["state"] = scenario == "unnumbered" ? "running" : scenario;
                node["session"]!["index"] = scenario == "unnumbered" ? null : JsonValue.Create(1);
                if (scenario == "waitingForPerson") node["session"]!["personNeeded"] = new JsonObject { ["kind"] = "confirm", ["text"] = "Continue" };
                break;
        }
        using var parsed = JsonDocument.Parse(node.ToJsonString(WriterOptions));
        Assert.Empty(MethodBlockRules.Validate(parsed.RootElement));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void IndependentCheckerAcceptsProgressWithinDoneAndSkippedCounts(int completed)
    {
        var node = JsonNode.Parse(Build(Running()).GetRawText())!;
        node["plan"]!["steps"] = new JsonArray(
            new JsonObject { ["label"] = "finished", ["state"] = "done" },
            new JsonObject { ["label"] = "cancelled", ["state"] = "skipped" });
        node["progress"]!["completed"] = completed;
        using var parsed = JsonDocument.Parse(node.ToJsonString(WriterOptions));

        Assert.Empty(MethodBlockRules.Validate(parsed.RootElement));
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void IndependentCheckerEnforcesTheNewExtraArrayLimit(int count, bool valid)
    {
        var node = JsonNode.Parse(Build(Running()).GetRawText())!;
        node["extra"]!["items"] = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        using var parsed = JsonDocument.Parse(node.ToJsonString(WriterOptions));
        var violations = MethodBlockRules.Validate(parsed.RootElement);
        if (valid) Assert.Empty(violations);
        else Assert.Contains(violations, violation => violation.StartsWith("Rule 17:", StringComparison.Ordinal));
    }

    private static MonitoringMethod OversizedChecklistFixture()
    {
        const int stepCount = 256;
        const int labelLength = 96;
        var steps = Enumerable.Range(1, stepCount).Select(i => new MethodStep(
            i.ToString("D3", CultureInfo.InvariantCulture).PadRight(labelLength, 'x'),
            i switch { 1 => StepState.Done, 2 => StepState.Now, 3 => StepState.Skipped, _ => StepState.Todo })).ToArray();
        var method = Running();
        return method with
        {
            Plan = method.Plan with { TotalSteps = stepCount, Steps = steps },
            Progress = new MethodProgress(1, stepCount), Session = method.Session with { Index = 2 },
            Extra = Fields(32, new string('e', 240))
        };
    }

    private static void AssertOnlyTrailingTodoStepsWereRemoved(MonitoringMethod method, JsonElement block)
    {
        Assert.True(block.GetProperty("available").GetBoolean());
        var actual = block.GetProperty("plan").GetProperty("steps").EnumerateArray().ToArray();
        var input = method.Plan.Steps!;
        Assert.InRange(actual.Length, 3, input.Count - 1);
        Assert.Equal(input.Take(actual.Length).Select(step => step.Label), actual.Select(step => step.GetProperty("label").GetString()));
        Assert.Equal(input.Take(actual.Length).Select(step => step.State.ToString().ToLowerInvariant()),
            actual.Select(step => step.GetProperty("state").GetString()));
        Assert.All(input.Skip(actual.Length), step => Assert.Equal(StepState.Todo, step.State));
        Assert.Equal(method.Extra.Keys, block.GetProperty("extra").EnumerateObject().Select(property => property.Name));
        Assert.All(block.GetProperty("extra").EnumerateObject(), property => Assert.Equal(method.Extra[property.Name], property.Value.GetString()));
    }

    [Fact]
    public void TheDefaultByteLimitTrimsAFullLengthAsciiChecklistToAPrefix()
    {
        var method = OversizedChecklistFixture();

        var published = FamilyMethodBlock.Build(method, online: true, Now);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(published, WriterOptions);
        using var document = JsonDocument.Parse(json);

        Assert.True(json.Length <= MaxBlockBytes);
        Assert.Empty(MethodBlockRules.Validate(document.RootElement));
        AssertOnlyTrailingTodoStepsWereRemoved(method, document.RootElement);
    }

    [Fact]
    public void TheSizeSeamCannotRaiseThePublishedByteLimit()
    {
        var method = OversizedChecklistFixture();

        var block = Build(method, maxBlockBytes: MaxBlockBytes * 2);

        AssertOnlyTrailingTodoStepsWereRemoved(method, block);
        Assert.Equal(Build(method).GetRawText(), block.GetRawText());
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
