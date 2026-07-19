namespace GpuSuite.Core.Models;

/// <summary>One pre-run hardware check result. Fatal checks abort the affected benchmark; non-fatal ones warn.</summary>
public sealed class HardwareCheck
{
    public string Name { get; set; } = "";
    public bool Passed { get; set; }
    /// <summary>A failed Fatal check aborts the game; a failed non-fatal check is logged and the game proceeds.</summary>
    public bool Fatal { get; set; }
    public string Detail { get; set; } = "";

    public static HardwareCheck Pass(string name, string detail, bool fatal = true) => new() { Name = name, Passed = true, Fatal = fatal, Detail = detail };
    public static HardwareCheck Fail(string name, string detail, bool fatal = true) => new() { Name = name, Passed = false, Fatal = fatal, Detail = detail };
}

/// <summary>The aggregate result of the pre-game hardware validation gate.</summary>
public sealed class HardwareValidationResult
{
    public List<HardwareCheck> Checks { get; set; } = new();

    /// <summary>True when no FATAL check failed (non-fatal warnings don't block the benchmark).</summary>
    public bool Ok => Checks.All(c => c.Passed || !c.Fatal);
    public IEnumerable<HardwareCheck> FatalFailures => Checks.Where(c => !c.Passed && c.Fatal);
    public IEnumerable<HardwareCheck> Warnings => Checks.Where(c => !c.Passed && !c.Fatal);

    /// <summary>A clear, classified reason string for the aborted benchmark (joins the fatal failures).</summary>
    public string FailureReason => string.Join("; ", FatalFailures.Select(c => $"{c.Name}: {c.Detail}"));
    public string Summary => $"{Checks.Count(c => c.Passed)}/{Checks.Count} checks passed";
}

/// <summary>
/// Configuration for the pre-game hardware-validation gate (Milestone 2). A failed FATAL check aborts only
/// the affected benchmark (the game is classified <see cref="FailureClass.HardwarePrecheck"/> and skipped);
/// the roster continues. Set a field to its skip value (0 / null / false) to disable that check.
/// </summary>
public sealed class HardwareValidationConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Substring the detected GPU name must contain. Null = fall back to <see cref="SuiteConfig.PreferredGpu"/>; empty = skip.</summary>
    public string? ExpectedGpuModel { get; set; }
    /// <summary>Minimum total VRAM in GB (with a 1 GB tolerance). 0 = skip.</summary>
    public int ExpectedVramGb { get; set; }
    /// <summary>Minimum current PCIe link width (e.g. 16). 0 = skip. Non-fatal (a downshift warns).</summary>
    public int MinPcieWidth { get; set; }
    /// <summary>Minimum current PCIe link generation (e.g. 4 or 5). 0 = skip. Non-fatal.</summary>
    public int MinPcieGen { get; set; }
    /// <summary>Pre-run GPU-temperature ceiling in °C — a GPU already hotter than this aborts the benchmark.</summary>
    public double MaxGpuTempC { get; set; } = 87.0;
    /// <summary>Minimum free disk space (GB) on the results drive.</summary>
    public double MinFreeDiskGb { get; set; } = 5.0;

    public bool RequireMonitor { get; set; } = true;
    public bool RequirePresentMon { get; set; } = true;
    public bool RequireTelemetry { get; set; } = true;
    /// <summary>Require a live Powenetics PMD. Auto-forced when <see cref="SuiteConfig.PowerSource"/> == "powenetics".</summary>
    public bool RequirePowenetics { get; set; }
}
