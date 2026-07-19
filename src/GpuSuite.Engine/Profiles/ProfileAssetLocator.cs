namespace GpuSuite.Engine.Profiles;

/// <summary>Resolves bot, route and template assets across the bundled profile root and active packs.</summary>
public static class ProfileAssetLocator
{
    private static readonly object Gate = new();
    private static string _profilesRoot = Path.GetFullPath("profiles");
    private static string[] _roots = [_profilesRoot];

    public static void Configure(string profilesRoot)
    {
        lock (Gate)
        {
            _profilesRoot = Path.GetFullPath(profilesRoot);
            try { _roots = new ProfilePackManager(_profilesRoot).GetActiveAssetRoots().ToArray(); }
            catch { _roots = [_profilesRoot]; }
        }
    }

    public static string? Find(string category, string fileName)
    {
        string[] roots;
        lock (Gate) roots = _roots.ToArray();
        foreach (string root in roots)
        {
            string candidate = Path.Combine(root, category, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static string ResolveReference(string reference, string? preferredRoot = null)
    {
        string expanded = Environment.ExpandEnvironmentVariables(reference);
        if (Path.IsPathRooted(expanded)) return expanded;

        string normalized = expanded.Replace('/', Path.DirectorySeparatorChar);
        string prefix = "profiles" + Path.DirectorySeparatorChar;
        string withoutProfiles = normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[prefix.Length..] : normalized;

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredRoot))
        {
            candidates.Add(Path.Combine(preferredRoot, normalized));
            candidates.Add(Path.Combine(preferredRoot, withoutProfiles));
        }
        string[] roots;
        lock (Gate) roots = _roots.ToArray();
        foreach (string root in roots)
        {
            candidates.Add(Path.Combine(root, normalized));
            candidates.Add(Path.Combine(root, withoutProfiles));
        }
        return candidates.FirstOrDefault(File.Exists) ?? expanded;
    }

    public static string RootForAsset(string assetPath)
    {
        string full = Path.GetFullPath(assetPath);
        string[] roots;
        lock (Gate) roots = _roots.ToArray();
        return roots.OrderByDescending(r => r.Length)
            .FirstOrDefault(r => full.StartsWith(Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) ?? _profilesRoot;
    }
}
