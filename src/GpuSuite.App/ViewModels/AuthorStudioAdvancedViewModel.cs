using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpuSuite.Authoring;
using GpuSuite.Core.Io;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Automation;
using Microsoft.Win32;

namespace GpuSuite.App.ViewModels;

public partial class AuthoringPair : ObservableObject
{
    [ObservableProperty] private string key = "";
    [ObservableProperty] private string value = "";
}

public partial class AuthorStudioViewModel
{
    public ObservableCollection<DraftRouteDocument> Routes { get; } = new();
    public ObservableCollection<RouteStep> RouteSteps { get; } = new();
    public ObservableCollection<DraftFileItem> Templates { get; } = new();
    public ObservableCollection<DraftFileItem> Assets { get; } = new();
    public ObservableCollection<NavScreen> NavScreens { get; } = new();
    public ObservableCollection<NavStep> NavSteps { get; } = new();
    public ObservableCollection<string> AnySignatures { get; } = new();
    public ObservableCollection<string> AllSignatures { get; } = new();
    public ObservableCollection<string> NoneSignatures { get; } = new();
    public ObservableCollection<string> SettingOptions { get; } = new();
    public ObservableCollection<AuthoringPair> SettingValueMap { get; } = new();
    public ObservableCollection<AuthoringPair> VariantOverrides { get; } = new();
    public ObservableCollection<ConfigEdit> SettingConfigEdits { get; } = new();
    public ObservableCollection<RegistryEdit> SettingRegistryEdits { get; } = new();
    public ObservableCollection<AuthoringPair> SettingConfigEditMap { get; } = new();
    public ObservableCollection<AuthoringPair> SettingRegistryEditMap { get; } = new();
    public ObservableCollection<ConfigEdit> ResolutionConfigEdits { get; } = new();
    public ObservableCollection<RegistryEdit> ResolutionRegistryEdits { get; } = new();
    public ObservableCollection<AuthoringPair> ResolutionConfigEditMap { get; } = new();
    public ObservableCollection<AuthoringPair> ResolutionRegistryEditMap { get; } = new();
    public Array RouteStepKinds => Enum.GetValues(typeof(RouteStepKind));

    [ObservableProperty] private int authorTabIndex;
    [ObservableProperty] private DraftRouteDocument? selectedRoute;
    [ObservableProperty] private RouteStep? selectedRouteStep;
    [ObservableProperty] private DraftFileItem? selectedTemplate;
    [ObservableProperty] private DraftFileItem? selectedAsset;
    [ObservableProperty] private NavScreen? selectedNavScreen;
    [ObservableProperty] private NavStep? selectedNavStep;
    [ObservableProperty] private string newRoutePath = "new-route.json";
    [ObservableProperty] private string duplicateRoutePath = "route-copy.json";
    [ObservableProperty] private string routeEditPath = "";
    [ObservableProperty] private string newTemplatePath = "settings.template.json";
    [ObservableProperty] private string templateEditPath = "";
    [ObservableProperty] private string templateText = "";
    [ObservableProperty] private string newAssetPath = "readme.txt";
    [ObservableProperty] private string assetEditPath = "";
    [ObservableProperty] private string newScreenId = "screen-state";
    [ObservableProperty] private string duplicateScreenId = "screen-copy";
    [ObservableProperty] private string newAnySignature = "";
    [ObservableProperty] private string newAllSignature = "";
    [ObservableProperty] private string newNoneSignature = "";
    [ObservableProperty] private string? selectedAnySignature;
    [ObservableProperty] private string? selectedAllSignature;
    [ObservableProperty] private string? selectedNoneSignature;
    [ObservableProperty] private string newSettingOption = "";
    [ObservableProperty] private string textAlternativesText = "";
    [ObservableProperty] private string abortIfTextsText = "";
    [ObservableProperty] private AuthoringPair? selectedSettingMapRow;
    [ObservableProperty] private AuthoringPair? selectedVariantOverride;
    [ObservableProperty] private ConfigEdit? selectedSettingConfigEdit;
    [ObservableProperty] private RegistryEdit? selectedSettingRegistryEdit;
    [ObservableProperty] private AuthoringPair? selectedSettingConfigEditMapRow;
    [ObservableProperty] private AuthoringPair? selectedSettingRegistryEditMapRow;
    [ObservableProperty] private ConfigEdit? selectedResolutionConfigEdit;
    [ObservableProperty] private RegistryEdit? selectedResolutionRegistryEdit;
    [ObservableProperty] private AuthoringPair? selectedResolutionConfigEditMapRow;
    [ObservableProperty] private AuthoringPair? selectedResolutionRegistryEditMapRow;
    [ObservableProperty] private bool suppressHistory;

    public bool HasRoute => SelectedRoute is not null;
    public bool HasTemplate => SelectedTemplate is not null;
    public bool HasAsset => SelectedAsset is not null;
    public bool HasGraph => SelectedBot?.Bot.Graph is not null;
    public BotActionType SelectedActionType
    {
        get => SelectedAction?.Action.Type ?? BotActionType.Wait;
        set
        {
            if (SelectedAction is null || SelectedAction.Action.Type == value) return;
            SelectedAction.Action.Type = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedActionGuidance));
            CollectionViewSource.GetDefaultView(Timeline)?.Refresh();
            RefreshBotPreview();
        }
    }
    public string SelectedActionGuidance => SelectedAction?.Action.Type switch
    {
        null => "Select a timeline step to edit it. Start with a common step above; only open advanced sections when the route needs them.",
        BotActionType.WaitForText => "Enter the expected on-screen phrase in Text gate. Duration is the timeout; Required keeps the run fail-closed, and Min consecutive protects against a single OCR miss.",
        BotActionType.PressUntilText => "Enter the key and target screen text. Set Required and a Max presses limit so an unexpected menu cannot receive unlimited input.",
        BotActionType.TapIfText => "Enter the key and expected screen text. The tap is skipped when that screen cannot be proven; a blank target is rejected by validation.",
        BotActionType.MarkStart => "This opens the measured window. Place it only after the game is visibly ready and all loading or menu navigation is complete.",
        BotActionType.MarkEnd => "This closes the measured window. Place it immediately after the deterministic benchmark movement.",
        BotActionType.KeyDown or BotActionType.KeyUp or BotActionType.KeyTap => "Enter a keyboard key name. Use Down and Up as a matched pair; use Tap for a bounded press. Duration controls a tap hold.",
        BotActionType.PadButtonDown or BotActionType.PadButtonUp or BotActionType.PadButtonTap => "Enter an Xbox-style button name such as A, B, Start or Up. Use Down and Up as a matched pair.",
        BotActionType.PadLeftStick or BotActionType.PadRightStick => "X and Y are stick positions from -1 to 1. Duration controls how long the position is held.",
        BotActionType.PadTrigger => "Button chooses left or right, X is the trigger value from 0 to 1, and Duration controls the hold.",
        BotActionType.MouseMove => "dX and dY are relative mouse movement; Duration spreads the move over time.",
        BotActionType.MouseMoveAbs => "X and Y are normalized screen coordinates from 0 to 1.",
        BotActionType.MouseClick => "Button is left, right or middle. Duration controls the click hold.",
        BotActionType.Wait => "Duration is a simple pause in milliseconds. Prefer a required screen or gameplay gate when readiness can vary.",
        BotActionType.WaitForGameplayBand => "Set the expected FPS floor and ceiling, timeout, polling interval and sustain window. Required should remain enabled for a readiness proof.",
        BotActionType.SmartTraverse => "Use only for route discovery outside Author Studio. Set Duration, motion thresholds and a Record path under routes/, then replay the result for measured runs.",
        BotActionType.Gx10Traverse => "Describe the movement Goal, set Duration and a Record path under routes/. Discovery uses GX10 outside the editor; measured runs should replay the saved route.",
        BotActionType.ReplayRoute => "Choose an existing path under routes/. Replay is deterministic and is the preferred action inside a measured window.",
        BotActionType.AssertWorldMotion => "Set Duration and Stuck threshold. Required makes missing world-motion evidence invalidate the run instead of measuring a menu.",
        _ => "This is an advanced action. Edit only the fields used by its type and validate the draft before export."
    };
    public string RoutePreview => SelectedRoute is null ? "No route selected." : $"{SelectedRoute.Route.Steps.Count} steps · approximately {SelectedRoute.Route.TotalMs:N0} ms · recorded with {SelectedRoute.Route.RecordedDevice.DefaultIfBlank("unspecified device")}";
    public string AssetPreview => SelectedAsset is null ? "No asset selected." : $"{SelectedAsset.RelativePath}\n{SelectedAsset.Bytes:N0} bytes · {Path.GetExtension(SelectedAsset.RelativePath).DefaultIfBlank("no extension")}";

    private readonly Dictionary<string, DraftUndoHistory<string>> _histories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _templateDrafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _templateSaved = new(StringComparer.OrdinalIgnoreCase);

    partial void OnAuthorTabIndexChanged(int value) => RefreshAdvancedCommandStates();
    partial void OnSelectedRouteChanging(DraftRouteDocument? value) => CheckpointRouteBeforeSelection(SelectedRoute);
    partial void OnSelectedRouteChanged(DraftRouteDocument? value)
    {
        RouteSteps.Clear(); if (value is not null) foreach (var step in value.Route.Steps) RouteSteps.Add(step);
        SelectedRouteStep = RouteSteps.FirstOrDefault(); RouteEditPath = value is null ? "" : StripCategory(value.RelativePath, "routes");
        OnPropertyChanged(nameof(HasRoute)); OnPropertyChanged(nameof(RoutePreview)); RegisterCurrentSnapshot(); RefreshAdvancedCommandStates();
    }
    partial void OnSelectedTemplateChanging(DraftFileItem? value)
    {
        if (!SuppressHistory && SelectedTemplate is not null) { _templateDrafts[SelectedTemplate.RelativePath] = TemplateText; CheckpointTemplateBeforeSelection(SelectedTemplate); }
    }
    partial void OnSelectedTemplateChanged(DraftFileItem? value)
    {
        if (value is null) { TemplateText=""; TemplateEditPath=""; }
        else
        {
            TemplateEditPath=StripCategory(value.RelativePath,"templates");
            if(!_templateSaved.ContainsKey(value.RelativePath)) _templateSaved[value.RelativePath]=_drafts.ReadTemplate(Draft!,TemplateEditPath);
            TemplateText=_templateDrafts.TryGetValue(value.RelativePath,out var draft)?draft:_templateSaved[value.RelativePath];
        }
        OnPropertyChanged(nameof(HasTemplate)); RegisterCurrentSnapshot(); RefreshAdvancedCommandStates();
    }
    partial void OnSelectedAssetChanged(DraftFileItem? value)
    {
        AssetEditPath=value is null?"":StripCategory(value.RelativePath,"assets"); OnPropertyChanged(nameof(HasAsset)); OnPropertyChanged(nameof(AssetPreview)); RefreshAdvancedCommandStates();
    }
    partial void OnSelectedSettingChanging(GameSetting? value) => ApplySettingRows(SelectedSetting);
    partial void OnSelectedSettingChanged(GameSetting? value) => LoadSettingRows(value);
    partial void OnSelectedSettingConfigEditChanging(ConfigEdit? value) => ApplyPairMap(SelectedSettingConfigEdit?.ValueMap,SettingConfigEditMap);
    partial void OnSelectedSettingConfigEditChanged(ConfigEdit? value) => LoadPairMap(value?.ValueMap,SettingConfigEditMap);
    partial void OnSelectedSettingRegistryEditChanging(RegistryEdit? value) => ApplyPairMap(SelectedSettingRegistryEdit?.ValueMap,SettingRegistryEditMap);
    partial void OnSelectedSettingRegistryEditChanged(RegistryEdit? value) => LoadPairMap(value?.ValueMap,SettingRegistryEditMap);
    partial void OnSelectedResolutionConfigEditChanging(ConfigEdit? value) => ApplyPairMap(SelectedResolutionConfigEdit?.ValueMap,ResolutionConfigEditMap);
    partial void OnSelectedResolutionConfigEditChanged(ConfigEdit? value) => LoadPairMap(value?.ValueMap,ResolutionConfigEditMap);
    partial void OnSelectedResolutionRegistryEditChanging(RegistryEdit? value) => ApplyPairMap(SelectedResolutionRegistryEdit?.ValueMap,ResolutionRegistryEditMap);
    partial void OnSelectedResolutionRegistryEditChanged(RegistryEdit? value) => LoadPairMap(value?.ValueMap,ResolutionRegistryEditMap);
    partial void OnSelectedVariantChanging(GameVariant? value) => ApplyVariantRows(SelectedVariant);
    partial void OnSelectedVariantChanged(GameVariant? value) => LoadVariantRows(value);
    partial void OnSelectedActionChanging(DraftBotActionDocument? value) => ApplyActionLists(SelectedAction?.Action);
    partial void OnSelectedActionChanged(DraftBotActionDocument? value) { LoadActionLists(value?.Action); OnPropertyChanged(nameof(SelectedActionType)); OnPropertyChanged(nameof(SelectedActionGuidance)); }
    partial void OnSelectedBotChanging(DraftBotDocument? value) { ApplyAdvancedBotForm(); CheckpointBotBeforeSelection(SelectedBot); }
    partial void OnSelectedNavScreenChanging(NavScreen? value) => ApplySignatureRows(SelectedNavScreen);
    partial void OnSelectedNavScreenChanged(NavScreen? value) => LoadSignatureRows(value);

    private bool CanEditRoute() => SelectedRoute is not null && CanEdit();
    private bool CanEditTemplate() => SelectedTemplate is not null && CanEdit();
    private bool CanEditAsset() => SelectedAsset is not null && CanEdit();
    private bool CanEditGraph() => SelectedBot?.Bot.Graph is not null && CanEditBot();
    private bool CanEditScreen() => SelectedNavScreen is not null && CanEditGraph();
    private bool CanUndoAdvanced() => CurrentHistory()?.CanUndo == true;
    private bool CanRedoAdvanced() => CurrentHistory()?.CanRedo == true;

    [RelayCommand(CanExecute=nameof(CanEditBot))] private void CreateGraph()
    {
        _drafts.EnsureGraph(SelectedBot!); LoadGraph(SelectedBot); CaptureUndoCheckpoint(); OnPropertyChanged(nameof(HasGraph)); RefreshAdvancedCommandStates();
    }
    [RelayCommand(CanExecute=nameof(CanEditGraph))] private void ClearGraph()
    {
        if(MessageBox.Show("Clear this bot's navigation graph? The linear action timeline is retained.","Clear graph",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        _drafts.ClearGraph(SelectedBot!); LoadGraph(SelectedBot); CaptureUndoCheckpoint(); OnPropertyChanged(nameof(HasGraph)); RefreshAdvancedCommandStates();
    }
    [RelayCommand(CanExecute=nameof(CanEditGraph))] private void AddScreen()
    {
        var item=_drafts.AddScreen(SelectedBot!,NewScreenId.Trim()); NavScreens.Add(item); SelectedNavScreen=item; CaptureUndoCheckpoint();
    }
    [RelayCommand(CanExecute=nameof(CanEditScreen))] private void DuplicateScreen()
    {
        var item=_drafts.DuplicateScreen(SelectedBot!,SelectedNavScreen!,DuplicateScreenId.Trim()); NavScreens.Add(item); SelectedNavScreen=item; CaptureUndoCheckpoint();
    }
    [RelayCommand(CanExecute=nameof(CanEditScreen))] private void DeleteScreen()
    {
        var item=SelectedNavScreen!; _drafts.DeleteScreen(SelectedBot!,item); NavScreens.Remove(item); SelectedNavScreen=NavScreens.FirstOrDefault(); CaptureUndoCheckpoint();
    }
    [RelayCommand(CanExecute=nameof(CanEditScreen))] private void MoveScreenUp()=>MoveScreen(-1);
    [RelayCommand(CanExecute=nameof(CanEditScreen))] private void MoveScreenDown()=>MoveScreen(1);
    private void MoveScreen(int direction){int index=NavScreens.IndexOf(SelectedNavScreen!);_drafts.MoveScreen(SelectedBot!,index,direction);ReloadCollection(NavScreens,SelectedBot!.Bot.Graph!.Screens);CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditScreen))] private void AddNavStep(){var item=_drafts.AddNavStep(SelectedNavScreen!);NavSteps.Add(item);SelectedNavStep=item;CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditScreen))] private void DeleteNavStep(){if(SelectedNavStep is null)return;_drafts.DeleteNavStep(SelectedNavScreen!,SelectedNavStep);NavSteps.Remove(SelectedNavStep);SelectedNavStep=NavSteps.FirstOrDefault();CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditScreen))] private void MoveNavStepUp()=>MoveNavStep(-1);
    [RelayCommand(CanExecute=nameof(CanEditScreen))] private void MoveNavStepDown()=>MoveNavStep(1);
    private void MoveNavStep(int direction){if(SelectedNavStep is null)return;int index=NavSteps.IndexOf(SelectedNavStep);_drafts.MoveNavStep(SelectedNavScreen!,index,direction);ReloadCollection(NavSteps,SelectedNavScreen!.Do);CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditScreen))] private void AddAnySignature(){AddSignature(AnySignatures,NewAnySignature);NewAnySignature="";}
    [RelayCommand(CanExecute=nameof(CanEditScreen))] private void AddAllSignature(){AddSignature(AllSignatures,NewAllSignature);NewAllSignature="";}
    [RelayCommand(CanExecute=nameof(CanEditScreen))] private void AddNoneSignature(){AddSignature(NoneSignatures,NewNoneSignature);NewNoneSignature="";}
    [RelayCommand(CanExecute=nameof(CanEditScreen))] private void RemoveAnySignature(){if(SelectedAnySignature is not null)AnySignatures.Remove(SelectedAnySignature);ApplySignatureRows(SelectedNavScreen);CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditScreen))] private void RemoveAllSignature(){if(SelectedAllSignature is not null)AllSignatures.Remove(SelectedAllSignature);ApplySignatureRows(SelectedNavScreen);CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditScreen))] private void RemoveNoneSignature(){if(SelectedNoneSignature is not null)NoneSignatures.Remove(SelectedNoneSignature);ApplySignatureRows(SelectedNavScreen);CaptureUndoCheckpoint();}

    [RelayCommand(CanExecute=nameof(CanEdit))] private async Task CreateRouteAsync()=>await RunAdvancedAsync(()=>_drafts.CreateRoute(Draft!,NewRoutePath.Trim()),r=>{Routes.Add(r);SelectedRoute=r;},"Created route");
    [RelayCommand(CanExecute=nameof(CanEditRoute))] private async Task DuplicateRouteAsync()=>await RunAdvancedAsync(()=>_drafts.DuplicateRoute(Draft!,SelectedRoute!,DuplicateRoutePath.Trim()),r=>{Routes.Add(r);SelectedRoute=r;},"Duplicated route");
    [RelayCommand(CanExecute=nameof(CanEdit))] private async Task ImportRouteAsync()
    {
        var dialog=new OpenFileDialog{Title="Import a recorded route",Filter="JSON route (*.json)|*.json|All files (*.*)|*.*"}; if(dialog.ShowDialog()!=true)return;
        await RunAdvancedAsync(()=>_drafts.ImportRoute(Draft!,dialog.FileName,NewRoutePath.Trim()),r=>{Routes.Add(r);SelectedRoute=r;},"Imported route");
    }
    [RelayCommand(CanExecute=nameof(CanEditRoute))] private async Task SaveRouteAsync()
    {
        var old=SelectedRoute!; CaptureUndoCheckpoint();var dirtyBots=new HashSet<DraftBotDocument>(Bots.Where(_drafts.HasUnsavedChanges),ReferenceEqualityComparer.Instance);
        await RunAdvancedAsync(()=>(_drafts.SaveRoute(Draft!,old,RouteEditPath.Trim(),dirtyBots),_drafts.GetBots(Draft!)),r=>{ReplaceRoute(old,r.Item1);ReloadCleanBots(dirtyBots,r.Item2);},"Saved route");
    }
    [RelayCommand(CanExecute=nameof(CanEditRoute))] private async Task DeleteRouteAsync()
    {
        var route=SelectedRoute!;var guard=_drafts.CanDeleteRoute(Draft!,route,Bots);string refs=guard.CanDelete?"":$"\n\nReferenced by: {string.Join(", ",guard.References)}. Delete anyway?";
        if(MessageBox.Show($"Delete '{route.RelativePath}'?{refs}","Delete route",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        await RunAdvancedAsync(()=>{_drafts.DeleteRoute(Draft!,route,!guard.CanDelete,Bots);return true;},_=>{Routes.Remove(route);SelectedRoute=Routes.FirstOrDefault();},"Deleted route");
    }
    [RelayCommand(CanExecute=nameof(CanEditRoute))] private void AddRouteStep(){var step=new RouteStep{Kind=RouteStepKind.Forward,DurationMs=1000};SelectedRoute!.Route.Steps.Add(step);RouteSteps.Add(step);SelectedRouteStep=step;OnPropertyChanged(nameof(RoutePreview));CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditRoute))] private void DeleteRouteStep(){if(SelectedRouteStep is null)return;SelectedRoute!.Route.Steps.Remove(SelectedRouteStep);RouteSteps.Remove(SelectedRouteStep);SelectedRouteStep=RouteSteps.FirstOrDefault();OnPropertyChanged(nameof(RoutePreview));CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditRoute))] private void MoveRouteStepUp()=>MoveRouteStep(-1);
    [RelayCommand(CanExecute=nameof(CanEditRoute))] private void MoveRouteStepDown()=>MoveRouteStep(1);
    private void MoveRouteStep(int direction){if(SelectedRouteStep is null)return;int index=RouteSteps.IndexOf(SelectedRouteStep),target=index+direction;if(index<0||target<0||target>=RouteSteps.Count)return;(SelectedRoute!.Route.Steps[index],SelectedRoute.Route.Steps[target])=(SelectedRoute.Route.Steps[target],SelectedRoute.Route.Steps[index]);ReloadCollection(RouteSteps,SelectedRoute.Route.Steps);OnPropertyChanged(nameof(RoutePreview));CaptureUndoCheckpoint();}

    [RelayCommand(CanExecute=nameof(CanEdit))] private async Task CreateTemplateAsync()=>await RunAdvancedAsync(()=>_drafts.CreateTemplate(Draft!,NewTemplatePath.Trim()),t=>{Templates.Add(t);SelectedTemplate=t;},"Created template");
    [RelayCommand(CanExecute=nameof(CanEdit))] private async Task ImportTemplateAsync()
    {
        var dialog=new OpenFileDialog{Title="Import a text template",Filter="Text and JSON (*.json;*.txt;*.ini;*.cfg;*.xml)|*.json;*.txt;*.ini;*.cfg;*.xml|All files (*.*)|*.*"};if(dialog.ShowDialog()!=true)return;
        await RunAdvancedAsync(()=>_drafts.ImportTemplate(Draft!,dialog.FileName,NewTemplatePath.Trim()),t=>{Templates.Add(t);SelectedTemplate=t;},"Imported template");
    }
    [RelayCommand(CanExecute=nameof(CanEditTemplate))] private async Task SaveTemplateAsync()
    {
        var item=SelectedTemplate!;string text=TemplateText,path=TemplateEditPath.Trim();CaptureUndoCheckpoint();var dirtyProfiles=new HashSet<DraftProfileDocument>(Profiles.Where(_drafts.HasUnsavedChanges),ReferenceEqualityComparer.Instance);
        await RunAdvancedAsync(()=>{_drafts.SaveTemplate(Draft!,StripCategory(item.RelativePath,"templates"),text,path,dirtyProfiles);return (_drafts.GetTemplates(Draft!).Single(x=>StripCategory(x.RelativePath,"templates").Equals(path,StringComparison.OrdinalIgnoreCase)),_drafts.GetProfiles(Draft!));},t=>{ReplaceTemplate(item,t.Item1,text);ReloadCleanProfiles(dirtyProfiles,t.Item2);},"Saved template");
    }
    [RelayCommand(CanExecute=nameof(CanEditTemplate))] private async Task DeleteTemplateAsync()
    {
        var item=SelectedTemplate!;var guard=_drafts.CanDeleteTemplate(Draft!,StripCategory(item.RelativePath,"templates"),Profiles);string refs=guard.CanDelete?"":$"\n\nReferenced by: {string.Join(", ",guard.References)}. Delete anyway?";if(MessageBox.Show($"Delete '{item.RelativePath}'?{refs}","Delete template",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        await RunAdvancedAsync(()=>{_drafts.DeleteTemplate(Draft!,StripCategory(item.RelativePath,"templates"),!guard.CanDelete,Profiles);return true;},_=>RemoveTemplateFromEditor(item),"Deleted template");
    }
    [RelayCommand(CanExecute=nameof(CanEdit))] private async Task ImportAssetAsync()
    {
        var dialog=new OpenFileDialog{Title="Import a pack asset",Filter="All files (*.*)|*.*"};if(dialog.ShowDialog()!=true)return;
        await RunAdvancedAsync(()=>_drafts.ImportAsset(Draft!,dialog.FileName,NewAssetPath.Trim()),a=>{Assets.Add(a);SelectedAsset=a;},"Imported asset");
    }
    [RelayCommand(CanExecute=nameof(CanEditAsset))] private async Task RenameAssetAsync()
    {
        var item=SelectedAsset!;string path=AssetEditPath.Trim();await RunAdvancedAsync(()=>{_drafts.RenameAsset(Draft!,StripCategory(item.RelativePath,"assets"),path);return _drafts.GetAssets(Draft!).Single(x=>StripCategory(x.RelativePath,"assets").Equals(path,StringComparison.OrdinalIgnoreCase));},a=>{int i=Assets.IndexOf(item);Assets[i]=a;SelectedAsset=a;},"Renamed asset");
    }
    [RelayCommand(CanExecute=nameof(CanEditAsset))] private async Task DeleteAssetAsync()
    {
        var item=SelectedAsset!;if(MessageBox.Show($"Delete '{item.RelativePath}'?","Delete asset",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        await RunAdvancedAsync(()=>{_drafts.DeleteAsset(Draft!,StripCategory(item.RelativePath,"assets"));return true;},_=>{Assets.Remove(item);SelectedAsset=Assets.FirstOrDefault();},"Deleted asset");
    }

    [RelayCommand(CanExecute=nameof(CanEditProfile))] private void AddSettingOption(){if(string.IsNullOrWhiteSpace(NewSettingOption))return;SettingOptions.Add(NewSettingOption.Trim());NewSettingOption="";ApplySettingRows(SelectedSetting);CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditProfile))] private void RemoveSettingOption(){if(SettingOptions.Count>0)SettingOptions.RemoveAt(SettingOptions.Count-1);ApplySettingRows(SelectedSetting);CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditProfile))] private void AddSettingMapRow(){SettingValueMap.Add(new AuthoringPair());CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditProfile))] private void RemoveSettingMapRow(){if(SelectedSettingMapRow is null)return;SettingValueMap.Remove(SelectedSettingMapRow);ApplySettingRows(SelectedSetting);CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditProfile))] private void AddVariantOverride(){VariantOverrides.Add(new AuthoringPair());CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditProfile))] private void RemoveVariantOverride(){if(SelectedVariantOverride is null)return;VariantOverrides.Remove(SelectedVariantOverride);ApplyVariantRows(SelectedVariant);CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditProfile))] private void AddSettingConfigEdit(){var item=new ConfigEdit{Pattern="pattern",Replacement="{VALUE}"};SettingConfigEdits.Add(item);SelectedSettingConfigEdit=item;ApplySettingRows(SelectedSetting);CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditProfile))] private void RemoveSettingConfigEdit(){if(SelectedSettingConfigEdit is null)return;SettingConfigEdits.Remove(SelectedSettingConfigEdit);SelectedSettingConfigEdit=SettingConfigEdits.FirstOrDefault();ApplySettingRows(SelectedSetting);CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditProfile))] private void AddSettingRegistryEdit(){var item=new RegistryEdit{ValueName="value",Kind="dword"};SettingRegistryEdits.Add(item);SelectedSettingRegistryEdit=item;ApplySettingRows(SelectedSetting);CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditProfile))] private void RemoveSettingRegistryEdit(){if(SelectedSettingRegistryEdit is null)return;SettingRegistryEdits.Remove(SelectedSettingRegistryEdit);SelectedSettingRegistryEdit=SettingRegistryEdits.FirstOrDefault();ApplySettingRows(SelectedSetting);CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditProfile))] private void AddSettingConfigEditMapRow(){SettingConfigEditMap.Add(new AuthoringPair());CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditProfile))] private void RemoveSettingConfigEditMapRow(){if(SelectedSettingConfigEditMapRow is null)return;SettingConfigEditMap.Remove(SelectedSettingConfigEditMapRow);ApplyPairMap(SelectedSettingConfigEdit?.ValueMap,SettingConfigEditMap);CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditProfile))] private void AddSettingRegistryEditMapRow(){SettingRegistryEditMap.Add(new AuthoringPair());CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditProfile))] private void RemoveSettingRegistryEditMapRow(){if(SelectedSettingRegistryEditMapRow is null)return;SettingRegistryEditMap.Remove(SelectedSettingRegistryEditMapRow);ApplyPairMap(SelectedSettingRegistryEdit?.ValueMap,SettingRegistryEditMap);CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditProfile))] private void AddResolutionConfigEdit(){var item=new ConfigEdit{Pattern="pattern",Replacement="{WIDTH}"};ResolutionConfigEdits.Add(item);SelectedResolutionConfigEdit=item;ApplyResolutionRows();CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditProfile))] private void RemoveResolutionConfigEdit(){if(SelectedResolutionConfigEdit is null)return;ResolutionConfigEdits.Remove(SelectedResolutionConfigEdit);SelectedResolutionConfigEdit=ResolutionConfigEdits.FirstOrDefault();ApplyResolutionRows();CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditProfile))] private void AddResolutionRegistryEdit(){var item=new RegistryEdit{ValueName="value",Kind="dword"};ResolutionRegistryEdits.Add(item);SelectedResolutionRegistryEdit=item;ApplyResolutionRows();CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditProfile))] private void RemoveResolutionRegistryEdit(){if(SelectedResolutionRegistryEdit is null)return;ResolutionRegistryEdits.Remove(SelectedResolutionRegistryEdit);SelectedResolutionRegistryEdit=ResolutionRegistryEdits.FirstOrDefault();ApplyResolutionRows();CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditProfile))] private void AddResolutionConfigEditMapRow(){ResolutionConfigEditMap.Add(new AuthoringPair());CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditProfile))] private void RemoveResolutionConfigEditMapRow(){if(SelectedResolutionConfigEditMapRow is null)return;ResolutionConfigEditMap.Remove(SelectedResolutionConfigEditMapRow);ApplyPairMap(SelectedResolutionConfigEdit?.ValueMap,ResolutionConfigEditMap);CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditProfile))] private void AddResolutionRegistryEditMapRow(){ResolutionRegistryEditMap.Add(new AuthoringPair());CaptureUndoCheckpoint();}
    [RelayCommand(CanExecute=nameof(CanEditProfile))] private void RemoveResolutionRegistryEditMapRow(){if(SelectedResolutionRegistryEditMapRow is null)return;ResolutionRegistryEditMap.Remove(SelectedResolutionRegistryEditMapRow);ApplyPairMap(SelectedResolutionRegistryEdit?.ValueMap,ResolutionRegistryEditMap);CaptureUndoCheckpoint();}

    [RelayCommand(CanExecute=nameof(CanUndoAdvanced))] private void UndoAdvanced(){if(TryCaptureUndoCheckpoint())RestoreHistory(undo:true);}
    [RelayCommand(CanExecute=nameof(CanRedoAdvanced))] private void RedoAdvanced(){if(TryCaptureUndoCheckpoint())RestoreHistory(undo:false);}

    public bool TryCaptureUndoCheckpoint()
    {
        if(SuppressHistory||Draft is null)return true;
        if(!TryCommitPendingUiEdits())return false;
        try { CaptureUndoCheckpointCore(); return true; }
        catch(Exception ex) { StatusMessage="Author Studio could not capture the current edit: "+ex.Message; return false; }
    }
    public void CaptureUndoCheckpoint() => _ = TryCaptureUndoCheckpoint();
    private void CaptureUndoCheckpointCore()
    {
        if(SuppressHistory||Draft is null)return;FlushAllEditorForms();
        Checkpoint("metadata", CaptureMetadataState());
        if(SelectedProfile is not null)Checkpoint("profile|"+SelectedProfile.FilePath,_drafts.CaptureProfileState(SelectedProfile));
        ApplyAdvancedBotForm();if(SelectedBot is not null)Checkpoint("bot|"+SelectedBot.FilePath,_drafts.CaptureBotState(SelectedBot));
        if(SelectedRoute is not null)Checkpoint("route|"+SelectedRoute.FilePath,_drafts.CaptureRouteState(SelectedRoute));
        if(SelectedTemplate is not null){_templateDrafts[SelectedTemplate.RelativePath]=TemplateText;Checkpoint("template|"+SelectedTemplate.RelativePath,TemplateText);}
        OnPropertyChanged(nameof(RoutePreview));
        RefreshAdvancedCommandStates();
    }

    private void Checkpoint(string key,string current)
    {
        if(!_lastSnapshots.TryGetValue(key,out var previous)){_lastSnapshots[key]=current;return;}if(previous==current)return;
        History(key).Capture(previous);_lastSnapshots[key]=current;
    }
    private void CheckpointProfileBeforeSelection(DraftProfileDocument? document){if(SuppressHistory||document is null)return;Checkpoint("profile|"+document.FilePath,_drafts.CaptureProfileState(document));}
    private void CheckpointBotBeforeSelection(DraftBotDocument? document){if(SuppressHistory||document is null)return;Checkpoint("bot|"+document.FilePath,_drafts.CaptureBotState(document));}
    private void CheckpointRouteBeforeSelection(DraftRouteDocument? document){if(SuppressHistory||document is null)return;Checkpoint("route|"+document.FilePath,_drafts.CaptureRouteState(document));}
    private void CheckpointTemplateBeforeSelection(DraftFileItem? document){if(SuppressHistory||document is null)return;Checkpoint("template|"+document.RelativePath,TemplateText);}
    private DraftUndoHistory<string> History(string key){if(!_histories.TryGetValue(key,out var history))_histories[key]=history=new DraftUndoHistory<string>(x=>x,60);return history;}
    private string? CurrentHistoryKey()=>AuthorTabIndex switch{0=>"metadata",1 when SelectedProfile is not null=>"profile|"+SelectedProfile.FilePath,2 when SelectedBot is not null=>"bot|"+SelectedBot.FilePath,3 when SelectedRoute is not null=>"route|"+SelectedRoute.FilePath,4 when SelectedTemplate is not null=>"template|"+SelectedTemplate.RelativePath,_=>null};
    private DraftUndoHistory<string>? CurrentHistory(){string? key=CurrentHistoryKey();return key is not null&&_histories.TryGetValue(key,out var history)?history:null;}
    private void RestoreHistory(bool undo)
    {
        string? key=CurrentHistoryKey();if(key is null)return;var history=History(key);string current=_lastSnapshots[key];string restored=undo?history.Undo(current):history.Redo(current);if(restored==current)return;
        SuppressHistory=true;
        try
        {
            if(key == "metadata") RestoreMetadataState(restored);
            else if(key.StartsWith("profile|")){var old=SelectedProfile!;var next=_drafts.RestoreProfileState(old,restored);int i=Profiles.IndexOf(old);Profiles[i]=next;SelectedProfile=next;}
            else if(key.StartsWith("bot|")){var old=SelectedBot!;var next=_drafts.RestoreBotState(old,restored);int i=Bots.IndexOf(old);Bots[i]=next;SelectedBot=next;}
            else if(key.StartsWith("route|")){var old=SelectedRoute!;var next=_drafts.RestoreRouteState(old,restored);int i=Routes.IndexOf(old);Routes[i]=next;SelectedRoute=next;}
            else if(key.StartsWith("template|")){TemplateText=restored;_templateDrafts[SelectedTemplate!.RelativePath]=restored;}
            _lastSnapshots[key]=restored;StatusMessage=undo?"Undid the last Author Studio edit.":"Redid the Author Studio edit.";
        }
        finally{SuppressHistory=false;RefreshAdvancedCommandStates();}
    }

    private void RegisterCurrentSnapshot()
    {
        if(SuppressHistory)return;
        RegisterMetadataSnapshot();
        if(SelectedProfile is not null)_lastSnapshots.TryAdd("profile|"+SelectedProfile.FilePath,_drafts.CaptureProfileState(SelectedProfile));
        if(SelectedBot is not null)_lastSnapshots.TryAdd("bot|"+SelectedBot.FilePath,_drafts.CaptureBotState(SelectedBot));
        if(SelectedRoute is not null)_lastSnapshots.TryAdd("route|"+SelectedRoute.FilePath,_drafts.CaptureRouteState(SelectedRoute));
        if(SelectedTemplate is not null)_lastSnapshots.TryAdd("template|"+SelectedTemplate.RelativePath,TemplateText);
    }
    private void LoadSettingRows(GameSetting? setting){SettingOptions.Clear();SettingValueMap.Clear();SettingConfigEdits.Clear();SettingRegistryEdits.Clear();if(setting is null)return;foreach(var option in setting.Options)SettingOptions.Add(option);foreach(var pair in setting.Apply.ValueMap)SettingValueMap.Add(new AuthoringPair{Key=pair.Key,Value=pair.Value});foreach(var edit in setting.Apply.Edits)SettingConfigEdits.Add(edit);foreach(var edit in setting.Apply.RegistryEdits)SettingRegistryEdits.Add(edit);SelectedSettingConfigEdit=SettingConfigEdits.FirstOrDefault();SelectedSettingRegistryEdit=SettingRegistryEdits.FirstOrDefault();}
    private void ApplySettingRows(GameSetting? setting){if(setting is null)return;ApplyPairMap(SelectedSettingConfigEdit?.ValueMap,SettingConfigEditMap);ApplyPairMap(SelectedSettingRegistryEdit?.ValueMap,SettingRegistryEditMap);setting.Options=SettingOptions.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();setting.Apply.ValueMap=PairsToDictionary(SettingValueMap);setting.Apply.Edits=SettingConfigEdits.ToList();setting.Apply.RegistryEdits=SettingRegistryEdits.ToList();}
    private void LoadVariantRows(GameVariant? variant){VariantOverrides.Clear();if(variant is null)return;foreach(var pair in variant.Settings)VariantOverrides.Add(new AuthoringPair{Key=pair.Key,Value=pair.Value});}
    private void ApplyVariantRows(GameVariant? variant){if(variant is not null)variant.Settings=PairsToDictionary(VariantOverrides);}
    private static Dictionary<string,string> PairsToDictionary(IEnumerable<AuthoringPair> rows){var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);foreach(var row in rows)if(!string.IsNullOrWhiteSpace(row.Key))result[row.Key.Trim()]=row.Value;return result;}
    private static void LoadPairMap(Dictionary<string,string>? source,ObservableCollection<AuthoringPair> target){target.Clear();if(source is null)return;foreach(var pair in source)target.Add(new AuthoringPair{Key=pair.Key,Value=pair.Value});}
    private static void ApplyPairMap(Dictionary<string,string>? target,IEnumerable<AuthoringPair> source){if(target is null)return;target.Clear();foreach(var pair in PairsToDictionary(source))target[pair.Key]=pair.Value;}
    private void LoadResolutionRows(DraftProfileDocument? document){ResolutionConfigEdits.Clear();ResolutionRegistryEdits.Clear();if(document is null)return;foreach(var edit in document.Profile.ResolutionApply.Edits)ResolutionConfigEdits.Add(edit);foreach(var edit in document.Profile.ResolutionApply.RegistryEdits)ResolutionRegistryEdits.Add(edit);SelectedResolutionConfigEdit=ResolutionConfigEdits.FirstOrDefault();SelectedResolutionRegistryEdit=ResolutionRegistryEdits.FirstOrDefault();}
    private void ApplyResolutionRows(){if(SelectedProfile is null)return;ApplyPairMap(SelectedResolutionConfigEdit?.ValueMap,ResolutionConfigEditMap);ApplyPairMap(SelectedResolutionRegistryEdit?.ValueMap,ResolutionRegistryEditMap);SelectedProfile.Profile.ResolutionApply.Edits=ResolutionConfigEdits.ToList();SelectedProfile.Profile.ResolutionApply.RegistryEdits=ResolutionRegistryEdits.ToList();}
    private void LoadGraph(DraftBotDocument? bot){NavScreens.Clear();if(bot?.Bot.Graph is not null)foreach(var screen in bot.Bot.Graph.Screens)NavScreens.Add(screen);SelectedNavScreen=NavScreens.FirstOrDefault();OnPropertyChanged(nameof(HasGraph));}
    private void LoadActionLists(BotAction? action){TextAlternativesText=action is null?"":string.Join(Environment.NewLine,action.TextAlternatives);AbortIfTextsText=action is null?"":string.Join(Environment.NewLine,action.AbortIfTexts);}
    private void ApplyActionLists(BotAction? action){if(action is null)return;action.TextAlternatives=ParseLines(TextAlternativesText);action.AbortIfTexts=ParseLines(AbortIfTextsText);}
    private static List<string> ParseLines(string text)=>text.Split(['\r','\n',';'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private void LoadSignatureRows(NavScreen? screen){AnySignatures.Clear();AllSignatures.Clear();NoneSignatures.Clear();NavSteps.Clear();if(screen is null)return;foreach(var x in screen.AnyOf)AnySignatures.Add(x);foreach(var x in screen.AllOf)AllSignatures.Add(x);foreach(var x in screen.NoneOf)NoneSignatures.Add(x);foreach(var x in screen.Do)NavSteps.Add(x);SelectedNavStep=NavSteps.FirstOrDefault();}
    private void ApplySignatureRows(NavScreen? screen){if(screen is null)return;screen.AnyOf=AnySignatures.ToList();screen.AllOf=AllSignatures.ToList();screen.NoneOf=NoneSignatures.ToList();}
    private void AddSignature(ObservableCollection<string> list,string text){if(string.IsNullOrWhiteSpace(text))return;list.Add(text.Trim());ApplySignatureRows(SelectedNavScreen);CaptureUndoCheckpoint();}
    private void AdvancedLoadProfile(DraftProfileDocument? document){LoadSettingRows(null);LoadVariantRows(null);LoadResolutionRows(document);RegisterCurrentSnapshot();}
    private void AdvancedLoadBot(DraftBotDocument? document){LoadGraph(document);RegisterCurrentSnapshot();}
    private void ApplyAdvancedBotForm(){ApplyActionLists(SelectedAction?.Action);ApplySignatureRows(SelectedNavScreen);}
    private void FlushAllEditorForms(){ApplyProfileForm();ApplyAdvancedBotForm();}

    private string CaptureMetadataState() => new JsonObject
    {
        ["id"] = PackId, ["name"] = PackName, ["version"] = PackVersion, ["publisher"] = Publisher,
        ["description"] = Description, ["license"] = License, ["homepage"] = Homepage,
        ["minimumEngineVersion"] = MinimumEngineVersion
    }.ToJsonString();
    private void RegisterMetadataSnapshot() => _lastSnapshots.TryAdd("metadata", CaptureMetadataState());
    private void RestoreMetadataState(string state)
    {
        var metadata = JsonNode.Parse(state) as JsonObject ?? throw new InvalidDataException("Metadata history state is invalid.");
        PackId=metadata["id"]?.GetValue<string>() ?? ""; PackName=metadata["name"]?.GetValue<string>() ?? "";
        PackVersion=metadata["version"]?.GetValue<string>() ?? ""; Publisher=metadata["publisher"]?.GetValue<string>() ?? "";
        Description=metadata["description"]?.GetValue<string>() ?? ""; License=metadata["license"]?.GetValue<string>() ?? "";
        Homepage=metadata["homepage"]?.GetValue<string>() ?? ""; MinimumEngineVersion=metadata["minimumEngineVersion"]?.GetValue<string>() ?? "";
    }
    private void ApplyAdvancedProfileForm(){ApplySettingRows(SelectedSetting);ApplyVariantRows(SelectedVariant);ApplyResolutionRows();}
    private bool HasAdvancedUnsavedChanges()
    {
        if(SelectedTemplate is not null)_templateDrafts[SelectedTemplate.RelativePath]=TemplateText;
        return Routes.Any(_drafts.HasUnsavedChanges)||_templateDrafts.Any(x=>!_templateSaved.TryGetValue(x.Key,out var saved)||saved!=x.Value);
    }
    private async Task RefreshAdvancedDocumentsAsync()
    {
        SuppressHistory=true;
        try
        {
            SelectedRoute=null;SelectedTemplate=null;SelectedAsset=null;Routes.Clear();Templates.Clear();Assets.Clear();_templateDrafts.Clear();_templateSaved.Clear();_histories.Clear();_lastSnapshots.Clear();if(Draft is null)return;
            var data=await Task.Run(()=>(_drafts.GetRoutes(Draft),_drafts.GetTemplates(Draft),_drafts.GetAssets(Draft)));foreach(var x in data.Item1)Routes.Add(x);foreach(var x in data.Item2)Templates.Add(x);foreach(var x in data.Item3)Assets.Add(x);SelectedRoute=Routes.FirstOrDefault();SelectedTemplate=Templates.FirstOrDefault();SelectedAsset=Assets.FirstOrDefault();
        }
        finally{SuppressHistory=false;RegisterCurrentSnapshot();}
    }
    private void RefreshAdvancedCommandStates()
    {
        foreach(var command in new IRelayCommand[]{CreateGraphCommand,ClearGraphCommand,AddScreenCommand,DuplicateScreenCommand,DeleteScreenCommand,MoveScreenUpCommand,MoveScreenDownCommand,AddNavStepCommand,DeleteNavStepCommand,MoveNavStepUpCommand,MoveNavStepDownCommand,AddAnySignatureCommand,AddAllSignatureCommand,AddNoneSignatureCommand,RemoveAnySignatureCommand,RemoveAllSignatureCommand,RemoveNoneSignatureCommand,CreateRouteCommand,DuplicateRouteCommand,ImportRouteCommand,SaveRouteCommand,DeleteRouteCommand,AddRouteStepCommand,DeleteRouteStepCommand,MoveRouteStepUpCommand,MoveRouteStepDownCommand,CreateTemplateCommand,ImportTemplateCommand,SaveTemplateCommand,DeleteTemplateCommand,ImportAssetCommand,RenameAssetCommand,DeleteAssetCommand,AddSettingOptionCommand,RemoveSettingOptionCommand,AddSettingMapRowCommand,RemoveSettingMapRowCommand,AddVariantOverrideCommand,RemoveVariantOverrideCommand,AddSettingConfigEditCommand,RemoveSettingConfigEditCommand,AddSettingRegistryEditCommand,RemoveSettingRegistryEditCommand,AddSettingConfigEditMapRowCommand,RemoveSettingConfigEditMapRowCommand,AddSettingRegistryEditMapRowCommand,RemoveSettingRegistryEditMapRowCommand,AddResolutionConfigEditCommand,RemoveResolutionConfigEditCommand,AddResolutionRegistryEditCommand,RemoveResolutionRegistryEditCommand,AddResolutionConfigEditMapRowCommand,RemoveResolutionConfigEditMapRowCommand,AddResolutionRegistryEditMapRowCommand,RemoveResolutionRegistryEditMapRowCommand,UndoAdvancedCommand,RedoAdvancedCommand})command.NotifyCanExecuteChanged();
    }
    private async Task RunAdvancedAsync<T>(Func<T> work,Action<T> apply,string success){if(!TryCommitPendingUiEdits())return;IsBusy=true;try{var result=await Task.Run(work);apply(result);await RefreshDraftInventoryAsync();StatusMessage=success+".";}catch(Exception ex){StatusMessage=success+" failed: "+ex.Message;}finally{IsBusy=false;RefreshAdvancedCommandStates();}}
    private void ReplaceRoute(DraftRouteDocument old,DraftRouteDocument next){MigrateHistory("route|"+old.FilePath,"route|"+next.FilePath);int i=Routes.IndexOf(old);Routes[i]=next;SelectedRoute=next;RefreshBotPreview();}
    private void ReplaceTemplate(DraftFileItem old,DraftFileItem next,string text)
    {
        MigrateHistory("template|"+old.RelativePath,"template|"+next.RelativePath);int i=Templates.IndexOf(old);Templates[i]=next;SuppressHistory=true;
        try{_templateDrafts[next.RelativePath]=text;_templateSaved[next.RelativePath]=text;SelectedTemplate=next;}finally{SuppressHistory=false;}
        if(!old.RelativePath.Equals(next.RelativePath,StringComparison.OrdinalIgnoreCase)){_templateDrafts.Remove(old.RelativePath);_templateSaved.Remove(old.RelativePath);}
    }
    private void RemoveTemplateFromEditor(DraftFileItem item)
    {
        Templates.Remove(item);SuppressHistory=true;try{SelectedTemplate=Templates.FirstOrDefault();}finally{SuppressHistory=false;}_templateDrafts.Remove(item.RelativePath);_templateSaved.Remove(item.RelativePath);
    }
    private void ReloadCleanBots(HashSet<DraftBotDocument> dirty,IReadOnlyList<DraftBotDocument> reloaded)
    {
        string? selectedId=SelectedBot?.Bot.Id;for(int i=0;i<Bots.Count;i++){if(dirty.Contains(Bots[i]))continue;var next=reloaded.FirstOrDefault(x=>x.Bot.Id.Equals(Bots[i].Bot.Id,StringComparison.OrdinalIgnoreCase));if(next is not null){Bots[i]=next;string key="bot|"+next.FilePath;_histories.Remove(key);_lastSnapshots[key]=_drafts.CaptureBotState(next);}}SelectedBot=Bots.FirstOrDefault(x=>x.Bot.Id.Equals(selectedId,StringComparison.OrdinalIgnoreCase))??Bots.FirstOrDefault();RefreshBotIds();
    }
    private void ReloadCleanProfiles(HashSet<DraftProfileDocument> dirty,IReadOnlyList<DraftProfileDocument> reloaded)
    {
        string? selectedId=SelectedProfile?.Profile.Id;for(int i=0;i<Profiles.Count;i++){if(dirty.Contains(Profiles[i]))continue;var next=reloaded.FirstOrDefault(x=>x.Profile.Id.Equals(Profiles[i].Profile.Id,StringComparison.OrdinalIgnoreCase));if(next is not null){Profiles[i]=next;string key="profile|"+next.FilePath;_histories.Remove(key);_lastSnapshots[key]=_drafts.CaptureProfileState(next);}}SelectedProfile=Profiles.FirstOrDefault(x=>x.Profile.Id.Equals(selectedId,StringComparison.OrdinalIgnoreCase))??Profiles.FirstOrDefault();
    }
    private void MigrateHistory(string oldKey,string newKey){if(oldKey.Equals(newKey,StringComparison.OrdinalIgnoreCase))return;if(_histories.Remove(oldKey,out var history))_histories[newKey]=history;if(_lastSnapshots.Remove(oldKey,out var snapshot))_lastSnapshots[newKey]=snapshot;}
    private void MigrateProfileHistory(DraftProfileDocument old,DraftProfileDocument next)=>MigrateHistory("profile|"+old.FilePath,"profile|"+next.FilePath);
    private void MigrateBotHistory(DraftBotDocument old,DraftBotDocument next)=>MigrateHistory("bot|"+old.FilePath,"bot|"+next.FilePath);
    private static string StripCategory(string path,string category){string prefix=category+"/";return path.Replace('\\','/').StartsWith(prefix,StringComparison.OrdinalIgnoreCase)?path.Replace('\\','/')[prefix.Length..]:path;}
    private static void ReloadCollection<T>(ObservableCollection<T> target,IEnumerable<T> source){target.Clear();foreach(var item in source)target.Add(item);}
}

file static class AuthorStudioStringExtensions
{
    public static string DefaultIfBlank(this string? value,string fallback)=>string.IsNullOrWhiteSpace(value)?fallback:value;
}
