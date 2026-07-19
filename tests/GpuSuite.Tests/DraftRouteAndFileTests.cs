using System.Text.Json.Nodes;
using GpuSuite.Authoring;
using GpuSuite.Engine.Automation;
using GpuSuite.Engine.Profiles;
using Xunit;

namespace GpuSuite.Tests;

public sealed class DraftRouteAndFileTests : IDisposable
{
    private readonly string _temp=Path.Combine(Path.GetTempPath(),"GpuSuiteRoutes",Guid.NewGuid().ToString("N"));
    private readonly DraftPackService _service=new();
    public DraftRouteAndFileTests()=>Directory.CreateDirectory(_temp);
    public void Dispose(){if(Directory.Exists(_temp))Directory.Delete(_temp,true);}
    [Fact] public void RouteRenameRewritesReplayAndRecordReferencesAndGuardsDelete()
    {
        var draft=Draft(); var route=_service.CreateRoute(draft,"walk.json"); route.Route.Steps.Add(new RouteStep{Kind=RouteStepKind.Forward,DurationMs=400}); _service.SaveRoute(draft,route);
        var bot=_service.CreateBot(draft,"nav"); var replay=_service.AddBotAction(bot,BotActionType.ReplayRoute); replay.Action.RoutePath="routes/walk.json"; var record=_service.AddBotAction(bot,BotActionType.SmartTraverse); record.Action.RecordRoutePath="routes/walk.json"; _service.SaveBot(draft,bot);
        var renamed=_service.SaveRoute(draft,route,"nested/walk-2.json"); var saved=_service.GetBots(draft).Single();
        Assert.All(saved.Timeline.Where(x=>x.Action.RoutePath is not null||x.Action.RecordRoutePath is not null),x=>Assert.Contains("routes/nested/walk-2.json",x.Action.RoutePath??x.Action.RecordRoutePath));
        Assert.False(_service.CanDeleteRoute(draft,renamed).CanDelete); Assert.Throws<InvalidOperationException>(()=>_service.DeleteRoute(draft,renamed));
    }
    [Fact] public void GraphScreenAndStepUnknownFieldsSurviveReorder()
    {
        var draft=Draft(); var bot=_service.CreateBot(draft,"graph-bot"); var graph=_service.EnsureGraph(bot); var one=_service.AddScreen(bot,"one"); var two=_service.AddScreen(bot,"two"); var step=_service.AddNavStep(one); step.Key="Enter"; _service.SaveBot(draft,bot);
        var raw=JsonNode.Parse(File.ReadAllText(bot.FilePath))!.AsObject(); raw["graph"]!["screens"]![0]!["future"]="one"; raw["graph"]!["screens"]![0]!["do"]![0]!["stepFuture"]=true; File.WriteAllText(bot.FilePath,raw.ToJsonString());
        bot=_service.GetBots(draft).Single(); _service.MoveScreen(bot,1,-1); _service.SaveBot(draft,bot); raw=JsonNode.Parse(File.ReadAllText(bot.FilePath))!.AsObject();
        Assert.Equal("one",raw["graph"]!["screens"]![1]!["future"]!.GetValue<string>()); Assert.True(raw["graph"]!["screens"]![1]!["do"]![0]!["stepFuture"]!.GetValue<bool>());
    }
    [Fact] public void DuplicateScreenCarriesItsTrackedExtensionFields()
    {
        var draft=Draft();var bot=_service.CreateBot(draft,"graph-copy");var screen=_service.AddScreen(bot,"source");_service.AddNavStep(screen).Key="A";_service.AddNavStep(screen).Key="B";bot=_service.SaveBot(draft,bot);var raw=JsonNode.Parse(File.ReadAllText(bot.FilePath))!;raw["graph"]!["screens"]![0]!["future"]="screen";raw["graph"]!["screens"]![0]!["do"]![0]!["futureStep"]="removed";raw["graph"]!["screens"]![0]!["do"]![1]!["futureStep"]="survivor";File.WriteAllText(bot.FilePath,raw.ToJsonString());
        bot=_service.GetBots(draft).Single();screen=bot.Bot.Graph!.Screens[0];_service.DeleteNavStep(screen,screen.Do[0]);_service.DuplicateScreen(bot,screen,"copy");_service.SaveBot(draft,bot);raw=JsonNode.Parse(File.ReadAllText(bot.FilePath))!;
        Assert.Equal("copy",raw["graph"]!["screens"]![1]!["id"]!.GetValue<string>());Assert.Equal("screen",raw["graph"]!["screens"]![1]!["future"]!.GetValue<string>());Assert.Equal("B",raw["graph"]!["screens"]![1]!["do"]![0]!["key"]!.GetValue<string>());Assert.Equal("survivor",raw["graph"]!["screens"]![1]!["do"]![0]!["futureStep"]!.GetValue<string>());
    }
    [Fact] public void DocumentDuplicatesIncludeUnsavedVisualState()
    {
        var draft=Draft();var profile=_service.GetProfiles(draft).Single();profile.Profile.Name="Unsaved name";profile.Profile.Variants.Add(new GpuSuite.Core.Models.GameVariant{Id="unsaved",Settings=new Dictionary<string,string>{{"quality","High"}}});var profileCopy=_service.DuplicateProfile(draft,profile,"profile-copy");Assert.Equal("Unsaved name",profileCopy.Profile.Name);Assert.Equal("High",profileCopy.Profile.Variants.Single().Settings["quality"]);
        var bot=_service.CreateBot(draft,"source-bot");_service.AddBotAction(bot,BotActionType.Wait).Action.Note="unsaved action";_service.AddScreen(bot,"unsaved-screen");var botCopy=_service.DuplicateBot(draft,bot,"bot-copy");Assert.Equal("unsaved action",botCopy.Timeline.Single().Action.Note);Assert.Equal("unsaved-screen",botCopy.Bot.Graph!.Screens.Single().Id);
    }
    [Fact] public void TemplatesAndAssetsRejectTraversalAndCollisions()
    {
        var draft=Draft(); _service.CreateTemplate(draft,"nested/a.txt","hello"); Assert.Equal("hello",_service.ReadTemplate(draft,"nested/a.txt")); Assert.Throws<InvalidDataException>(()=>_service.CreateTemplate(draft,"../bad.txt")); Assert.Throws<IOException>(()=>_service.CreateTemplate(draft,"nested/a.txt"));
        string source=Path.Combine(_temp,"asset.bin"); File.WriteAllBytes(source,[1,2,3]); _service.ImportAsset(draft,source,"images/a.bin"); Assert.Single(_service.GetAssets(draft)); Assert.Throws<IOException>(()=>_service.ImportAsset(draft,source,"images/a.bin"));
    }
    [Fact] public void UndoHistoryIsBoundedAndDoesNotShareState()
    {
        var left=new DraftUndoHistory<string>(x=>x,2); var right=new DraftUndoHistory<string>(x=>x,2); left.Capture("one");left.Capture("two");left.Capture("three");
        Assert.Equal("three",left.Undo("four"));Assert.Equal("two",left.Undo("three"));Assert.False(left.CanUndo);Assert.Equal("three",left.Redo("two"));Assert.False(right.CanUndo);
    }
    [Fact] public void RouteUnknownFieldsAndStepIdentitySurviveReorderAndHistory()
    {
        var draft=Draft();var route=_service.CreateRoute(draft,"future.json");route.Route.Steps.Add(new RouteStep{Kind=RouteStepKind.Forward,DurationMs=10});route.Route.Steps.Add(new RouteStep{Kind=RouteStepKind.Turn,Deg=90});route=_service.SaveRoute(draft,route);
        var raw=JsonNode.Parse(File.ReadAllText(route.FilePath))!.AsObject();raw["futureRoute"]="keep";raw["Steps"]![0]!["futureStep"]="forward";File.WriteAllText(route.FilePath,raw.ToJsonString());
        route=_service.GetRoutes(draft).Single();string baseline=_service.CaptureRouteState(route);(route.Route.Steps[0],route.Route.Steps[1])=(route.Route.Steps[1],route.Route.Steps[0]);route=_service.SaveRoute(draft,route);raw=JsonNode.Parse(File.ReadAllText(route.FilePath))!.AsObject();
        Assert.Equal("keep",raw["futureRoute"]!.GetValue<string>());Assert.Equal("forward",raw["Steps"]![1]!["futureStep"]!.GetValue<string>());
        var restored=_service.RestoreRouteState(route,baseline);Assert.Equal(RouteStepKind.Forward,restored.Route.Steps[0].Kind);Assert.Equal("keep",JsonNode.Parse(_service.CaptureRouteState(restored))!["futureRoute"]!.GetValue<string>());
    }
    [Fact] public void DirtyInMemoryRouteReferencesParticipateInRenameAndDeleteGuard()
    {
        var draft=Draft();var route=_service.CreateRoute(draft,"old.json");var bot=_service.CreateBot(draft,"dirty");var action=_service.AddBotAction(bot,BotActionType.ReplayRoute);action.Action.RoutePath="routes/old.json";
        Assert.False(_service.CanDeleteRoute(draft,route,[bot]).CanDelete);var renamed=_service.SaveRoute(draft,route,"new.json",[bot]);Assert.Equal("routes/new.json",action.Action.RoutePath);Assert.False(_service.CanDeleteRoute(draft,renamed,[bot]).CanDelete);
    }
    [Fact] public void TemplateRenameRewritesReferencesAndDeleteIsGuarded()
    {
        var draft=Draft();_service.CreateTemplate(draft,"settings.json","{}");var profile=_service.GetProfiles(draft).Single();profile.Profile.ResolutionApply.TemplateFilePath="templates/settings.json";_service.SaveProfile(draft,profile);profile=_service.GetProfiles(draft).Single();
        _service.SaveTemplate(draft,"settings.json","{}","renamed/settings.json",[profile]);Assert.Equal("templates/renamed/settings.json",profile.Profile.ResolutionApply.TemplateFilePath);Assert.Equal("templates/renamed/settings.json",_service.GetProfiles(draft).Single().Profile.ResolutionApply.TemplateFilePath);
        Assert.False(_service.CanDeleteTemplate(draft,"renamed/settings.json",[profile]).CanDelete);Assert.Throws<InvalidOperationException>(()=>_service.DeleteTemplate(draft,"renamed/settings.json",false,[profile]));
    }
    [Fact] public void RouteRenameRollsBackDiskAndDirtyReferencesWhenACommitMoveFails()
    {
        var files = new ThrowOnceFileOperations(); var service = new DraftPackService(fileOperations: files);
        var draft=service.Create(Path.Combine(_temp,"rollback-route"),new ProfilePackManifest{Id="studio.rollback-route",Name="routes",Version="1.0.0",MinimumEngineVersion="0.1.0"});
        var route=service.CreateRoute(draft,"old.json");route.Route.Steps.Add(new RouteStep{Kind=RouteStepKind.Forward,DurationMs=30});route=service.SaveRoute(draft,route);
        var saved=service.CreateBot(draft,"saved");service.AddBotAction(saved,BotActionType.ReplayRoute).Action.RoutePath="routes/old.json";service.SaveBot(draft,saved);
        var dirty=service.CreateBot(draft,"dirty");service.AddBotAction(dirty,BotActionType.SmartTraverse).Action.RecordRoutePath="routes/old.json";
        byte[] routeBefore=File.ReadAllBytes(route.FilePath);byte[] botBefore=File.ReadAllBytes(Path.Combine(draft.RootDirectory,"bots","saved.json"));
        files.FailMoveNumber(2);
        Assert.Throws<IOException>(()=>service.SaveRoute(draft,route,"renamed.json",[dirty]));
        Assert.Equal(routeBefore,File.ReadAllBytes(route.FilePath));Assert.Equal(botBefore,File.ReadAllBytes(Path.Combine(draft.RootDirectory,"bots","saved.json")));
        Assert.False(File.Exists(Path.Combine(draft.RootDirectory,"routes","renamed.json")));Assert.Equal("routes/old.json",dirty.Timeline.Single().Action.RecordRoutePath);AssertNoRenameTemps(draft.RootDirectory);
    }
    [Fact] public void RouteRenameKeepsUnknownFieldsInRewrittenBotDocuments()
    {
        var draft=Draft();var route=_service.CreateRoute(draft,"old.json");route.Route.Steps.Add(RouteStep.Fwd(10));route=_service.SaveRoute(draft,route);
        var bot=_service.CreateBot(draft,"extensions");_service.AddBotAction(bot,BotActionType.ReplayRoute).Action.RoutePath="routes/old.json";_service.SaveBot(draft,bot);
        var raw=JsonNode.Parse(File.ReadAllText(bot.FilePath))!.AsObject();raw["futureBot"]="kept";raw["actions"]![0]!["futureAction"]="kept";File.WriteAllText(bot.FilePath,raw.ToJsonString());
        _service.SaveRoute(draft,route,"new.json");raw=JsonNode.Parse(File.ReadAllText(bot.FilePath))!.AsObject();
        Assert.Equal("kept",raw["futureBot"]!.GetValue<string>());Assert.Equal("kept",raw["actions"]![0]!["futureAction"]!.GetValue<string>());Assert.Equal("routes/new.json",raw["actions"]![0]!["routePath"]!.GetValue<string>());
    }
    [Fact] public void TemplateRenameRollsBackDiskAndDirtyReferencesWhenACommitMoveFails()
    {
        var files = new ThrowOnceFileOperations(); var service = new DraftPackService(fileOperations: files);
        var draft=service.Create(Path.Combine(_temp,"rollback-template"),new ProfilePackManifest{Id="studio.rollback-template",Name="templates",Version="1.0.0",MinimumEngineVersion="0.1.0"});
        service.CreateTemplate(draft,"old.json","{\"future\":true}");var saved=service.GetProfiles(draft).Single();saved.Profile.ResolutionApply.TemplateFilePath="templates/old.json";service.SaveProfile(draft,saved);
        var dirty=service.GetProfiles(draft).Single();dirty.Profile.Name="dirty editor value";dirty.Profile.ResolutionApply.TemplateFilePath="templates/old.json";
        byte[] templateBefore=File.ReadAllBytes(Path.Combine(draft.RootDirectory,"templates","old.json"));byte[] profileBefore=File.ReadAllBytes(saved.FilePath);
        files.FailMoveNumber(2);
        Assert.Throws<IOException>(()=>service.SaveTemplate(draft,"old.json","{\"future\":\"changed\"}","new.json",[dirty]));
        Assert.Equal(templateBefore,File.ReadAllBytes(Path.Combine(draft.RootDirectory,"templates","old.json")));Assert.Equal(profileBefore,File.ReadAllBytes(saved.FilePath));
        Assert.False(File.Exists(Path.Combine(draft.RootDirectory,"templates","new.json")));Assert.Equal("templates/old.json",dirty.Profile.ResolutionApply.TemplateFilePath);AssertNoRenameTemps(draft.RootDirectory);
    }
    [Fact] public void RouteRenameWritesPersistedBaselineButKeepsUnrelatedDirtyEditorState()
    {
        var draft=Draft();var route=_service.CreateRoute(draft,"old.json");route.Route.Steps.Add(RouteStep.Fwd(10));route=_service.SaveRoute(draft,route);
        var saved=_service.CreateBot(draft,"baseline");_service.AddBotAction(saved,BotActionType.ReplayRoute).Action.RoutePath="routes/old.json";_service.SaveBot(draft,saved);
        var dirty=_service.GetBots(draft).Single();dirty.Bot.Description="unsaved description";
        _service.SaveRoute(draft,route,"new.json",[dirty]);
        var persisted=_service.GetBots(draft).Single();Assert.Equal("routes/new.json",persisted.Timeline.Single().Action.RoutePath);Assert.NotEqual("unsaved description",persisted.Bot.Description);
        Assert.Equal("routes/new.json",dirty.Timeline.Single().Action.RoutePath);Assert.Equal("unsaved description",dirty.Bot.Description);
    }
    [Fact] public void TemplateRenameWritesPersistedBaselineButKeepsUnrelatedDirtyEditorState()
    {
        var draft=Draft();_service.CreateTemplate(draft,"old.json","{}");var saved=_service.GetProfiles(draft).Single();saved.Profile.ResolutionApply.TemplateFilePath="templates/old.json";_service.SaveProfile(draft,saved);
        var dirty=_service.GetProfiles(draft).Single();dirty.Profile.Name="unsaved profile name";
        _service.SaveTemplate(draft,"old.json","{}","new.json",[dirty]);
        var persisted=_service.GetProfiles(draft).Single();Assert.Equal("templates/new.json",persisted.Profile.ResolutionApply.TemplateFilePath);Assert.NotEqual("unsaved profile name",persisted.Profile.Name);
        Assert.Equal("templates/new.json",dirty.Profile.ResolutionApply.TemplateFilePath);Assert.Equal("unsaved profile name",dirty.Profile.Name);
    }
    [Fact] public void RenameRollsBackAfterOldPathDeleteFails()
    {
        var files=new ThrowOnceFileOperations();var service=new DraftPackService(fileOperations:files);var draft=service.Create(Path.Combine(_temp,"delete-failure"),new ProfilePackManifest{Id="studio.delete-failure",Name="delete",Version="1.0.0",MinimumEngineVersion="0.1.0"});
        var route=service.CreateRoute(draft,"old.json");route.Route.Steps.Add(RouteStep.Fwd(10));route=service.SaveRoute(draft,route);var bot=service.CreateBot(draft,"saved");service.AddBotAction(bot,BotActionType.ReplayRoute).Action.RoutePath="routes/old.json";service.SaveBot(draft,bot);
        byte[] routeBefore=File.ReadAllBytes(route.FilePath);byte[] botBefore=File.ReadAllBytes(bot.FilePath);files.FailDeletePath(route.FilePath);
        Assert.Throws<IOException>(()=>service.SaveRoute(draft,route,"new.json"));Assert.Equal(routeBefore,File.ReadAllBytes(route.FilePath));Assert.Equal(botBefore,File.ReadAllBytes(bot.FilePath));Assert.False(File.Exists(Path.Combine(draft.RootDirectory,"routes","new.json")));AssertNoRenameTemps(draft.RootDirectory);
    }
    [Fact] public void RenameSurfacesRollbackFailuresAlongsideTheOriginalFailure()
    {
        var files=new ThrowOnceFileOperations();var service=new DraftPackService(fileOperations:files);var draft=service.Create(Path.Combine(_temp,"rollback-failure"),new ProfilePackManifest{Id="studio.rollback-failure",Name="rollback",Version="1.0.0",MinimumEngineVersion="0.1.0"});
        var route=service.CreateRoute(draft,"old.json");route.Route.Steps.Add(RouteStep.Fwd(10));route=service.SaveRoute(draft,route);var bot=service.CreateBot(draft,"saved");service.AddBotAction(bot,BotActionType.ReplayRoute).Action.RoutePath="routes/old.json";service.SaveBot(draft,bot);
        files.FailMoveNumber(2,3);
        var error=Assert.Throws<AggregateException>(()=>service.SaveRoute(draft,route,"new.json"));Assert.True(error.InnerExceptions.Count>=2);Assert.Contains(error.InnerExceptions,ex=>ex.Message.Contains("Injected authoring move failure"));
    }
    [Fact] public void RemovedSettingAndVariantMapKeysDoNotReappearOnSave()
    {
        var draft=Draft();var profile=_service.GetProfiles(draft).Single();profile.Profile.Settings.Add(new GpuSuite.Core.Models.GameSetting{Key="quality",Apply=new GpuSuite.Core.Models.SettingApply{ValueMap=new Dictionary<string,string>{{"High","3"},{"Low","1"}}}});profile.Profile.Variants.Add(new GpuSuite.Core.Models.GameVariant{Id="custom",Settings=new Dictionary<string,string>{{"quality","High"},{"rt","On"}}});profile=_service.SaveProfile(draft,profile);
        profile.Profile.Settings[0].Apply.ValueMap.Remove("Low");profile.Profile.Variants[0].Settings.Remove("rt");_service.SaveProfile(draft,profile);var raw=JsonNode.Parse(File.ReadAllText(profile.FilePath))!;
        Assert.Null(raw["settings"]![0]!["apply"]!["valueMap"]!["Low"]);Assert.Null(raw["variants"]![0]!["settings"]!["rt"]);
    }
    [Fact] public void ImportsEnforceSizeLimitOnTheLockedSourceHandle()
    {
        var draft=Draft();string large=Path.Combine(_temp,"large.bin");using(var stream=new FileStream(large,FileMode.CreateNew,FileAccess.Write,FileShare.None))stream.SetLength(16L*1024*1024+1);
        Assert.Throws<InvalidDataException>(()=>_service.ImportAsset(draft,large,"large.bin"));Assert.Throws<InvalidDataException>(()=>_service.ImportTemplate(draft,large,"large.txt"));Assert.False(File.Exists(Path.Combine(draft.RootDirectory,"assets","large.bin")));
    }
    [Fact] public void NestedApplyEditUnknownFieldsFollowSurvivorsAfterRemoval()
    {
        var draft=Draft();var profile=_service.GetProfiles(draft).Single();var setting=new GpuSuite.Core.Models.GameSetting{Key="quality",Apply=new GpuSuite.Core.Models.SettingApply{Method="config-file",Edits=[new GpuSuite.Core.Models.ConfigEdit{Pattern="first",Replacement="1"},new GpuSuite.Core.Models.ConfigEdit{Pattern="second",Replacement="2"}],RegistryEdits=[new GpuSuite.Core.Models.RegistryEdit{ValueName="first"},new GpuSuite.Core.Models.RegistryEdit{ValueName="second"}]}};profile.Profile.Settings.Add(setting);profile.Profile.ResolutionApply.Edits=[new GpuSuite.Core.Models.ConfigEdit{Pattern="r-first"},new GpuSuite.Core.Models.ConfigEdit{Pattern="r-second"}];profile.Profile.ResolutionApply.RegistryEdits=[new GpuSuite.Core.Models.RegistryEdit{ValueName="r-first"},new GpuSuite.Core.Models.RegistryEdit{ValueName="r-second"}];profile=_service.SaveProfile(draft,profile);
        var raw=JsonNode.Parse(File.ReadAllText(profile.FilePath))!;raw["settings"]![0]!["apply"]!["edits"]![0]!["future"]="drop";raw["settings"]![0]!["apply"]!["edits"]![1]!["future"]="setting-survivor";raw["settings"]![0]!["apply"]!["registryEdits"]![1]!["future"]="registry-survivor";raw["resolutionApply"]!["edits"]![1]!["future"]="resolution-survivor";raw["resolutionApply"]!["registryEdits"]![1]!["future"]="resolution-registry-survivor";File.WriteAllText(profile.FilePath,raw.ToJsonString());
        profile=_service.GetProfiles(draft).Single();profile.Profile.Settings[0].Apply.Edits.RemoveAt(0);profile.Profile.Settings[0].Apply.RegistryEdits.RemoveAt(0);profile.Profile.ResolutionApply.Edits.RemoveAt(0);profile.Profile.ResolutionApply.RegistryEdits.RemoveAt(0);_service.SaveProfile(draft,profile);raw=JsonNode.Parse(File.ReadAllText(profile.FilePath))!;
        Assert.Equal("setting-survivor",raw["settings"]![0]!["apply"]!["edits"]![0]!["future"]!.GetValue<string>());Assert.Equal("registry-survivor",raw["settings"]![0]!["apply"]!["registryEdits"]![0]!["future"]!.GetValue<string>());Assert.Equal("resolution-survivor",raw["resolutionApply"]!["edits"]![0]!["future"]!.GetValue<string>());Assert.Equal("resolution-registry-survivor",raw["resolutionApply"]!["registryEdits"]![0]!["future"]!.GetValue<string>());
    }
    private DraftPackProject Draft()=>_service.Create(Path.Combine(_temp,"draft"),new ProfilePackManifest{Id="studio.routes",Name="routes",Version="1.0.0",MinimumEngineVersion="0.1.0"});
    private static void AssertNoRenameTemps(string root) => Assert.Empty(Directory.EnumerateFiles(root,"*.rename-tmp",SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(root,"*.rollback-tmp",SearchOption.AllDirectories)));

    private sealed class ThrowOnceFileOperations : IAuthoringFileOperations
    {
        private readonly HashSet<int> _failedMoveNumbers=[];
        private int _moveNumber;
        private string? _deletePath;
        public void FailMoveNumber(params int[] numbers){foreach(int number in numbers)_failedMoveNumbers.Add(number);}
        public void FailDeletePath(string path)=>_deletePath=Path.GetFullPath(path);
        public bool Exists(string path) => File.Exists(path);
        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
        public void WriteAllBytes(string path, byte[] contents) => File.WriteAllBytes(path,contents);
        public void Move(string source,string destination,bool overwrite)
        {
            _moveNumber++;if(_failedMoveNumbers.Remove(_moveNumber))throw new IOException("Injected authoring move failure.");
            File.Move(source,destination,overwrite);
        }
        public void Delete(string path){if(_deletePath is not null&&Path.GetFullPath(path).Equals(_deletePath,StringComparison.OrdinalIgnoreCase)){_deletePath=null;throw new IOException("Injected authoring delete failure.");}File.Delete(path);}
    }
}
