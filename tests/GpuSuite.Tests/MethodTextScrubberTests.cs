using GpuSuite.Engine.Orchestration;
using Xunit;

namespace GpuSuite.Tests;

public sealed class MethodTextScrubberTests
{
    // The placeholders are expanded at run time so that no drive or network path appears literally in this source file.
    private static string Expand(string text) => text
        .Replace("%C%", "C" + ":" + "\\")
        .Replace("%D%", "D" + ":" + "/")
        .Replace("%UNC%", new string('\\', 2));

    [Theory]
    [InlineData(@"Could not find file '%C%Users\someone\My Games\profile.ini'.", "Could not find file '[path]'.")]
    [InlineData(@"Could not find file ""%D%data/run 1/out.csv"" today", @"Could not find file ""[path]"" today")]
    [InlineData(@"Share %UNC%storage\Shared Folder\Data is unreachable", "Share [path]")]
    [InlineData(@"Saved to %C%Temp\x.json", "Saved to [path]")]
    public void AbsolutePathsAreRemoved(string text, string expected)
    {
        Assert.Equal(expected, MethodTextScrubber.Scrub(Expand(text)));
    }

    [Fact]
    public void PlainTextIsLeftAlone()
    {
        const string text = "Benchmark start was not detected. Rendered at 1920x1080, ratio 16:9 (C: is not a path here).";

        Assert.Equal(text, MethodTextScrubber.Scrub(text));
    }

    [Fact]
    public void TheUserNameIsRemovedAsAWholeWordOnly()
    {
        string user = Environment.UserName;
        if (string.IsNullOrWhiteSpace(user)) return;

        Assert.Equal("profile of [user] failed", MethodTextScrubber.Scrub($"profile of {user} failed"));
        Assert.Equal($"{user}x stays", MethodTextScrubber.Scrub($"{user}x stays"));
    }
}
