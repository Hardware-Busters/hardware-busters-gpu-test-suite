using System.Text.RegularExpressions;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Models;

namespace GpuSuite.Engine.Launch;

/// <summary>
/// Reads the render-settings fingerprint a profile declares (<see cref="GameProfile.SettingsFingerprintKeys"/>)
/// off the game's OWN config files, so every run records what the game was actually set to render — upscaler,
/// render resolution, frame generation. Without this, an "as-set" number silently mixes natives, upscales and
/// frame-generated presents in one column (the 2026-07-02 roster audit: AW2's 263 fps was DLSS-Performance +
/// multi-frame-gen ≈ 77 rendered, while Ratchet's 81 was true native 4K + RT).
///
/// Strictly READ-ONLY and best-effort: a missing file, unmatched pattern or bad regex records a marker value
/// for that key; nothing here ever throws or blocks a run. Each distinct file is read once per extraction.
/// </summary>
public static class SettingsFingerprinter
{
    public static (Dictionary<string, string>? Values, bool? FrameGenActive) Extract(GameProfile game, RunLogger? log)
    {
        if (game.SettingsFingerprintKeys.Count == 0) return (null, null);

        var values = new Dictionary<string, string>();
        bool? fgActive = null;
        var fileCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var k in game.SettingsFingerprintKeys)
        {
            string value;
            try
            {
                if (!string.IsNullOrWhiteSpace(k.RegistryKey))
                {
                    value = ReadRegistryValue(k);
                    if (k.IsFrameGen && !value.StartsWith('('))
                        fgActive = !k.FrameGenOffValues.Any(off => string.Equals(off, value, StringComparison.OrdinalIgnoreCase));
                    values[string.IsNullOrWhiteSpace(k.Label) ? $"key{values.Count}" : k.Label] =
                        k.ValueMap.TryGetValue(value, out var rm) ? rm : value;
                    continue;
                }

                var path = Environment.ExpandEnvironmentVariables(k.ConfigFilePath);
                if (!fileCache.TryGetValue(path, out var text))
                {
                    text = File.Exists(path) ? File.ReadAllText(path) : null;
                    fileCache[path] = text;
                }

                if (text is null) value = "(file missing)";
                else
                {
                    var m = Regex.Match(text, k.Pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
                    if (!m.Success) value = "(not found)";
                    else
                    {
                        var raw = (m.Groups.Count > 1 ? m.Groups[1].Value : m.Value).Trim();
                        value = k.ValueMap.TryGetValue(raw, out var mapped) ? mapped : raw;
                        if (k.IsFrameGen)
                            fgActive = !k.FrameGenOffValues.Any(off =>
                                string.Equals(off, raw, StringComparison.OrdinalIgnoreCase));
                    }
                }
            }
            catch (Exception ex) { value = $"(error: {ex.GetType().Name})"; }
            values[string.IsNullOrWhiteSpace(k.Label) ? $"key{values.Count}" : k.Label] = value;
        }

        log?.Info("Fingerprint",
            $"As-set render settings: {string.Join(", ", values.Select(p => $"{p.Key}={p.Value}"))}" +
            (fgActive == true ? "  ⚠ FRAME GEN ON — measured fps counts generated presents, not rendered frames." : ""));
        return (values, fgActive);
    }

    /// <summary>Read one registry-backed fingerprint value (raw, pre-map). Best-effort like the file path:
    /// a missing key/value records a marker, never throws.</summary>
    private static string ReadRegistryValue(FingerprintKey k)
    {
        int i = k.RegistryKey!.IndexOf('\\');
        if (i <= 0) return "(bad registry path)";
        var hive = k.RegistryKey[..i].ToUpperInvariant();
        var sub = k.RegistryKey[(i + 1)..];
        var root = hive switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => Microsoft.Win32.Registry.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => Microsoft.Win32.Registry.LocalMachine,
            _ => null
        };
        if (root is null) return "(bad registry hive)";
        using var key = root.OpenSubKey(sub, writable: false);
        if (key is null) return "(key missing)";
        var v = key.GetValue(k.RegistryValueName ?? "");
        return v switch
        {
            null => "(value missing)",
            int d => unchecked((uint)d).ToString(System.Globalization.CultureInfo.InvariantCulture),
            long q => q.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => v.ToString() ?? "(value missing)"
        };
    }
}
