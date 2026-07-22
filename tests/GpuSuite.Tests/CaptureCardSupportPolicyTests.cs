using GpuSuite.Core.Diagnostics;
using GpuSuite.App.Services;
using GpuSuite.Engine.Diagnostics;
using Xunit;

namespace GpuSuite.Tests;

public sealed class CaptureCardSupportPolicyTests
{
    [Fact]
    public void ExactQualifiedModelConfiguredAndEnumeratedPasses()
    {
        var result = CaptureCardSupportPolicy.Evaluate(
            CaptureCardSupportPolicy.QualifiedDeviceName,
            [CaptureCardSupportPolicy.QualifiedDeviceName]);

        Assert.True(result.IsQualified);
        Assert.Contains(CaptureCardSupportPolicy.QualifiedDeviceName, result.Detail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("USB Video")]
    [InlineData("Elgato 4K60 Pro")]
    [InlineData("Elgato 4K60 Pro MK.2")]
    [InlineData("Elgato 4K Pro (Video)")]
    [InlineData("elgato 4k pro")]
    public void EmptyGenericAndUnqualifiedModelsFail(string configuredDevice)
    {
        var result = CaptureCardSupportPolicy.Evaluate(configuredDevice, [configuredDevice]);

        Assert.False(result.IsQualified);
        Assert.Contains(CaptureCardSupportPolicy.QualifiedDeviceName, result.Detail);
    }

    [Fact]
    public void ExactConfigurationStillFailsWhenExactDeviceWasNotEnumerated()
    {
        var result = CaptureCardSupportPolicy.Evaluate(
            CaptureCardSupportPolicy.QualifiedDeviceName,
            ["Elgato 4K60 Pro", "USB Video"]);

        Assert.False(result.IsQualified);
        Assert.Contains("not enumerated", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FullPreflightMapsCaptureCardHardwareFindingToBlocker()
    {
        var check = new DoctorCheck("Capture card (Elgato/HDMI)", DoctorStatus.Hardware, "unqualified");
        var mapped = FullPreflightService.MapDoctorCheck(check);

        Assert.Equal(CheckStatus.Blocker, mapped.Status);
    }
}
