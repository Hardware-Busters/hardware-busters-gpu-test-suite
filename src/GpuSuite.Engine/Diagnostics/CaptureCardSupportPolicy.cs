namespace GpuSuite.Engine.Diagnostics;

/// <summary>
/// Admission policy for the capture device used by the full automated game-benchmark workflow.
/// This deliberately evaluates only configured/enumerated DirectShow names; diagnostic capture commands
/// remain free to use any device name supplied by the operator.
/// </summary>
public static class CaptureCardSupportPolicy
{
    public const string QualifiedDeviceName = "Elgato 4K Pro";
    public const string ExperimentalDeviceName = "Elgato 4K X";

    public static readonly IReadOnlyList<string> PermittedDeviceNames =
        [QualifiedDeviceName, ExperimentalDeviceName];

    public const string PolicyStatement =
        "Elgato Game Capture 4K Pro is the only capture-card model currently qualified by this project. Elgato 4K X is permitted experimentally and is not independently qualified by Hardware Busters.";

    public static string PermittedDeviceNamesText =>
        string.Join(" or ", PermittedDeviceNames.Select(name => $"\"{name}\""));

    /// <summary>
    /// Exact, case-sensitive match on a permitted DirectShow name — surrounding whitespace excepted.
    /// Workflow admission includes experimental devices and does not imply independent hardware qualification.
    /// Trimming is deliberate and does not weaken the policy: whitespace does not name a different device,
    /// and <c>CaptureCardGrabber</c> already trims before handing the name to ffmpeg. Comparing untrimmed
    /// here meant a pasted "Elgato 4K Pro " hard-blocked pre-flight while the grabber itself worked — and
    /// the blocker text echoed the name <em>trimmed</em>, so it read as "X is not X".
    /// </summary>
    public static bool IsQualifiedDeviceName(string? deviceName) =>
        PermittedDeviceNames.Any(name => string.Equals(deviceName?.Trim(), name, StringComparison.Ordinal));

    public static CaptureCardQualification Evaluate(string? configuredDevice, IEnumerable<string>? enumeratedDevices)
    {
        if (!IsQualifiedDeviceName(configuredDevice))
        {
            string configured = string.IsNullOrWhiteSpace(configuredDevice) ? "not set" : $"\"{configuredDevice.Trim()}\"";
            return new(false,
                $"settings.captureCardDevice is {configured}; it must be exactly {PermittedDeviceNamesText}. {PolicyStatement}");
        }

        string normalizedConfigured = configuredDevice!.Trim();
        var devices = (enumeratedDevices ?? Array.Empty<string>()).ToArray();
        if (devices.Any(device => string.Equals(device?.Trim(), normalizedConfigured, StringComparison.Ordinal)))
        {
            string detail = $"permitted configured device present: \"{normalizedConfigured}\".";
            if (normalizedConfigured == ExperimentalDeviceName)
                detail += " Experimental support; not independently qualified by Hardware Busters.";
            return new(true, detail);
        }

        return new(false,
            $"configured permitted device \"{normalizedConfigured}\" was not enumerated. Devices seen: " +
            (devices.Length == 0 ? "none." : string.Join("; ", devices) + "."));
    }
}

public sealed record CaptureCardQualification(bool IsQualified, string Detail);
