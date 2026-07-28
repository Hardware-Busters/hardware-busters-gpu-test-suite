using GpuSuite.Core.Models;
using GpuSuite.Engine.Diagnostics;
using GpuSuite.Engine.Orchestration;
using GpuSuite.Measurement;
using Xunit;

namespace GpuSuite.Tests;

public sealed class FrameCapturePreflightPolicyTests
{
    [Fact]
    public void PresentMonPinnedAndRtssForbiddenTitlesRequirePresentMon()
    {
        var plan = FrameCapturePreflightPlan.Build(
        [
            new GameProfile { Id = "win32", FrameProvider = "presentmon" },
            new GameProfile { Id = "ratchet", ForbidRtss = true }
        ], "rtss");

        Assert.True(plan.RequiresPresentMon);
        Assert.True(plan.RequiresRtss);
        Assert.False(plan.Evaluate(presentMonAvailable: false, rtssAvailable: true, syntheticFramesForced: false).IsReady);
    }

    [Fact]
    public void RtssPinnedEnabledTitleRequiresRtssButDoesNotRequirePresentMon()
    {
        var plan = FrameCapturePreflightPlan.Build(
            [new GameProfile { Id = "xbox", FrameProvider = "rtss" }], "presentmon");

        Assert.False(plan.RequiresPresentMon);
        Assert.True(plan.RequiresRtss);
        Assert.False(plan.Evaluate(presentMonAvailable: true, rtssAvailable: false, syntheticFramesForced: false).IsReady);
        Assert.True(plan.Evaluate(presentMonAvailable: true, rtssAvailable: true, syntheticFramesForced: false).IsReady);
    }

    [Fact]
    public void AutoRosterRequiresAtLeastOneBackendWithoutForcingRtss()
    {
        var plan = FrameCapturePreflightPlan.Build(
            [new GameProfile { Id = "auto" }], "auto");

        Assert.False(plan.RequiresPresentMon);
        Assert.False(plan.RequiresRtss);
        Assert.True(plan.MayUseRtssFallback);
        Assert.False(plan.Evaluate(presentMonAvailable: false, rtssAvailable: false, syntheticFramesForced: false).IsReady);
        Assert.True(plan.Evaluate(presentMonAvailable: true, rtssAvailable: false, syntheticFramesForced: false).IsReady);
    }

    [Fact]
    public void DisabledProfilesDoNotCreateBackendRequirements()
    {
        var plan = FrameCapturePreflightPlan.Build(
            [new GameProfile { Id = "disabled", Enabled = false, FrameProvider = "rtss" }], "rtss");

        Assert.False(plan.HasEnabledGames);
        Assert.True(plan.Evaluate(false, false, false).IsReady);
    }

    [Fact]
    public void LateRtssRequiresRtssAndRecordsAffectedGame()
    {
        var plan = FrameCapturePreflightPlan.Build(
            [new GameProfile { Id = "late", Name = "Late title", LateRtss = true }], "presentmon");

        Assert.True(plan.RequiresRtss);
        Assert.Contains("late (Late title)", plan.RtssGames);
        Assert.False(plan.Evaluate(presentMonAvailable: true, rtssAvailable: false, syntheticFramesForced: false).IsReady);
        Assert.True(TestOrchestrator.TryCreateBackendPrecheckFailure(
            new GameProfile { Id = "late", LateRtss = true }, backendReady: false, "RTSS.exe was not found", out var failure));
        Assert.Equal(FailureClass.HardwarePrecheck, failure.FailureClass);
        Assert.Contains("requested FPS / frametime backend unavailable before launch", failure.Reason);
    }

    [Fact]
    public void ForbidRtssWinsOverLateRtss()
    {
        var plan = FrameCapturePreflightPlan.Build(
            [new GameProfile { Id = "safe", LateRtss = true, ForbidRtss = true }], "rtss");

        Assert.True(plan.RequiresPresentMon);
        Assert.False(plan.RequiresRtss);
        Assert.Contains("safe", plan.PresentMonGames);
    }

    [Fact]
    public void GlobalRtssOverridesProfilePresentMonUnlessRtssIsForbidden()
    {
        var plan = FrameCapturePreflightPlan.Build(
            [new GameProfile { Id = "global-wins", FrameProvider = "presentmon" }], "rtss");

        Assert.False(plan.RequiresPresentMon);
        Assert.True(plan.RequiresRtss);
        Assert.Contains("global-wins", plan.RtssGames);
    }

    [Fact]
    public void MissingRtssPinnedBackendAbortsBeforeLaunchEvenWhenPresentMonIsAvailable()
    {
        var game = new GameProfile { Id = "rtss-pinned", FrameProvider = "rtss" };
        var plan = FrameCapturePreflightPlan.Build([game], "presentmon");

        Assert.False(plan.Evaluate(presentMonAvailable: true, rtssAvailable: false, syntheticFramesForced: false).IsReady);
        Assert.True(TestOrchestrator.TryCreateBackendPrecheckFailure(game, backendReady: false,
            "RTSS requested but unavailable", out var failure));
        Assert.Equal(GameStatus.Failed, failure.Status);
        Assert.Equal(FailureClass.HardwarePrecheck, failure.FailureClass);
    }

    [Fact]
    public void MixedGlobalRtssAndRatchetRequiresBothBackends()
    {
        var plan = FrameCapturePreflightPlan.Build(
        [
            new GameProfile { Id = "xbox" },
            new GameProfile { Id = "ratchet", Name = "Ratchet", ForbidRtss = true }
        ], "rtss");

        Assert.True(plan.RequiresPresentMon);
        Assert.True(plan.RequiresRtss);
        Assert.Contains("ratchet (Ratchet)", plan.PresentMonGames);
        Assert.Contains("xbox", plan.RtssGames);
    }

    [Fact]
    public void MixedRosterInitialProbeNeverStartsRtss()
    {
        Assert.False(FrameProviderPolicy.ShouldStartRtssDuringInitialProbe(
            globalProvider: "presentmon", presentMonLive: true, planUsesRtss: true));
        Assert.False(FrameProviderPolicy.ShouldStartRtssDuringInitialProbe(
            globalProvider: "rtss", presentMonLive: true, planUsesRtss: false));
    }

    [Fact]
    public void RatchetForbidRtssRequiresPresentMonAndNeverRequestsInitialRtssStartup()
    {
        var game = new GameProfile { Id = "ratchet", ForbidRtss = true, LateRtss = true };
        var plan = FrameCapturePreflightPlan.Build([game], "rtss");

        Assert.True(plan.RequiresPresentMon);
        Assert.False(plan.RequiresRtss);
        Assert.Equal("presentmon", FrameProviderPolicy.Resolve("rtss", "rtss", presentMonLive: true, forbidRtss: true));
        Assert.False(FrameProviderPolicy.ShouldStartRtssDuringInitialProbe(
            globalProvider: "rtss", presentMonLive: true, planUsesRtss: false));
    }
}
