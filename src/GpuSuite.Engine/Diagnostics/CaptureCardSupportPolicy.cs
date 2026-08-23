namespace GpuSuite.Engine.Diagnostics;

/// <summary>
/// Qualification policy for the capture device used by the full automated game-benchmark workflow.
/// This deliberately evaluates only configured/enumerated DirectShow names; diagnostic capture commands
/// remain free to use any device name supplied by the operator.
/// </summary>
public static class CaptureCardSupportPolicy
{
    public const string QualifiedDeviceName = "Elgato 4K Pro";

    public const string PolicyStatement =
        "Elgato Game Capture 4K Pro is required for the full supported automated game-benchmark workflow and is the only capture-card model currently qualified by this project.";

    /// <summary>
    /// Exact, case-sensitive match on the qualified DirectShow name — surrounding whitespace excepted.
    /// Trimming is deliberate and does not weaken the policy: whitespace does not name a different device,
    /// and <c>CaptureCardGrabber</c> already trims before handing the name to ffmpeg. Comparing untrimmed
    /// here meant a pasted "Elgato 4K Pro " hard-blocked pre-flight while the grabber itself worked — and
    /// the blocker text echoed the name <em>trimmed</em>, so it read as "X is not X".
    /// </summary>
    public static bool IsQualifiedDeviceName(string? deviceName) =>
        string.Equals(deviceName?.Trim(), QualifiedDeviceName, StringComparison.Ordinal);

    public static CaptureCardQualification Evaluate(string? configuredDevice, IEnumerable<string>? enumeratedDevices)
    {
        if (!IsQualifiedDeviceName(configuredDevice))
        {
            string configured = string.IsNullOrWhiteSpace(configuredDevice) ? "not set" : $"\"{configuredDevice.Trim()}\"";
            return new(false,
                $"settings.captureCardDevice is {configured}; it must be exactly \"{QualifiedDeviceName}\". {PolicyStatement}");
        }

        var devices = (enumeratedDevices ?? Array.Empty<string>()).ToArray();
        if (devices.Any(IsQualifiedDeviceName))
            return new(true, $"qualified configured device present: \"{QualifiedDeviceName}\".");

        return new(false,
            $"configured qualified device \"{QualifiedDeviceName}\" was not enumerated. Devices seen: " +
            (devices.Length == 0 ? "none." : string.Join("; ", devices) + "."));
    }
}

public sealed record CaptureCardQualification(bool IsQualified, string Detail);
