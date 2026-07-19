using GpuSuite.Core.Models;
using GpuSuite.Engine.Profiles;

namespace GpuSuite.App.Services;

/// <summary>
/// Loads/saves game profiles (profiles/*.json) via the engine's <see cref="ProfileManager"/>.
/// A fresh manager is built each call so it always reflects the current <see cref="Workspace.ProfilesDir"/>.
/// </summary>
public sealed class ProfileService
{
    private readonly Workspace _ws;
    public ProfileService(Workspace ws) => _ws = ws;

    private ProfileManager Manager => new(_ws.ProfilesDir);

    public IReadOnlyList<GameProfile> LoadAll() => Manager.LoadAll();
    public GameProfile? Load(string id) => Manager.Load(id);
    public void Save(GameProfile profile) => Manager.Save(profile);
}
