using System.Text.Json;
using System.Text.Json.Nodes;
using GpuSuite.Core.Io;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Automation;

namespace GpuSuite.Authoring;

/// <summary>A typed profile form plus its original JSON tree. Saving merges form fields into the tree so unmodeled data survives.</summary>
public sealed class DraftProfileDocument
{
    public required string FilePath { get; init; }
    public required JsonObject Raw { get; init; }
    public required GameProfile Profile { get; init; }
    internal JsonObject BaselineTyped { get; init; } = new();
    public string RelativePath { get; init; } = "";
    internal Dictionary<SceneProfile, JsonObject> SceneRaw { get; } = new(ReferenceEqualityComparer.Instance);
    internal Dictionary<GameSetting, JsonObject> SettingRaw { get; } = new(ReferenceEqualityComparer.Instance);
    internal Dictionary<GameVariant, JsonObject> VariantRaw { get; } = new(ReferenceEqualityComparer.Instance);
    internal Dictionary<ConfigEdit, JsonObject> ConfigEditRaw { get; } = new(ReferenceEqualityComparer.Instance);
    internal Dictionary<RegistryEdit, JsonObject> RegistryEditRaw { get; } = new(ReferenceEqualityComparer.Instance);
}

/// <summary>One timeline action keeps its own original JSON object, preserving advanced action fields across reordering.</summary>
public sealed class DraftBotActionDocument
{
    public required JsonObject Raw { get; init; }
    public required BotAction Action { get; init; }
}

/// <summary>A typed bot form plus raw JSON for graph and unmodeled fields.</summary>
public sealed class DraftBotDocument
{
    public required string FilePath { get; init; }
    public required JsonObject Raw { get; init; }
    public required BotScript Bot { get; init; }
    public required List<DraftBotActionDocument> Timeline { get; init; }
    internal JsonObject BaselineTyped { get; init; } = new();
    public string RelativePath { get; init; } = "";
    internal Dictionary<NavScreen, JsonObject> ScreenRaw { get; } = new(ReferenceEqualityComparer.Instance);
    internal Dictionary<NavStep, JsonObject> NavStepRaw { get; } = new(ReferenceEqualityComparer.Instance);
    internal bool GraphCleared { get; set; }
}

public sealed record DraftDeleteGuard(bool CanDelete, IReadOnlyList<string> References);
