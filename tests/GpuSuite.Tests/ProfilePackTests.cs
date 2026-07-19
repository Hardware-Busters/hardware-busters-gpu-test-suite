using System.IO.Compression;
using GpuSuite.Core.Io;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Automation;
using GpuSuite.Engine.Profiles;
using Xunit;

namespace GpuSuite.Tests;

public sealed class ProfilePackTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "GpuSuitePackTests", Guid.NewGuid().ToString("N"));

    public ProfilePackTests() => Directory.CreateDirectory(_temp);
    public void Dispose() { if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true); }

    [Fact]
    public void FlatProfilesRemainTheBundledOfficialPack()
    {
        string profiles = Path.Combine(_temp, "profiles");
        Directory.CreateDirectory(profiles);
        Json.Save(Path.Combine(profiles, "official-game.json"), Game("official-game"));

        var packs = new ProfilePackManager(profiles, "1.0.0").Discover();

        var pack = Assert.Single(packs);
        Assert.True(pack.IsBundled);
        Assert.True(pack.IsUsable);
        Assert.Equal("Hardware Busters verified", pack.TrustLabel);
        Assert.Equal(1, pack.ProfileCount);
        Assert.Single(new ProfileManager(profiles).LoadAll());
    }

    [Fact]
    public void ImportedPackCanBeDisabledAndReenabled()
    {
        string profiles = CreateOfficialRoot();
        string archive = CreateArchive("community.sample", "community-game");
        var manager = new ProfilePackManager(profiles, "1.0.0");

        manager.InstallArchive(archive);
        Assert.Contains(new ProfileManager(profiles).LoadAll(), p => p.Id == "community-game");

        manager.SetEnabled("community.sample", false);
        Assert.DoesNotContain(new ProfileManager(profiles).LoadAll(), p => p.Id == "community-game");

        manager.SetEnabled("community.sample", true);
        Assert.Contains(new ProfileManager(profiles).LoadAll(), p => p.Id == "community-game");
    }

    [Fact]
    public void PackBotResolvesItsOwnRouteAsset()
    {
        string profiles = CreateOfficialRoot();
        string archive = CreateArchive("community.routes", "route-game", includeBot: true);
        new ProfilePackManager(profiles, "1.0.0").InstallArchive(archive);
        _ = new ProfileManager(profiles).LoadAll();

        var bot = BotScriptLibrary.Resolve("community_route");

        Assert.NotNull(bot);
        string route = Assert.Single(bot!.Actions).RoutePath!;
        Assert.True(Path.IsPathRooted(route));
        Assert.True(File.Exists(route));
        Assert.Contains(Path.Combine("packs", "community.routes", "routes"), route, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConflictingProfileIdRejectsTheImportedPackWithoutOverridingOfficial()
    {
        string profiles = CreateOfficialRoot("same-game");
        string archive = CreateArchive("community.conflict", "same-game");
        var manager = new ProfilePackManager(profiles, "1.0.0");
        var error = Assert.Throws<InvalidDataException>(() => manager.InstallArchive(archive));

        Assert.Contains("conflicts", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(manager.Discover(), p => p.Manifest.Id == "community.conflict");
        Assert.Single(new ProfileManager(profiles).LoadAll(), p => p.Id == "same-game");
    }

    [Fact]
    public void ArchivePathTraversalIsRejected()
    {
        string profiles = CreateOfficialRoot();
        string archivePath = Path.Combine(_temp, "unsafe.gtsprofilepack");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry(ProfilePackManager.ManifestFileName);
            using (var writer = new StreamWriter(manifest.Open())) writer.Write(Json.ToString(Manifest("unsafe.pack")));
            var evil = archive.CreateEntry("../escaped.txt");
            using var evilWriter = new StreamWriter(evil.Open());
            evilWriter.Write("nope");
        }

        Assert.Throws<InvalidDataException>(() => new ProfilePackManager(profiles, "1.0.0").InstallArchive(archivePath));
        Assert.False(File.Exists(Path.Combine(_temp, "escaped.txt")));
    }

    [Fact]
    public void ExportedPackCanBeInstalledIntoAnotherWorkspace()
    {
        string source = CreateOfficialRoot();
        var sourceManager = new ProfilePackManager(source, "1.0.0");
        sourceManager.InstallArchive(CreateArchive("community.export", "exported-game"));
        string exported = Path.Combine(_temp, "roundtrip.gtsprofilepack");
        sourceManager.ExportArchive("community.export", exported);

        string destination = Path.Combine(_temp, "destination", "profiles");
        Directory.CreateDirectory(destination);
        var installed = new ProfilePackManager(destination, "1.0.0").InstallArchive(exported);

        Assert.Equal("community.export", installed.Pack.Manifest.Id);
        Assert.Contains(new ProfileManager(destination).LoadAll(), p => p.Id == "exported-game");
    }

    [Fact]
    public void ValidatedReplacementAtomicallyUpdatesAnInstalledPack()
    {
        string profiles = CreateOfficialRoot();
        var manager = new ProfilePackManager(profiles, "1.0.0");
        manager.InstallArchive(CreateArchive("community.update", "old-game"));

        var result = manager.InstallArchive(CreateArchive("community.update", "new-game"), replace: true);

        Assert.True(result.Replaced);
        var loaded = new ProfileManager(profiles).LoadAll();
        Assert.DoesNotContain(loaded, p => p.Id == "old-game");
        Assert.Contains(loaded, p => p.Id == "new-game");
        Assert.DoesNotContain(Directory.EnumerateDirectories(manager.ImportedPacksRoot),
            p => Path.GetFileName(p).Contains(".backup-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NewerMinimumEngineVersionIsNotActivated()
    {
        string profiles = CreateOfficialRoot();
        string archive = CreateArchive("community.future", "future-game", minimumEngineVersion: "99.0.0");

        Assert.Throws<InvalidDataException>(() => new ProfilePackManager(profiles, "1.0.0").InstallArchive(archive));
        Assert.DoesNotContain(new ProfileManager(profiles).LoadAll(), p => p.Id == "future-game");
    }

    private string CreateOfficialRoot(string gameId = "official-game")
    {
        string root = Path.Combine(_temp, Guid.NewGuid().ToString("N"), "profiles");
        Directory.CreateDirectory(root);
        Json.Save(Path.Combine(root, gameId + ".json"), Game(gameId));
        return root;
    }

    private string CreateArchive(string packId, string gameId, bool includeBot = false, string minimumEngineVersion = "0.1.0")
    {
        string archivePath = Path.Combine(_temp, packId + Guid.NewGuid().ToString("N") + ProfilePackManager.ArchiveExtension);
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        WriteEntry(archive, ProfilePackManager.ManifestFileName, Json.ToString(Manifest(packId, gameId, minimumEngineVersion)));
        var game = Game(gameId);
        if (includeBot) game.Scenes[0].BotScript = "community_route";
        WriteEntry(archive, $"profiles/{gameId}.json", Json.ToString(game));
        if (includeBot)
        {
            var bot = new BotScript
            {
                Id = "community_route", Loop = false,
                Actions = [new BotAction { Type = BotActionType.ReplayRoute, RoutePath = "routes/community.route.json" }]
            };
            WriteEntry(archive, "bots/community_route.json", Json.ToString(bot));
            WriteEntry(archive, "routes/community.route.json", "{\"steps\":[]}");
        }
        return archivePath;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static ProfilePackManifest Manifest(string id, string game = "game", string minimum = "0.1.0") => new()
    {
        Id = id, Name = id, Version = "1.0.0", Publisher = "Test publisher",
        MinimumEngineVersion = minimum, Games = [game]
    };

    private static GameProfile Game(string id) => new()
    {
        Id = id,
        Name = id,
        Enabled = true,
        CaptureProcessName = "Game.exe",
        Launch = new LaunchSpec { Store = "Standalone" },
        Scenes = [new SceneProfile { Id = "scene", Name = "Scene", Kind = SceneKind.BotDriven, CaptureSeconds = 35 }]
    };
}
