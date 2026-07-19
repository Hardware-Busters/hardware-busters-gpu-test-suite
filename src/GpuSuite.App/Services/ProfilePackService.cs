using GpuSuite.Engine.Profiles;

namespace GpuSuite.App.Services;

/// <summary>Workspace-aware façade over the engine's profile-pack manager.</summary>
public sealed class ProfilePackService
{
    private readonly Workspace _workspace;
    public ProfilePackService(Workspace workspace) => _workspace = workspace;

    private ProfilePackManager Manager => new(_workspace.ProfilesDir, GpuSuite.BuildInfo.Version);

    public IReadOnlyList<ProfilePackInfo> Discover() => Manager.Discover();
    public ProfilePackInstallResult Install(string archivePath, bool replace = false)
        => Manager.InstallArchive(archivePath, replace);
    public void Export(string packId, string destinationPath) => Manager.ExportArchive(packId, destinationPath);
    public void SetEnabled(string packId, bool enabled)
    {
        Manager.SetEnabled(packId, enabled);
        _workspace.NotifyContentChanged();
    }
    public void Remove(string packId)
    {
        Manager.Remove(packId);
        _workspace.NotifyContentChanged();
    }
    public string PacksDirectory => Manager.ImportedPacksRoot;
    public void NotifyInstalled() => _workspace.NotifyContentChanged();
}
