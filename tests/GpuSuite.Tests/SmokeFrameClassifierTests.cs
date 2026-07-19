using GpuSuite.Engine.Diagnostics;
using GpuSuite.Engine.Vision;
using Xunit;

namespace GpuSuite.Tests;

public sealed class SmokeFrameClassifierTests
{
    [Theory]
    [InlineData("Please sign in to continue")]
    [InlineData("Update required")]
    [InlineData("Accept the license agreement")]
    [InlineData("Game files are corrupt")]
    public void Explicit_blocker_text_is_blocked(string text)
    {
        var finding = SmokeFrameClassifier.Classify(Frame(text));
        Assert.Equal(SmokeFrameVerdict.Blocked, finding.Verdict);
    }

    [Fact]
    public void Ordinary_account_menu_is_not_a_login_blocker()
    {
        var finding = SmokeFrameClassifier.Classify(Frame("Account", "Options", "Play"));
        Assert.Equal(SmokeFrameVerdict.Clear, finding.Verdict);
    }

    [Fact]
    public void No_signal_slate_is_blocked()
    {
        var finding = SmokeFrameClassifier.Classify(Frame("NO", "SIGNAL", "elgato"));
        Assert.Equal(SmokeFrameVerdict.Blocked, finding.Verdict);
    }

    [Fact]
    public void Missing_ocr_is_a_warning_not_a_false_pass()
    {
        var finding = SmokeFrameClassifier.Classify(null);
        Assert.Equal(SmokeFrameVerdict.Warning, finding.Verdict);
    }

    [Fact]
    public void Blank_captured_frame_is_a_warning_not_a_false_pass()
    {
        var finding = SmokeFrameClassifier.Classify(Frame());
        Assert.Equal(SmokeFrameVerdict.Warning, finding.Verdict);
    }

    [Fact]
    public void Crash_recovery_prompt_is_a_warning()
    {
        var finding = SmokeFrameClassifier.Classify(Frame("Your last session ended unexpectedly", "Continue", "Safe Mode"));
        Assert.Equal(SmokeFrameVerdict.Warning, finding.Verdict);
    }

    private static OcrFrame Frame(params string[] lines) => new()
    {
        Width = 3840,
        Height = 2160,
        Lines = lines.Select((text, i) => new OcrLine { Text = text, Y = i * 30, H = 20 }).ToList()
    };
}
