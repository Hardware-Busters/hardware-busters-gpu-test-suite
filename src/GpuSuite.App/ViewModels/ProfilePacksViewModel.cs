using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpuSuite.App.Services;
using GpuSuite.Engine.Profiles;
using Microsoft.Win32;

namespace GpuSuite.App.ViewModels;

public sealed class ProfilePackRowViewModel
{
    public required ProfilePackInfo Pack { get; init; }
    public string Id => Pack.Manifest.Id;
    public string Name => Pack.Manifest.Name;
    public string Version => Pack.Manifest.Version;
    public string Publisher => string.IsNullOrWhiteSpace(Pack.Manifest.Publisher) ? "Unknown publisher" : Pack.Manifest.Publisher;
    public string Description => Pack.Manifest.Description;
    public string Trust => Pack.TrustLabel;
    public string Signature => Pack.SignatureLabel;
    public string Status => Pack.Status;
    public bool IsBundled => Pack.IsBundled;
    public bool IsEnabled => Pack.IsEnabled;
    public bool HasProblem => Pack.Errors.Count > 0 || !Pack.IsCompatible;
    public string Counts => $"{Pack.ProfileCount} profile(s) · {Pack.BotCount} bot(s) · {Pack.RouteCount} route(s)";
    public string Fingerprint => Pack.Fingerprint;
    public string Compatibility => Pack.IsCompatible
        ? $"Compatible with engine {GpuSuite.BuildInfo.Version}"
        : $"Requires engine {Pack.Manifest.MinimumEngineVersion} or later";
    public string Findings => Pack.Errors.Count == 0 ? "No validation findings." : string.Join(Environment.NewLine, Pack.Errors);
    public string EnableButtonText => IsEnabled ? "Disable pack" : "Enable pack";
}

/// <summary>Installs and manages isolated data-only benchmark profile packs.</summary>
public partial class ProfilePacksViewModel : ObservableObject
{
    private readonly ProfilePackService _service;
    private bool _loaded;

    public ObservableCollection<ProfilePackRowViewModel> Packs { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleEnabledCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private ProfilePackRowViewModel? selectedPack;

    [ObservableProperty] private string summary = "Loading profile packs…";
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleEnabledCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private bool isBusy;

    public ProfilePacksViewModel(ProfilePackService service, Workspace workspace)
    {
        _service = service;
        workspace.Changed += () => _loaded = false;
    }

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            string? selectedId = SelectedPack?.Id;
            var discovered = await Task.Run(_service.Discover);
            Packs.Clear();
            foreach (var pack in discovered) Packs.Add(new ProfilePackRowViewModel { Pack = pack });
            SelectedPack = Packs.FirstOrDefault(p => p.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)) ?? Packs.FirstOrDefault();
            int profiles = discovered.Where(p => p.IsUsable).Sum(p => p.ProfileCount);
            int enabled = discovered.Count(p => p.IsUsable);
            int problems = discovered.Count(p => p.Errors.Count > 0 || !p.IsCompatible);
            Summary = $"{discovered.Count} pack(s) · {enabled} active · {profiles} runnable profile(s)" +
                      (problems > 0 ? $" · {problems} need attention" : "");
        }
        catch (Exception ex) { StatusMessage = "Could not load profile packs: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Install a GPU Test Suite profile pack",
            Filter = "GPU Test Suite profile pack (*.gtsprofilepack)|*.gtsprofilepack|ZIP archive (*.zip)|*.zip",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;
        var trust = MessageBox.Show(
            "Profile packs can launch games, change declared game configuration or registry values, and inject keyboard/controller input. Install packs only from a publisher you trust.\n\nValidate and install this pack?",
            "Trust this profile pack?", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (trust != MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            ProfilePackInstallResult result;
            try { result = await Task.Run(() => _service.Install(dialog.FileName)); }
            catch (IOException ex) when (ex.Message.Contains("already installed", StringComparison.OrdinalIgnoreCase))
            {
                var answer = MessageBox.Show(ex.Message + "\n\nReplace the installed copy with this archive?",
                    "Update profile pack", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
                result = await Task.Run(() => _service.Install(dialog.FileName, replace: true));
            }
            _service.NotifyInstalled();
            _loaded = false;
            StatusMessage = result.Replaced ? $"Updated {result.Pack.Manifest.Name}." : $"Installed {result.Pack.Manifest.Name}.";
            IsBusy = false;
            await EnsureLoadedAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show("The pack was not installed.\n\n" + ex.Message,
                "Profile pack validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusMessage = "Import rejected; no installed files were changed.";
        }
        finally { IsBusy = false; }
    }

    private bool HasSelection() => SelectedPack is not null && !IsBusy;
    private bool CanRemove() => SelectedPack is { IsBundled: false } && !IsBusy;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ToggleEnabledAsync()
    {
        if (SelectedPack is null) return;
        string name = SelectedPack.Name;
        bool enable = !SelectedPack.IsEnabled;
        await Task.Run(() => _service.SetEnabled(SelectedPack.Id, enable));
        _loaded = false;
        StatusMessage = enable ? $"Enabled {name}." : $"Disabled {name}. Its profiles will not enter a run.";
        await EnsureLoadedAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ExportAsync()
    {
        if (SelectedPack is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export profile pack",
            Filter = "GPU Test Suite profile pack (*.gtsprofilepack)|*.gtsprofilepack",
            FileName = $"{SelectedPack.Id}-{SelectedPack.Version}{ProfilePackManager.ArchiveExtension}",
            AddExtension = true,
            DefaultExt = ProfilePackManager.ArchiveExtension
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await Task.Run(() => _service.Export(SelectedPack.Id, dialog.FileName));
            StatusMessage = $"Exported {SelectedPack.Name} to {dialog.FileName}.";
        }
        catch (Exception ex) { StatusMessage = "Export failed: " + ex.Message; }
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync()
    {
        if (SelectedPack is null) return;
        var answer = MessageBox.Show(
            $"Remove '{SelectedPack.Name}' and its local profiles, bots and routes?\n\nExisting benchmark results are not deleted.",
            "Remove profile pack", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        string name = SelectedPack.Name;
        await Task.Run(() => _service.Remove(SelectedPack.Id));
        _loaded = false;
        StatusMessage = $"Removed {name}.";
        await EnsureLoadedAsync();
    }

    [RelayCommand]
    private void OpenPacksFolder()
    {
        Directory.CreateDirectory(_service.PacksDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", _service.PacksDirectory) { UseShellExecute = true });
    }
}
