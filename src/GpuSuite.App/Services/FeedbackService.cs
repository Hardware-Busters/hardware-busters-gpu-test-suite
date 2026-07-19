using System.Diagnostics;
namespace GpuSuite.App.Services;
public sealed class FeedbackService
{
    public const string IssueTrackerUrl = "https://github.com/crmaris/hardware-busters-gpu-test-suite/issues/new/choose";
    public FeedbackService(Workspace workspace) { }
    public void OpenPublicIssue()
    {
        // Public builds only open the issue form. They never collect or transmit local diagnostics.
        if (Process.Start(new ProcessStartInfo(IssueTrackerUrl) { UseShellExecute = true }) is null)
            throw new InvalidOperationException("Could not open the public issue tracker.");
    }
}