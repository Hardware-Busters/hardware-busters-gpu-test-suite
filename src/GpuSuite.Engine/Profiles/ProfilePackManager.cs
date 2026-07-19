using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;
using GpuSuite.Core.Io;
using GpuSuite.Core.Models;

namespace GpuSuite.Engine.Profiles;

/// <summary>
/// Discovers, validates and manages data-only benchmark packs. The legacy flat profiles directory is
/// treated as the bundled official pack; imported packs live under profiles/packs/{pack-id} and are
/// never overwritten by normal application upgrades.
/// </summary>
public sealed class ProfilePackManager
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestFileName = "profile-pack.json";
    public const string StateFileName = "profile-packs.state.json";
    public const string ArchiveExtension = ".gtsprofilepack";

    private const int MaxArchiveEntries = 5000;
    private const long MaxArchiveBytes = 256L * 1024 * 1024;
    private const long MaxEntryBytes = 64L * 1024 * 1024;
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9.-]{1,79}$", RegexOptions.Compiled);

    private readonly string _profilesRoot;
    private readonly Version _engineVersion;

    public string ProfilesRoot => _profilesRoot;
    public string ImportedPacksRoot => Path.Combine(_profilesRoot, "packs");
    public string StatePath => Path.Combine(_profilesRoot, StateFileName);

    public ProfilePackManager(string profilesRoot, string? engineVersion = null)
    {
        _profilesRoot = Path.GetFullPath(profilesRoot);
        _engineVersion = ParseVersion(engineVersion)
                         ?? Assembly.GetEntryAssembly()?.GetName().Version
                         ?? typeof(ProfilePackManager).Assembly.GetName().Version
                         ?? new Version(0, 1, 0);
    }

    public IReadOnlyList<ProfilePackInfo> Discover()
    {
        var state = LoadState();
        var packs = new List<ProfilePackInfo> { ReadPack(_profilesRoot, isBundled: true, state) };

        if (Directory.Exists(ImportedPacksRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(ImportedPacksRoot).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (Path.GetFileName(directory).StartsWith(".", StringComparison.Ordinal)) continue;
                packs.Add(ReadPack(directory, isBundled: false, state));
            }
        }

        ValidateCrossPackCollisions(packs);
        return packs;
    }

    public IReadOnlyList<string> GetActiveProfileFiles()
        => Discover().Where(p => p.IsUsable).SelectMany(ProfileFiles).ToArray();

    public IReadOnlyList<string> GetActiveAssetRoots()
        => Discover().Where(p => p.IsUsable).Select(p => p.RootDirectory).ToArray();

    public ProfilePackInstallResult InstallArchive(string archivePath, bool replace = false)
    {
        if (!File.Exists(archivePath)) throw new FileNotFoundException("Profile-pack archive was not found.", archivePath);
        Directory.CreateDirectory(ImportedPacksRoot);

        string stagingParent = Path.Combine(Path.GetTempPath(), "GpuSuiteProfilePacks");
        Directory.CreateDirectory(stagingParent);
        string staging = Path.Combine(stagingParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        try
        {
            ExtractSafely(archivePath, staging);
            var staged = ValidateExtractedPack(staging);
            var manifest = staged.Manifest;
            ValidateCandidateCollisions(staged, manifest.Id);

            string destination = Path.Combine(ImportedPacksRoot, manifest.Id);
            bool exists = Directory.Exists(destination);
            if (exists && !replace)
                throw new IOException($"Profile pack '{manifest.Id}' is already installed. Choose Replace to update it.");

            string? backup = null;
            try
            {
                if (exists)
                {
                    backup = Path.Combine(ImportedPacksRoot,
                        "." + manifest.Id + ".backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
                    Directory.Move(destination, backup);
                }
                Directory.Move(staging, destination);
                staging = "";
            }
            catch
            {
                if (!Directory.Exists(destination) && backup is not null && Directory.Exists(backup))
                    Directory.Move(backup, destination);
                throw;
            }
            if (backup is not null && Directory.Exists(backup))
            {
                try { Directory.Delete(backup, recursive: true); }
                catch { /* The validated update is installed; a stale backup can be cleaned later. */ }
            }

            var installed = Discover().First(p => p.Manifest.Id.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase));
            ProfileAssetLocator.Configure(_profilesRoot);
            return new ProfilePackInstallResult(installed, exists);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(staging) && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>Validates an archive with the same safe extraction and pack checks used by import, without installing it or reconfiguring asset resolution.</summary>
    public ProfilePackArchiveValidationResult ValidateArchive(string archivePath)
    {
        if (!File.Exists(archivePath)) throw new FileNotFoundException("Profile-pack archive was not found.", archivePath);
        string parent = Path.Combine(Path.GetTempPath(), "GpuSuiteProfilePackValidation");
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            ExtractSafely(archivePath, staging);
            var pack = ValidateExtractedPack(staging);
            return new ProfilePackArchiveValidationResult(pack.Manifest, pack.Fingerprint, pack.ProfileCount, pack.BotCount, pack.RouteCount);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public void ExportArchive(string packId, string destinationPath)
    {
        var pack = Find(packId);
        string fullDestination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        if (File.Exists(fullDestination)) File.Delete(fullDestination);

        using var archive = ZipFile.Open(fullDestination, ZipArchiveMode.Create);
        AddPackToArchive(archive, pack);
    }

    public void SetEnabled(string packId, bool enabled)
    {
        _ = Find(packId);
        var state = LoadState();
        state.Enabled[packId] = enabled;
        Json.Save(StatePath, state);
        ProfileAssetLocator.Configure(_profilesRoot);
    }

    public void Remove(string packId)
    {
        var pack = Find(packId);
        if (pack.IsBundled) throw new InvalidOperationException("The bundled Hardware Busters pack cannot be removed.");
        EnsureChildPath(ImportedPacksRoot, pack.RootDirectory);
        Directory.Delete(pack.RootDirectory, recursive: true);
        var state = LoadState();
        state.Enabled.Remove(packId);
        Json.Save(StatePath, state);
        ProfileAssetLocator.Configure(_profilesRoot);
    }

    private ProfilePackInfo Find(string packId)
        => Discover().FirstOrDefault(p => p.Manifest.Id.Equals(packId, StringComparison.OrdinalIgnoreCase))
           ?? throw new KeyNotFoundException($"Profile pack '{packId}' is not installed.");

    private ProfilePackInfo ValidateExtractedPack(string staging)
    {
        string manifestPath = Path.Combine(staging, ManifestFileName);
        if (!File.Exists(manifestPath)) throw new InvalidDataException($"The archive root must contain {ManifestFileName}.");
        var manifest = Json.Load<ProfilePackManifest>(manifestPath)
                       ?? throw new InvalidDataException("The profile-pack manifest is not valid JSON.");
        var staged = ReadPack(staging, isBundled: false, new ProfilePackState());
        if (staged.Errors.Count > 0 || !staged.IsCompatible)
            throw new InvalidDataException(string.Join(Environment.NewLine, staged.Errors.Concat(
                staged.IsCompatible ? Array.Empty<string>() : new[] { $"Requires engine {manifest.MinimumEngineVersion} or later." })));
        return staged;
    }

    private ProfilePackInfo ReadPack(string root, bool isBundled, ProfilePackState state)
    {
        string manifestPath = Path.Combine(root, ManifestFileName);
        ProfilePackManifest manifest;
        if (File.Exists(manifestPath))
        {
            try { manifest = Json.Load<ProfilePackManifest>(manifestPath) ?? new ProfilePackManifest(); }
            catch (Exception ex)
            {
                manifest = new ProfilePackManifest { Id = Path.GetFileName(root), Name = Path.GetFileName(root) };
                var bad = NewInfo(manifest, root, isBundled, state);
                bad.Errors.Add("Manifest cannot be read: " + ex.Message);
                return bad;
            }
        }
        else if (isBundled)
        {
            manifest = new ProfilePackManifest
            {
                Id = "hardware-busters.verified", Name = "Hardware Busters Verified Profiles",
                Version = (typeof(ProfilePackManager).Assembly.GetName().Version ?? new Version(0, 1, 0)).ToString(),
                Publisher = "Hardware Busters",
                Description = "Bundled, bench-validated profiles supplied with Hardware Busters GPU Test Suite."
            };
        }
        else
        {
            manifest = new ProfilePackManifest { Id = Path.GetFileName(root), Name = Path.GetFileName(root) };
        }

        var info = NewInfo(manifest, root, isBundled, state);
        Validate(info, manifestPath);
        info.ProfileCount = ProfileFiles(info).Count;
        info.BotCount = CountJson(Path.Combine(root, isBundled ? "bots" : Path.Combine("bots")));
        info.RouteCount = CountJson(Path.Combine(root, isBundled ? "routes" : Path.Combine("routes")));
        info.Fingerprint = ComputeFingerprint(root, isBundled);
        return info;
    }

    private ProfilePackInfo NewInfo(ProfilePackManifest manifest, string root, bool isBundled, ProfilePackState state)
    {
        bool enabled = !state.Enabled.TryGetValue(manifest.Id, out bool saved) || saved;
        return new ProfilePackInfo
        {
            Manifest = manifest,
            RootDirectory = Path.GetFullPath(root),
            IsBundled = isBundled,
            IsEnabled = enabled
        };
    }

    private void Validate(ProfilePackInfo info, string manifestPath)
    {
        var m = info.Manifest;
        if (!info.IsBundled && !File.Exists(manifestPath)) info.Errors.Add($"Missing {ManifestFileName}.");
        if (m.SchemaVersion != CurrentSchemaVersion) info.Errors.Add($"Unsupported schemaVersion {m.SchemaVersion}; expected {CurrentSchemaVersion}.");
        if (!IdPattern.IsMatch(m.Id ?? "")) info.Errors.Add("Pack id must be 2–80 lowercase letters, numbers, dots or hyphens.");
        if (string.IsNullOrWhiteSpace(m.Name)) info.Errors.Add("Pack name is required.");
        if (ParseVersion(m.Version) is null) info.Errors.Add($"Pack version '{m.Version}' is not a valid version.");
        var minimum = ParseVersion(m.MinimumEngineVersion);
        if (minimum is null) info.Errors.Add($"minimumEngineVersion '{m.MinimumEngineVersion}' is not valid.");
        else info.IsCompatible = _engineVersion >= minimum;

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in ProfileFiles(info))
        {
            try
            {
                var profile = Json.Load<GameProfile>(file);
                if (profile is null || string.IsNullOrWhiteSpace(profile.Id))
                    info.Errors.Add($"Profile '{Path.GetFileName(file)}' has no id.");
                else if (!ids.Add(profile.Id))
                    info.Errors.Add($"Profile id '{profile.Id}' is declared more than once in this pack.");
            }
            catch (Exception ex) { info.Errors.Add($"Profile '{Path.GetFileName(file)}' is invalid: {ex.Message}"); }
        }
        if (ProfileFiles(info).Count == 0) info.Errors.Add("Pack contains no game profiles.");
    }

    private static void ValidateCrossPackCollisions(List<ProfilePackInfo> packs)
    {
        var owners = new Dictionary<string, ProfilePackInfo>(StringComparer.OrdinalIgnoreCase);
        var botOwners = new Dictionary<string, ProfilePackInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in packs.OrderByDescending(p => p.IsBundled).ThenBy(p => p.Manifest.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (!pack.IsEnabled || !pack.IsCompatible || pack.Errors.Count > 0) continue;
            foreach (string file in ProfileFiles(pack))
            {
                GameProfile? profile;
                try { profile = Json.Load<GameProfile>(file); } catch { continue; }
                if (profile is null || string.IsNullOrWhiteSpace(profile.Id)) continue;
                if (owners.TryGetValue(profile.Id, out var owner))
                    pack.Errors.Add($"Profile id '{profile.Id}' conflicts with pack '{owner.Manifest.Name}'.");
                else owners[profile.Id] = pack;
            }
            foreach (string bot in BotFiles(pack))
            {
                string id = Path.GetFileNameWithoutExtension(bot);
                if (botOwners.TryGetValue(id, out var owner))
                    pack.Errors.Add($"Bot id '{id}' conflicts with pack '{owner.Manifest.Name}'.");
                else botOwners[id] = pack;
            }
        }
    }

    private void ValidateCandidateCollisions(ProfilePackInfo candidate, string replacingId)
    {
        var existing = Discover().Where(p => p.IsUsable && !p.Manifest.Id.Equals(replacingId, StringComparison.OrdinalIgnoreCase)).ToArray();
        var profileOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var botOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in existing)
        {
            foreach (string file in ProfileFiles(pack))
            {
                try
                {
                    string? id = Json.Load<GameProfile>(file)?.Id;
                    if (!string.IsNullOrWhiteSpace(id)) profileOwners[id] = pack.Manifest.Name;
                }
                catch { /* the installed pack's own validation reports it */ }
            }
            foreach (string bot in BotFiles(pack)) botOwners[Path.GetFileNameWithoutExtension(bot)] = pack.Manifest.Name;
        }

        var errors = new List<string>();
        foreach (string file in ProfileFiles(candidate))
        {
            string? id = Json.Load<GameProfile>(file)?.Id;
            if (!string.IsNullOrWhiteSpace(id) && profileOwners.TryGetValue(id, out string? owner))
                errors.Add($"Profile id '{id}' conflicts with pack '{owner}'.");
        }
        foreach (string bot in BotFiles(candidate))
        {
            string id = Path.GetFileNameWithoutExtension(bot);
            if (botOwners.TryGetValue(id, out string? owner)) errors.Add($"Bot id '{id}' conflicts with pack '{owner}'.");
        }
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }

    private static IReadOnlyList<string> ProfileFiles(ProfilePackInfo info)
    {
        string directory = info.IsBundled ? info.RootDirectory : Path.Combine(info.RootDirectory, "profiles");
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        return Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Where(f => !Path.GetFileName(f).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase)
                     && !Path.GetFileName(f).Equals(StateFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> BotFiles(ProfilePackInfo info)
    {
        string directory = Path.Combine(info.RootDirectory, "bots");
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray()
            : Array.Empty<string>();
    }

    private static int CountJson(string directory)
        => Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).Count() : 0;

    private ProfilePackState LoadState()
    {
        try { return Json.Load<ProfilePackState>(StatePath) ?? new ProfilePackState(); }
        catch { return new ProfilePackState(); }
    }

    private static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string clean = value.Trim().TrimStart('v', 'V');
        int suffix = clean.IndexOfAny(['-', '+']);
        if (suffix >= 0) clean = clean[..suffix];
        return Version.TryParse(clean, out var version) ? version : null;
    }

    private static string ComputeFingerprint(string root, bool isBundled)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        string importedRoot = Path.GetFullPath(Path.Combine(root, "packs")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(f => !Path.GetFileName(f).Equals(StateFileName, StringComparison.OrdinalIgnoreCase))
                     .Where(f => !isBundled || !Path.GetFullPath(f).StartsWith(importedRoot, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => Path.GetRelativePath(root, f), StringComparer.OrdinalIgnoreCase))
        {
            byte[] name = Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file).Replace('\\', '/').ToLowerInvariant());
            hash.AppendData(name);
            using var stream = File.OpenRead(file);
            byte[] buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset())[..16];
    }

    private static void ExtractSafely(string archivePath, string destination)
    {
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        int count = 0;
        long total = 0;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (++count > MaxArchiveEntries) throw new InvalidDataException("Profile pack contains too many files.");
            if (entry.Length > MaxEntryBytes || (total += entry.Length) > MaxArchiveBytes)
                throw new InvalidDataException("Profile pack exceeds the safe extraction size limit.");
            string output = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Profile pack contains an unsafe path.");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(output); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            entry.ExtractToFile(output, overwrite: false);
        }
    }

    private static void AddPackToArchive(ZipArchive archive, ProfilePackInfo pack)
    {
        string manifest = Path.Combine(pack.RootDirectory, ManifestFileName);
        if (File.Exists(manifest)) archive.CreateEntryFromFile(manifest, ManifestFileName, CompressionLevel.Optimal);
        else
        {
            var entry = archive.CreateEntry(ManifestFileName, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(Json.ToString(pack.Manifest));
        }

        if (pack.IsBundled)
        {
            foreach (string file in ProfileFiles(pack))
                archive.CreateEntryFromFile(file, "profiles/" + Path.GetFileName(file), CompressionLevel.Optimal);
            foreach (string folder in new[] { "bots", "routes", "templates" })
                AddDirectory(archive, Path.Combine(pack.RootDirectory, folder), folder);
        }
        else
        {
            foreach (string folder in new[] { "profiles", "bots", "routes", "templates", "assets" })
                AddDirectory(archive, Path.Combine(pack.RootDirectory, folder), folder);
        }
    }

    private static void AddDirectory(ZipArchive archive, string source, string archiveRoot)
    {
        if (!Directory.Exists(source)) return;
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            archive.CreateEntryFromFile(file, archiveRoot + "/" + relative, CompressionLevel.Optimal);
        }
    }

    private static void EnsureChildPath(string parent, string child)
    {
        string root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to modify a path outside the profile-pack directory.");
    }
}
