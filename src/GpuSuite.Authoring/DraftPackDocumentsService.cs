using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GpuSuite.Core.Io;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Automation;
using GpuSuite.Engine.Profiles;

namespace GpuSuite.Authoring;

public sealed partial class DraftPackService
{
    private static readonly JsonSerializerOptions FormJsonOptions = new(Json.Options) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    public IReadOnlyList<DraftProfileDocument> GetProfiles(DraftPackProject project)
    {
        string root = RequireDraft(project);
        return SafeFiles(root, "profiles", recursive: false).Where(IsJson).Select(file => LoadProfile(root, file)).ToArray();
    }

    public IReadOnlyList<DraftBotDocument> GetBots(DraftPackProject project)
    {
        string root = RequireDraft(project);
        return SafeFiles(root, "bots", recursive: false).Where(IsJson).Select(file => LoadBot(root, file)).ToArray();
    }

    public DraftProfileDocument CreateProfile(DraftPackProject project, string id, string? name = null)
    {
        string root = RequireDraft(project);
        EnsureAvailableId(root, "profiles", id, isBot: false);
        var profile = new GameProfile { Id = id, Name = string.IsNullOrWhiteSpace(name) ? id : name, Enabled = false, Launch = new LaunchSpec { Store = "Manual" } };
        var document = NewProfile(root, Path.Combine(root, "profiles", id + ".json"), profile);
        AtomicCreate(document.FilePath, document.Raw.ToJsonString(Json.Options));
        SaveProfile(project, document);
        return document;
    }

    public DraftProfileDocument DuplicateProfile(DraftPackProject project, DraftProfileDocument source, string newId, string? name = null)
    {
        string root = RequireDraft(project);
        EnsureDocumentUnderRoot(root, source.FilePath, "profiles");
        EnsureAvailableId(root, "profiles", newId, isBot: false);
        var raw = CaptureProfileObject(source);
        raw["id"] = newId;
        if (!string.IsNullOrWhiteSpace(name)) raw["name"] = name;
        return SaveNewProfile(project, Path.Combine(root, "profiles", newId + ".json"), raw);
    }

    public DraftProfileDocument SaveProfile(DraftPackProject project, DraftProfileDocument document, string? previousId = null)
    {
        string root = RequireDraft(project);
        EnsureDocumentUnderRoot(root, document.FilePath, "profiles");
        EnsureContentId(document.Profile.Id);
        string target = Path.Combine(root, "profiles", document.Profile.Id + ".json");
        if (!Path.GetFullPath(target).Equals(Path.GetFullPath(document.FilePath), StringComparison.OrdinalIgnoreCase)) EnsureFileAvailable(target, document.FilePath);
        MergeProfile(document);
        WriteDocument(document.FilePath, target, document.Raw.ToJsonString(Json.Options));
        SynchronizeGames(root, project.Manifest);
        AtomicWriteJson(Path.Combine(root, ProfilePackManager.ManifestFileName), project.Manifest);
        return LoadProfile(root, target);
    }

    public void DeleteProfile(DraftPackProject project, DraftProfileDocument document)
    {
        string root = RequireDraft(project);
        EnsureDocumentUnderRoot(root, document.FilePath, "profiles");
        File.Delete(document.FilePath);
        SynchronizeGames(root, project.Manifest);
        AtomicWriteJson(Path.Combine(root, ProfilePackManager.ManifestFileName), project.Manifest);
    }

    public DraftBotDocument CreateBot(DraftPackProject project, string id, string? description = null)
    {
        string root = RequireDraft(project);
        EnsureAvailableId(root, "bots", id, isBot: true);
        var bot = new BotScript { Id = id, Description = description ?? "", Loop = false };
        var document = NewBot(root, Path.Combine(root, "bots", id + ".json"), bot);
        AtomicCreate(document.FilePath, document.Raw.ToJsonString(Json.Options));
        SaveBot(project, document);
        return document;
    }

    public DraftBotDocument DuplicateBot(DraftPackProject project, DraftBotDocument source, string newId, string? description = null)
    {
        string root = RequireDraft(project);
        EnsureDocumentUnderRoot(root, source.FilePath, "bots");
        EnsureAvailableId(root, "bots", newId, isBot: true);
        var raw = CaptureBotObject(source);
        raw["id"] = newId;
        if (description is not null) raw["description"] = description;
        return SaveNewBot(project, Path.Combine(root, "bots", newId + ".json"), raw);
    }

    public DraftBotDocument SaveBot(DraftPackProject project, DraftBotDocument document, string? previousId = null)
    {
        string root = RequireDraft(project);
        EnsureDocumentUnderRoot(root, document.FilePath, "bots");
        string rawPreviousId = previousId ?? document.Raw["id"]?.GetValue<string>() ?? document.Bot.Id;
        EnsureContentId(document.Bot.Id);
        string target = Path.Combine(root, "bots", document.Bot.Id + ".json");
        if (!Path.GetFullPath(target).Equals(Path.GetFullPath(document.FilePath), StringComparison.OrdinalIgnoreCase)) EnsureFileAvailable(target, document.FilePath);
        document.Bot.Actions = document.Timeline.Select(x => x.Action).ToList();
        MergeTypedIntoRaw(document.Raw, JsonObjectFrom(document.Bot), "actions", "graph");
        document.Raw["actions"] = new JsonArray(document.Timeline.Select(action => MergeAction(action)).ToArray());
        MergeGraph(document);
        WriteDocument(document.FilePath, target, document.Raw.ToJsonString(Json.Options));
        if (!rawPreviousId.Equals(document.Bot.Id, StringComparison.OrdinalIgnoreCase)) RewriteBotReferences(project, rawPreviousId, document.Bot.Id);
        return LoadBot(root, target);
    }

    public DraftDeleteGuard CanDeleteBot(DraftPackProject project, DraftBotDocument document, IEnumerable<GameProfile>? inMemoryProfiles = null)
    {
        string root = RequireDraft(project);
        EnsureDocumentUnderRoot(root, document.FilePath, "bots");
        string savedId = SavedBotId(document);
        var references = new List<string>();
        foreach (var profile in GetProfiles(project).Select(x => x.Profile).Concat(inMemoryProfiles ?? []))
        {
            if (ReferencedBots(profile).Any(id => id.Equals(savedId, StringComparison.OrdinalIgnoreCase))) references.Add(profile.Id);
        }
        return new DraftDeleteGuard(references.Count == 0, references.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public void DeleteBot(DraftPackProject project, DraftBotDocument document, bool force = false, IEnumerable<GameProfile>? inMemoryProfiles = null)
    {
        var guard = CanDeleteBot(project, document, inMemoryProfiles);
        string savedId = SavedBotId(document);
        if (!guard.CanDelete && !force) throw new InvalidOperationException($"Bot '{savedId}' is referenced by: {string.Join(", ", guard.References)}.");
        EnsureDocumentUnderRoot(RequireDraft(project), document.FilePath, "bots");
        File.Delete(document.FilePath);
    }

    public bool HasUnsavedChanges(DraftProfileDocument document)
        => !JsonNode.DeepEquals(document.BaselineTyped, JsonObjectFrom(document.Profile));

    public bool HasUnsavedChanges(DraftBotDocument document)
    {
        var current = JsonObjectFrom(document.Bot);
        current["actions"] = new JsonArray(document.Timeline.Select(item => (JsonNode)JsonObjectFrom(item.Action)).ToArray());
        return !JsonNode.DeepEquals(document.BaselineTyped, current);
    }

    public string CaptureProfileState(DraftProfileDocument document) => CaptureProfileObject(document).ToJsonString(Json.Options);
    public DraftProfileDocument RestoreProfileState(DraftProfileDocument document, string state)
    {
        var raw = JsonNode.Parse(state) as JsonObject ?? throw new InvalidDataException("Profile history state is invalid.");
        var profile = raw.Deserialize<GameProfile>(Json.Options) ?? throw new InvalidDataException("Profile history state is invalid.");
        return CreateProfileDocument(Path.GetDirectoryName(Path.GetDirectoryName(document.FilePath))!, document.FilePath, raw, profile, document.BaselineTyped);
    }
    public string CaptureBotState(DraftBotDocument document) => CaptureBotObject(document).ToJsonString(Json.Options);
    public DraftBotDocument RestoreBotState(DraftBotDocument document, string state)
    {
        var raw = JsonNode.Parse(state) as JsonObject ?? throw new InvalidDataException("Bot history state is invalid.");
        return CreateBotDocument(Path.GetDirectoryName(Path.GetDirectoryName(document.FilePath))!, document.FilePath, raw, document.BaselineTyped);
    }

    public void MoveBotAction(DraftBotDocument document, int index, int direction)
    {
        int target = index + direction;
        if (index < 0 || index >= document.Timeline.Count || target < 0 || target >= document.Timeline.Count) return;
        (document.Timeline[index], document.Timeline[target]) = (document.Timeline[target], document.Timeline[index]);
    }

    public DraftBotActionDocument AddBotAction(DraftBotDocument document, BotActionType type = BotActionType.Wait)
    {
        var action = new BotAction { Type = type, DurationMs = type == BotActionType.Wait ? 1000 : 0 };
        var item = new DraftBotActionDocument { Action = action, Raw = JsonObjectFrom(action) };
        document.Timeline.Add(item);
        return item;
    }

    public void RemoveBotAction(DraftBotDocument document, DraftBotActionDocument action) => document.Timeline.Remove(action);

    private static DraftProfileDocument LoadProfile(string root, string file)
    {
        var raw = LoadObject(file);
        var profile = raw.Deserialize<GameProfile>(Json.Options) ?? throw new InvalidDataException("Profile cannot be parsed: " + file);
        return CreateProfileDocument(root, file, raw, profile);
    }
    private static DraftBotDocument LoadBot(string root, string file)
    {
        var raw = LoadObject(file);
        var bot = raw.Deserialize<BotScript>(Json.Options) ?? throw new InvalidDataException("Bot cannot be parsed: " + file);
        var timeline = new List<DraftBotActionDocument>();
        if (raw["actions"] is JsonArray rawActions)
        {
            foreach (var rawAction in rawActions.OfType<JsonObject>())
            {
                var action = rawAction.Deserialize<BotAction>(Json.Options) ?? throw new InvalidDataException("Bot action cannot be parsed: " + file);
                timeline.Add(new DraftBotActionDocument { Raw = rawAction, Action = action });
            }
        }
        var document = new DraftBotDocument { FilePath = file, Raw = raw, Bot = bot, Timeline = timeline, BaselineTyped = JsonObjectFrom(bot), RelativePath = Relative(root, file) };
        TrackGraphRaw(document); return document;
    }
    private static DraftProfileDocument NewProfile(string root, string path, GameProfile profile)
        => CreateProfileDocument(root, path, JsonObjectFrom(profile), profile);
    private static DraftBotDocument NewBot(string root, string path, BotScript bot)
        => new() { FilePath = path, Raw = JsonObjectFrom(bot), Bot = bot, Timeline = [], BaselineTyped = JsonObjectFrom(bot), RelativePath = Relative(root, path) };
    private DraftProfileDocument SaveNewProfile(DraftPackProject project, string path, JsonObject raw)
    {
        var profile = raw.Deserialize<GameProfile>(Json.Options)!;
        var doc = CreateProfileDocument(project.RootDirectory, path, raw, profile);
        AtomicCreate(path, raw.ToJsonString(Json.Options));
        return SaveProfile(project, doc);
    }
    private DraftBotDocument SaveNewBot(DraftPackProject project, string path, JsonObject raw)
    {
        var bot = raw.Deserialize<BotScript>(Json.Options)!;
        var doc = new DraftBotDocument { FilePath = path, Raw = raw, Bot = bot, Timeline = [], BaselineTyped = JsonObjectFrom(bot), RelativePath = Relative(project.RootDirectory, path) };
        if (raw["actions"] is JsonArray actions) foreach (var node in actions.OfType<JsonObject>()) doc.Timeline.Add(new DraftBotActionDocument { Raw = node, Action = node.Deserialize<BotAction>(Json.Options)! });
        AtomicCreate(path, raw.ToJsonString(Json.Options));
        TrackGraphRaw(doc);
        return SaveBot(project, doc);
    }
    private static JsonObject LoadObject(string file) => JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new InvalidDataException("Expected a JSON object: " + file);
    private static JsonObject JsonObjectFrom<T>(T value) => JsonNode.Parse(JsonSerializer.Serialize(value, FormJsonOptions)) as JsonObject ?? throw new InvalidOperationException("Could not serialize JSON object.");
    private static DraftProfileDocument CreateProfileDocument(string root, string file, JsonObject raw, GameProfile profile, JsonObject? baseline = null)
    {
        var document = new DraftProfileDocument { FilePath = file, Raw = raw, Profile = profile, BaselineTyped = baseline?.DeepClone().AsObject() ?? JsonObjectFrom(profile), RelativePath = Relative(root, file) };
        TrackRaw(profile.Scenes, raw["scenes"] as JsonArray, document.SceneRaw);
        TrackRaw(profile.Settings, raw["settings"] as JsonArray, document.SettingRaw);
        TrackRaw(profile.Variants, raw["variants"] as JsonArray, document.VariantRaw);
        TrackRaw(profile.ResolutionApply.Edits, raw["resolutionApply"]?["edits"] as JsonArray, document.ConfigEditRaw);
        TrackRaw(profile.ResolutionApply.RegistryEdits, raw["resolutionApply"]?["registryEdits"] as JsonArray, document.RegistryEditRaw);
        if(raw["settings"] is JsonArray rawSettings)
            for(int i=0;i<profile.Settings.Count&&i<rawSettings.Count;i++)
                if(rawSettings[i] is JsonObject rawSetting)
                {
                    TrackRaw(profile.Settings[i].Apply.Edits,rawSetting["apply"]?["edits"] as JsonArray,document.ConfigEditRaw);
                    TrackRaw(profile.Settings[i].Apply.RegistryEdits,rawSetting["apply"]?["registryEdits"] as JsonArray,document.RegistryEditRaw);
                }
        return document;
    }
    private static void TrackRaw<T>(IReadOnlyList<T> typed, JsonArray? raw, Dictionary<T, JsonObject> tracking) where T : class
    {
        if (raw is null) return;
        for (int index = 0; index < typed.Count && index < raw.Count; index++)
            if (raw[index] is JsonObject item) tracking[typed[index]] = item;
    }
    private static void MergeProfile(DraftProfileDocument document)
    {
        var typed = JsonObjectFrom(document.Profile);
        MergeTypedIntoRaw(document.Raw, typed, "scenes", "settings", "variants", "resolutionApply");
        document.Raw["scenes"] = MergeTrackedArray(document.Profile.Scenes, typed["scenes"] as JsonArray, document.SceneRaw);
        document.Raw["settings"] = MergeSettings(document,typed["settings"] as JsonArray,capture:false);
        document.Raw["variants"] = MergeTrackedArray(document.Profile.Variants, typed["variants"] as JsonArray, document.VariantRaw);
        document.Raw["resolutionApply"] = MergeResolutionApply(document,typed["resolutionApply"] as JsonObject,capture:false);
    }
    private static JsonObject CaptureProfileObject(DraftProfileDocument document)
    {
        var raw = document.Raw.DeepClone().AsObject(); var typed = JsonObjectFrom(document.Profile);
        MergeTypedIntoRaw(raw, typed, "scenes", "settings", "variants", "resolutionApply");
        raw["scenes"] = CaptureTrackedArray(document.Profile.Scenes, typed["scenes"] as JsonArray, document.SceneRaw);
        raw["settings"] = MergeSettings(document,typed["settings"] as JsonArray,capture:true);
        raw["variants"] = CaptureTrackedArray(document.Profile.Variants, typed["variants"] as JsonArray, document.VariantRaw);
        raw["resolutionApply"] = MergeResolutionApply(document,typed["resolutionApply"] as JsonObject,capture:true);
        return raw;
    }
    private static JsonArray MergeSettings(DraftProfileDocument document,JsonArray? typed,bool capture)
    {
        var result=new JsonArray();if(typed is null)return result;
        for(int i=0;i<document.Profile.Settings.Count;i++)
        {
            var setting=document.Profile.Settings[i];var typedSetting=typed[i] as JsonObject??new JsonObject();var rawSetting=document.SettingRaw.TryGetValue(setting,out var tracked)?(capture?tracked.DeepClone().AsObject():tracked):new JsonObject();
            MergeTypedIntoRaw(rawSetting,typedSetting,"apply");var typedApply=typedSetting["apply"] as JsonObject??new JsonObject();var rawApply=rawSetting["apply"] as JsonObject??new JsonObject();MergeTypedIntoRaw(rawApply,typedApply,"edits","registryEdits");
            rawApply["edits"]=capture?CaptureTrackedArray(setting.Apply.Edits,typedApply["edits"] as JsonArray,document.ConfigEditRaw):MergeTrackedArray(setting.Apply.Edits,typedApply["edits"] as JsonArray,document.ConfigEditRaw);
            rawApply["registryEdits"]=capture?CaptureTrackedArray(setting.Apply.RegistryEdits,typedApply["registryEdits"] as JsonArray,document.RegistryEditRaw):MergeTrackedArray(setting.Apply.RegistryEdits,typedApply["registryEdits"] as JsonArray,document.RegistryEditRaw);
            rawSetting["apply"]=rawApply;result.Add(capture?rawSetting:rawSetting.DeepClone());
        }
        return result;
    }
    private static JsonObject MergeResolutionApply(DraftProfileDocument document,JsonObject? typed,bool capture)
    {
        typed??=new JsonObject();var raw=document.Raw["resolutionApply"] is JsonObject existing?(capture?existing.DeepClone().AsObject():existing):new JsonObject();MergeTypedIntoRaw(raw,typed,"edits","registryEdits");
        raw["edits"]=capture?CaptureTrackedArray(document.Profile.ResolutionApply.Edits,typed["edits"] as JsonArray,document.ConfigEditRaw):MergeTrackedArray(document.Profile.ResolutionApply.Edits,typed["edits"] as JsonArray,document.ConfigEditRaw);
        raw["registryEdits"]=capture?CaptureTrackedArray(document.Profile.ResolutionApply.RegistryEdits,typed["registryEdits"] as JsonArray,document.RegistryEditRaw):MergeTrackedArray(document.Profile.ResolutionApply.RegistryEdits,typed["registryEdits"] as JsonArray,document.RegistryEditRaw);return capture?raw:raw.DeepClone().AsObject();
    }
    private static JsonArray CaptureTrackedArray<T>(IReadOnlyList<T> items, JsonArray? typed, Dictionary<T, JsonObject> tracking) where T : class
    {
        var result = new JsonArray();
        for (int index=0;index<items.Count;index++)
        {
            var typedObject = typed is not null && index<typed.Count ? typed[index] as JsonObject : null;
            if (typedObject is null) { result.Add(typed?[index]?.DeepClone()); continue; }
            var raw = tracking.TryGetValue(items[index], out var tracked) ? tracked.DeepClone().AsObject() : new JsonObject();
            MergeTypedIntoRaw(raw, typedObject); result.Add(raw);
        }
        return result;
    }
    private static JsonArray MergeTrackedArray<T>(IReadOnlyList<T> items, JsonArray? typed, Dictionary<T, JsonObject> tracking) where T : class
    {
        var result = new JsonArray();
        for (int index = 0; index < items.Count; index++)
        {
            JsonNode? serialized = typed is not null && index < typed.Count ? typed[index] : null;
            if (serialized is JsonObject typedObject && tracking.TryGetValue(items[index], out var raw))
            {
                MergeTypedIntoRaw(raw, typedObject);
                result.Add(raw.DeepClone());
            }
            else result.Add(serialized?.DeepClone());
        }
        return result;
    }
    private static JsonNode MergeAction(DraftBotActionDocument item)
    {
        MergeTypedIntoRaw(item.Raw, JsonObjectFrom(item.Action));
        return item.Raw.DeepClone();
    }
    private static JsonObject CaptureBotObject(DraftBotDocument document)
    {
        var raw = document.Raw.DeepClone().AsObject(); var typed = JsonObjectFrom(document.Bot);
        MergeTypedIntoRaw(raw, typed, "actions", "graph");
        var actions = new JsonArray();
        foreach (var item in document.Timeline)
        {
            var actionRaw = item.Raw.DeepClone().AsObject(); MergeTypedIntoRaw(actionRaw, JsonObjectFrom(item.Action)); actions.Add(actionRaw);
        }
        raw["actions"] = actions;
        if (document.Bot.Graph is null) raw["graph"] = null;
        else
        {
            var graphTyped=JsonObjectFrom(document.Bot.Graph); var graphRaw=(document.Raw["graph"] as JsonObject)?.DeepClone().AsObject() ?? new JsonObject();
            MergeTypedIntoRaw(graphRaw,graphTyped,"screens"); var screens=new JsonArray();
            foreach(var screen in document.Bot.Graph.Screens)
            {
                var typedScreen=JsonObjectFrom(screen); var rawScreen=document.ScreenRaw.TryGetValue(screen,out var oldScreen)?oldScreen.DeepClone().AsObject():new JsonObject();
                MergeTypedIntoRaw(rawScreen,typedScreen,"do"); var steps=new JsonArray();
                foreach(var step in screen.Do) { var stepRaw=document.NavStepRaw.TryGetValue(step,out var oldStep)?oldStep.DeepClone().AsObject():new JsonObject(); MergeTypedIntoRaw(stepRaw,JsonObjectFrom(step)); steps.Add(stepRaw); }
                rawScreen["do"]=steps; screens.Add(rawScreen);
            }
            graphRaw["screens"]=screens; raw["graph"]=graphRaw;
        }
        return raw;
    }
    private static DraftBotDocument CreateBotDocument(string root, string file, JsonObject raw, JsonObject? baseline = null)
    {
        var bot = raw.Deserialize<BotScript>(Json.Options) ?? throw new InvalidDataException("Bot history state is invalid."); var timeline = new List<DraftBotActionDocument>();
        if(raw["actions"] is JsonArray actions) foreach(var node in actions.OfType<JsonObject>()) timeline.Add(new DraftBotActionDocument{Raw=node,Action=node.Deserialize<BotAction>(Json.Options)!});
        var document=new DraftBotDocument{FilePath=file,Raw=raw,Bot=bot,Timeline=timeline,BaselineTyped=baseline?.DeepClone().AsObject()??JsonObjectFrom(bot),RelativePath=Relative(root,file)}; TrackGraphRaw(document); return document;
    }
    private static void TrackGraphRaw(DraftBotDocument document)
    {
        if (document.Bot.Graph is null || document.Raw["graph"] is not JsonObject graph || graph["screens"] is not JsonArray screens) return;
        for (int i=0; i<document.Bot.Graph.Screens.Count && i<screens.Count; i++)
        {
            if (screens[i] is not JsonObject rawScreen) continue;
            var screen=document.Bot.Graph.Screens[i]; document.ScreenRaw[screen]=rawScreen;
            if (rawScreen["do"] is JsonArray steps) for(int j=0;j<screen.Do.Count&&j<steps.Count;j++) if(steps[j] is JsonObject rawStep) document.NavStepRaw[screen.Do[j]]=rawStep;
        }
    }
    private static void MergeGraph(DraftBotDocument document)
    {
        if (document.Bot.Graph is null) { if (document.GraphCleared) document.Raw["graph"] = null; return; }
        var typed=JsonObjectFrom(document.Bot.Graph);
        var raw=document.Raw["graph"] as JsonObject ?? new JsonObject();
        MergeTypedIntoRaw(raw, typed, "screens");
        var result=new JsonArray();
        foreach(var screen in document.Bot.Graph.Screens)
        {
            var typedScreen=JsonObjectFrom(screen); var rawScreen=document.ScreenRaw.TryGetValue(screen,out var found)?found:new JsonObject();
            MergeTypedIntoRaw(rawScreen,typedScreen,"do"); var steps=new JsonArray();
            foreach(var step in screen.Do) { var typedStep=JsonObjectFrom(step); var rawStep=document.NavStepRaw.TryGetValue(step,out var old)?old:new JsonObject(); MergeTypedIntoRaw(rawStep,typedStep); steps.Add(rawStep.DeepClone()); }
            rawScreen["do"]=steps; result.Add(rawScreen.DeepClone());
        }
        raw["screens"]=result; document.Raw["graph"]=raw;
    }
    private static void MergeTypedIntoRaw(JsonObject raw, JsonObject typed, params string[] preserveRawKeys)
    {
        foreach (var pair in typed)
        {
            if (preserveRawKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)) continue;
            if (pair.Value is JsonObject typedChild && raw[pair.Key] is JsonObject rawChild)
            {
                // These objects are user-authored dictionaries, not extensible model records. A merge would
                // resurrect keys that the visual grid removed or renamed.
                if (pair.Key.Equals("valueMap", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("settings", StringComparison.OrdinalIgnoreCase)) raw[pair.Key] = typedChild.DeepClone();
                else MergeTypedIntoRaw(rawChild, typedChild);
            }
            else if (pair.Value is JsonArray typedArray && raw[pair.Key] is JsonArray rawArray)
                raw[pair.Key] = pair.Key.Equals("scenes", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("settings", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("variants", StringComparison.OrdinalIgnoreCase)
                    ? MergeKeyedArray(rawArray, typedArray)
                    : MergePositionalArray(rawArray, typedArray);
            else raw[pair.Key] = pair.Value?.DeepClone();
        }
    }
    private static JsonArray MergePositionalArray(JsonArray previous, JsonArray updated)
    {
        var result = new JsonArray();
        for (int index = 0; index < updated.Count; index++)
        {
            JsonNode? typed = updated[index];
            JsonNode? raw = index < previous.Count ? previous[index] : null;
            if (typed is JsonObject typedObject && raw is JsonObject rawObject)
            {
                MergeTypedIntoRaw(rawObject, typedObject);
                result.Add(rawObject.DeepClone());
            }
            else if (typed is JsonArray typedArray && raw is JsonArray rawArray) result.Add(MergePositionalArray(rawArray, typedArray));
            else result.Add(typed?.DeepClone());
        }
        return result;
    }
    private static JsonArray MergeKeyedArray(JsonArray previous, JsonArray updated)
    {
        var previousItems = previous.ToArray();
        var old = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in previousItems.OfType<JsonObject>())
        {
            string key = candidate["id"]?.GetValue<string>() ?? candidate["key"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrWhiteSpace(key)) old.TryAdd(key, candidate);
        }
        var used = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
        var result = new JsonArray();
        for (int index = 0; index < updated.Count; index++)
        {
            var item = updated[index];
            if (item is not JsonObject typed) { result.Add(item?.DeepClone()); continue; }
            string id = typed["id"]?.GetValue<string>() ?? typed["key"]?.GetValue<string>() ?? "";
            JsonObject? raw = null;
            if (!string.IsNullOrWhiteSpace(id) && old.TryGetValue(id, out var keyed) && !used.Contains(keyed)) raw = keyed;
            // The visual editor mutates keyed rows in place and only appends new rows. Falling back
            // to the same unused position keeps unknown fields attached when a row's id/key changes.
            else if (index < previousItems.Length && previousItems[index] is JsonObject positional && !used.Contains(positional)) raw = positional;
            if (raw is not null) { used.Add(raw); MergeTypedIntoRaw(raw, typed); result.Add(raw.DeepClone()); }
            else result.Add(typed.DeepClone());
        }
        return result;
    }
    private void RewriteBotReferences(DraftPackProject project, string oldId, string newId)
    {
        foreach (var document in GetProfiles(project))
        {
            bool changed = false;
            if (Matches(document.Profile.SettingsBotScript, oldId)) { document.Profile.SettingsBotScript = newId; changed = true; }
            foreach (var scene in document.Profile.Scenes)
            {
                if (Matches(scene.BotScript, oldId)) { scene.BotScript = newId; changed = true; }
                if (Matches(scene.StartBotScript, oldId)) { scene.StartBotScript = newId; changed = true; }
                if (Matches(scene.ReRunBotScript, oldId)) { scene.ReRunBotScript = newId; changed = true; }
                if (Matches(scene.Warmup?.Script, oldId)) { scene.Warmup!.Script = newId; changed = true; }
            }
            if (changed) SaveProfile(project, document);
        }
    }
    private static bool Matches(string? value, string id) => value?.Equals(id, StringComparison.OrdinalIgnoreCase) == true;
    private static string SavedBotId(DraftBotDocument document)
        => document.Raw["id"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(document.FilePath);
    private void EnsureAvailableId(string root, string folder, string id, bool isBot)
    {
        EnsureContentId(id);
        string target = Path.Combine(root, folder, id + ".json");
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("A draft file already uses this id: " + target);
        if (SafeFiles(root, folder, recursive: false).Where(IsJson).Any(file => (isBot ? LoadBot(root, file).Bot.Id : LoadProfile(root, file).Profile.Id).Equals(id, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException($"The id '{id}' is already used.");
    }
    private static void EnsureContentId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !ContentIdPattern.IsMatch(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Id may contain only letters, numbers, dots, underscores and hyphens (maximum 128 characters).");
    }
    private static void WriteDocument(string current, string target, string content)
    {
        if (Path.GetFullPath(current).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            AtomicWrite(current, content);
            return;
        }
        AtomicCreate(target, content);
        try
        {
            EnsureNoReparseInExistingAncestors(current);
            File.Delete(current);
        }
        catch
        {
            try { File.Delete(target); } catch { }
            throw;
        }
    }
    private static void AtomicCreate(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        EnsureNoReparseInExistingAncestors(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content);
            EnsureNoReparseInExistingAncestors(path);
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void EnsureFileAvailable(string target, string current) { if (File.Exists(target) && !Path.GetFullPath(target).Equals(Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase)) throw new IOException("A draft file already uses this id: " + target); }
    private static void EnsureDocumentUnderRoot(string root, string file, string category)
    {
        string expected = Path.Combine(root, category);
        if (!IsWithin(file, expected) || !File.Exists(file)) throw new InvalidOperationException("Document is not a current draft " + category + " file.");
        EnsureNoReparseInExistingAncestors(file);
        EnsureNoReparse(file);
    }
}
