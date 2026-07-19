using System.Text;
using System.Text.Json.Nodes;
using GpuSuite.Core.Io;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Automation;

namespace GpuSuite.Authoring;

public sealed class DraftRouteDocument
{
    public required string FilePath { get; init; }
    public required JsonObject Raw { get; init; }
    public required RecordedRoute Route { get; init; }
    public string RelativePath { get; init; } = "";
    internal string RootDirectory { get; init; } = "";
    internal JsonObject BaselineTyped { get; init; } = new();
    internal Dictionary<RouteStep, JsonObject> StepRaw { get; } = new(ReferenceEqualityComparer.Instance);
}
public sealed record DraftFileItem(string RelativePath, long Bytes, bool IsText);

public sealed partial class DraftPackService
{
    private const long MaxAuthoringFileBytes = 16L * 1024 * 1024;
    public IReadOnlyList<DraftRouteDocument> GetRoutes(DraftPackProject project)
    {
        string root = RequireDraft(project);
        return SafeFiles(root, "routes", true).Where(IsJson).Select(file => LoadRoute(root, file)).ToArray();
    }
    public DraftRouteDocument CreateRoute(DraftPackProject project, string relativePath)
    {
        string path = ResolveAuthorFile(project, "routes", relativePath, ".json");
        EnsureNewFile(path);
        var route = new RecordedRoute();
        var document = CreateRouteDocument(project.RootDirectory, path, JsonNode.Parse(route.ToJson())!.AsObject(), route);
        AtomicCreate(path, document.Raw.ToJsonString(Json.Options)); return document;
    }
    public DraftRouteDocument DuplicateRoute(DraftPackProject project, DraftRouteDocument source, string relativePath)
    {
        EnsureDocumentUnderRoot(RequireDraft(project), source.FilePath, "routes");
        string target = ResolveAuthorFile(project, "routes", relativePath, ".json"); EnsureNewFile(target);
        var raw = CaptureRouteObject(source);
        AtomicCreate(target, raw.ToJsonString(Json.Options)); return LoadRoute(project.RootDirectory, target);
    }
    public DraftRouteDocument SaveRoute(DraftPackProject project, DraftRouteDocument document, string? renamedRelativePath = null, IEnumerable<DraftBotDocument>? inMemoryBots = null)
    {
        string root = RequireDraft(project); EnsureDocumentUnderRoot(root, document.FilePath, "routes");
        string target = renamedRelativePath is null ? document.FilePath : ResolveAuthorFile(project, "routes", renamedRelativePath, ".json");
        if (!target.Equals(document.FilePath, StringComparison.OrdinalIgnoreCase)) EnsureNewFile(target);
        var raw = CaptureRouteObject(document);
        string oldReference = Relative(root, document.FilePath); string newReference = Relative(root, target);
        if (oldReference.Equals(newReference, StringComparison.OrdinalIgnoreCase))
        {
            WriteDocument(document.FilePath, target, raw.ToJsonString(Json.Options));
            return LoadRoute(root, target);
        }

        // Renames alter more than one file.  Prepare every dependent document first, then make the
        // disk change as one rollback-capable transaction.  In-memory dirty documents are changed only
        // after disk commit, so a failed rename cannot disturb their selection, dirty state, or undo stack.
        var loaded = GetBots(project);
        var inMemory = (inMemoryBots ?? []).ToArray();
        var writes = new List<AuthoringWrite> { new(target, Encoding.UTF8.GetBytes(raw.ToJsonString(Json.Options))) };
        // Always rewrite the independently loaded persisted document.  An in-memory document can
        // contain unrelated unsaved changes, but the on-disk baseline must still receive the rename.
        foreach (var bot in loaded)
        {
            if (!RewriteRouteReferences(bot, oldReference, newReference)) continue;
            writes.Add(new(bot.FilePath, Encoding.UTF8.GetBytes(CaptureBotState(bot))));
        }
        CommitRenameTransaction(document.FilePath, target, writes);
        foreach (var bot in inMemory) RewriteRouteReferences(bot, oldReference, newReference);
        return LoadRoute(root, target);
    }
    public DraftDeleteGuard CanDeleteRoute(DraftPackProject project, DraftRouteDocument document, IEnumerable<DraftBotDocument>? inMemoryBots = null)
    {
        string root = RequireDraft(project); EnsureDocumentUnderRoot(root, document.FilePath, "routes"); string reference = Relative(root, document.FilePath);
        var uses = GetBots(project).Concat(inMemoryBots ?? []).Where(b => b.Timeline.Any(a => EqualsRoute(a.Action.RoutePath, reference) || EqualsRoute(a.Action.RecordRoutePath, reference))).Select(b => b.Bot.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new DraftDeleteGuard(uses.Length == 0, uses);
    }
    public void DeleteRoute(DraftPackProject project, DraftRouteDocument document, bool force = false, IEnumerable<DraftBotDocument>? inMemoryBots = null)
    {
        var guard = CanDeleteRoute(project, document, inMemoryBots); if (!guard.CanDelete && !force) throw new InvalidOperationException("Route is referenced by: " + string.Join(", ", guard.References)); EnsureNoReparseInExistingAncestors(document.FilePath); File.Delete(document.FilePath);
    }
    public DraftRouteDocument ImportRoute(DraftPackProject project, string sourceFile, string relativePath)
    {
        using var source=OpenExternalSource(sourceFile); string target = ResolveAuthorFile(project, "routes", relativePath, ".json"); EnsureNewFile(target);
        using var reader=new StreamReader(source,Encoding.UTF8,detectEncodingFromByteOrderMarks:true);string content = reader.ReadToEnd(); var route = RecordedRoute.FromJson(content) ?? throw new InvalidDataException("Imported route is not valid JSON.");
        var raw = JsonNode.Parse(content) as JsonObject ?? throw new InvalidDataException("Imported route must be a JSON object.");
        AtomicCreate(target, raw.ToJsonString(Json.Options));
        return CreateRouteDocument(project.RootDirectory, target, raw, route);
    }
    public bool HasUnsavedChanges(DraftRouteDocument document) => !JsonNode.DeepEquals(document.BaselineTyped, JsonNode.Parse(document.Route.ToJson()));
    public string CaptureRouteState(DraftRouteDocument document) => CaptureRouteObject(document).ToJsonString(Json.Options);
    public DraftRouteDocument RestoreRouteState(DraftRouteDocument document, string state)
    {
        var raw = JsonNode.Parse(state) as JsonObject ?? throw new InvalidDataException("Route history state is invalid.");
        var route = RecordedRoute.FromJson(raw.ToJsonString()) ?? throw new InvalidDataException("Route history state is invalid.");
        return CreateRouteDocument(document.RootDirectory, document.FilePath, raw, route, document.BaselineTyped);
    }
    public IReadOnlyList<DraftFileItem> GetTemplates(DraftPackProject project) => GetDraftFiles(project, "templates", true);
    public IReadOnlyList<DraftFileItem> GetAssets(DraftPackProject project) => GetDraftFiles(project, "assets", false);
    public string ReadTemplate(DraftPackProject project, string relativePath) { string p = ResolveAuthorFile(project, "templates", relativePath, null); EnsureDocumentUnderRoot(RequireDraft(project), p, "templates"); return File.ReadAllText(p); }
    public DraftFileItem CreateTemplate(DraftPackProject project, string relativePath, string content = "{}") { string p = ResolveAuthorFile(project, "templates", relativePath, null); EnsureNewFile(p); AtomicCreate(p, content); return DescribeFile(project.RootDirectory,p,true); }
    public DraftFileItem ImportTemplate(DraftPackProject project, string sourceFile, string relativePath)
    {
        using var source=OpenExternalSource(sourceFile);using var reader=new StreamReader(source,Encoding.UTF8,detectEncodingFromByteOrderMarks:true);string content = reader.ReadToEnd();
        string p = ResolveAuthorFile(project, "templates", relativePath, null); EnsureNewFile(p); AtomicCreate(p, content); return DescribeFile(project.RootDirectory, p, true);
    }
    public void SaveTemplate(DraftPackProject project, string relativePath, string content, string? renameTo = null, IEnumerable<DraftProfileDocument>? inMemoryProfiles = null)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaxAuthoringFileBytes) throw new InvalidDataException("Template exceeds the authoring size limit.");
        string root=RequireDraft(project); string current = ResolveAuthorFile(project, "templates", relativePath, null); EnsureDocumentUnderRoot(root, current, "templates"); string target = renameTo is null ? current : ResolveAuthorFile(project, "templates", renameTo, null); if (!target.Equals(current,StringComparison.OrdinalIgnoreCase)) EnsureNewFile(target);
        string oldReference=Relative(root,current),newReference=Relative(root,target);
        if (oldReference.Equals(newReference, StringComparison.OrdinalIgnoreCase))
        {
            WriteDocument(current, target, content);
            return;
        }

        var loaded = GetProfiles(project);
        var inMemory = (inMemoryProfiles ?? []).ToArray();
        var writes = new List<AuthoringWrite> { new(target, Encoding.UTF8.GetBytes(content)) };
        // See route rename above: this is the persisted baseline, not the caller's dirty document.
        foreach (var profile in loaded)
        {
            if (!RewriteTemplateReferences(profile, oldReference, newReference)) continue;
            writes.Add(new(profile.FilePath, Encoding.UTF8.GetBytes(CaptureProfileState(profile))));
        }
        CommitRenameTransaction(current, target, writes);
        foreach (var profile in inMemory) RewriteTemplateReferences(profile, oldReference, newReference);
    }
    public DraftDeleteGuard CanDeleteTemplate(DraftPackProject project,string relativePath,IEnumerable<DraftProfileDocument>? inMemoryProfiles=null)
    {
        string root=RequireDraft(project),path=ResolveAuthorFile(project,"templates",relativePath,null);EnsureDocumentUnderRoot(root,path,"templates");string reference=Relative(root,path);
        var uses=GetProfiles(project).Concat(inMemoryProfiles??[]).Where(x=>EqualsPackPath(x.Profile.ResolutionApply.TemplateFilePath,reference)).Select(x=>x.Profile.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();return new DraftDeleteGuard(uses.Length==0,uses);
    }
    public void DeleteTemplate(DraftPackProject project, string relativePath,bool force=false,IEnumerable<DraftProfileDocument>? inMemoryProfiles=null) { var guard=CanDeleteTemplate(project,relativePath,inMemoryProfiles);if(!guard.CanDelete&&!force)throw new InvalidOperationException("Template is referenced by: "+string.Join(", ",guard.References));string p = ResolveAuthorFile(project, "templates", relativePath, null); EnsureDocumentUnderRoot(RequireDraft(project), p, "templates"); EnsureNoReparseInExistingAncestors(p); File.Delete(p); }
    public DraftFileItem ImportAsset(DraftPackProject project, string sourceFile, string relativePath)
    {
        using var source=OpenExternalSource(sourceFile); string path = ResolveAuthorFile(project, "assets", relativePath, null); EnsureNewFile(path); string directory=Path.GetDirectoryName(path)!;Directory.CreateDirectory(directory);EnsureNoReparseInExistingAncestors(path);
        string temporary=Path.Combine(directory,$".{Path.GetFileName(path)}.{Guid.NewGuid():N}.authoring-tmp");
        try
        {
            using(var destination=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){source.CopyTo(destination);destination.Flush(true);}
            EnsureNoReparseInExistingAncestors(path);EnsureNewFile(path);File.Move(temporary,path,overwrite:false);
        }
        finally{try{EnsureNoReparseInExistingAncestors(temporary);if(File.Exists(temporary))File.Delete(temporary);}catch{}}
        return DescribeFile(project.RootDirectory, path, false);
    }
    public void RenameAsset(DraftPackProject project, string relativePath, string renameTo) { string current = ResolveAuthorFile(project, "assets", relativePath, null); EnsureDocumentUnderRoot(RequireDraft(project), current, "assets"); string target = ResolveAuthorFile(project, "assets", renameTo, null); EnsureNewFile(target); Directory.CreateDirectory(Path.GetDirectoryName(target)!); EnsureNoReparseInExistingAncestors(current); EnsureNoReparseInExistingAncestors(target); File.Move(current, target); }
    public void DeleteAsset(DraftPackProject project, string relativePath) { string p = ResolveAuthorFile(project, "assets", relativePath, null); EnsureDocumentUnderRoot(RequireDraft(project), p, "assets"); EnsureNoReparseInExistingAncestors(p); File.Delete(p); }
    public NavGraph EnsureGraph(DraftBotDocument document) { document.GraphCleared=false; return document.Bot.Graph ??= new NavGraph(); }
    public void ClearGraph(DraftBotDocument document) { document.Bot.Graph = null; document.GraphCleared=true; }
    public NavScreen AddScreen(DraftBotDocument document, string id) { EnsureContentId(id); var screen = new NavScreen { Id = id }; EnsureGraph(document).Screens.Add(screen); return screen; }
    public NavScreen DuplicateScreen(DraftBotDocument document, NavScreen source, string id)
    {
        EnsureContentId(id); var copy = new NavScreen { Id = id, AnyOf = [..source.AnyOf], AllOf = [..source.AllOf], NoneOf = [..source.NoneOf], WholeWord = source.WholeWord, Goal = source.Goal, MarkStartHere = source.MarkStartHere, Do = source.Do.Select(s => new NavStep { Key=s.Key, Repeat=s.Repeat, HoldMs=s.HoldMs, GapMs=s.GapMs, Note=s.Note, Vk=s.Vk }).ToList() }; EnsureGraph(document).Screens.Add(copy);
        var rawCopy=document.ScreenRaw.TryGetValue(source,out var rawSource)?rawSource.DeepClone().AsObject():JsonObjectFrom(source);MergeTypedIntoRaw(rawCopy,JsonObjectFrom(source),"do");rawCopy["id"]=id;
        var steps=new JsonArray();for(int i=0;i<source.Do.Count;i++){var rawStep=document.NavStepRaw.TryGetValue(source.Do[i],out var tracked)?tracked.DeepClone().AsObject():new JsonObject();MergeTypedIntoRaw(rawStep,JsonObjectFrom(source.Do[i]));steps.Add(rawStep);document.NavStepRaw[copy.Do[i]]=rawStep;}rawCopy["do"]=steps;document.ScreenRaw[copy]=rawCopy;
        return copy;
    }
    public void MoveScreen(DraftBotDocument document, int index, int direction) => Move(EnsureGraph(document).Screens, index, direction);
    public void DeleteScreen(DraftBotDocument document, NavScreen screen) => EnsureGraph(document).Screens.Remove(screen);
    public NavStep AddNavStep(NavScreen screen) { var step = new NavStep(); screen.Do.Add(step); return step; }
    public void MoveNavStep(NavScreen screen, int index, int direction) => Move(screen.Do, index, direction);
    public void DeleteNavStep(NavScreen screen, NavStep step) => screen.Do.Remove(step);
    private static void Move<T>(List<T> list, int index, int direction) { int target=index+direction; if(index>=0&&target>=0&&index<list.Count&&target<list.Count) (list[index],list[target])=(list[target],list[index]); }
    private static bool EqualsRoute(string? value, string route) => !string.IsNullOrWhiteSpace(value) && value.Replace('\\','/').Equals(route, StringComparison.OrdinalIgnoreCase);
    private static bool EqualsPackPath(string? value,string reference)=>!string.IsNullOrWhiteSpace(value)&&value.Replace('\\','/').Equals(reference,StringComparison.OrdinalIgnoreCase);
    private static bool RewriteRouteReferences(DraftBotDocument bot, string oldReference, string newReference)
    {
        bool changed = false;
        foreach (var action in bot.Timeline)
        {
            if (EqualsRoute(action.Action.RoutePath, oldReference)) { action.Action.RoutePath=newReference; changed=true; }
            if (EqualsRoute(action.Action.RecordRoutePath, oldReference)) { action.Action.RecordRoutePath=newReference; changed=true; }
        }
        return changed;
    }
    private static bool RewriteTemplateReferences(DraftProfileDocument profile, string oldReference, string newReference)
    {
        if (!EqualsPackPath(profile.Profile.ResolutionApply.TemplateFilePath, oldReference)) return false;
        profile.Profile.ResolutionApply.TemplateFilePath = newReference;
        return true;
    }

    private sealed record AuthoringWrite(string Destination, byte[] Contents);
    private sealed record AuthoringSnapshot(string Path, byte[]? Contents);

    /// <summary>Stages each replacement before changing a draft file and restores every touched byte sequence on failure.</summary>
    private void CommitRenameTransaction(string oldPath, string newPath, IReadOnlyList<AuthoringWrite> writes)
    {
        var allPaths = writes.Select(x => Path.GetFullPath(x.Destination)).Append(Path.GetFullPath(oldPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var path in allPaths) EnsureNoReparseInExistingAncestors(path);
        var snapshots = allPaths.Select(path => new AuthoringSnapshot(path, _fileOperations.Exists(path) ? _fileOperations.ReadAllBytes(path) : null)).ToArray();
        var staged = new List<(string Temporary, string Destination)>();
        bool rollbackAttempted = false;
        try
        {
            foreach (var write in writes)
            {
                string destination = Path.GetFullPath(write.Destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                EnsureNoReparseInExistingAncestors(destination);
                string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".rename-tmp";
                EnsureNoReparseInExistingAncestors(temporary);
                _fileOperations.WriteAllBytes(temporary, write.Contents);
                staged.Add((temporary, destination));
            }
            foreach (var item in staged)
            {
                EnsureNoReparseInExistingAncestors(item.Destination);
                _fileOperations.Move(item.Temporary, item.Destination, overwrite: true);
            }
            EnsureNoReparseInExistingAncestors(oldPath);
            _fileOperations.Delete(oldPath);
        }
        catch (Exception original)
        {
            rollbackAttempted = true;
            var rollbackErrors = RestoreRenameSnapshots(snapshots).Concat(CleanupStagedFiles(staged)).ToList();
            if (rollbackErrors.Count > 0)
                throw new AggregateException("Authoring rename failed and rollback was incomplete.", new[] { original }.Concat(rollbackErrors));
            throw;
        }
        finally
        {
            if (!rollbackAttempted)
            {
                var cleanupErrors = CleanupStagedFiles(staged);
                if (cleanupErrors.Count > 0)
                    throw new AggregateException("Authoring rename completed but temporary-file cleanup failed.", cleanupErrors);
            }
        }
    }

    private IReadOnlyList<Exception> CleanupStagedFiles(IEnumerable<(string Temporary, string Destination)> staged)
    {
        var failures = new List<Exception>();
        foreach (var item in staged)
            try { if (_fileOperations.Exists(item.Temporary)) _fileOperations.Delete(item.Temporary); }
            catch (Exception ex) { failures.Add(new IOException("Could not clean up authoring rename temporary file: " + item.Temporary, ex)); }
        return failures;
    }

    private IReadOnlyList<Exception> RestoreRenameSnapshots(IEnumerable<AuthoringSnapshot> snapshots)
    {
        var failures = new List<Exception>();
        foreach (var snapshot in snapshots)
        {
            try
            {
                EnsureNoReparseInExistingAncestors(snapshot.Path);
                if (snapshot.Contents is null)
                {
                    if (_fileOperations.Exists(snapshot.Path)) _fileOperations.Delete(snapshot.Path);
                    continue;
                }
                string temporary = snapshot.Path + "." + Guid.NewGuid().ToString("N") + ".rollback-tmp";
                try
                {
                    _fileOperations.WriteAllBytes(temporary, snapshot.Contents);
                    _fileOperations.Move(temporary, snapshot.Path, overwrite: true);
                }
                finally { if (_fileOperations.Exists(temporary)) _fileOperations.Delete(temporary); }
            }
            catch (Exception ex)
            {
                failures.Add(new IOException("Could not restore authoring file during rename rollback: " + snapshot.Path, ex));
            }
        }
        return failures;
    }
    private IReadOnlyList<DraftFileItem> GetDraftFiles(DraftPackProject project, string category, bool text) { string root=RequireDraft(project); return SafeFiles(root, category, true).Select(f=>DescribeFile(root,f,text)).ToArray(); }
    private static DraftFileItem DescribeFile(string root,string file,bool text)=>new(Relative(root,file),new FileInfo(file).Length,text);
    private string ResolveAuthorFile(DraftPackProject project, string category, string relativePath, string? requiredExtension)
    {
        string root=RequireDraft(project); if(string.IsNullOrWhiteSpace(relativePath)||Path.IsPathRooted(relativePath)) throw new InvalidDataException("A relative draft path is required.");
        if(requiredExtension is not null && !relativePath.EndsWith(requiredExtension,StringComparison.OrdinalIgnoreCase)) relativePath+=requiredExtension;
        string target=Path.GetFullPath(Path.Combine(root,category,relativePath.Replace('/',Path.DirectorySeparatorChar))); if(!IsWithin(target,Path.Combine(root,category))) throw new InvalidDataException("Draft path escapes its category."); EnsureNoReparseInExistingAncestors(target); return target;
    }
    private static void EnsureNewFile(string path) { if(File.Exists(path)||Directory.Exists(path)) throw new IOException("Draft file already exists: "+path); }
    private static FileStream OpenExternalSource(string source)
    {
        string path=Path.GetFullPath(source);if(!File.Exists(path))throw new FileNotFoundException("Import source was not found.",path);EnsureNoReparseInExistingAncestors(path);EnsureNoReparse(path);
        FileStream? stream=null;
        try
        {
            // Excluding write/delete sharing pins the validated file for the entire read. It cannot be grown,
            // replaced, or swapped to a reparse target between validation and use.
            stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);EnsureNoReparseInExistingAncestors(path);EnsureNoReparse(path);
            if(stream.Length>MaxAuthoringFileBytes)throw new InvalidDataException("Import exceeds the authoring size limit.");return stream;
        }
        catch{stream?.Dispose();throw;}
    }

    private static DraftRouteDocument LoadRoute(string root, string file)
    {
        var raw = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new InvalidDataException("Route must be a JSON object: " + Relative(root, file));
        var route = RecordedRoute.FromJson(raw.ToJsonString()) ?? throw new InvalidDataException("Invalid route: " + Relative(root, file));
        return CreateRouteDocument(root, file, raw, route);
    }
    private static DraftRouteDocument CreateRouteDocument(string root, string file, JsonObject raw, RecordedRoute route, JsonObject? baseline = null)
    {
        var document = new DraftRouteDocument { FilePath=file, Raw=raw, Route=route, RelativePath=Relative(root,file), RootDirectory=root, BaselineTyped=baseline?.DeepClone().AsObject() ?? JsonNode.Parse(route.ToJson())!.AsObject() };
        if (raw["Steps"] is JsonArray rawSteps)
            for (int i=0;i<route.Steps.Count&&i<rawSteps.Count;i++) if(rawSteps[i] is JsonObject step) document.StepRaw[route.Steps[i]]=step;
        return document;
    }
    private static JsonObject CaptureRouteObject(DraftRouteDocument document)
    {
        var raw = document.Raw.DeepClone().AsObject();
        var typed = JsonNode.Parse(document.Route.ToJson())!.AsObject();
        MergeTypedIntoRaw(raw, typed, "Steps");
        var steps = new JsonArray();
        foreach (var step in document.Route.Steps)
        {
            var typedStep = RouteStepObject(step); var rawStep = document.StepRaw.TryGetValue(step, out var tracked) ? tracked.DeepClone().AsObject() : new JsonObject();
            MergeTypedIntoRaw(rawStep, typedStep); steps.Add(rawStep);
        }
        raw["Steps"] = steps; return raw;
    }
    private static JsonObject RouteStepObject(RouteStep step)
    {
        var holder=new RecordedRoute{Steps=[step]};return JsonNode.Parse(holder.ToJson())!["Steps"]![0]!.AsObject();
    }
}
