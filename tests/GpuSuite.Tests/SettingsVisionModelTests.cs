using GpuSuite.App.ViewModels;
using GpuSuite.Core.Remote;
using Xunit;

namespace GpuSuite.Tests;

public sealed class SettingsVisionModelTests
{
    [Fact]
    public void ProbeStatusChecksTheEditedCustomAiModelInsteadOfTheOldDefault()
    {
        var status = new Gx10Status
        {
            Reachable = true,
            Version = "test",
            Models = [new Gx10Model { Name = "qwen2.5vl:7b" }]
        };

        string result = SettingsViewModel.DescribeReachableStatus(status, "gemma3:12b");

        Assert.Contains("'gemma3:12b' NOT pulled", result);
        Assert.DoesNotContain("'qwen2.5vl:7b' present", result);
    }

    [Fact]
    public void SettingsViewExposesTheModelUsedByTheCustomAiProbe()
    {
        string root = RepoRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "src", "GpuSuite.App", "Views", "SettingsView.xaml"));

        Assert.Contains("Config.NavSupervisorVisionModel", xaml);
        Assert.Contains("CUSTOM AI VISION MODEL", xaml);
        Assert.Contains("These separate custom-local fields are used only by the Custom local Ollama option", xaml);
    }

    private static string RepoRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "GpuTestSuite.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
