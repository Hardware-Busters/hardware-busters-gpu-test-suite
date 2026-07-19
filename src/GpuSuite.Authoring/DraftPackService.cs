using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GpuSuite.Core.Io;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Automation;
using GpuSuite.Engine.Profiles;

namespace GpuSuite.Authoring;

/// <summary>Safe, source-aware operations over editable unpacked profile-pack drafts.</summary>
public sealed partial class DraftPackService
{
    public const string DraftMarkerFileName = ".gtsauthoring.json";
    private static readonly Regex PackIdPattern = new("^[a-z0-9][a-z0-9.-]{1,79}$", RegexOptions.Compiled);
    private static readonly Regex ContentIdPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled);
    private static readonly string[] ContentDirectories = ["profiles", "bots", "routes", "templates", "assets"];
    private readonly Func<IEnumerable<string>> _protectedRoots;
    private readonly IAuthoringFileOperations _fileOperations;

    public DraftPackService(IEnumerable<string>? protectedRoots = null, IAuthoringFileOperations? fileOperations = null)
        : this(() => protectedRoots ?? [], fileOperations) { }

    /// <summary>Uses a live provider so workspace changes immediately update the installed roots that authoring must protect.</summary>
    public DraftPackService(Func<IEnumerable<string>> protectedRoots, IAuthoringFileOperations? fileOperations = null)
    {
        _protectedRoots = protectedRoots ?? throw new ArgumentNullException(nameof(protectedRoots));
        _fileOperations = fileOperations ?? new SystemAuthoringFileOperations();
    }

    /// <summary>Creates a marker-backed editable draft only outside configured installed/bundled roots.</summary>
    public DraftPackProject Create(string destinationDirectory, ProfilePackManifest manifest)
    {
        string root = RequireNewDraftDestination(destinationDirectory);
        bool createdRoot = !Directory.Exists(root);
        ValidateManifestForWrite(manifest);
        Directory.CreateDirectory(root);
        try
        {
            foreach (string directory in ContentDirectories) Directory.CreateDirectory(Path.Combine(root, directory));
            var starter = new GameProfile
            {
                Id = manifest.Id + ".starter", Name = "Starter profile (configure before enabling)", Enabled = false,
                CaptureProcessName = "Game.exe", Launch = new LaunchSpec { Store = "Manual" },
                Scenes = [new SceneProfile { Id = "starter-scene", Name = "Starter scene", CaptureSeconds = 30 }]
            };
            manifest.Games = [starter.Id];
            AtomicWriteJson(Path.Combine(root, "profiles", starter.Id + ".json"), starter);
            AtomicWriteJson(Path.Combine(root, ProfilePackManager.ManifestFileName), manifest);
            AtomicWriteJson(Path.Combine(root, DraftMarkerFileName), new DraftMarker());
            return Open(root);
        }
        catch
        {
            if (createdRoot && Directory.Exists(root)) Directory.Delete(root, recursive: true);
            throw;
        }
    }

    /// <summary>Opens only a marker-backed authoring draft. Pack install roots are never editable drafts.</summary>
    public DraftPackProject Open(string directory)
    {
        string root = RequireDraftRoot(directory);
        var manifest = Json.Load<ProfilePackManifest>(Path.Combine(root, ProfilePackManager.ManifestFileName))
                       ?? throw new InvalidDataException("Draft manifest is not valid JSON.");
        return new DraftPackProject { RootDirectory = root, Manifest = manifest, Inventory = Inventory(root) };
    }

    /// <summary>Copies a bundled, installed or unpacked source into a new marker-backed draft without modifying the source.</summary>
    public DraftPackProject Clone(string sourceDirectory, string destinationDirectory)
    {
        string source = NormalizeDirectory(sourceDirectory);
        EnsureDirectorySafe(source);
        if (!File.Exists(Path.Combine(source, ProfilePackManager.ManifestFileName)))
            throw new InvalidDataException("The source folder is not an unpacked profile-pack.");
        string destination = RequireNewDraftDestination(destinationDirectory);
        if (IsWithin(destination, source)) throw new InvalidOperationException("A clone destination cannot be inside its source pack.");
        bool createdDestination = !Directory.Exists(destination);
        Directory.CreateDirectory(destination);
        try
        {
            CopyFileChecked(Path.Combine(source, ProfilePackManager.ManifestFileName), Path.Combine(destination, ProfilePackManager.ManifestFileName));
            bool flatBundledLayout = !Directory.Exists(Path.Combine(source, "profiles"));
            if (flatBundledLayout) CopyBundledProfiles(source, destination);
            else CopyCategory(source, destination, "profiles");
            if (flatBundledLayout) CopyBundledBots(source, destination);
            else CopyCategory(source, destination, "bots");
            foreach (string category in ContentDirectories.Where(c => c is not ("profiles" or "bots"))) CopyCategory(source, destination, category);
            var manifest = Json.Load<ProfilePackManifest>(Path.Combine(destination, ProfilePackManager.ManifestFileName))
                           ?? throw new InvalidDataException("Cloned manifest is not valid JSON.");
            SynchronizeGames(destination, manifest);
            AtomicWriteJson(Path.Combine(destination, ProfilePackManager.ManifestFileName), manifest);
            AtomicWriteJson(Path.Combine(destination, DraftMarkerFileName), new DraftMarker());
            return Open(destination);
        }
        catch
        {
            if (createdDestination && Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            throw;
        }
    }

    public DraftPackProject SaveMetadata(DraftPackProject project)
    {
        string root = RequireDraft(project);
        ValidateManifestForWrite(project.Manifest);
        SynchronizeGames(root, project.Manifest);
        AtomicWriteJson(Path.Combine(root, ProfilePackManager.ManifestFileName), project.Manifest);
        return Open(root);
    }

    public DraftValidationResult Validate(DraftPackProject project, string? engineVersion = null)
    {
        string root = RequireDraft(project);
        var result = new DraftValidationResult();
        try
        {
            // Traverse every exportable branch up front so a symlink cannot hide in otherwise unreferenced assets.
            foreach (string category in ContentDirectories) _ = SafeFiles(root, category, recursive: true);
            ValidateManifest(project.Manifest, engineVersion, result);
            var profiles = ValidateProfiles(root, result);
            ValidateManifestGames(project.Manifest, profiles, result);
            var bots = ValidateBots(root, result);
            ValidateRoutes(root, result);
            foreach (var (_, profile) in profiles)
            {
                // Disabled profiles are allowed to be incomplete authoring templates (the bundled example is one).
                if (!profile.Profile.Enabled) continue;
                foreach (string id in ReferencedBots(profile.Profile))
                    if (!bots.Contains(id)) result.Error("profile.botReference", $"Profile references missing bot '{id}'.", profile.Path, Location("profile", profile.Path, profile.Profile.Id, controlKey: "botReference"));
                if (!string.IsNullOrWhiteSpace(profile.Profile.ResolutionApply.TemplateFilePath))
                    ValidateExistingPackReference(root, profile.Profile.ResolutionApply.TemplateFilePath!, "template", result, profile.Path, requiredPrefix: "templates/", location: Location("profile", profile.Path, profile.Profile.Id, controlKey: "resolutionApply.templateFilePath"));
                foreach (var scene in profile.Profile.Scenes)
                    if (!string.IsNullOrWhiteSpace(scene.Warmup?.Script) && !bots.Contains(scene.Warmup.Script))
                        result.Error("scene.warmupReference", $"Scene references missing warm-up bot '{scene.Warmup.Script}'.", profile.Path, Location("profile", profile.Path, scene.Id, profile.Profile.Scenes.IndexOf(scene), "scenes.warmup.script"));
            }
        }
        catch (InvalidDataException ex) { result.Error("draft.path", ex.Message); }
        return result;
    }

    public DraftExportResult Export(DraftPackProject project, string destinationArchive, string? engineVersion = null)
    {
        string root = RequireDraft(project);
        string destination = Path.GetFullPath(destinationArchive);
        EnsureNoReparseInExistingAncestors(destination);
        if (IsWithin(destination, root)) throw new InvalidOperationException("The export destination cannot be inside the draft folder.");
        SynchronizeGames(root, project.Manifest);
        ValidateManifestForWrite(project.Manifest);
        var validation = Validate(project, engineVersion);
        if (!validation.IsValid) throw new InvalidDataException("Draft validation failed:" + Environment.NewLine + string.Join(Environment.NewLine, validation.Errors.Select(e => e.Message)));
        AtomicWriteJson(Path.Combine(root, ProfilePackManager.ManifestFileName), project.Manifest);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                AddArchiveFile(archive, root, ProfilePackManager.ManifestFileName);
                    foreach (string category in ContentDirectories)
                    foreach (string file in SafeFiles(root, category, recursive: true).Where(IsPortableContentFile)) AddArchiveFile(archive, root, file);
            }
            // Uses the importer's extraction and directory validation but has no installed-pack or asset-locator side effect.
            _ = new ProfilePackManager(Path.Combine(Path.GetTempPath(), "GpuSuiteAuthoringValidation", Guid.NewGuid().ToString("N")), engineVersion).ValidateArchive(temporary);
            string sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(temporary)));
            AtomicReplace(temporary, destination);
            return new DraftExportResult(destination, sha256, sha256[..16]);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public DraftPackInventory Inventory(string root)
    {
        root = RequireDraftRoot(root);
        return new DraftPackInventory
        {
            Profiles = SafeFiles(root, "profiles", recursive: false).Select(f => Relative(root, f)).ToArray(),
            Bots = SafeFiles(root, "bots", recursive: false).Select(f => Relative(root, f)).ToArray(),
            Routes = SafeFiles(root, "routes", recursive: true).Select(f => Relative(root, f)).ToArray(),
            Templates = SafeFiles(root, "templates", recursive: true).Select(f => Relative(root, f)).ToArray(),
            Assets = SafeFiles(root, "assets", recursive: true).Select(f => Relative(root, f)).ToArray()
        };
    }

    private Dictionary<string, (GameProfile Profile, string Path)> ValidateProfiles(string root, DraftValidationResult result)
    {
        var profiles = new Dictionary<string, (GameProfile Profile, string Path)>(StringComparer.OrdinalIgnoreCase);
        ValidateNestedJson(root, "profiles", result);
        foreach (string file in SafeFiles(root, "profiles", recursive: false).Where(IsJson))
        {
            string relative = Relative(root, file);
            try
            {
                if (ContainsMachinePath(File.ReadAllText(file))) result.Error("profile.machinePath", "Profile contains an absolute machine path; use a portable environment variable or pack-relative asset path.", relative);
                var profile = Json.Load<GameProfile>(file);
                if (profile is null || string.IsNullOrWhiteSpace(profile.Id)) { result.Error("profile.id", "Profile has no id.", relative); continue; }
                if (!ContentIdPattern.IsMatch(profile.Id)) result.Error("profile.id", "Profile id may contain only letters, numbers, dots, underscores and hyphens.", relative);
                if (!Path.GetFileNameWithoutExtension(file).Equals(profile.Id, StringComparison.OrdinalIgnoreCase)) result.Error("profile.filename", $"Profile filename must match id '{profile.Id}'.", relative);
                if (!profiles.TryAdd(profile.Id, (profile, relative))) result.Error("profile.duplicate", $"Profile id '{profile.Id}' is declared more than once.", relative);
                ValidateProfileBasics(profile, result, relative);
            }
            catch (Exception ex) { result.Error("profile.parse", "Profile cannot be parsed: " + ex.Message, relative); }
        }
        if (profiles.Count == 0) result.Error("profile.missing", "A draft pack requires at least one top-level profile under profiles/.");
        return profiles;
    }

    private HashSet<string> ValidateBots(string root, DraftValidationResult result)
    {
        var bots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ValidateNestedJson(root, "bots", result);
        foreach (string file in SafeFiles(root, "bots", recursive: false).Where(IsJson))
        {
            string relative = Relative(root, file);
            try
            {
                var bot = Json.Load<BotScript>(file);
                if (bot is null || string.IsNullOrWhiteSpace(bot.Id)) { result.Error("bot.id", "Bot has no id.", relative); continue; }
                if (!ContentIdPattern.IsMatch(bot.Id)) result.Error("bot.id", "Bot id may contain only letters, numbers, dots, underscores and hyphens.", relative);
                if (!bots.Add(bot.Id)) result.Error("bot.duplicate", $"Bot id '{bot.Id}' is declared more than once.", relative);
                if (!Path.GetFileNameWithoutExtension(file).Equals(bot.Id, StringComparison.OrdinalIgnoreCase)) result.Error("bot.filename", $"Bot filename must match id '{bot.Id}'.", relative);
                ValidateBot(root, bot, result, relative);
            }
            catch (Exception ex) { result.Error("bot.parse", "Bot cannot be parsed: " + ex.Message, relative); }
        }
        return bots;
    }

    private static void ValidateBot(string root, BotScript bot, DraftValidationResult result, string path)
    {
        if (!Enum.IsDefined(bot.InputDevice)) result.Error("bot.device", "Bot input device is not supported.", path, Location("bot", path, bot.Id, controlKey: "inputDevice"));
        if (bot.Actions.Count == 0 && bot.Graph is null && string.IsNullOrWhiteSpace(bot.VisionGoal)) result.Error("bot.actions", "Bot needs actions, a navigation graph, or a vision goal.", path, Location("bot", path, bot.Id, controlKey: "actions"));
        for (int actionIndex = 0; actionIndex < bot.Actions.Count; actionIndex++)
        {
            var action = bot.Actions[actionIndex];
            DraftValidationLocation location(string controlKey) => Location("bot", path, bot.Id, actionIndex, "actions." + controlKey);
            if (!Enum.IsDefined(action.Type)) { result.Error("bot.action", "Bot has an unsupported action type.", path, location("type")); continue; }
            if (action.DurationMs < 0) result.Error("bot.duration", "Bot action duration cannot be negative.", path, location("durationMs"));
            if ((action.Type is BotActionType.KeyDown or BotActionType.KeyUp or BotActionType.KeyTap or BotActionType.PressUntilText or BotActionType.TapIfText or BotActionType.PadButtonDown or BotActionType.PadButtonUp or BotActionType.PadButtonTap) && string.IsNullOrWhiteSpace(action.Key)) result.Error("bot.key", "This bot action needs a key or pad button.", path, location("key"));
            if (action.Type == BotActionType.WaitForText
                && string.IsNullOrWhiteSpace(action.Text)
                && !action.TextAlternatives.Any(text => !string.IsNullOrWhiteSpace(text)))
                result.Error("bot.text", "WaitForText needs screen text or at least one alternative text.", path, location("text"));
            if (action.Type is BotActionType.PressUntilText or BotActionType.TapIfText && string.IsNullOrWhiteSpace(action.Text))
                result.Error("bot.text", $"{action.Type} needs screen text.", path, location("text"));
            if (action.Type == BotActionType.MouseMoveAbs && (action.X is < 0 or > 1 || action.Y is < 0 or > 1)) result.Error("bot.mouse", "Absolute mouse coordinates must be between 0 and 1.", path, location("coordinates"));
            if (action.Type == BotActionType.ReplayRoute && string.IsNullOrWhiteSpace(action.RoutePath)) result.Error("route.missingReference", "ReplayRoute requires RoutePath.", path, location("routePath"));
            if (!string.IsNullOrWhiteSpace(action.RoutePath)) ValidateExistingPackReference(root, action.RoutePath!, "route", result, path, requiredPrefix: "routes/", location: location("routePath"));
            if (!string.IsNullOrWhiteSpace(action.RecordRoutePath)) ValidateRecordRouteReference(root, action.RecordRoutePath!, result, path, location("recordRoutePath"));
        }
    }

    private static void ValidateRoutes(string root, DraftValidationResult result)
    {
        foreach (string file in SafeFiles(root, "routes", recursive: true).Where(IsJson))
        {
            var route = RecordedRoute.FromJson(File.ReadAllText(file));
            if (route is null) result.Error("route.parse", "Route cannot be parsed as a recorded route.", Relative(root, file), Location("route", Relative(root, file), controlKey: "root"));
            else if (route.Steps.Count == 0) result.Error("route.empty", "A recorded route needs at least one movement step.", Relative(root, file), Location("route", Relative(root, file), controlKey: "steps"));
        }
    }

    private static void ValidateProfileBasics(GameProfile profile, DraftValidationResult result, string path)
    {
        bool starter = !profile.Enabled;
        if (!starter && string.IsNullOrWhiteSpace(profile.Name)) result.Error("profile.name", "Enabled profile needs a name.", path, Location("profile", path, profile.Id, controlKey: "name"));
        if (!starter && string.IsNullOrWhiteSpace(profile.CaptureProcessName)) result.Error("profile.capture", "Enabled profile needs a capture process name.", path, Location("profile", path, profile.Id, controlKey: "captureProcessName"));
        if (!starter && profile.Scenes.Count == 0) result.Error("profile.scenes", "Enabled profile needs at least one scene.", path, Location("profile", path, profile.Id, controlKey: "scenes"));
        if (!starter && (profile.SupportedResolutions.Count == 0 || profile.SupportedResolutions.Any(r => string.IsNullOrWhiteSpace(r) || Resolution.FromName(r) is null) || profile.SupportedResolutions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != profile.SupportedResolutions.Count)) result.Error("profile.resolutions", "Enabled profile needs unique supported resolutions (1080p, 1440p or 4K and their accepted aliases).", path, Location("profile", path, profile.Id, controlKey: "supportedResolutions"));
        AddDuplicateErrors(profile.Scenes.Select(s => s.Id), "scene", result, path);
        AddDuplicateErrors(profile.Settings.Select(s => s.Key), "setting", result, path);
        AddDuplicateErrors(profile.Variants.Select(v => v.Id), "variant", result, path);
        for (int sceneIndex = 0; sceneIndex < profile.Scenes.Count; sceneIndex++)
        {
            var scene = profile.Scenes[sceneIndex];
            if (!starter && (string.IsNullOrWhiteSpace(scene.Id) || string.IsNullOrWhiteSpace(scene.Name) || scene.CaptureSeconds <= 0)) result.Error("scene.basic", "Enabled profile scenes need an id, name and positive capture duration.", path, Location("profile", path, scene.Id, sceneIndex, "scenes.basic"));
        }
    }

    private static void AddDuplicateErrors(IEnumerable<string> ids, string kind, DraftValidationResult result, string path)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in ids)
        {
            if (string.IsNullOrWhiteSpace(id) || !ContentIdPattern.IsMatch(id)) result.Error(kind + ".id", $"{kind} ids may contain only letters, numbers, dots, underscores and hyphens.", path);
            else if (!seen.Add(id)) result.Error(kind + ".duplicate", $"{kind} ids must be unique.", path);
        }
    }

    private static IEnumerable<string> ReferencedBots(GameProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.SettingsBotScript)) yield return profile.SettingsBotScript;
        foreach (var scene in profile.Scenes)
        {
            if (!string.IsNullOrWhiteSpace(scene.BotScript)) yield return scene.BotScript;
            if (!string.IsNullOrWhiteSpace(scene.StartBotScript)) yield return scene.StartBotScript;
            if (!string.IsNullOrWhiteSpace(scene.ReRunBotScript)) yield return scene.ReRunBotScript;
            if (!string.IsNullOrWhiteSpace(scene.Warmup?.Script)) yield return scene.Warmup.Script;
        }
    }

    private static void ValidateManifest(ProfilePackManifest manifest, string? engineVersion, DraftValidationResult result)
    {
        if (manifest.SchemaVersion != ProfilePackManager.CurrentSchemaVersion) result.Error("manifest.schema", $"schemaVersion must be {ProfilePackManager.CurrentSchemaVersion}.");
        if (!PackIdPattern.IsMatch(manifest.Id ?? "")) result.Error("manifest.id", "Pack id must be 2-80 lowercase letters, numbers, dots or hyphens.");
        if (string.IsNullOrWhiteSpace(manifest.Name)) result.Error("manifest.name", "Pack name is required.");
        if (!TryVersion(manifest.Version, out _)) result.Error("manifest.version", "Pack version is not valid.");
        if (!TryVersion(manifest.MinimumEngineVersion, out var minimum)) result.Error("manifest.minimumEngineVersion", "minimumEngineVersion is not valid.");
        else if (TryVersion(engineVersion, out var engine) && engine < minimum) result.Error("manifest.engine", $"Pack requires engine {manifest.MinimumEngineVersion} or later.");
    }

    private static void ValidateManifestGames(ProfilePackManifest manifest, IReadOnlyDictionary<string, (GameProfile Profile, string Path)> profiles, DraftValidationResult result)
    {
        var declared = manifest.Games.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (manifest.Games.Count != declared.Length || declared.Any(id => !ContentIdPattern.IsMatch(id)))
            result.Error("manifest.games", "manifest games must contain unique, safe profile ids.");
        foreach (string id in declared)
            if (!profiles.ContainsKey(id)) result.Error("manifest.games", $"Manifest game '{id}' has no matching profile.");
        foreach (var pair in profiles.Where(p => p.Value.Profile.Enabled))
            if (!declared.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)) result.Error("manifest.games", $"Enabled profile '{pair.Key}' must be listed in manifest games.");
    }

    private static void ValidateExistingPackReference(string root, string reference, string kind, DraftValidationResult result, string owner, string requiredPrefix, DraftValidationLocation? location = null)
    {
        if (!TryResolvePackReference(root, reference, out var target, out string error)) { result.Error(kind + ".path", error, owner, location); return; }
        string relative = Relative(root, target);
        if (!relative.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase)) { result.Error(kind + ".path", $"{kind} reference must stay under {requiredPrefix}.", owner, location); return; }
        if (!File.Exists(target)) result.Error(kind + ".missing", $"Referenced {kind} does not exist: {reference}", owner, location);
    }

    private static void ValidateRecordRouteReference(string root, string reference, DraftValidationResult result, string owner, DraftValidationLocation? location = null)
    {
        string portable = reference.Replace('\\', '/');
        if (portable.StartsWith("Results/", StringComparison.OrdinalIgnoreCase))
        {
            if (Path.IsPathRooted(reference) || portable.Split('/').Any(part => part is "" or "." or ".."))
                result.Error("route.recordPath", "Results record paths must be simple portable relative paths.", owner, location);
            return;
        }
        if (!TryResolvePackReference(root, reference, out var target, out string error)) { result.Error("route.recordPath", error, owner, location); return; }
        if (!Relative(root, target).StartsWith("routes/", StringComparison.OrdinalIgnoreCase)) { result.Error("route.recordPath", "RecordRoutePath must stay under routes/.", owner, location); return; }
        string? parent = Path.GetDirectoryName(target);
        if (parent is null || !Directory.Exists(parent)) result.Error("route.recordPath", "RecordRoutePath parent directory does not exist.", owner, location);
    }

    private static DraftValidationLocation Location(string documentKind, string? relativePath, string? itemId = null, int? itemIndex = null, string? controlKey = null)
        => new(documentKind, relativePath, itemId, itemIndex, controlKey);

    private static bool TryResolvePackReference(string root, string reference, out string target, out string error)
    {
        target = ""; error = "";
        if (Path.IsPathRooted(reference) || reference.Contains('%')) { error = "Pack references must be portable, relative paths."; return false; }
        target = Path.GetFullPath(Path.Combine(root, reference.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(target, root)) { error = "Pack reference escapes the draft folder."; return false; }
        return true;
    }

    private static void ValidateNestedJson(string root, string category, DraftValidationResult result)
    {
        foreach (string file in SafeFiles(root, category, recursive: true).Where(IsJson))
            if (Relative(root, file).Split('/').Length > 2) result.Error(category + ".nested", $"{category} JSON must be directly under {category}/ because the runtime only loads top-level files.", Relative(root, file));
    }

    private static void SynchronizeGames(string root, ProfilePackManifest manifest)
    {
        var profiles = new List<(string Id, bool Enabled)>();
        foreach (string file in SafeFiles(root, "profiles", recursive: false).Where(IsJson))
        {
            var profile = Json.Load<GameProfile>(file) ?? throw new InvalidDataException("Cannot synchronize games from an invalid profile: " + Relative(root, file));
            if (string.IsNullOrWhiteSpace(profile.Id)) throw new InvalidDataException("Cannot synchronize games from a profile without an id: " + Relative(root, file));
            profiles.Add((profile.Id, profile.Enabled));
        }
        var enabled = profiles.Where(p => p.Enabled).Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (enabled.Count > 0) manifest.Games = enabled;
        else
        {
            var available = profiles.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            manifest.Games = manifest.Games.Where(available.Contains).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            if (manifest.Games.Count == 0 && profiles.Count > 0) manifest.Games = [profiles[0].Id];
        }
    }

    private static void CopyBundledProfiles(string source, string destination)
    {
        string targetDirectory = Path.Combine(destination, "profiles");
        Directory.CreateDirectory(targetDirectory);
        foreach (string file in SafeTopLevelFiles(source).Where(IsJson).Where(f => !Path.GetFileName(f).Equals(ProfilePackManager.ManifestFileName, StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Equals(ProfilePackManager.StateFileName, StringComparison.OrdinalIgnoreCase)))
        {
            var profile = Json.Load<GameProfile>(file) ?? throw new InvalidDataException("Bundled profile cannot be parsed: " + file);
            if (!string.IsNullOrWhiteSpace(profile.ResolutionApply.TemplateFilePath)) profile.ResolutionApply.TemplateFilePath = StripBundledProfilesPrefix(profile.ResolutionApply.TemplateFilePath!);
            AtomicWriteJson(Path.Combine(targetDirectory, Path.GetFileName(file)), profile);
        }
    }

    private static void CopyBundledBots(string source, string destination)
    {
        string targetDirectory = Path.Combine(destination, "bots");
        Directory.CreateDirectory(targetDirectory);
        foreach (string file in SafeFiles(source, "bots", recursive: false).Where(IsJson))
        {
            var bot = Json.Load<BotScript>(file) ?? throw new InvalidDataException("Bundled bot cannot be parsed: " + file);
            foreach (var action in bot.Actions)
            {
                if (!string.IsNullOrWhiteSpace(action.RoutePath)) action.RoutePath = StripBundledProfilesPrefix(action.RoutePath!);
                if (!string.IsNullOrWhiteSpace(action.RecordRoutePath) && action.RecordRoutePath!.Replace('\\', '/').StartsWith("profiles/", StringComparison.OrdinalIgnoreCase))
                    action.RecordRoutePath = StripBundledProfilesPrefix(action.RecordRoutePath!);
            }
            AtomicWriteJson(Path.Combine(targetDirectory, Path.GetFileName(file)), bot);
        }
    }

    private static string StripBundledProfilesPrefix(string reference)
    {
        string normalized = reference.Replace('\\', '/');
        return normalized.StartsWith("profiles/", StringComparison.OrdinalIgnoreCase) ? normalized["profiles/".Length..] : normalized;
    }

    private static void CopyCategory(string source, string destination, string category)
    {
        string output = Path.Combine(destination, category);
        Directory.CreateDirectory(output);
        var files = SafeFiles(source, category, recursive: true);
        if (category is "profiles" or "bots")
        {
            if (files.Where(IsJson).Any(file => Relative(source, file).Split('/').Length > 2))
                throw new InvalidDataException($"{category} JSON must be top-level because the runtime ignores nested files.");
            files = files.Where(file => Relative(source, file).Split('/').Length == 2).ToArray();
        }
        foreach (string file in files.Where(IsPortableContentFile))
        {
            string target = Path.Combine(destination, Relative(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CopyFileChecked(file, target);
        }
    }

    private static void CopyFileChecked(string source, string destination)
    {
        EnsureNoReparse(source);
        if (File.Exists(destination)) throw new IOException("Clone destination would overwrite an existing file: " + destination);
        File.Copy(source, destination, overwrite: false);
    }

    private static void AddArchiveFile(ZipArchive archive, string root, string file)
        => archive.CreateEntryFromFile(Path.IsPathRooted(file) ? file : Path.Combine(root, file), Path.IsPathRooted(file) ? Relative(root, file) : file.Replace('\\', '/'), CompressionLevel.Optimal);

    private string RequireNewDraftDestination(string path)
    {
        string full = NormalizeDirectory(path);
        EnsureNoReparseInExistingAncestors(full);
        if (IsProtected(full)) throw new InvalidOperationException("Draft folders cannot be created in the installed or bundled profile-pack root.");
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any()) throw new IOException("Destination folder must be new or empty: " + full);
        return full;
    }

    private string RequireDraftRoot(string path)
    {
        string root = NormalizeDirectory(path);
        if (IsProtected(root)) throw new InvalidOperationException("Installed and bundled profile-pack folders must be cloned before authoring.");
        EnsureDirectorySafe(root);
        if (!File.Exists(Path.Combine(root, DraftMarkerFileName))) throw new InvalidOperationException("This folder is not an Author Studio draft. Clone the pack first.");
        if (!File.Exists(Path.Combine(root, ProfilePackManager.ManifestFileName))) throw new InvalidDataException($"Draft root must contain {ProfilePackManager.ManifestFileName}.");
        EnsureNoReparse(Path.Combine(root, DraftMarkerFileName));
        EnsureNoReparse(Path.Combine(root, ProfilePackManager.ManifestFileName));
        return root;
    }

    private string RequireDraft(DraftPackProject project)
    {
        if (project is null) throw new InvalidOperationException("No authoring draft is open.");
        return RequireDraftRoot(project.RootDirectory);
    }

    private bool IsProtected(string candidate)
        => _protectedRoots().Where(root => !string.IsNullOrWhiteSpace(root)).Any(root => IsWithinCanonical(candidate, root));
    private static string NormalizeDirectory(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static bool IsWithin(string candidate, string root)
    {
        string c = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return c.Equals(r, StringComparison.OrdinalIgnoreCase) || c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsWithinCanonical(string candidate, string root)
        => IsWithin(CanonicalizePath(candidate), CanonicalizePath(root));
    private static void EnsureDirectorySafe(string directory)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Folder was not found: " + directory);
        EnsureNoReparseInExistingAncestors(directory);
        EnsureNoReparse(directory);
    }
    private static void EnsureNoReparseInExistingAncestors(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Authoring destinations and their ancestors cannot be reparse points: " + current);
            string? parent = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }
    private static string CanonicalizePath(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? "";
        string current = root;
        foreach (string part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(current, part);
            if (Directory.Exists(next) && (File.GetAttributes(next) & FileAttributes.ReparsePoint) != 0)
                current = new DirectoryInfo(next).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? next;
            else current = next;
        }
        return Path.GetFullPath(current);
    }
    private static void EnsureNoReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse points are not allowed in authoring pack content: " + path);
    }
    private static IReadOnlyList<string> SafeFiles(string root, string category, bool recursive)
    {
        string directory = Path.Combine(root, category);
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        EnsureNoReparse(directory);
        var result = new List<string>();
        Traverse(directory, recursive, result);
        return result.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static IReadOnlyList<string> SafeTopLevelFiles(string root)
    {
        EnsureDirectorySafe(root);
        var files = new List<string>();
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            EnsureNoReparse(entry);
            if (File.Exists(entry)) files.Add(entry);
        }
        return files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static void Traverse(string directory, bool recursive, List<string> result)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            EnsureNoReparse(entry);
            if (File.Exists(entry)) result.Add(entry);
            else if (Directory.Exists(entry) && recursive) Traverse(entry, recursive, result);
        }
    }
    private static bool IsJson(string file) => Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase);
    private static bool IsPortableContentFile(string file)
    {
        string name = Path.GetFileName(file);
        return !name.Equals(DraftMarkerFileName, StringComparison.OrdinalIgnoreCase)
               && !name.Equals(".env", StringComparison.OrdinalIgnoreCase)
               && !name.Equals("key", StringComparison.OrdinalIgnoreCase)
               && !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
               && !name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
               && !name.EndsWith(ProfilePackManager.ArchiveExtension, StringComparison.OrdinalIgnoreCase);
    }
    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');
    private static bool ContainsMachinePath(string content)
        => Regex.IsMatch(content, @"(?i)[a-z]:(?:\\\\|/)(?:users|documents and settings)(?:\\\\|/)")
           || Regex.IsMatch(content, """(?i)"\\\\\\\\[^"\\]+\\\\[^"\\]+""");
    private static bool TryVersion(string? raw, out Version version)
    {
        string value = (raw ?? "").Trim().TrimStart('v', 'V'); int suffix = value.IndexOfAny(['-', '+']); if (suffix >= 0) value = value[..suffix];
        return Version.TryParse(value, out version!);
    }
    private static void ValidateManifestForWrite(ProfilePackManifest manifest)
    {
        if (manifest is null || manifest.SchemaVersion != ProfilePackManager.CurrentSchemaVersion) throw new InvalidDataException($"Author Studio supports manifest schema {ProfilePackManager.CurrentSchemaVersion} only.");
        if (!PackIdPattern.IsMatch(manifest.Id ?? "")) throw new InvalidDataException("A draft needs a valid lowercase pack id.");
        if (string.IsNullOrWhiteSpace(manifest.Name)) throw new InvalidDataException("A draft needs a pack name.");
        if (!TryVersion(manifest.Version, out _) || !TryVersion(manifest.MinimumEngineVersion, out _)) throw new InvalidDataException("Pack and minimum engine versions must be valid versions.");
    }
    private static void AtomicWriteJson<T>(string path, T value) => AtomicWrite(path, Json.ToString(value));
    private static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, content, Encoding.UTF8); AtomicReplace(temp, path); } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void AtomicReplace(string temporary, string destination)
    {
        // Re-check at the last possible moment so an existing destination file cannot be
        // swapped for a symlink after the operation's initial containment validation.
        EnsureNoReparseInExistingAncestors(destination);
        if (File.Exists(destination)) File.Replace(temporary, destination, null); else File.Move(temporary, destination);
    }

    private sealed class DraftMarker { public int SchemaVersion { get; set; } = 1; public DateTime CreatedUtc { get; set; } = DateTime.UtcNow; }
}
