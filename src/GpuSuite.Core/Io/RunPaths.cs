using System.Text;

namespace GpuSuite.Core.Io;

/// <summary>
/// Deterministic, filesystem-safe folder layout:
/// Results/{gpu}/{game}/{scene}/{resolution}/run_{n}/...
/// Every raw artifact, summary and log for a run lives under its run folder.
/// </summary>
public sealed class RunPaths
{
    public string ResultsRoot { get; }
    public string GpuName { get; }

    public RunPaths(string resultsRoot, string gpuName)
    {
        ResultsRoot = resultsRoot;
        GpuName = gpuName;
    }

    public static string Sanitize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "unknown";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        var outp = sb.ToString().Trim('_', '.');
        return string.IsNullOrEmpty(outp) ? "unknown" : outp;
    }

    public string GpuDir => Path.Combine(ResultsRoot, Sanitize(GpuName));

    /// <summary>True when a variant id denotes the implicit profile-default model (no extra folder level).</summary>
    private static bool IsDefaultVariant(string? variant) =>
        string.IsNullOrWhiteSpace(variant) || variant.Equals("default", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Scene output dir. A non-default graphics variant ("extra model") inserts an extra segment between
    /// scene and resolution — Results/{gpu}/{game}/{scene}/{variant}/{res} — so e.g. DLSS-on and DLSS-off
    /// runs never collide. The default (no variant) keeps the original .../{scene}/{res} layout.
    /// </summary>
    public string SceneDir(string game, string scene, string resolution, string? variant = null) =>
        IsDefaultVariant(variant)
            ? Path.Combine(GpuDir, Sanitize(game), Sanitize(scene), Sanitize(resolution))
            : Path.Combine(GpuDir, Sanitize(game), Sanitize(scene), Sanitize(variant!), Sanitize(resolution));

    public string RunDir(string game, string scene, string resolution, int repeat, string? variant = null) =>
        Path.Combine(SceneDir(game, scene, resolution, variant), $"run_{repeat}");

    public string EnsureRunDir(string game, string scene, string resolution, int repeat, string? variant = null)
    {
        var dir = Path.GetFullPath(RunDir(game, scene, resolution, repeat, variant));
        var gpuRoot = Path.GetFullPath(GpuDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!dir.StartsWith(gpuRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to prepare run directory outside the GPU results root: {dir}");

        // A run_N folder is a replaceable attempt slot. Reusing it without clearing left old
        // health_incident/evidence files beside a new valid run and poisoned Bot Diagnostics.
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string EnsureSceneDir(string game, string scene, string resolution, string? variant = null)
    {
        var dir = SceneDir(game, scene, resolution, variant);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string ReportPath => Path.Combine(GpuDir, "report.html");
    public string SuiteResultJson => Path.Combine(GpuDir, "suite_result.json");
    /// <summary>Append-only snapshots of completed suites. The top-level suite_result.json remains the
    /// convenient "latest" pointer, while these snapshots prevent a targeted rerun from erasing the
    /// diagnostic/result history for every other game.</summary>
    public string SuiteHistoryDir => Path.Combine(GpuDir, "suite_history");
    public string SuiteHistoryJson(DateTime generatedUtc) => Path.Combine(SuiteHistoryDir,
        $"suite_result_{generatedUtc.ToUniversalTime():yyyyMMdd_HHmmss_fff}.json");
}
