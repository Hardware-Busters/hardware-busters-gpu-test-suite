using GpuSuite.Engine.Profiles;

namespace GpuSuite.Authoring;

/// <summary>An editable, unpacked profile pack. Installed and bundled packs are never draft projects.</summary>
public sealed class DraftPackProject
{
    public required string RootDirectory { get; init; }
    public required ProfilePackManifest Manifest { get; set; }
    public DraftPackInventory Inventory { get; init; } = new();
}

public sealed class DraftPackInventory
{
    public IReadOnlyList<string> Profiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Bots { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Routes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Templates { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Assets { get; init; } = Array.Empty<string>();
    public int TotalFiles => Profiles.Count + Bots.Count + Routes.Count + Templates.Count + Assets.Count;
}

public enum DraftValidationSeverity { Error, Warning }

/// <summary>Stable editor address for an issue.  The display message may evolve; these keys are for selection and focus.</summary>
public sealed record DraftValidationLocation(string DocumentKind, string? RelativePath, string? ItemId = null, int? ItemIndex = null, string? ControlKey = null);

public sealed record DraftValidationIssue(DraftValidationSeverity Severity, string Code, string Message, string? RelativePath = null, DraftValidationLocation? Location = null);

public sealed class DraftValidationResult
{
    public List<DraftValidationIssue> Issues { get; } = new();
    public bool IsValid => Issues.All(i => i.Severity != DraftValidationSeverity.Error);
    public IEnumerable<DraftValidationIssue> Errors => Issues.Where(i => i.Severity == DraftValidationSeverity.Error);
    public IEnumerable<DraftValidationIssue> Warnings => Issues.Where(i => i.Severity == DraftValidationSeverity.Warning);
    public void Error(string code, string message, string? path = null, DraftValidationLocation? location = null) => Issues.Add(new(DraftValidationSeverity.Error, code, message, path, location ?? DefaultLocation(code, path)));
    public void Warning(string code, string message, string? path = null, DraftValidationLocation? location = null) => Issues.Add(new(DraftValidationSeverity.Warning, code, message, path, location ?? DefaultLocation(code, path)));

    private static DraftValidationLocation DefaultLocation(string code, string? path)
    {
        string prefix = code.Split('.', 2)[0].ToLowerInvariant();
        string documentKind = prefix switch
        {
            "manifest" => "manifest",
            "profile" or "profiles" or "scene" or "setting" or "variant" or "template" => "profile",
            "bot" or "bots" => "bot",
            "route" or "routes" => "route",
            "asset" or "assets" => "asset",
            _ => "draft"
        };
        string relativePath = path ?? documentKind switch
        {
            "manifest" => "profile-pack.json",
            "profile" => "profiles/",
            "bot" => "bots/",
            "route" => "routes/",
            "asset" => "assets/",
            _ => ".gtsauthoring.json"
        };
        return new DraftValidationLocation(documentKind, relativePath, ControlKey: code);
    }
}

public sealed record DraftExportResult(string ArchivePath, string Sha256, string Fingerprint);
