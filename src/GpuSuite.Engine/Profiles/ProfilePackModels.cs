using System.Text.Json;
using System.Text.Json.Serialization;

namespace GpuSuite.Engine.Profiles;

/// <summary>Portable metadata stored at the root of every .gtsprofilepack archive.</summary>
public sealed class ProfilePackManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Publisher { get; set; } = "";
    public string Description { get; set; } = "";
    public string License { get; set; } = "";
    public string Homepage { get; set; } = "";
    public string MinimumEngineVersion { get; set; } = "0.1.0";
    public List<string> Games { get; set; } = new();
    /// <summary>Preserves forward-compatible publisher metadata when a draft is opened and saved by an older app.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Side-effect-free structural result for a portable pack archive.</summary>
public sealed record ProfilePackArchiveValidationResult(ProfilePackManifest Manifest, string Fingerprint, int ProfileCount, int BotCount, int RouteCount);

/// <summary>Validated, local view of one bundled or imported profile pack.</summary>
public sealed class ProfilePackInfo
{
    public required ProfilePackManifest Manifest { get; init; }
    public required string RootDirectory { get; init; }
    public bool IsBundled { get; init; }
    public bool IsEnabled { get; internal set; }
    public bool IsCompatible { get; internal set; } = true;
    public int ProfileCount { get; internal set; }
    public int BotCount { get; internal set; }
    public int RouteCount { get; internal set; }
    public string Fingerprint { get; internal set; } = "";
    public List<string> Errors { get; } = new();

    public string TrustLabel => IsBundled ? "Hardware Busters verified" : "Community / local";
    public string SignatureLabel => IsBundled ? "Bundled with this application" : "Unsigned community content";
    public bool IsUsable => IsEnabled && IsCompatible && Errors.Count == 0;
    public string Status => Errors.Count > 0 ? "Invalid" : !IsCompatible ? "Incompatible" : IsEnabled ? "Enabled" : "Disabled";
}

internal sealed class ProfilePackState
{
    public Dictionary<string, bool> Enabled { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record ProfilePackInstallResult(ProfilePackInfo Pack, bool Replaced);
