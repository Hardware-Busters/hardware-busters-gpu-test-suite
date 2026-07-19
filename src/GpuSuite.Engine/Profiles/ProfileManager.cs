using GpuSuite.Core.Io;
using GpuSuite.Core.Models;

namespace GpuSuite.Engine.Profiles;

/// <summary>
/// (2) Game Profile Manager. Loads/saves <see cref="GameProfile"/> JSON files from the
/// profiles directory. Each file is one game; the file name is the profile id.
/// </summary>
public sealed class ProfileManager
{
    private readonly string _dir;
    public ProfileManager(string profilesDir)
    {
        _dir = profilesDir;
        ProfileAssetLocator.Configure(profilesDir);
    }

    public IReadOnlyList<GameProfile> LoadAll()
    {
        var list = new List<GameProfile>();
        if (!Directory.Exists(_dir)) return list;
        var manager = new ProfilePackManager(_dir);
        foreach (var file in manager.GetActiveProfileFiles())
        {
            var p = Json.Load<GameProfile>(file);
            if (p is not null && !string.IsNullOrWhiteSpace(p.Id))
            {
                if (!string.IsNullOrWhiteSpace(p.ResolutionApply.TemplateFilePath))
                    p.ResolutionApply.TemplateFilePath = ProfileAssetLocator.ResolveReference(
                        p.ResolutionApply.TemplateFilePath, ProfileAssetLocator.RootForAsset(file));
                list.Add(p);
            }
        }
        return list;
    }

    public GameProfile? Load(string id)
        => LoadAll().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public void Save(GameProfile profile)
    {
        Directory.CreateDirectory(_dir);
        string? existing = new ProfilePackManager(_dir).GetActiveProfileFiles()
            .FirstOrDefault(file =>
            {
                try { return string.Equals(Json.Load<GameProfile>(file)?.Id, profile.Id, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });
        Json.Save(existing ?? Path.Combine(_dir, profile.Id + ".json"), profile);
    }
}
