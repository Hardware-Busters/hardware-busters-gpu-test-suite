using GpuSuite.Core.Diagnostics;
using GpuSuite.Engine.Diagnostics;
using GpuSuite.Engine.Discovery;
using GpuSuite.Engine.Validation;
using GpuSuite.Measurement;

namespace GpuSuite.App.Services;

/// <summary>One non-game section in the full pre-flight report.</summary>
public sealed class PreflightGroup
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<GameCheck> Checks { get; init; }

    public CheckStatus Overall => Checks.Any(c => c.Status == CheckStatus.Blocker) ? CheckStatus.Blocker
        : Checks.Any(c => c.Status == CheckStatus.Warn) ? CheckStatus.Warn
        : CheckStatus.Ok;
    public int PassedCount => Checks.Count(c => c.Status is CheckStatus.Ok or CheckStatus.Info or CheckStatus.Skip);
    public int WarningCount => Checks.Count(c => c.Status == CheckStatus.Warn);
    public int BlockerCount => Checks.Count(c => c.Status == CheckStatus.Blocker);
    public string Summary => $"{PassedCount}/{Checks.Count} clear" +
        (WarningCount > 0 ? $" · {WarningCount} warning(s)" : "") +
        (BlockerCount > 0 ? $" · {BlockerCount} blocked" : "");
}

/// <summary>Combined machine, live-bench, and game readiness result shown by the Run page.</summary>
public sealed class FullPreflightResult
{
    public required GameReadinessReport Games { get; init; }
    public required IReadOnlyList<PreflightGroup> Groups { get; init; }

    public int WarningCount => Games.WarnCount + Groups.Sum(g => g.WarningCount);
    public int BlockerCount => Games.BlockerCount + Groups.Sum(g => g.BlockerCount);
    public CheckStatus Overall => BlockerCount > 0 ? CheckStatus.Blocker
        : WarningCount > 0 ? CheckStatus.Warn
        : CheckStatus.Ok;
}

/// <summary>One visible stage in the launch-clearance pass.</summary>
public sealed record FullPreflightProgress(int Step, int TotalSteps, string Headline, string Detail);

/// <summary>
/// Runs the complete launch-nothing clearance pass used by the Run page: software/services, live measurement
/// hardware, and every game profile. It starts required store clients so login/update state can refresh and may
/// start RTSS/Ollama's normal probe path, but never launches a game,
/// edits a profile, installs software, or changes benchmark settings.
/// </summary>
public sealed class FullPreflightService
{
    private readonly Workspace _ws;
    private readonly ProfileService _profiles;

    public FullPreflightService(Workspace ws, ProfileService profiles)
    {
        _ws = ws;
        _profiles = profiles;
    }

    public Task<FullPreflightResult> RunAsync(
        IProgress<FullPreflightProgress>? progress = null,
        CancellationToken ct = default) => Task.Run(async () =>
    {
        const int totalSteps = 5;
        var cfg = _ws.Config;
        progress?.Report(new FullPreflightProgress(0, totalSteps,
            "Preparing full pre-flight", "Loading the complete benchmark profile inventory."));
        var profiles = _profiles.LoadAll();

        progress?.Report(new FullPreflightProgress(1, totalSteps,
            "Checking launcher sessions", "Starting required store clients and refreshing login/update state."));
        await LauncherSessionPrimer.EnsureRunningAsync(profiles, ct).ConfigureAwait(false);

        progress?.Report(new FullPreflightProgress(2, totalSteps,
            "Checking installed games", "Discovering launcher libraries, installations, updates, and automation coverage."));
        var catalog = new LauncherDiscovery().DiscoverAll();
        var games = new GameReadinessChecker(_ws.ProfilesDir).Check(profiles, catalog);

        progress?.Report(new FullPreflightProgress(3, totalSteps,
            "Checking machine services", "Verifying runtimes, capture tools, controller support, and AI services."));
        using var log = new RunLogger(null, echoToConsole: false);
        var doctor = await new DoctorService(cfg, log).RunAsync(cfg.NavSupervisorVisionModel, ct).ConfigureAwait(false);
        var machine = new PreflightGroup
        {
            Id = "machine",
            Name = "Machine & services",
            Description = "Runtime, capture tools, controller driver, capture card, and AI services.",
            Checks = doctor.Select(MapDoctorCheck).ToList()
        };

        progress?.Report(new FullPreflightProgress(4, totalSteps,
            "Probing the live benchmark bench", "Checking frame capture, telemetry, power, temperatures, storage, and vision."));
        bool usesRtss = profiles.Where(p => p.Enabled)
            .Any(p => string.Equals(p.FrameProvider, "rtss", StringComparison.OrdinalIgnoreCase));
        using var factory = new MeasurementFactory(cfg);
        factory.ProbeAll(usesRtss);
        var hardware = new HardwareValidator(cfg, log).Validate(factory);
        var bench = new PreflightGroup
        {
            Id = "bench",
            Name = "Live benchmark bench",
            Description = "GPU identity, frame capture, telemetry, power, disk, temperature, and vision endpoint.",
            Checks = hardware.Checks.Select(c => new GameCheck(
                c.Name,
                c.Passed ? CheckStatus.Ok : c.Fatal ? CheckStatus.Blocker : CheckStatus.Warn,
                c.Detail,
                c.Passed ? null : c.Fatal ? "Resolve this before starting the sweep." : "Benchmarking can continue using the documented fallback."))
                .ToList()
        };

        progress?.Report(new FullPreflightProgress(totalSteps, totalSteps,
            "Full pre-flight complete", "Preparing the readiness summary and detailed findings."));
        return new FullPreflightResult { Games = games, Groups = [machine, bench] };
    }, ct);

    internal static GameCheck MapDoctorCheck(DoctorCheck check)
    {
        var status = check.Status switch
        {
            DoctorStatus.Ok => CheckStatus.Ok,
            DoctorStatus.Missing => CheckStatus.Blocker,
            DoctorStatus.Warn => CheckStatus.Warn,
            DoctorStatus.Hardware when check.Component.StartsWith("Capture card", StringComparison.OrdinalIgnoreCase)
                => CheckStatus.Blocker,
            DoctorStatus.Hardware => CheckStatus.Info,
            _ => CheckStatus.Info
        };

        // Vision/OCR bots cannot operate without ffmpeg even though the generic doctor permits degraded use.
        if (check.Component.StartsWith("ffmpeg", StringComparison.OrdinalIgnoreCase) && check.Status != DoctorStatus.Ok)
            status = CheckStatus.Blocker;

        return new GameCheck(check.Component, status, check.Detail, check.FixHint);
    }
}
