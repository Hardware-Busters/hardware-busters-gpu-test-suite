using System.IO.Compression;
using GpuSuite.Authoring;
using GpuSuite.Core.Io;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Automation;
using GpuSuite.Engine.Profiles;
using Xunit;

namespace GpuSuite.Tests;

public sealed class DraftPackServiceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "GpuSuiteAuthoringTests", Guid.NewGuid().ToString("N"));
    private readonly DraftPackService _service = new();
    public DraftPackServiceTests() => Directory.CreateDirectory(_temp);
    public void Dispose() { if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true); }

    [Fact]
    public void CreateOpenAndSaveMetadataUseAnUnpackedDraft()
    {
        string root = Path.Combine(_temp, "new-draft");
        var created = _service.Create(root, Manifest("studio.created"));
        Assert.True(File.Exists(Path.Combine(root, "profile-pack.json")));
        Assert.True(File.Exists(Path.Combine(root, "profiles", "studio.created.starter.json")));
        Assert.All(new[] { "bots", "routes", "templates", "assets" }, d => Assert.True(Directory.Exists(Path.Combine(root, d))));

        created.Manifest.Name = "Saved authoring pack";
        var reopened = _service.SaveMetadata(created);
        Assert.Equal("Saved authoring pack", reopened.Manifest.Name);
        Assert.Single(reopened.Inventory.Profiles);
    }

    [Fact]
    public void ValidationReportsInvalidBotsReferencesAndRoutes()
    {
        var draft = _service.Create(Path.Combine(_temp, "invalid"), Manifest("studio.invalid"));
        var profile = Json.Load<GameProfile>(Path.Combine(draft.RootDirectory, draft.Inventory.Profiles.Single()))!;
        profile.Enabled = true;
        profile.SupportedResolutions = ["4K"];
        profile.SettingsBotScript = "missing-bot";
        Json.Save(Path.Combine(draft.RootDirectory, "profiles", "studio.invalid.starter.json"), profile);
        Json.Save(Path.Combine(draft.RootDirectory, "bots", "wrong-name.json"), new BotScript
        {
            Id = "real-bot", Actions = [new BotAction { Type = BotActionType.ReplayRoute, RoutePath = "../outside.json" }]
        });

        var result = _service.Validate(draft, "1.0.0");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, i => i.Code == "profile.botReference");
        Assert.Contains(result.Errors, i => i.Code == "bot.filename");
        Assert.Contains(result.Errors, i => i.Code == "route.path");
    }

    [Fact]
    public void ValidationIssuesExposeStableEditorLocations()
    {
        var draft = _service.Create(Path.Combine(_temp, "locations"), Manifest("studio.locations"));
        var bot = _service.CreateBot(draft, "broken");
        _service.AddBotAction(bot, BotActionType.ReplayRoute);
        _service.SaveBot(draft, bot);
        var result = _service.Validate(draft);
        var issue = Assert.Single(result.Issues, x => x.Code == "route.missingReference");
        Assert.Equal("bot", issue.Location?.DocumentKind);
        Assert.Equal("bots/broken.json", issue.Location?.RelativePath);
        Assert.Equal("broken", issue.Location?.ItemId);
        Assert.Equal(0, issue.Location?.ItemIndex);
        Assert.Equal("actions.routePath", issue.Location?.ControlKey);
    }

    [Fact]
    public void EveryValidationIssueFamilyHasAStableLocationFallback()
    {
        var draft = _service.Create(Path.Combine(_temp, "location-fallbacks"), Manifest("studio.locations-fallback"));
        draft.Manifest.Id = "INVALID ID";
        File.WriteAllText(Path.Combine(draft.RootDirectory, "profiles", "broken.json"), "{");
        Json.Save(Path.Combine(draft.RootDirectory, "profiles", "semantic.json"), new GameProfile
        {
            Id = "semantic", Enabled = true, Name = "", CaptureProcessName = "", SupportedResolutions = [],
            Scenes = [new SceneProfile { Id = "scene", Name = "", CaptureSeconds = 0 }]
        });
        File.WriteAllText(Path.Combine(draft.RootDirectory, "bots", "broken.json"), "{");
        Directory.CreateDirectory(Path.Combine(draft.RootDirectory, "bots", "nested"));
        Json.Save(Path.Combine(draft.RootDirectory, "bots", "nested", "ignored.json"), new BotScript { Id = "ignored", Actions = [new BotAction { Type = BotActionType.Wait, DurationMs = 1 }] });
        File.WriteAllText(Path.Combine(draft.RootDirectory, "routes", "broken.json"), "[]");

        var result = _service.Validate(draft);

        Assert.Contains(result.Issues, x => x.Code == "manifest.id" && x.Location?.DocumentKind == "manifest" && x.Location.RelativePath == "profile-pack.json");
        Assert.Contains(result.Issues, x => x.Code == "profile.parse" && x.Location?.DocumentKind == "profile" && x.Location.RelativePath == "profiles/broken.json");
        Assert.Contains(result.Issues, x => x.Code == "profile.capture" && x.Location?.ControlKey == "captureProcessName");
        Assert.Contains(result.Issues, x => x.Code == "scene.basic" && x.Location?.ItemIndex == 0);
        Assert.Contains(result.Issues, x => x.Code == "bot.parse" && x.Location?.DocumentKind == "bot" && x.Location.RelativePath == "bots/broken.json");
        Assert.Contains(result.Issues, x => x.Code == "bots.nested" && x.Location?.DocumentKind == "bot" && x.Location.RelativePath == "bots/nested/ignored.json");
        Assert.Contains(result.Issues, x => x.Code == "route.parse" && x.Location?.DocumentKind == "route" && x.Location.RelativePath == "routes/broken.json");
        Assert.All(result.Issues, x => { Assert.NotNull(x.Location); Assert.False(string.IsNullOrWhiteSpace(x.Location!.RelativePath)); Assert.False(string.IsNullOrWhiteSpace(x.Location.ControlKey)); });
    }

    [Fact]
    public void EditCommitCoordinatorFailsWithoutThrowingAndLeavesTheTransitionSourceSelected()
    {
        string message = "";
        int selectedDocument = 1;
        bool committed = AuthoringEditCommitCoordinator.TryCommit([() => false], text => message = text);
        if (committed) selectedDocument = 2;

        Assert.False(committed);
        Assert.Equal(1, selectedDocument);
        Assert.Contains("highlighted", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EditCommitCoordinatorHandlesDeferredFailuresAndScalarBindingErrors()
    {
        string message = "";
        bool committed = AuthoringEditCommitCoordinator.TryCommit([() => throw new InvalidOperationException("bad deferred checkpoint")], text => message = text);

        Assert.False(committed);
        Assert.Contains("bad deferred checkpoint", message);
        Assert.False(AuthoringEditCommitCoordinator.IsScalarBindingValid(bindingHasError: true, validationHasError: false));
        Assert.False(AuthoringEditCommitCoordinator.IsScalarBindingValid(bindingHasError: false, validationHasError: true));
        Assert.True(AuthoringEditCommitCoordinator.IsScalarBindingValid(bindingHasError: false, validationHasError: false));
    }

    [Fact]
    public void EditCommitCoordinatorOnlyClassifiesOutboundControlsAsTransitions()
    {
        Assert.False(AuthoringEditCommitCoordinator.RequiresOutboundPointerCommit(AuthoringEditCommitCoordinator.InputSurface.Editor));
        Assert.False(AuthoringEditCommitCoordinator.RequiresOutboundPointerCommit(AuthoringEditCommitCoordinator.InputSurface.Other));
        Assert.True(AuthoringEditCommitCoordinator.RequiresOutboundPointerCommit(AuthoringEditCommitCoordinator.InputSurface.DocumentSelector));
        Assert.True(AuthoringEditCommitCoordinator.RequiresOutboundPointerCommit(AuthoringEditCommitCoordinator.InputSurface.Tab));
        Assert.True(AuthoringEditCommitCoordinator.RequiresOutboundPointerCommit(AuthoringEditCommitCoordinator.InputSurface.Button));
        Assert.False(AuthoringEditCommitCoordinator.RequiresOutboundKeyCommit(AuthoringEditCommitCoordinator.InputSurface.Editor, "Left"));
        Assert.False(AuthoringEditCommitCoordinator.RequiresOutboundKeyCommit(AuthoringEditCommitCoordinator.InputSurface.Editor, "Down"));
        Assert.True(AuthoringEditCommitCoordinator.RequiresOutboundKeyCommit(AuthoringEditCommitCoordinator.InputSurface.DocumentSelector, "Down"));
        Assert.True(AuthoringEditCommitCoordinator.RequiresOutboundKeyCommit(AuthoringEditCommitCoordinator.InputSurface.Other, "Tab"));
    }

    [Fact]
    public void CloneCopiesToNewDraftWithoutMutatingSource()
    {
        var source = _service.Create(Path.Combine(_temp, "source"), Manifest("studio.source"));
        string sourceManifest = File.ReadAllText(Path.Combine(source.RootDirectory, "profile-pack.json"));
        var clone = _service.Clone(source.RootDirectory, Path.Combine(_temp, "clone"));
        clone.Manifest.Name = "Clone name";
        _service.SaveMetadata(clone);

        Assert.Equal(sourceManifest, File.ReadAllText(Path.Combine(source.RootDirectory, "profile-pack.json")));
        Assert.Equal("Clone name", _service.Open(clone.RootDirectory).Manifest.Name);
    }

    [Fact]
    public void ExportRoundTripsThroughExistingPackManagerAndExcludesEditorFiles()
    {
        var draft = _service.Create(Path.Combine(_temp, "export"), Manifest("studio.export"));
        File.WriteAllText(Path.Combine(draft.RootDirectory, ".gtsauthoring.json"), "editor state");
        File.WriteAllText(Path.Combine(draft.RootDirectory, ".env"), "secret");
        File.WriteAllText(Path.Combine(draft.RootDirectory, "key"), "secret");
        File.WriteAllText(Path.Combine(draft.RootDirectory, "old.gtsprofilepack"), "old archive");
        File.WriteAllText(Path.Combine(draft.RootDirectory, "profile-packs.state.json"), "state");
        File.WriteAllText(Path.Combine(draft.RootDirectory, "root-junk.txt"), "junk");
        Directory.CreateDirectory(Path.Combine(draft.RootDirectory, "Results"));
        File.WriteAllText(Path.Combine(draft.RootDirectory, "Results", "local.log"), "local data");
        File.WriteAllText(Path.Combine(draft.RootDirectory, "profiles", "scratch.tmp"), "temp");
        string archive = Path.Combine(_temp, "export.gtsprofilepack");

        var export = _service.Export(draft, archive, "1.0.0");
        using (var zip = ZipFile.OpenRead(archive))
        {
            Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("gtsauthoring", StringComparison.OrdinalIgnoreCase) || e.FullName.StartsWith("Results/", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || e.FullName.Equals(".env", StringComparison.OrdinalIgnoreCase) || e.FullName.Equals("key", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".gtsprofilepack", StringComparison.OrdinalIgnoreCase) || e.FullName.Equals("profile-packs.state.json", StringComparison.OrdinalIgnoreCase) || e.FullName.Equals("root-junk.txt", StringComparison.OrdinalIgnoreCase));
        }
        string profiles = Path.Combine(_temp, "target", "profiles");
        var installed = new ProfilePackManager(profiles, "1.0.0").InstallArchive(archive);
        Assert.Equal("studio.export", installed.Pack.Manifest.Id);
        Assert.Equal(64, export.Sha256.Length);
    }

    [Fact]
    public void FailedExportDoesNotOverwriteAGoodDestination()
    {
        var good = _service.Create(Path.Combine(_temp, "good"), Manifest("studio.good"));
        string archive = Path.Combine(_temp, "existing.gtsprofilepack");
        _service.Export(good, archive, "1.0.0");
        byte[] original = File.ReadAllBytes(archive);

        var invalid = _service.Create(Path.Combine(_temp, "bad"), Manifest("studio.bad"));
        var profile = Json.Load<GameProfile>(Path.Combine(invalid.RootDirectory, invalid.Inventory.Profiles.Single()))!;
        profile.Enabled = true;
        profile.SupportedResolutions = ["4K"];
        profile.SettingsBotScript = "missing";
        Json.Save(Path.Combine(invalid.RootDirectory, "profiles", "studio.bad.starter.json"), profile);

        Assert.Throws<InvalidDataException>(() => _service.Export(_service.Open(invalid.RootDirectory), archive, "1.0.0"));
        Assert.Equal(original, File.ReadAllBytes(archive));
    }

    [Fact]
    public void InstalledRootCannotBeOpenedOrMutatedButCanBeClonedAsBundledLayout()
    {
        string profiles = Path.Combine(_temp, "profiles");
        Directory.CreateDirectory(profiles);
        Json.Save(Path.Combine(profiles, "profile-pack.json"), Manifest("hardware-busters.verified"));
        Json.Save(Path.Combine(profiles, "official.json"), new GameProfile { Id = "official", Name = "Official", Enabled = false });
        Directory.CreateDirectory(Path.Combine(profiles, "packs", "other"));
        File.WriteAllText(Path.Combine(profiles, "profile-packs.state.json"), "{}");
        string originalManifest = File.ReadAllText(Path.Combine(profiles, "profile-pack.json"));
        var protectedService = new DraftPackService([profiles]);

        Assert.Throws<InvalidOperationException>(() => protectedService.Open(profiles));
        Assert.Throws<InvalidOperationException>(() => protectedService.Create(Path.Combine(profiles, "new-draft"), Manifest("studio.blocked")));
        var clone = protectedService.Clone(profiles, Path.Combine(_temp, "bundled-clone"));

        Assert.Equal(originalManifest, File.ReadAllText(Path.Combine(profiles, "profile-pack.json")));
        Assert.True(File.Exists(Path.Combine(clone.RootDirectory, DraftPackService.DraftMarkerFileName)));
        Assert.True(File.Exists(Path.Combine(clone.RootDirectory, "profiles", "official.json")));
        Assert.False(Directory.Exists(Path.Combine(clone.RootDirectory, "packs")));
        Assert.False(File.Exists(Path.Combine(clone.RootDirectory, "profile-packs.state.json")));
    }

    [Fact]
    public void CloneAndExportRejectDraftContainmentDestinations()
    {
        var draft = _service.Create(Path.Combine(_temp, "containment"), Manifest("studio.containment"));

        Assert.Throws<InvalidOperationException>(() => _service.Clone(draft.RootDirectory, Path.Combine(draft.RootDirectory, "clone")));
        Assert.Throws<InvalidOperationException>(() => _service.Export(draft, Path.Combine(draft.RootDirectory, "pack.gtsprofilepack"), "1.0.0"));
    }

    [Fact]
    public void NestedRuntimeIgnoredProfilesAreValidationErrors()
    {
        var draft = _service.Create(Path.Combine(_temp, "nested"), Manifest("studio.nested"));
        Directory.CreateDirectory(Path.Combine(draft.RootDirectory, "profiles", "nested"));
        Json.Save(Path.Combine(draft.RootDirectory, "profiles", "nested", "hidden.json"), new GameProfile { Id = "hidden" });

        var result = _service.Validate(_service.Open(draft.RootDirectory), "1.0.0");

        Assert.Contains(result.Errors, i => i.Code == "profiles.nested");
    }

    [Fact]
    public void MissingRouteTemplateAndWarmupBotAreStructuralErrors()
    {
        var draft = _service.Create(Path.Combine(_temp, "references"), Manifest("studio.references"));
        var profilePath = Path.Combine(draft.RootDirectory, "profiles", "studio.references.starter.json");
        var profile = Json.Load<GameProfile>(profilePath)!;
        profile.Enabled = true;
        profile.SupportedResolutions = ["4K"];
        profile.ResolutionApply.TemplateFilePath = "templates/missing.json";
        profile.Scenes[0].Warmup = new SceneWarmupConfig { Enabled = true, Script = "warmup-bot" };
        Json.Save(profilePath, profile);
        Json.Save(Path.Combine(draft.RootDirectory, "bots", "route-bot.json"), new BotScript { Id = "route-bot", Actions = [new BotAction { Type = BotActionType.ReplayRoute }] });
        File.WriteAllText(Path.Combine(draft.RootDirectory, "routes", "invalid.json"), "{");

        var result = _service.Validate(_service.Open(draft.RootDirectory), "1.0.0");

        Assert.Contains(result.Errors, i => i.Code == "template.missing");
        Assert.Contains(result.Errors, i => i.Code == "scene.warmupReference");
        Assert.Contains(result.Errors, i => i.Code == "route.missingReference");
        Assert.Contains(result.Errors, i => i.Code == "route.parse");
    }

    [Fact]
    public void OcrActionsWithoutTargetsAndEmptyRoutesAreValidationErrors()
    {
        var draft = _service.Create(Path.Combine(_temp, "empty-safety-evidence"), Manifest("studio.empty-safety-evidence"));
        Json.Save(Path.Combine(draft.RootDirectory, "bots", "unsafe.json"), new BotScript
        {
            Id = "unsafe",
            Actions =
            [
                new BotAction { Type = BotActionType.WaitForText, Required = true },
                new BotAction { Type = BotActionType.WaitForText, Required = true, TextAlternatives = ["READY"] },
                new BotAction { Type = BotActionType.PressUntilText, Key = "Enter", TextAlternatives = ["READY"] },
                new BotAction { Type = BotActionType.TapIfText, Key = "Enter", TextAlternatives = ["READY"] }
            ]
        });
        File.WriteAllText(Path.Combine(draft.RootDirectory, "routes", "empty.json"), new RecordedRoute().ToJson());

        var result = _service.Validate(_service.Open(draft.RootDirectory), "1.0.0");

        Assert.Equal(3, result.Errors.Count(issue => issue.Code == "bot.text"));
        Assert.Contains(result.Errors, issue => issue.Code == "route.empty");
    }

    [Fact]
    public void UnknownManifestMembersSurviveAuthoringSave()
    {
        var draft = _service.Create(Path.Combine(_temp, "extensions"), Manifest("studio.extensions"));
        string manifestPath = Path.Combine(draft.RootDirectory, "profile-pack.json");
        File.WriteAllText(manifestPath, """{"schemaVersion":1,"id":"studio.extensions","name":"extensions","version":"1.0.0","minimumEngineVersion":"0.1.0","customPublisherField":{"kept":true}}""");

        var reopened = _service.Open(draft.RootDirectory);
        _service.SaveMetadata(reopened);

        Assert.Contains("customPublisherField", File.ReadAllText(manifestPath));
    }

    [Fact]
    public void ExportedDraftInstallsAndResolvesBotRouteAndTemplate()
    {
        var draft = _service.Create(Path.Combine(_temp, "resolution"), Manifest("studio.resolution"));
        string profilePath = Path.Combine(draft.RootDirectory, "profiles", "studio.resolution.starter.json");
        var profile = Json.Load<GameProfile>(profilePath)!;
        profile.SettingsBotScript = "route-bot";
        profile.ResolutionApply.TemplateFilePath = "templates/settings.json";
        profile.Scenes[0].BotScript = "route-bot";
        Json.Save(profilePath, profile);
        Json.Save(Path.Combine(draft.RootDirectory, "bots", "route-bot.json"), new BotScript { Id = "route-bot", Actions = [new BotAction { Type = BotActionType.ReplayRoute, RoutePath = "routes/path.json" }] });
        File.WriteAllText(Path.Combine(draft.RootDirectory, "routes", "path.json"), new RecordedRoute { Steps = [RouteStep.Fwd(1000)] }.ToJson());
        File.WriteAllText(Path.Combine(draft.RootDirectory, "templates", "settings.json"), "{}");
        string archive = Path.Combine(_temp, "resolution.gtsprofilepack");

        _service.Export(_service.Open(draft.RootDirectory), archive, "1.0.0");
        string profiles = Path.Combine(_temp, "installed", "profiles");
        new ProfilePackManager(profiles, "1.0.0").InstallArchive(archive);
        var loaded = new ProfileManager(profiles).Load("studio.resolution.starter")!;
        var bot = BotScriptLibrary.Resolve("route-bot");

        Assert.True(Path.IsPathRooted(loaded.ResolutionApply.TemplateFilePath));
        Assert.True(File.Exists(loaded.ResolutionApply.TemplateFilePath));
        Assert.NotNull(bot);
        Assert.True(Path.IsPathRooted(bot!.Actions.Single().RoutePath));
        Assert.True(File.Exists(bot.Actions.Single().RoutePath));
    }

    [Fact]
    public void ArchiveValidationDoesNotReconfigureAssetResolution()
    {
        var draft = _service.Create(Path.Combine(_temp, "side-effect"), Manifest("studio.side-effect"));
        string archive = Path.Combine(_temp, "side-effect.gtsprofilepack");
        _service.Export(draft, archive, "1.0.0");
        string resolverRoot = Path.Combine(_temp, "resolver");
        Directory.CreateDirectory(Path.Combine(resolverRoot, "templates"));
        File.WriteAllText(Path.Combine(resolverRoot, "templates", "sentinel.txt"), "ok");
        Json.Save(Path.Combine(resolverRoot, "resolver-game.json"), new GameProfile { Id = "resolver-game", Name = "Resolver", Enabled = false });
        ProfileAssetLocator.Configure(resolverRoot);

        _ = new ProfilePackManager(Path.Combine(_temp, "validation-only"), "1.0.0").ValidateArchive(archive);

        Assert.Equal(Path.Combine(resolverRoot, "templates", "sentinel.txt"), ProfileAssetLocator.Find("templates", "sentinel.txt"));
    }

    [Fact]
    public void ReparsePointsInAllowlistedContentAreRejectedWhenSupported()
    {
        var draft = _service.Create(Path.Combine(_temp, "reparse"), Manifest("studio.reparse"));
        string target = Path.Combine(_temp, "reparse-target");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(draft.RootDirectory, "assets", "linked"), target);
        }
        catch (UnauthorizedAccessException) { return; }
        catch (PlatformNotSupportedException) { return; }
        catch (IOException) { return; }

        var result = _service.Validate(draft, "1.0.0");

        Assert.Contains(result.Errors, i => i.Code == "draft.path");
    }

    [Fact]
    public void LiveProtectedRootProviderTracksWorkspaceChanges()
    {
        string first = Path.Combine(_temp, "workspace-one", "profiles");
        string second = Path.Combine(_temp, "workspace-two", "profiles");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        string current = first;
        var service = new DraftPackService(() => [current]);

        Assert.Throws<InvalidOperationException>(() => service.Create(Path.Combine(first, "draft"), Manifest("studio.first")));
        current = second;
        Assert.Throws<InvalidOperationException>(() => service.Create(Path.Combine(second, "draft"), Manifest("studio.second")));
    }

    [Fact]
    public void ReparseAliasesCannotBypassProtectedOrDraftContainment()
    {
        var draft = _service.Create(Path.Combine(_temp, "alias-draft"), Manifest("studio.alias"));
        string alias = Path.Combine(_temp, "alias-to-draft");
        try { Directory.CreateSymbolicLink(alias, draft.RootDirectory); }
        catch (UnauthorizedAccessException) { return; }
        catch (PlatformNotSupportedException) { return; }
        catch (IOException) { return; }

        Assert.Throws<InvalidDataException>(() => _service.Export(draft, Path.Combine(alias, "escape.gtsprofilepack"), "1.0.0"));
        var protectedService = new DraftPackService([draft.RootDirectory]);
        Assert.Throws<InvalidDataException>(() => protectedService.Create(Path.Combine(alias, "child"), Manifest("studio.protected-alias")));
    }

    [Fact]
    public void ExistingArchiveFileSymlinkCannotRedirectExportIntoDraft()
    {
        var draft = _service.Create(Path.Combine(_temp, "file-alias-draft"), Manifest("studio.file-alias"));
        string manifest = Path.Combine(draft.RootDirectory, ProfilePackManager.ManifestFileName);
        string original = File.ReadAllText(manifest);
        string archiveAlias = Path.Combine(_temp, "redirect.gtsprofilepack");
        try { File.CreateSymbolicLink(archiveAlias, manifest); }
        catch (UnauthorizedAccessException) { return; }
        catch (PlatformNotSupportedException) { return; }
        catch (IOException) { return; }

        Assert.Throws<InvalidDataException>(() => _service.Export(draft, archiveAlias, "1.0.0"));
        Assert.Equal(original, File.ReadAllText(manifest));
    }

    [Fact]
    public void UnsafeIdsResolutionsAndPortablePathsAreRejected()
    {
        var draft = _service.Create(Path.Combine(_temp, "unsafe-fields"), Manifest("studio.unsafe"));
        string original = Path.Combine(draft.RootDirectory, "profiles", "studio.unsafe.starter.json");
        var profile = Json.Load<GameProfile>(original)!;
        profile.Id = "../unsafe";
        profile.Enabled = true;
        profile.SupportedResolutions = ["5K"];
        profile.Notes = "Local path C:" + "/Users/author/game";
        profile.Scenes[0].Id = "bad/scene";
        string renamed = Path.Combine(draft.RootDirectory, "profiles", "wrong-name.json");
        File.Move(original, renamed);
        Json.Save(renamed, profile);

        var result = _service.Validate(_service.Open(draft.RootDirectory), "1.0.0");

        Assert.Contains(result.Errors, i => i.Code == "profile.id");
        Assert.Contains(result.Errors, i => i.Code == "profile.filename");
        Assert.Contains(result.Errors, i => i.Code == "profile.resolutions");
        Assert.Contains(result.Errors, i => i.Code == "profile.machinePath");
        Assert.Contains(result.Errors, i => i.Code == "scene.id");
    }

    [Fact]
    public void RepositoryBundledPackCloneValidatesExportsAndInstalls()
    {
        string? root = FindRepositoryRoot();
        Assert.NotNull(root);
        string bundled = Path.Combine(root!, "profiles");
        var service = new DraftPackService([bundled]);
        var clone = service.Clone(bundled, Path.Combine(_temp, "real-bundled-clone"));

        var validation = service.Validate(clone, "99.0.0");
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors.Select(e => $"{e.Code}: {e.Message} ({e.RelativePath})")));

        string archive = Path.Combine(_temp, "real-bundled.gtsprofilepack");
        service.Export(clone, archive, "99.0.0");
        string installedRoot = Path.Combine(_temp, "real-installed", "profiles");
        var installed = new ProfilePackManager(installedRoot, "99.0.0").InstallArchive(archive);
        Assert.Equal("hardware-busters.verified", installed.Pack.Manifest.Id);
        Assert.NotEmpty(new ProfileManager(installedRoot).LoadAll());
    }

    private static string? FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "GpuTestSuite.sln")) && File.Exists(Path.Combine(directory.FullName, "profiles", "profile-pack.json")))
                return directory.FullName;
        return null;
    }

    private static ProfilePackManifest Manifest(string id) => new()
    {
        Id = id, Name = id, Version = "1.0.0", Publisher = "Test", MinimumEngineVersion = "0.1.0"
    };
}
