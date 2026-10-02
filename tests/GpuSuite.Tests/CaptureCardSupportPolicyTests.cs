using GpuSuite.Core.Diagnostics;
using GpuSuite.App.Services;
using GpuSuite.Engine.Diagnostics;
using Xunit;

namespace GpuSuite.Tests;

public sealed class CaptureCardSupportPolicyTests
{
    [Theory]
    [InlineData("Elgato 4K Pro")]
    [InlineData("Elgato 4K X")]
    public void ExactPermittedModelConfiguredAndEnumeratedPasses(string deviceName)
    {
        var result = CaptureCardSupportPolicy.Evaluate(
            deviceName,
            [deviceName]);

        Assert.True(result.IsQualified);
        Assert.Contains(deviceName, result.Detail);
    }

    [Theory]
    [InlineData("Elgato 4K Pro ")]
    [InlineData(" Elgato 4K Pro")]
    [InlineData("  Elgato 4K Pro  ")]
    [InlineData("\tElgato 4K Pro\r\n")]
    [InlineData(" Elgato 4K X ")]
    [InlineData("\tElgato 4K X\r\n")]
    public void SurroundingWhitespaceIsToleratedBecauseTheGrabberTrimsToo(string configuredDevice)
    {
        // Regression: the policy compared untrimmed while CaptureCardGrabber trims before calling ffmpeg,
        // so a pasted trailing space hard-blocked pre-flight on a bench that would have captured fine —
        // and the blocker echoed the name trimmed, reading as "X is not X". Whitespace does not name a
        // different device. Case sensitivity is NOT relaxed; "elgato 4k pro" still fails above.
        var result = CaptureCardSupportPolicy.Evaluate(configuredDevice, [configuredDevice]);

        Assert.True(result.IsQualified);
    }

    [Theory]
    [InlineData("")]
    [InlineData("USB Video")]
    [InlineData("Elgato 4K60 Pro")]
    [InlineData("Elgato 4K60 Pro MK.2")]
    [InlineData("Elgato 4K Pro (Video)")]
    [InlineData("elgato 4k pro")]
    [InlineData("elgato 4k x")]
    [InlineData("Elgato 4K X (Video)")]
    [InlineData(null)]
    public void EmptyGenericAndUnqualifiedModelsFail(string? configuredDevice)
    {
        var result = CaptureCardSupportPolicy.Evaluate(configuredDevice, [configuredDevice ?? ""]);

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

    [Theory]
    [InlineData("Elgato 4K X", "Elgato 4K Pro")]
    [InlineData("Elgato 4K Pro", "Elgato 4K X")]
    public void ConfiguredDeviceDoesNotPassWhenOnlyTheOtherPermittedDeviceWasEnumerated(
        string configuredDevice, string enumeratedDevice)
    {
        var result = CaptureCardSupportPolicy.Evaluate(
            configuredDevice,
            [enumeratedDevice]);

        Assert.False(result.IsQualified);
        Assert.Contains(configuredDevice, result.Detail);
        Assert.Contains("not enumerated", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FourKXAdmissionExplicitlyReportsExperimentalStatus()
    {
        var result = CaptureCardSupportPolicy.Evaluate(
            CaptureCardSupportPolicy.ExperimentalDeviceName,
            [CaptureCardSupportPolicy.QualifiedDeviceName, CaptureCardSupportPolicy.ExperimentalDeviceName]);

        Assert.True(result.IsQualified);
        Assert.Contains("Experimental support", result.Detail);
        Assert.Contains("not independently qualified", result.Detail);
    }

    [Theory]
    [InlineData("Elgato 4K Pro")]
    [InlineData("Elgato 4K X")]
    public void MissingEnumerationFails(string configuredDevice)
    {
        Assert.False(CaptureCardSupportPolicy.Evaluate(configuredDevice, null).IsQualified);
        Assert.False(CaptureCardSupportPolicy.Evaluate(configuredDevice, []).IsQualified);
    }

    [Fact]
    public void FullPreflightMapsCaptureCardHardwareFindingToBlocker()
    {
        var check = new DoctorCheck("Capture-card model (full automation)", DoctorStatus.Hardware, "unqualified");
        var mapped = FullPreflightService.MapDoctorCheck(check);

        Assert.Equal(CheckStatus.Blocker, mapped.Status);
    }

    [Fact]
    public void FullPreflightMapsFfmpegVisionAndCaptureStreamHardwareFindingsToBlockers()
    {
        Assert.Equal(CheckStatus.Blocker, FullPreflightService.MapDoctorCheck(
            new DoctorCheck("FFmpeg (capture-card vision)", DoctorStatus.Warn, "missing")).Status);
        Assert.Equal(CheckStatus.Blocker, FullPreflightService.MapDoctorCheck(
            new DoctorCheck("Capture-card vision stream (FFmpeg)", DoctorStatus.Hardware, "no frame")).Status);
    }

    [Fact]
    public void FullPreflightStartsWithTheFourOrderedMeasurementAndVisionChainRows()
    {
        IReadOnlyList<DoctorCheck> doctor =
        [
            new(".NET 9 desktop runtime", DoctorStatus.Ok, "present"),
            new("Capture-card vision stream (FFmpeg)", DoctorStatus.Ok, "bounded stream probe; no image retained"),
            new("PresentMon (FPS / frametime)", DoctorStatus.Ok, "present"),
            new("Capture-card model (full automation)", DoctorStatus.Ok, "qualified"),
            new("RTSS / RivaTuner (FPS / frametime fallback)", DoctorStatus.Warn, "optional"),
            new("FFmpeg (capture-card vision)", DoctorStatus.Ok, "present")
        ];

        var rows = FullPreflightService.BuildMachineChecks(doctor);

        Assert.Equal(new[]
        {
            "FPS / frametime — PresentMon",
            "FPS / frametime — RTSS",
            "Vision transport — FFmpeg",
            "Vision hardware — Elgato 4K Pro / 4K X (experimental)"
        }, rows.Take(4).Select(r => r.Name));
        Assert.Equal(2, rows.Count(r => r.Name.StartsWith("FPS / frametime —", StringComparison.Ordinal)));
        Assert.Single(rows, r => r.Name == "Vision hardware — Elgato 4K Pro / 4K X (experimental)");
        Assert.Contains("bounded stream probe; no image retained", rows[3].Detail);
    }
}
