using GpuSuite.App.Services;
using Xunit;

namespace GpuSuite.Tests;

public sealed class DisclaimerServiceTests
{
    [Fact]
    public void RememberedAcceptanceWritesTheCurrentVersion()
    {
        WithTemporaryDirectory(path =>
        {
            string acknowledgement = Path.Combine(path, "disclaimer-acknowledgement.txt");
            DateTime now = new(2026, 7, 19, 12, 34, 56, DateTimeKind.Utc);
            var service = new DisclaimerService(acknowledgement, () => (true, true), () => now, _ => throw new Xunit.Sdk.XunitException("Unexpected warning."));

            Assert.True(service.EnsureAcknowledged());
            string saved = File.ReadAllText(acknowledgement);
            Assert.Contains($"Version={DisclaimerService.CurrentVersion}", saved);
            Assert.Contains($"AcknowledgedUtc={now:O}", saved);
            Assert.True(service.IsCurrentVersionAcknowledged());
        });
    }

    [Fact]
    public void AcceptedButNotRememberedDoesNotWriteAnAcknowledgement()
    {
        WithTemporaryDirectory(path =>
        {
            string acknowledgement = Path.Combine(path, "disclaimer-acknowledgement.txt");
            var service = new DisclaimerService(acknowledgement, () => (true, false), () => DateTime.UtcNow, _ => { });

            Assert.True(service.EnsureAcknowledged());
            Assert.False(File.Exists(acknowledgement));
        });
    }

    [Fact]
    public void RejectedDisclaimerDoesNotWriteAnAcknowledgement()
    {
        WithTemporaryDirectory(path =>
        {
            string acknowledgement = Path.Combine(path, "disclaimer-acknowledgement.txt");
            var service = new DisclaimerService(acknowledgement, () => (false, false), () => DateTime.UtcNow, _ => { });

            Assert.False(service.EnsureAcknowledged());
            Assert.False(File.Exists(acknowledgement));
        });
    }

    [Fact]
    public void ExistingCurrentVersionBypassesThePrompt()
    {
        WithTemporaryDirectory(path =>
        {
            string acknowledgement = Path.Combine(path, "disclaimer-acknowledgement.txt");
            File.WriteAllText(acknowledgement, $"Version={DisclaimerService.CurrentVersion}{Environment.NewLine}");
            int prompts = 0;
            var service = new DisclaimerService(acknowledgement, () =>
            {
                prompts++;
                return (false, false);
            }, () => DateTime.UtcNow, _ => { });

            Assert.True(service.EnsureAcknowledged());
            Assert.Equal(0, prompts);
        });
    }

    [Fact]
    public void PersistenceFailureWarnsButStillAllowsTheAcceptedLaunch()
    {
        WithTemporaryDirectory(path =>
        {
            string blockedDirectory = Path.Combine(path, "not-a-directory");
            File.WriteAllText(blockedDirectory, "file deliberately blocks acknowledgement directory");
            string acknowledgement = Path.Combine(blockedDirectory, "disclaimer-acknowledgement.txt");
            var warnings = new List<string>();
            var service = new DisclaimerService(acknowledgement, () => (true, true), () => DateTime.UtcNow, warnings.Add);

            Assert.True(service.EnsureAcknowledged());
            Assert.Single(warnings);
            Assert.False(File.Exists(acknowledgement));
        });
    }

    private static void WithTemporaryDirectory(Action<string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "GpuSuiteDisclaimerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
