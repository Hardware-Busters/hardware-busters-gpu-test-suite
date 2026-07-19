using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpuSuite.App.Services;
using GpuSuite.Authoring;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Automation;
using GpuSuite.Engine.Profiles;
using Microsoft.Win32;

namespace GpuSuite.App.ViewModels;

public sealed record BotActionPreset(string Name, string Description, BotActionType Type);

/// <summary>Draft-only visual authoring shell. Typed controls edit documents that retain their original JSON trees.</summary>
public partial class AuthorStudioViewModel : ObservableObject
{
    private readonly DraftPackService _drafts;
    private readonly Workspace _workspace;
    public ObservableCollection<string> Inventory { get; } = new();
    public ObservableCollection<DraftValidationIssue> Problems { get; } = new();
    public ObservableCollection<DraftProfileDocument> Profiles { get; } = new();
    public ObservableCollection<DraftBotDocument> Bots { get; } = new();
    public ObservableCollection<string> BotIds { get; } = new();
    public ObservableCollection<string> Resolutions { get; } = new();
    public ObservableCollection<DraftBotActionDocument> Timeline { get; } = new();
    public Array BotActionTypes => Enum.GetValues(typeof(BotActionType));
    public Array BotDevices => Enum.GetValues(typeof(BotInputDevice));
    public Array SceneKinds => Enum.GetValues(typeof(SceneKind));
    public IReadOnlyList<BotActionPreset> CommonBotActionPresets { get; } =
    [
        new("Wait for screen text", "Safely wait for an OCR phrase before continuing. The preset is required and needs two consecutive matches.", BotActionType.WaitForText),
        new("Tap a key or button", "Tap Enter on keyboard bots or A on gamepad bots. Change the key or button after adding if needed.", BotActionType.KeyTap),
        new("Pause", "Wait for one second without sending input.", BotActionType.Wait),
        new("Start measurement", "Open the measured benchmark window. Put this after every menu, loading, and readiness gate.", BotActionType.MarkStart),
        new("End measurement", "Close the measured benchmark window.", BotActionType.MarkEnd),
        new("Replay a recorded route", "Replay an imported route deterministically. Select the correct route path after adding.", BotActionType.ReplayRoute),
        new("Discover a smart route", "Run adaptive forward movement outside Author Studio and record its path for later deterministic replay.", BotActionType.SmartTraverse),
        new("Prove world motion", "Fail the run unless capture evidence proves that the camera or character is moving in the world.", BotActionType.AssertWorldMotion)
    ];

    [ObservableProperty] private DraftPackProject? draft;
    [ObservableProperty] private DraftProfileDocument? selectedProfile;
    [ObservableProperty] private DraftBotDocument? selectedBot;
    [ObservableProperty] private DraftBotActionDocument? selectedAction;
    [ObservableProperty] private GameSetting? selectedSetting;
    [ObservableProperty] private GameVariant? selectedVariant;
    [ObservableProperty] private SceneProfile? selectedScene;
    [ObservableProperty] private string? selectedResolution;
    [ObservableProperty] private string newPackId = "my-studio.my-game";
    [ObservableProperty] private string newPackName = "My benchmark pack";
    [ObservableProperty] private string newProfileId = "new-profile";
    [ObservableProperty] private string newProfileName = "New profile";
    [ObservableProperty] private string duplicateProfileId = "profile-copy";
    [ObservableProperty] private string newBotId = "new-bot";
    [ObservableProperty] private string duplicateBotId = "bot-copy";
    [ObservableProperty] private string newResolution = "1080p";
    [ObservableProperty] private BotActionType newActionType = BotActionType.Wait;
    [ObservableProperty] private BotActionPreset? selectedBotActionPreset;
    [ObservableProperty] private string botJsonPreview = "";
    [ObservableProperty] private string statusMessage = "Create a draft, or clone an installed pack into a safe editable location.";
    [ObservableProperty] private string packId = "";
    [ObservableProperty] private string packName = "";
    [ObservableProperty] private string packVersion = "1.0.0";
    [ObservableProperty] private string publisher = "";
    [ObservableProperty] private string description = "";
    [ObservableProperty] private string license = "";
    [ObservableProperty] private string homepage = "";
    [ObservableProperty] private string minimumEngineVersion = "0.1.0";
    [ObservableProperty] private bool isBusy;

    /// <summary>The view fulfils this synchronously before an operation observes editor state.</summary>
    public event Func<bool>? PendingUiEditsCommitRequested;

    public bool HasDraft => Draft is not null;
    public bool HasProfile => SelectedProfile is not null;
    public bool HasBot => SelectedBot is not null;
    public bool IsEditorEnabled => !IsBusy;
    public string DraftPath => Draft?.RootDirectory ?? "No draft open";
    public string InventorySummary => Draft is null ? "No draft loaded." : $"{Draft.Inventory.Profiles.Count} profiles · {Draft.Inventory.Bots.Count} bots · {Draft.Inventory.Routes.Count} routes · {Draft.Inventory.Templates.Count} templates · {Draft.Inventory.Assets.Count} assets";

    public AuthorStudioViewModel(DraftPackService drafts, Workspace workspace)
    {
        _drafts = drafts;
        _workspace = workspace;
        SelectedBotActionPreset = CommonBotActionPresets[0];
    }
    partial void OnDraftChanged(DraftPackProject? value) { OnPropertyChanged(nameof(HasDraft)); OnPropertyChanged(nameof(DraftPath)); OnPropertyChanged(nameof(InventorySummary)); RefreshCommandStates(); }
    partial void OnSelectedProfileChanging(DraftProfileDocument? value) { if (SelectedProfile is not null) SelectedProfile.Profile.SupportedResolutions = Resolutions.ToList(); ApplyAdvancedProfileForm(); CheckpointProfileBeforeSelection(SelectedProfile); }
    partial void OnSelectedProfileChanged(DraftProfileDocument? value) { LoadProfileForm(value); AdvancedLoadProfile(value); OnPropertyChanged(nameof(HasProfile)); RefreshCommandStates(); }
    partial void OnSelectedBotChanged(DraftBotDocument? value) { LoadBotForm(value); AdvancedLoadBot(value); OnPropertyChanged(nameof(HasBot)); RefreshCommandStates(); }
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(IsEditorEnabled)); RefreshCommandStates(); }

    private bool CanStart() => !IsBusy;
    private bool CanEdit() => Draft is not null && !IsBusy;
    private bool CanEditProfile() => SelectedProfile is not null && CanEdit();
    private bool CanEditBot() => SelectedBot is not null && CanEdit();

    [RelayCommand(CanExecute = nameof(CanStart))] private async Task CreateAsync()
    {
        if (!ConfirmDiscardUnsavedChanges()) return;
        var dialog = new OpenFolderDialog { Title = "Select an empty folder for the new authoring draft", InitialDirectory = _workspace.Root };
        if (dialog.ShowDialog() == true) await RunDraftAsync(() => _drafts.Create(dialog.FolderName, new ProfilePackManifest { Id = NewPackId.Trim(), Name = NewPackName.Trim(), Version = "1.0.0", MinimumEngineVersion = "0.1.0" }), "Created draft");
    }
    [RelayCommand(CanExecute = nameof(CanStart))] private async Task OpenAsync()
    {
        if (!ConfirmDiscardUnsavedChanges()) return;
        var dialog = new OpenFolderDialog { Title = "Open an Author Studio draft", InitialDirectory = _workspace.Root };
        if (dialog.ShowDialog() == true) await RunDraftAsync(() => _drafts.Open(dialog.FolderName), "Opened draft");
    }
    [RelayCommand(CanExecute = nameof(CanStart))] private async Task CloneAsync()
    {
        if (!ConfirmDiscardUnsavedChanges()) return;
        var source = new OpenFolderDialog { Title = "Select a bundled, installed, or unpacked pack to clone", InitialDirectory = _workspace.ProfilesDir };
        if (source.ShowDialog() != true) return;
        var destination = new OpenFolderDialog { Title = "Select an empty folder for the editable clone", InitialDirectory = _workspace.Root };
        if (destination.ShowDialog() == true) await RunDraftAsync(() => _drafts.Clone(source.FolderName, destination.FolderName), "Created editable clone");
    }
    [RelayCommand(CanExecute = nameof(CanEdit))] private async Task SaveAsync()
    {
        if (!TryCommitPendingUiEdits()) return; CaptureUndoCheckpoint();
        var draft = Draft!; ApplyMetadata(draft); await RunDraftAsync(() => _drafts.SaveMetadata(draft), "Saved metadata atomically", reloadDocuments: false);
    }
    [RelayCommand(CanExecute = nameof(CanEdit))] private async Task ValidateAsync()
    {
        if (!TryCommitPendingUiEdits()) return; CaptureUndoCheckpoint();
        if (!EnsureDocumentsSaved("validate the draft")) return;
        var draft = Draft!; ApplyMetadata(draft); IsBusy = true;
        try { var result = await Task.Run(() => _drafts.Validate(draft, GpuSuite.BuildInfo.Version)); Problems.Clear(); foreach (var issue in result.Issues) Problems.Add(issue); StatusMessage = result.IsValid ? "Validation passed." : $"Validation found {result.Errors.Count()} error(s)."; }
        catch (Exception ex) { StatusMessage = "Validation failed: " + ex.Message; }
        finally { IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanEdit))] private async Task ExportAsync()
    {
        if (!TryCommitPendingUiEdits()) return; CaptureUndoCheckpoint();
        if (!EnsureDocumentsSaved("export the draft")) return;
        var draft = Draft!; var dialog = new SaveFileDialog { Title = "Export authoring draft", Filter = "GPU Test Suite profile pack (*.gtsprofilepack)|*.gtsprofilepack", FileName = $"{PackId}-{PackVersion}{ProfilePackManager.ArchiveExtension}", AddExtension = true, DefaultExt = ProfilePackManager.ArchiveExtension };
        if (dialog.ShowDialog() != true) return;
        ApplyMetadata(draft); IsBusy = true;
        try { var export = await Task.Run(() => _drafts.Export(draft, dialog.FileName, GpuSuite.BuildInfo.Version)); StatusMessage = $"Exported {export.ArchivePath} (SHA-256 {export.Fingerprint})."; }
        catch (Exception ex) { StatusMessage = "Export failed; existing destination was preserved. " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))] private async Task CreateProfileAsync()
    {
        var draft = Draft!; string id = NewProfileId.Trim(); string name = NewProfileName.Trim();
        await RunProfileOperationAsync(() => _drafts.CreateProfile(draft, id, name), "Created profile");
    }
    [RelayCommand(CanExecute = nameof(CanEditProfile))] private async Task DuplicateProfileAsync()
    {
        FlushAllEditorForms(); var draft = Draft!; var source = SelectedProfile!; string id = DuplicateProfileId.Trim();
        await RunProfileOperationAsync(() => _drafts.DuplicateProfile(draft, source, id), "Duplicated profile");
    }
    [RelayCommand(CanExecute = nameof(CanEditProfile))] private async Task SaveProfileAsync()
    {
        if (!TryCommitPendingUiEdits()) return; CaptureUndoCheckpoint();
        var draft = Draft!; var document = SelectedProfile!; ApplyProfileForm();
        await RunProfileOperationAsync(() => _drafts.SaveProfile(draft, document), "Saved profile", document);
    }
    [RelayCommand(CanExecute = nameof(CanEditProfile))] private async Task DeleteProfileAsync()
    {
        var draft = Draft!; var document = SelectedProfile!; string savedId = Path.GetFileNameWithoutExtension(document.FilePath);
        if (MessageBox.Show($"Delete draft profile '{savedId}'? This cannot be undone.", "Delete draft profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunEditorAsync(() => { _drafts.DeleteProfile(draft, document); return true; }, () => Profiles.Remove(document), "Deleted profile");
    }
    [RelayCommand(CanExecute = nameof(CanEditProfile))] private void AddResolution() { if (!string.IsNullOrWhiteSpace(NewResolution) && !Resolutions.Contains(NewResolution, StringComparer.OrdinalIgnoreCase)) Resolutions.Add(NewResolution.Trim()); }
    [RelayCommand(CanExecute = nameof(CanEditProfile))] private void RemoveResolution() { if (SelectedResolution is not null) Resolutions.Remove(SelectedResolution); }
    [RelayCommand(CanExecute = nameof(CanEditProfile))] private void AddSetting() { var p = SelectedProfile!.Profile; var item = new GameSetting { Key = "setting-" + (p.Settings.Count + 1), Label = "New setting" }; p.Settings.Add(item); RefreshList(p.Settings); SelectedSetting = item; }
    [RelayCommand(CanExecute = nameof(CanEditProfile))] private void RemoveSetting() { if (SelectedSetting is null) return; var items = SelectedProfile!.Profile.Settings; items.Remove(SelectedSetting); RefreshList(items); SelectedSetting = null; }
    [RelayCommand(CanExecute = nameof(CanEditProfile))] private void AddVariant() { var p = SelectedProfile!.Profile; var item = new GameVariant { Id = "variant-" + (p.Variants.Count + 1), Name = "New variant" }; p.Variants.Add(item); RefreshList(p.Variants); SelectedVariant = item; }
    [RelayCommand(CanExecute = nameof(CanEditProfile))] private void RemoveVariant() { if (SelectedVariant is null) return; var items = SelectedProfile!.Profile.Variants; items.Remove(SelectedVariant); RefreshList(items); SelectedVariant = null; }
    [RelayCommand(CanExecute = nameof(CanEditProfile))] private void AddScene() { var p = SelectedProfile!.Profile; var item = new SceneProfile { Id = "scene-" + (p.Scenes.Count + 1), Name = "New scene" }; p.Scenes.Add(item); RefreshList(p.Scenes); SelectedScene = item; }
    [RelayCommand(CanExecute = nameof(CanEditProfile))] private void RemoveScene() { if (SelectedScene is null) return; var items = SelectedProfile!.Profile.Scenes; items.Remove(SelectedScene); RefreshList(items); SelectedScene = items.FirstOrDefault(); }
    [RelayCommand(CanExecute = nameof(CanEditProfile))] private void CreateSceneWarmup() { if (SelectedScene is null || SelectedScene.Warmup is not null) return; SelectedScene.Warmup = new SceneWarmupConfig(); RefreshSelectedScene(); }
    [RelayCommand(CanExecute = nameof(CanEditProfile))] private void ClearSceneWarmup() { if (SelectedScene?.Warmup is null) return; SelectedScene.Warmup = null; RefreshSelectedScene(); }

    [RelayCommand(CanExecute = nameof(CanEdit))] private async Task CreateBotAsync()
    {
        var draft = Draft!; string id = NewBotId.Trim();
        await RunBotOperationAsync(() => _drafts.CreateBot(draft, id), "Created bot");
    }
    [RelayCommand(CanExecute = nameof(CanEditBot))] private async Task DuplicateBotAsync()
    {
        FlushAllEditorForms(); var draft = Draft!; var source = SelectedBot!; string id = DuplicateBotId.Trim();
        await RunBotOperationAsync(() => _drafts.DuplicateBot(draft, source, id), "Duplicated bot");
    }
    [RelayCommand(CanExecute = nameof(CanEditBot))] private async Task SaveBotAsync()
    {
        if (!TryCommitPendingUiEdits()) return; CaptureUndoCheckpoint();
        ApplyAdvancedBotForm(); var draft = Draft!; var document = SelectedBot!; string savedId = document.Raw["id"]?.GetValue<string>() ?? document.Bot.Id;
        await RunBotOperationAsync(() => _drafts.SaveBot(draft, document), "Saved bot", document, savedId);
    }
    [RelayCommand(CanExecute = nameof(CanEditBot))] private async Task DeleteBotAsync()
    {
        var draft = Draft!; var document = SelectedBot!; var profiles = Profiles.Select(x => x.Profile).ToArray();
        var guard = _drafts.CanDeleteBot(draft, document, profiles);
        string savedId = document.Raw["id"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(document.FilePath);
        string suffix = guard.CanDelete ? "" : $"\n\nIt is referenced by: {string.Join(", ", guard.References)}. Delete anyway?";
        if (MessageBox.Show($"Delete draft bot '{savedId}'?{suffix}", "Delete draft bot", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunEditorAsync(() => { _drafts.DeleteBot(draft, document, force: !guard.CanDelete, profiles); return true; }, () => Bots.Remove(document), "Deleted bot");
    }
    [RelayCommand(CanExecute = nameof(CanEditBot))] private void AddAction() => AddActionCore(NewActionType);
    [RelayCommand(CanExecute = nameof(CanEditBot))] private void AddPresetAction()
    {
        if (SelectedBotActionPreset is null) return;
        var type = SelectedBotActionPreset.Type;
        if (type == BotActionType.KeyTap && SelectedBot!.Bot.InputDevice == BotInputDevice.Gamepad) type = BotActionType.PadButtonTap;
        var item = AddActionCore(type);
        ApplyPresetDefaults(item.Action);
        RefreshBotPreview();
    }
    [RelayCommand(CanExecute = nameof(CanEditBot))] private void RemoveAction() { if (SelectedAction is null) return; _drafts.RemoveBotAction(SelectedBot!, SelectedAction); Timeline.Remove(SelectedAction); SelectedAction = null; RefreshBotPreview(); }
    [RelayCommand(CanExecute = nameof(CanEditBot))] private void MoveActionUp() => MoveAction(-1);
    [RelayCommand(CanExecute = nameof(CanEditBot))] private void MoveActionDown() => MoveAction(1);

    private void MoveAction(int direction)
    {
        if (SelectedAction is null || SelectedBot is null) return; int index = Timeline.IndexOf(SelectedAction); _drafts.MoveBotAction(SelectedBot, index, direction); Timeline.Clear(); foreach (var action in SelectedBot.Timeline) Timeline.Add(action); RefreshBotPreview();
    }
    private DraftBotActionDocument AddActionCore(BotActionType type)
    {
        var item = _drafts.AddBotAction(SelectedBot!, type);
        Timeline.Add(item);
        SelectedAction = item;
        RefreshBotPreview();
        return item;
    }
    private void ApplyPresetDefaults(BotAction action)
    {
        switch (action.Type)
        {
            case BotActionType.WaitForText:
                action.DurationMs = 60000; action.PollMs = 1200; action.Required = true; action.MinConsecutive = 2;
                break;
            case BotActionType.KeyTap:
                action.Key = "Enter"; action.DurationMs = 80;
                break;
            case BotActionType.PadButtonTap:
                action.Key = "A"; action.DurationMs = 100;
                break;
            case BotActionType.Wait:
                action.DurationMs = 1000;
                break;
            case BotActionType.ReplayRoute:
                action.RoutePath = Routes.FirstOrDefault()?.RelativePath;
                action.Required = true;
                break;
            case BotActionType.SmartTraverse:
                action.DurationMs = 30000; action.StuckThreshold = 1.0; action.StuckTriggerCount = 2;
                action.MaxRecoveryTurns = 6; action.MinHealthyMotionRatio = 0.6;
                action.RecordRoutePath = NextDiscoveryRoutePath();
                break;
            case BotActionType.AssertWorldMotion:
                action.DurationMs = 15000; action.StuckThreshold = 1.0; action.Required = true;
                break;
        }
    }
    private string NextDiscoveryRoutePath()
    {
        for (int suffix = 1; ; suffix++)
        {
            string name = suffix == 1 ? "discovered-route.json" : $"discovered-route-{suffix}.json";
            string candidate = "routes/" + name;
            bool alreadyUsed = Routes.Any(route => route.RelativePath.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                || Bots.SelectMany(bot => bot.Timeline).Any(item => candidate.Equals(item.Action.RecordRoutePath, StringComparison.OrdinalIgnoreCase));
            if (!alreadyUsed) return candidate;
        }
    }
    private void ApplyMetadata(DraftPackProject draft) { draft.Manifest.Id = PackId.Trim(); draft.Manifest.Name = PackName.Trim(); draft.Manifest.Version = PackVersion.Trim(); draft.Manifest.Publisher = Publisher.Trim(); draft.Manifest.Description = Description.Trim(); draft.Manifest.License = License.Trim(); draft.Manifest.Homepage = Homepage.Trim(); draft.Manifest.MinimumEngineVersion = MinimumEngineVersion.Trim(); }
    private static void RefreshList(object source) => CollectionViewSource.GetDefaultView(source)?.Refresh();
    private void RefreshSelectedScene() { var scene = SelectedScene; SelectedScene = null; SelectedScene = scene; }
    private void ApplyProfileForm() { if (SelectedProfile is not null) SelectedProfile.Profile.SupportedResolutions = Resolutions.ToList(); ApplyAdvancedProfileForm(); }
    private async Task RunDraftAsync(Func<DraftPackProject> work, string success, bool reloadDocuments = true)
    {
        IsBusy = true;
        try
        {
            Draft = await Task.Run(work); LoadDraftMetadata();
            if (reloadDocuments) await RefreshDocumentsAsync(); else RefreshInventory();
            StatusMessage = success + ": " + Draft.RootDirectory;
        }
        catch (Exception ex) { StatusMessage = "Author Studio: " + ex.Message; }
        finally { IsBusy = false; }
    }
    private async Task RunProfileOperationAsync(Func<DraftProfileDocument> work, string success, DraftProfileDocument? replacing = null)
    {
        if (!TryCommitPendingUiEdits()) return;
        IsBusy = true;
        try
        {
            var result = await Task.Run(work);
            if (replacing is not null && Profiles.IndexOf(replacing) is int index && index >= 0) { MigrateProfileHistory(replacing, result); Profiles[index] = result; } else Profiles.Add(result);
            SelectedProfile = result;
            await RefreshDraftInventoryAsync();
            StatusMessage = success + ": " + result.Profile.Id;
        }
        catch (Exception ex) { StatusMessage = "Profile edit failed: " + ex.Message; }
        finally { IsBusy = false; }
    }
    private async Task RunBotOperationAsync(Func<DraftBotDocument> work, string success, DraftBotDocument? replacing = null, string? renamedFrom = null)
    {
        if (!TryCommitPendingUiEdits()) return;
        FlushAllEditorForms();
        var dirtyProfiles = new HashSet<DraftProfileDocument>(Profiles.Where(_drafts.HasUnsavedChanges), ReferenceEqualityComparer.Instance);
        IsBusy = true;
        try
        {
            var result = await Task.Run(work);
            if (replacing is not null && Bots.IndexOf(replacing) is int index && index >= 0) { MigrateBotHistory(replacing, result); Bots[index] = result; } else Bots.Add(result);
            if (!string.IsNullOrWhiteSpace(renamedFrom) && !renamedFrom.Equals(result.Bot.Id, StringComparison.OrdinalIgnoreCase))
            {
                RewriteInMemoryBotReferences(dirtyProfiles, renamedFrom, result.Bot.Id);
                string? selectedId = SelectedProfile?.Profile.Id;
                var draft = Draft!;
                var reloaded = await Task.Run(() => _drafts.GetProfiles(draft));
                for (int profileIndex = 0; profileIndex < Profiles.Count; profileIndex++)
                {
                    if (dirtyProfiles.Contains(Profiles[profileIndex])) continue;
                    var replacement = reloaded.FirstOrDefault(x => x.Profile.Id.Equals(Profiles[profileIndex].Profile.Id, StringComparison.OrdinalIgnoreCase));
                    if (replacement is not null) Profiles[profileIndex] = replacement;
                }
                SelectedProfile = Profiles.FirstOrDefault(x => x.Profile.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)) ?? Profiles.FirstOrDefault();
            }
            SelectedBot = result; RefreshBotIds();
            await RefreshDraftInventoryAsync();
            StatusMessage = success + ": " + result.Bot.Id;
        }
        catch (Exception ex) { StatusMessage = "Bot edit failed: " + ex.Message; }
        finally { IsBusy = false; }
    }
    private async Task RunEditorAsync(Func<bool> work, Action updateCollections, string success)
    {
        if (!TryCommitPendingUiEdits()) return;
        IsBusy = true;
        try
        {
            await Task.Run(work); updateCollections(); RefreshBotIds();
            if (SelectedProfile is not null && !Profiles.Contains(SelectedProfile)) SelectedProfile = Profiles.FirstOrDefault();
            if (SelectedBot is not null && !Bots.Contains(SelectedBot)) SelectedBot = Bots.FirstOrDefault();
            await RefreshDraftInventoryAsync(); StatusMessage = success + ".";
        }
        catch (Exception ex) { StatusMessage = "Editor operation failed: " + ex.Message; }
        finally { IsBusy = false; }
    }
    private async Task RefreshDocumentsAsync(string? profileId = null, string? botId = null)
    {
        if (Draft is null) return;
        var draft = Draft; var documents = await Task.Run(() => (_drafts.Open(draft.RootDirectory), _drafts.GetProfiles(draft), _drafts.GetBots(draft)));
        Draft = documents.Item1; Profiles.Clear(); foreach (var item in documents.Item2) Profiles.Add(item); Bots.Clear(); foreach (var item in documents.Item3) Bots.Add(item); RefreshBotIds();
        SelectedProfile = Profiles.FirstOrDefault(x => x.Profile.Id.Equals(profileId ?? SelectedProfile?.Profile.Id, StringComparison.OrdinalIgnoreCase)) ?? Profiles.FirstOrDefault();
        SelectedBot = Bots.FirstOrDefault(x => x.Bot.Id.Equals(botId ?? SelectedBot?.Bot.Id, StringComparison.OrdinalIgnoreCase)) ?? Bots.FirstOrDefault();
        await RefreshAdvancedDocumentsAsync();
        RefreshInventory();
    }
    private async Task RefreshDraftInventoryAsync()
    {
        if (Draft is null) return;
        string root = Draft.RootDirectory;
        Draft = await Task.Run(() => _drafts.Open(root));
        RefreshInventory();
    }
    private void RefreshInventory()
    {
        Inventory.Clear();
        if (Draft is null) return;
        foreach (var file in Draft.Inventory.Profiles.Concat(Draft.Inventory.Bots).Concat(Draft.Inventory.Routes).Concat(Draft.Inventory.Templates).Concat(Draft.Inventory.Assets)) Inventory.Add(file);
        OnPropertyChanged(nameof(InventorySummary));
    }
    private void RefreshBotIds() { BotIds.Clear(); foreach (var id in Bots.Select(x => x.Bot.Id)) BotIds.Add(id); }
    private bool ConfirmDiscardUnsavedChanges()
    {
        if (Draft is null) return true;
        if (!TryCommitPendingUiEdits()) return false; CaptureUndoCheckpoint();
        FlushAllEditorForms();
        bool metadataChanged = PackId != Draft.Manifest.Id || PackName != Draft.Manifest.Name || PackVersion != Draft.Manifest.Version || Publisher != Draft.Manifest.Publisher || Description != Draft.Manifest.Description || License != Draft.Manifest.License || Homepage != Draft.Manifest.Homepage || MinimumEngineVersion != Draft.Manifest.MinimumEngineVersion;
        bool documentsChanged = Profiles.Any(_drafts.HasUnsavedChanges) || Bots.Any(_drafts.HasUnsavedChanges) || HasAdvancedUnsavedChanges();
        if (!metadataChanged && !documentsChanged) return true;
        return MessageBox.Show("This draft has unsaved editor changes. Discard them and switch drafts?", "Unsaved Author Studio changes", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }
    private bool EnsureDocumentsSaved(string operation)
    {
        if (!TryCommitPendingUiEdits()) return false; CaptureUndoCheckpoint();
        FlushAllEditorForms();
        if (!Profiles.Any(_drafts.HasUnsavedChanges) && !Bots.Any(_drafts.HasUnsavedChanges) && !HasAdvancedUnsavedChanges()) return true;
        MessageBox.Show($"Save the changed profile, bot, route, or template before you {operation}. Unsaved editor fields are never discarded or exported implicitly.", "Unsaved Author Studio changes", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }
    private static void RewriteInMemoryBotReferences(IEnumerable<DraftProfileDocument> documents, string oldId, string newId)
    {
        foreach (var document in documents)
        {
            var profile = document.Profile;
            if (profile.SettingsBotScript?.Equals(oldId, StringComparison.OrdinalIgnoreCase) == true) profile.SettingsBotScript = newId;
            foreach (var scene in profile.Scenes)
            {
                if (scene.BotScript?.Equals(oldId, StringComparison.OrdinalIgnoreCase) == true) scene.BotScript = newId;
                if (scene.StartBotScript?.Equals(oldId, StringComparison.OrdinalIgnoreCase) == true) scene.StartBotScript = newId;
                if (scene.ReRunBotScript?.Equals(oldId, StringComparison.OrdinalIgnoreCase) == true) scene.ReRunBotScript = newId;
                if (scene.Warmup?.Script?.Equals(oldId, StringComparison.OrdinalIgnoreCase) == true) scene.Warmup.Script = newId;
            }
        }
    }
    private void LoadDraftMetadata() { if (Draft is null) return; var m = Draft.Manifest; PackId = m.Id; PackName = m.Name; PackVersion = m.Version; Publisher = m.Publisher; Description = m.Description; License = m.License; Homepage = m.Homepage; MinimumEngineVersion = m.MinimumEngineVersion; RegisterMetadataSnapshot(); }
    public bool TryCommitPendingUiEdits()
        => AuthoringEditCommitCoordinator.TryCommit(PendingUiEditsCommitRequested?.GetInvocationList().Cast<Func<bool>>() ?? [], message => StatusMessage = message);
    private void LoadProfileForm(DraftProfileDocument? document) { Resolutions.Clear(); if (document is not null) foreach (var resolution in document.Profile.SupportedResolutions) Resolutions.Add(resolution); SelectedSetting = null; SelectedVariant = null; SelectedScene = document?.Profile.Scenes.FirstOrDefault(); }
    private void LoadBotForm(DraftBotDocument? document) { Timeline.Clear(); if (document is not null) foreach (var action in document.Timeline) Timeline.Add(action); SelectedAction = Timeline.FirstOrDefault(); RefreshBotPreview(); }
    private void RefreshBotPreview() => BotJsonPreview = SelectedBot?.Raw.ToJsonString(GpuSuite.Core.Io.Json.Options) ?? "";
    private void RefreshCommandStates()
    {
        foreach (var command in new IRelayCommand[] { CreateCommand, OpenCommand, CloneCommand, SaveCommand, ValidateCommand, ExportCommand, CreateProfileCommand, DuplicateProfileCommand, SaveProfileCommand, DeleteProfileCommand, AddResolutionCommand, RemoveResolutionCommand, AddSettingCommand, RemoveSettingCommand, AddVariantCommand, RemoveVariantCommand, AddSceneCommand, RemoveSceneCommand, CreateSceneWarmupCommand, ClearSceneWarmupCommand, CreateBotCommand, DuplicateBotCommand, SaveBotCommand, DeleteBotCommand, AddActionCommand, AddPresetActionCommand, RemoveActionCommand, MoveActionUpCommand, MoveActionDownCommand }) command.NotifyCanExecuteChanged();
        RefreshAdvancedCommandStates();
    }
}
