using System.IO;
using GpuSuite.Core.Io;
using GpuSuite.Core.Models;

namespace GpuSuite.App.Services;

/// <summary>One saved suite result set (per GPU folder): the loaded <see cref="SuiteResult"/> plus
/// the paths to its folder and HTML report.</summary>
public sealed record SuiteResultEntry(string GpuFolderName, string GpuDir, string JsonPath, string ReportPath, SuiteResult Result)
{
    public bool HasReport => !string.IsNullOrEmpty(ReportPath);
    public string DisplayLabel => $"{Result.GpuName} · {Result.GeneratedUtc:yyyy-MM-dd HH:mm} UTC" +
        (Result.RosterCompleted ? "" : " · partial");
}

/// <summary>
/// Loads past suite results from the workspace's Results root. Layout (see Core.Io.RunPaths):
/// Results/{gpu}/suite_result.json plus append-only suite_history snapshots. Read-only — scans +
/// deserializes off the UI thread and de-duplicates the latest pointer from its matching snapshot.
/// </summary>
public sealed class ResultsService
{
    private readonly Workspace _ws;
    public ResultsService(Workspace ws) => _ws = ws;

    public Task<IReadOnlyList<SuiteResultEntry>> LoadAllAsync() => Task.Run(() =>
    {
        var list = new List<SuiteResultEntry>();
        var root = _ws.ResultsDir;
        if (Directory.Exists(root))
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var current = Path.Combine(dir, "suite_result.json");
                var historyDir = Path.Combine(dir, "suite_history");
                var jsonPaths = new List<string>();
                if (File.Exists(current)) jsonPaths.Add(current);
                if (Directory.Exists(historyDir))
                    jsonPaths.AddRange(Directory.EnumerateFiles(historyDir, "suite_result_*.json", SearchOption.TopDirectoryOnly));

                foreach (var jsonPath in jsonPaths)
                {
                    var result = Json.Load<SuiteResult>(jsonPath);
                    if (result is null) continue;
                    InferLegacyPowerProvenance(result);
                    var report = string.Equals(jsonPath, current, StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(dir, "report.html") : "";
                    list.Add(new SuiteResultEntry(Path.GetFileName(dir), dir, jsonPath,
                        File.Exists(report) ? report : "", result));
                }
            }
        }
        // suite_result.json is also written to suite_history. Keep the current pointer when the two are
        // identical so "Open report" remains available, otherwise keep the archived snapshot.
        return (IReadOnlyList<SuiteResultEntry>)list
            .GroupBy(e => $"{e.GpuFolderName}|{e.Result.GeneratedUtc.Ticks}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(e => e.HasReport).First())
            .OrderByDescending(e => e.Result.GeneratedUtc)
            .ToList();
    });

    /// <summary>
    /// Old result JSON did not carry typed power provenance. Infer a conservative display classification
    /// in memory only: no legacy result can acquire Hardware Busters verified-power eligibility, and this
    /// loader never rewrites the user's artifact.
    /// </summary>
    private static void InferLegacyPowerProvenance(SuiteResult result)
    {
        foreach (var run in result.Aggregates.SelectMany(a => a.Runs))
        {
            run.Power.Measurement ??= PowerMeasurementMetadata.Unknown();
            if (run.Power.Measurement.Kind != PowerMeasurementKind.Unknown)
            {
                if (!PowerProvenance.IsDirectEfficiencyEligible(run.Power.Measurement))
                {
                    run.Power.GpuEnergyJoules = null;
                    run.Power.EnergyPerFrameJ = null;
                }
                continue;
            }

            string provider = run.PowerProviderName ?? "";
            var inferred = new PowerMeasurementMetadata
            {
                LegacyInferred = true,
                HardwareBustersVerifiedPowerEligible = false,
                QualificationNote = "Legacy result: provenance inferred from provider name; not Hardware Busters verified."
            };
            if (provider.Contains("powenetics", StringComparison.OrdinalIgnoreCase))
            {
                inferred.Kind = PowerMeasurementKind.PoweneticsDirect;
                inferred.Scope = "PCIe slot + auxiliary GPU rails (legacy inference)";
                inferred.HasPerRailData = false;
            }
            else if (provider.Contains("lhm", StringComparison.OrdinalIgnoreCase) || provider.Contains("librehardwaremonitor", StringComparison.OrdinalIgnoreCase))
            {
                inferred.Kind = PowerMeasurementKind.GpuReportedTelemetry;
                inferred.Scope = "GPU-reported board-power telemetry (legacy inference)";
            }
            else if (provider.Contains("synthetic", StringComparison.OrdinalIgnoreCase))
            {
                inferred.Kind = PowerMeasurementKind.Synthetic;
                inferred.Scope = "Synthetic workload model (legacy inference)";
            }
            else if (provider.Contains("replay", StringComparison.OrdinalIgnoreCase))
            {
                inferred.Kind = PowerMeasurementKind.Replay;
                inferred.Scope = "Replay data (legacy inference)";
            }
            else
            {
                inferred.Kind = PowerMeasurementKind.Unknown;
                inferred.Scope = "Unknown legacy power source";
            }
            run.Power.Measurement = inferred;
            // A legacy JSON may contain historical derived efficiency numbers. They stay on disk
            // untouched, but must not be surfaced as verified/publishable values in this session.
            run.Power.GpuEnergyJoules = null;
            run.Power.EnergyPerFrameJ = null;
        }

        foreach (var aggregate in result.Aggregates)
        {
            var measurements = aggregate.Runs.Select(r => r.Power.Measurement).ToList();
            bool compatible = PowerProvenance.AreCompatible(measurements);
            bool directEligible = compatible && measurements.Count > 0 && PowerProvenance.IsDirectEfficiencyEligible(measurements[0]);
            if (!directEligible)
            {
                aggregate.EnergyPerFrameJ = null;
                aggregate.FpsPerWatt = null;
            }
            if (!compatible)
            {
                aggregate.AvgGpuPowerW = null;
                aggregate.PeakGpuPowerW = null;
                aggregate.PowerComparisonNote = "Power and efficiency withheld: valid repeats use incompatible power provenance.";
                aggregate.PowerMeasurement = PowerMeasurementMetadata.Unknown(aggregate.PowerComparisonNote);
            }
            else if (aggregate.Runs.Count > 0)
                aggregate.PowerMeasurement = aggregate.Runs[0].Power.Measurement;
        }
    }
}
