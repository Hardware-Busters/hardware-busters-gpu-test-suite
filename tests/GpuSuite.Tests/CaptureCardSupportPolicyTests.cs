using GpuSuite.Core.Diagnostics;
using GpuSuite.App.Services;
using GpuSuite.Engine.Diagnostics;
using Xunit;

namespace GpuSuite.Tests;

public sealed class CaptureCardSupportPolicyTests
{
    [Theory]
    [InlineData("Elgato 4K Pro", true)]
    [InlineData("Elgato 4K X", false)]
    public void ExactConfiguredAndEnumeratedDeviceRetainsItsQualification(string deviceName, bool qualified)
    {
        var result = CaptureCardSupportPolicy.Evaluate(
            deviceName,
            [deviceName]);

        Assert.True(result.IsSupported);
        Assert.Equal(qualified, result.IsQualified);
        Assert.Equal(!qualified, result.IsExperimental);
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

        Assert.True(result.IsSupported);
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
        Assert.False(result.IsSupported);
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
    public void ConfiguredDeviceMustBeEnumeratedEvenWhenTheOtherModelIsPresent(string configured, string enumerated)
    {
        var result = CaptureCardSupportPolicy.Evaluate(configured, [enumerated]);
        Assert.False(result.IsSupported);
        Assert.False(result.IsQualified);
        Assert.False(result.IsExperimental);
        Assert.Contains(configured, result.Detail);
        Assert.Contains("not enumerated", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DoctorStatus.Hardware, DoctorService.BuildCaptureCardModelCheck(result).Status);
    }

    [Theory]
    [InlineData("Elgato 4K Pro")]
    [InlineData("Elgato 4K X")]
    public void MissingEnumerationNeverAdmitsEitherModel(string configured)
    {
        Assert.False(CaptureCardSupportPolicy.Evaluate(configured, null).IsSupported);
        Assert.False(CaptureCardSupportPolicy.Evaluate(configured, []).IsSupported);
    }

    [Fact]
    public void ExperimentalAdmissionDoesNotChangeTheQualifiedNameContract()
    {
        Assert.False(CaptureCardSupportPolicy.IsQualifiedDeviceName("Elgato 4K X"));
        Assert.True(CaptureCardSupportPolicy.IsSupportedDeviceName("Elgato 4K X"));
        var result = CaptureCardSupportPolicy.Evaluate("Elgato 4K X", ["Elgato 4K Pro", "Elgato 4K X"]);
        Assert.False(result.IsQualified);
        Assert.True(result.IsExperimental);
        Assert.Contains("not independently qualified", result.Detail);
    }

    [Theory]
    [InlineData("Elgato 4K Pro", DoctorStatus.Ok, CheckStatus.Ok)]
    [InlineData("Elgato 4K X", DoctorStatus.Warn, CheckStatus.Warn)]
    public void WorkingStreamPreservesModelQualificationStatus(string configured, DoctorStatus doctorStatus, CheckStatus preflightStatus)
    {
        var model = DoctorService.BuildCaptureCardModelCheck(CaptureCardSupportPolicy.Evaluate(configured, [configured]));
        Assert.Equal(doctorStatus, model.Status);
        var rows = FullPreflightService.BuildMachineChecks(
            [model, new(DoctorComponents.CaptureCardStream, DoctorStatus.Ok, "bounded stream passed")]);
        var vision = Assert.Single(rows, row => row.Name == "Vision hardware — capture card");
        Assert.Equal(preflightStatus, vision.Status);
        Assert.Contains(configured, vision.Detail);
    }

    [Theory]
    [InlineData("Elgato 4K Pro")]
    [InlineData("Elgato 4K X")]
    public void StreamFailureStillBlocksBothAdmittedModels(string configured)
    {
        var model = DoctorService.BuildCaptureCardModelCheck(CaptureCardSupportPolicy.Evaluate(configured, [configured]));
        var rows = FullPreflightService.BuildMachineChecks(
            [model, new(DoctorComponents.CaptureCardStream, DoctorStatus.Hardware, "stream failed")]);
        Assert.Equal(CheckStatus.Blocker, Assert.Single(rows, row => row.Name == "Vision hardware — capture card").Status);
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
            "Vision hardware — capture card"
        }, rows.Take(4).Select(r => r.Name));
        Assert.Equal(2, rows.Count(r => r.Name.StartsWith("FPS / frametime —", StringComparison.Ordinal)));
        Assert.Single(rows, r => r.Name == "Vision hardware — capture card");
        Assert.Contains("bounded stream probe; no image retained", rows[3].Detail);
    }
}
