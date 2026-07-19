using System.Runtime.ExceptionServices;
using System.Threading;
using GpuSuite.App;
using Xunit;

namespace GpuSuite.Tests;

public sealed class StartupLifecycleTests
{
    [Fact]
    public void AcceptedDisclaimerAssignsTheMainWindowBeforeItChangesShutdownOwnershipAndShows()
    {
        RunInSta(() =>
        {
            var lifetime = new RecordingLifetime();

            bool started = new StartupLifecycleCoordinator(lifetime).Start(() =>
            {
                lifetime.Events.Add("accept");
                return true;
            });

            Assert.True(started);
            Assert.Equal(
                ["accept", "assign-main-window", "shutdown-mode-on-main-window-close", "show-main-window"],
                lifetime.Events);
            Assert.Equal(0, lifetime.ShutdownCount);
        });
    }

    [Fact]
    public void RejectedOrClosedDisclaimerShutsDownOnceWithoutShowingAWindow()
    {
        RunInSta(() =>
        {
            var lifetime = new RecordingLifetime();

            bool started = new StartupLifecycleCoordinator(lifetime).Start(() =>
            {
                lifetime.Events.Add("accept");
                return false;
            });

            Assert.False(started);
            Assert.Equal(["accept", "shutdown"], lifetime.Events);
            Assert.Equal(1, lifetime.ShutdownCount);
            Assert.False(lifetime.MainWindowShown);
        });
    }

    [Fact]
    public void AcceptedLifecycleTerminatesExactlyOnceWhenTheMainWindowCloses()
    {
        RunInSta(() =>
        {
            var lifetime = new RecordingLifetime();
            var coordinator = new StartupLifecycleCoordinator(lifetime);

            Assert.True(coordinator.Start(() => true));
            lifetime.CloseMainWindow();
            lifetime.CloseMainWindow();

            Assert.Equal(1, lifetime.ShutdownCount);
            Assert.False(lifetime.MainWindowShown);
            Assert.Equal(0, lifetime.VisibleWindowCount);
        });
    }

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class RecordingLifetime : IApplicationLifetimeAdapter
    {
        private bool _shutdownModeOnMainWindowClose;
        private bool _shutdown;

        public List<string> Events { get; } = [];
        public bool MainWindowShown { get; private set; }
        public int ShutdownCount { get; private set; }
        public int VisibleWindowCount => MainWindowShown ? 1 : 0;

        public void AssignMainWindow() => Events.Add("assign-main-window");

        public void SetShutdownModeOnMainWindowClose()
        {
            _shutdownModeOnMainWindowClose = true;
            Events.Add("shutdown-mode-on-main-window-close");
        }

        public void ShowMainWindow()
        {
            MainWindowShown = true;
            Events.Add("show-main-window");
        }

        public void Shutdown()
        {
            if (_shutdown) return;
            _shutdown = true;
            ShutdownCount++;
            MainWindowShown = false;
            Events.Add("shutdown");
        }

        public void CloseMainWindow()
        {
            MainWindowShown = false;
            if (_shutdownModeOnMainWindowClose) Shutdown();
        }
    }
}
