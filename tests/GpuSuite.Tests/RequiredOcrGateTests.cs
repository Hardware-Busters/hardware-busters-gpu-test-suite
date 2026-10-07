using GpuSuite.Core.Diagnostics;
using GpuSuite.Engine.Automation;
using Xunit;

namespace GpuSuite.Tests;

public class RequiredOcrGateTests
{
    [Theory]
    [InlineData(BotActionType.WaitForText, true, true, true)]
    [InlineData(BotActionType.PressUntilText, true, true, true)]
    [InlineData(BotActionType.WaitForText, true, false, false)]
    [InlineData(BotActionType.PressUntilText, true, false, false)]
    [InlineData(BotActionType.WaitForText, false, true, false)]
    [InlineData(BotActionType.PressUntilText, false, true, false)]
    public async Task MissingOcr_AbortsRequiredRealGateBeforeMeasurement(
        BotActionType type, bool inject, bool required, bool aborts)
    {
        using var log = new RunLogger(null, echoToConsole: false);
        using var engine = new InputAutomationEngine(log, inject) { FastForward = true };
        // No key or target process: this regression test must never inject desktop input.
        var script = new BotScript
        {
            Id = "missing-ocr", Loop = false,
            Actions = [new BotAction { Type = type, Text = "Hebeth", Required = required, DurationMs = 1 }, BotAction.Begin()]
        };

        var result = await engine.RunAsync(script, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(aborts, result.Aborted);
        if (aborts)
        {
            Assert.Contains("cannot be evaluated", result.AbortReason);
            Assert.Contains("OCR", result.AbortReason);
            Assert.Null(result.StartOffsetSec);
        }
        else Assert.NotNull(result.StartOffsetSec);
    }
}
