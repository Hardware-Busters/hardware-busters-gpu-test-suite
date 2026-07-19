using GpuSuite.Core.Io;
using GpuSuite.Engine.Profiles;

namespace GpuSuite.Engine.Automation;

/// <summary>Resolves a bot-script id across the bundled profile root and active profile packs.</summary>
public static class BotScriptLibrary
{
    public static BotScript? Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (id.Equals("camera_path_basic", StringComparison.OrdinalIgnoreCase))
            return BotScript.CameraPathBasic();
        var path = ProfileAssetLocator.Find("bots", id + ".json");
        if (path is null) return null;
        var script = Json.Load<BotScript>(path);
        if (script is null) return null;
        string packRoot = ProfileAssetLocator.RootForAsset(path);
        foreach (var action in script.Actions)
        {
            if (!string.IsNullOrWhiteSpace(action.RoutePath))
                action.RoutePath = ProfileAssetLocator.ResolveReference(action.RoutePath, packRoot);
            if (!string.IsNullOrWhiteSpace(action.RecordRoutePath))
                action.RecordRoutePath = ProfileAssetLocator.ResolveReference(action.RecordRoutePath, packRoot);
        }
        return script;
    }
}
