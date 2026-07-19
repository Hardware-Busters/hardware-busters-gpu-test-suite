using System.Text.Json.Nodes;
using GpuSuite.Authoring;
using GpuSuite.Core.Io;
using GpuSuite.Engine.Automation;
using GpuSuite.Engine.Profiles;
using Xunit;

namespace GpuSuite.Tests;

public sealed class DraftDocumentServiceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "GpuSuiteAuthoringDocuments", Guid.NewGuid().ToString("N"));
    private readonly DraftPackService _service = new();
    public DraftDocumentServiceTests() => Directory.CreateDirectory(_temp);
    public void Dispose() { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); }

    [Fact]
    public void StructuredProfileEditsPersistAndPreserveUnknownMembers()
    {
        var draft = CreateDraft("studio.profileedit");
        var initial = _service.GetProfiles(draft).Single();
        initial.Raw["publisherExtension"] = "keep";
        initial.Raw["scenes"]![0]!["futureSceneField"] = true;
        initial.Raw["settings"] = new JsonArray(new JsonObject { ["key"] = "quality", ["label"] = "High", ["default"] = "High", ["apply"] = new JsonObject { ["method"] = "none" }, ["futureSettingField"] = 42 });
        initial.Raw["resolutionApply"]!["edits"] = new JsonArray(new JsonObject { ["pattern"] = "old", ["replacement"] = "old", ["futureEditField"] = "keep-nested" });
        Json.Save(initial.FilePath, initial.Raw);

        var document = _service.GetProfiles(draft).Single();
        document.Profile.Scenes[0].CaptureSeconds = 60;
        document.Profile.Settings.Single().Label = "Ultra";
        document.Profile.Variants.Add(new() { Id = "native", Name = "Native" });
        document.Profile.ResolutionApply.Edits[0].Replacement = "new";
        _service.SaveProfile(draft, document);

        var raw = JsonNode.Parse(File.ReadAllText(document.FilePath))!.AsObject();
        Assert.Equal("keep", raw["publisherExtension"]!.GetValue<string>());
        Assert.True(raw["scenes"]![0]!["futureSceneField"]!.GetValue<bool>());
        Assert.Equal("Ultra", raw["settings"]![0]!["label"]!.GetValue<string>());
        Assert.Equal(42, raw["settings"]![0]!["futureSettingField"]!.GetValue<int>());
        Assert.Equal("native", raw["variants"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("new", raw["resolutionApply"]!["edits"]![0]!["replacement"]!.GetValue<string>());
        Assert.Equal("keep-nested", raw["resolutionApply"]!["edits"]![0]!["futureEditField"]!.GetValue<string>());
    }

    [Fact]
    public void IdCreationUsesPortableContentPattern()
    {
        var draft = CreateDraft("studio.ids");
        Assert.Throws<InvalidDataException>(() => _service.CreateProfile(draft, "../escape"));
        Assert.Throws<InvalidDataException>(() => _service.CreateBot(draft, "bot with spaces"));
        Assert.Equal("Uppercase_1", _service.CreateProfile(draft, "Uppercase_1").Profile.Id);
        Assert.Equal("valid.bot-1", _service.CreateBot(draft, "valid.bot-1").Bot.Id);
    }

    [Fact]
    public void RenameRejectsTraversalIdsAndPreservesOriginalFiles()
    {
        var draft = CreateDraft("studio.rename-safety");
        var profile = _service.GetProfiles(draft).Single();
        string profilePath = profile.FilePath;
        profile.Profile.Id = "../escaped-profile";
        Assert.Throws<InvalidDataException>(() => _service.SaveProfile(draft, profile));
        Assert.True(File.Exists(profilePath));

        var bot = _service.CreateBot(draft, "safe-bot");
        string botPath = bot.FilePath;
        bot.Bot.Id = "../escaped-bot";
        Assert.Throws<InvalidDataException>(() => _service.SaveBot(draft, bot));
        Assert.True(File.Exists(botPath));
    }

    [Fact]
    public void RenamingNestedRowsKeepsTheirUnknownMembers()
    {
        var draft = CreateDraft("studio.nested-rename");
        var initial = _service.GetProfiles(draft).Single();
        initial.Raw["scenes"]![0]!["futureSceneField"] = "keep";
        Json.Save(initial.FilePath, initial.Raw);

        var document = _service.GetProfiles(draft).Single();
        document.Profile.Scenes[0].Id = "renamed-scene";
        _service.SaveProfile(draft, document);

        var raw = JsonNode.Parse(File.ReadAllText(document.FilePath))!.AsObject();
        Assert.Equal("renamed-scene", raw["scenes"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("keep", raw["scenes"]![0]!["futureSceneField"]!.GetValue<string>());
    }

    [Fact]
    public void RemoveThenRenameKeepsUnknownMembersWithTheSurvivingRow()
    {
        var draft = CreateDraft("studio.remove-rename");
        var initial = _service.GetProfiles(draft).Single();
        initial.Raw["scenes"] = new JsonArray(
            new JsonObject { ["id"] = "scene-a", ["name"] = "A", ["future"] = "from-a" },
            new JsonObject { ["id"] = "scene-b", ["name"] = "B", ["future"] = "from-b" });
        Json.Save(initial.FilePath, initial.Raw);

        var document = _service.GetProfiles(draft).Single();
        document.Profile.Scenes.RemoveAt(0);
        document.Profile.Scenes[0].Id = "scene-c";
        _service.SaveProfile(draft, document);

        var raw = JsonNode.Parse(File.ReadAllText(document.FilePath))!.AsObject();
        Assert.Single(raw["scenes"]!.AsArray());
        Assert.Equal("scene-c", raw["scenes"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("from-b", raw["scenes"]![0]!["future"]!.GetValue<string>());
    }

    [Fact]
    public void BotRenameReorderAndDeleteGuardPreserveGraphAndActionExtensions()
    {
        var draft = CreateDraft("studio.botedit");
        var profile = _service.GetProfiles(draft).Single();
        profile.Profile.SettingsBotScript = "nav-bot";
        _service.SaveProfile(draft, profile);
        var bot = _service.CreateBot(draft, "nav-bot");
        bot.Raw["graph"] = JsonNode.Parse("{\"customGraph\":{\"keep\":true}}");
        var first = _service.AddBotAction(bot, BotActionType.Wait);
        first.Action.Note = "first";
        first.Raw["futureActionField"] = "keep";
        var second = _service.AddBotAction(bot, BotActionType.KeyTap);
        second.Action.Key = "Space";
        _service.MoveBotAction(bot, 1, -1);
        bot.Bot.Id = "nav-bot-renamed";
        var saved = _service.SaveBot(draft, bot);

        Assert.False(File.Exists(Path.Combine(draft.RootDirectory, "bots", "nav-bot.json")));
        Assert.True(File.Exists(Path.Combine(draft.RootDirectory, "bots", "nav-bot-renamed.json")));
        Assert.Equal("nav-bot-renamed", _service.GetProfiles(draft).Single().Profile.SettingsBotScript);
        var raw = JsonNode.Parse(File.ReadAllText(saved.FilePath))!.AsObject();
        Assert.True(raw["graph"]!["customGraph"]!["keep"]!.GetValue<bool>());
        Assert.Equal("keep", raw["actions"]![1]!["futureActionField"]!.GetValue<string>());
        Assert.Equal("Space", raw["actions"]![0]!["key"]!.GetValue<string>());

        var guard = _service.CanDeleteBot(draft, saved);
        Assert.False(guard.CanDelete);
        Assert.Throws<InvalidOperationException>(() => _service.DeleteBot(draft, saved));
        var updatedProfile = _service.GetProfiles(draft).Single();
        updatedProfile.Profile.SettingsBotScript = null;
        _service.SaveProfile(draft, updatedProfile);
        _service.DeleteBot(draft, saved);
        Assert.Empty(_service.GetBots(draft));
    }

    [Fact]
    public void UnsavedBotRenameStillChecksReferencesToSavedIdBeforeDelete()
    {
        var draft = CreateDraft("studio.delete-guard");
        var profile = _service.GetProfiles(draft).Single();
        profile.Profile.SettingsBotScript = "saved-bot";
        _service.SaveProfile(draft, profile);
        var bot = _service.CreateBot(draft, "saved-bot");
        bot.Bot.Id = "unsaved-new-id";

        var guard = _service.CanDeleteBot(draft, bot);

        Assert.False(guard.CanDelete);
        Assert.Contains("studio.delete-guard.starter", guard.References);
        Assert.Throws<InvalidOperationException>(() => _service.DeleteBot(draft, bot));
        Assert.True(File.Exists(Path.Combine(draft.RootDirectory, "bots", "saved-bot.json")));
    }

    [Fact]
    public void DuplicateProfileAndBotCreateIndependentDraftFiles()
    {
        var draft = CreateDraft("studio.duplicate");
        var profile = _service.GetProfiles(draft).Single();
        var profileCopy = _service.DuplicateProfile(draft, profile, "studio.duplicate.copy", "Copy");
        var bot = _service.CreateBot(draft, "copy-bot");
        var botCopy = _service.DuplicateBot(draft, bot, "copy-bot-2");

        Assert.Equal("Copy", profileCopy.Profile.Name);
        Assert.True(File.Exists(profileCopy.FilePath));
        Assert.True(File.Exists(botCopy.FilePath));
        Assert.Equal(2, _service.GetProfiles(draft).Count);
        Assert.Equal(2, _service.GetBots(draft).Count);
    }

    [Fact]
    public void CreateDoesNotOverwriteMismatchedExistingFilenames()
    {
        var draft = CreateDraft("studio.filename-collision");
        string profilePath = Path.Combine(draft.RootDirectory, "profiles", "claimed.json");
        string botPath = Path.Combine(draft.RootDirectory, "bots", "claimed-bot.json");
        File.WriteAllText(profilePath, "{\"id\":\"different-profile\"}");
        File.WriteAllText(botPath, "{\"id\":\"different-bot\",\"actions\":[]}");
        string profileOriginal = File.ReadAllText(profilePath);
        string botOriginal = File.ReadAllText(botPath);

        Assert.Throws<IOException>(() => _service.CreateProfile(draft, "claimed"));
        Assert.Throws<IOException>(() => _service.CreateBot(draft, "claimed-bot"));
        Assert.Equal(profileOriginal, File.ReadAllText(profilePath));
        Assert.Equal(botOriginal, File.ReadAllText(botPath));
    }

    [Fact]
    public void DirtyDetectionCoversTypedProfileAndBotTimelineEdits()
    {
        var draft = CreateDraft("studio.dirty");
        var profile = _service.GetProfiles(draft).Single();
        var bot = _service.CreateBot(draft, "dirty-bot");
        Assert.False(_service.HasUnsavedChanges(profile));
        Assert.False(_service.HasUnsavedChanges(bot));

        profile.Profile.Name = "Unsaved profile name";
        _service.AddBotAction(bot, BotActionType.Wait).Action.DurationMs = 2500;

        Assert.True(_service.HasUnsavedChanges(profile));
        Assert.True(_service.HasUnsavedChanges(bot));
        Assert.False(_service.HasUnsavedChanges(_service.SaveProfile(draft, profile)));
        Assert.False(_service.HasUnsavedChanges(_service.SaveBot(draft, bot)));
    }

    [Fact]
    public void DeleteRejectsAReparsePointInTheDocumentAncestors()
    {
        var draft = CreateDraft("studio.delete-reparse");
        var profile = _service.GetProfiles(draft).Single();
        string profiles = Path.Combine(draft.RootDirectory, "profiles");
        string originalProfiles = Path.Combine(draft.RootDirectory, "profiles-original");
        string external = Path.Combine(_temp, "external-profiles");
        Directory.Move(profiles, originalProfiles);
        Directory.CreateDirectory(external);
        File.Copy(Path.Combine(originalProfiles, Path.GetFileName(profile.FilePath)), Path.Combine(external, Path.GetFileName(profile.FilePath)));
        try { Directory.CreateSymbolicLink(profiles, external); }
        catch (UnauthorizedAccessException) { Directory.Move(originalProfiles, profiles); return; }
        catch (PlatformNotSupportedException) { Directory.Move(originalProfiles, profiles); return; }
        catch (IOException) { Directory.Move(originalProfiles, profiles); return; }

        try { Assert.Throws<InvalidDataException>(() => _service.DeleteProfile(draft, profile)); }
        finally
        {
            Directory.Delete(profiles);
            Directory.Move(originalProfiles, profiles);
        }
    }

    private DraftPackProject CreateDraft(string id) => _service.Create(Path.Combine(_temp, id), new ProfilePackManifest { Id = id, Name = id, Version = "1.0.0", MinimumEngineVersion = "0.1.0" });
}
