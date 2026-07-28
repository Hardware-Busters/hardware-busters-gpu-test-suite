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
/// hardware, and every game profile. It starts required store clients so login/update state can refresh, but never
/// arms RTSS's global hook, launches a game,
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
        var framePlan = FrameCapturePreflightPlan.Build(profiles, cfg.FrameProvider);

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
        var doctor = await new DoctorService(cfg, log).RunAsync(cfg.NavSupervisorVisionModel, ct, framePlan).ConfigureAwait(false);
        var machine = new PreflightGroup
        {
            Id = "machine",
            Name = "Machine & services",
            Description = "Runtime, FPS/frametime tools, FFmpeg vision input, qualified capture card, controller driver, and AI services.",
            Checks = BuildMachineChecks(doctor)
        };

        progress?.Report(new FullPreflightProgress(4, totalSteps,
            "Probing the live benchmark bench", "Checking FPS/frametime readiness, telemetry, power, temperatures, storage, and vision."));
        bool usesRtss = framePlan.RequiresRtss;
        using var factory = new MeasurementFactory(cfg);
        // Full pre-flight must not arm RTSS's global hook. Doctor verifies installation/readiness; the
        // runtime still starts RTSS only at a compatible game boundary.
        factory.ProbeAll(usesRtss, allowRtssStartup: false);
        bool rtssReadyForLazyStart = doctor.Any(c =>
            c.Component.StartsWith("RTSS / RivaTuner", StringComparison.OrdinalIgnoreCase) &&
            c.Status == DoctorStatus.Ok);
        var hardware = new HardwareValidator(cfg, log).Validate(factory, framePlan, rtssReadyForLazyStart);
        var bench = new PreflightGroup
        {
            Id = "bench",
            Name = "Live benchmark bench",
            Description = "GPU identity, FPS/frametime backend, telemetry, power, disk, temperature, and vision endpoint.",
            // The machine group starts with the one authoritative FPS chain. Keep the runtime validator's
            // duplicate row out of this UI-only snapshot; it remains intact for every game at run time.
            Checks = hardware.Checks.Where(c => !c.Name.Equals("FPS / frametime capture", StringComparison.OrdinalIgnoreCase)).Select(c => new GameCheck(
                c.Name,
                c.Passed ? CheckStatus.Ok : c.Fatal ? CheckStatus.Blocker : CheckStatus.Warn,
                c.Detail,
                c.Passed ? null : c.Name.StartsWith("FPS / frametime", StringComparison.OrdinalIgnoreCase)
                    ? "Install/configure the listed FPS / frametime backend and re-run pre-flight. An override cannot create trustworthy frame data; runtime validation still rejects invalid runs."
                    : c.Fatal ? "Resolve this before starting the sweep." : "Benchmarking can continue using the documented fallback."))
                .ToList()
        };

        progress?.Report(new FullPreflightProgress(totalSteps, totalSteps,
            "Full pre-flight complete", "Preparing the readiness summary and detailed findings."));
        return new FullPreflightResult { Games = games, Groups = [machine, bench] };
    }, ct);

    /// <summary>Builds the operator-facing machine chain in a fixed order before ancillary doctor rows.</summary>
    internal static IReadOnlyList<GameCheck> BuildMachineChecks(IReadOnlyList<DoctorCheck> doctor)
    {
        DoctorCheck? presentMon = doctor.FirstOrDefault(c => c.Component.StartsWith("PresentMon", StringComparison.OrdinalIgnoreCase));
        DoctorCheck? rtss = doctor.FirstOrDefault(c => c.Component.StartsWith("RTSS / RivaTuner", StringComparison.OrdinalIgnoreCase));
        DoctorCheck? ffmpeg = doctor.FirstOrDefault(c => c.Component.StartsWith("FFmpeg", StringComparison.OrdinalIgnoreCase));
        DoctorCheck? cardModel = doctor.FirstOrDefault(c => c.Component.StartsWith("Capture-card model", StringComparison.OrdinalIgnoreCase));
        DoctorCheck? cardStream = doctor.FirstOrDefault(c => c.Component.StartsWith("Capture-card vision stream", StringComparison.OrdinalIgnoreCase));

        var result = new List<GameCheck>
        {
            Rename(presentMon, "FPS / frametime — PresentMon"),
            Rename(rtss, "FPS / frametime — RTSS"),
            Rename(ffmpeg, "Vision transport — FFmpeg"),
            MergeElgato(cardModel, cardStream)
        };

        result.AddRange(doctor.Where(c =>
                !ReferenceEquals(c, presentMon) && !ReferenceEquals(c, rtss) && !ReferenceEquals(c, ffmpeg) &&
                !ReferenceEquals(c, cardModel) && !ReferenceEquals(c, cardStream))
            .Select(MapDoctorCheck));
        return result;
    }

    private static GameCheck Rename(DoctorCheck? check, string name) => check is null
        ? new GameCheck(name, CheckStatus.Info, "Not reported by this pre-flight run.", null)
        : MapDoctorCheck(check) with { Name = name };

    private static GameCheck MergeElgato(DoctorCheck? model, DoctorCheck? stream)
    {
        var checks = new[] { model, stream }.Where(c => c is not null).Cast<DoctorCheck>().ToArray();
        if (checks.Length == 0)
            return new GameCheck("Vision hardware — Elgato Game Capture 4K Pro", CheckStatus.Info, "Not reported by this pre-flight run.", null);

        var mapped = checks.Select(MapDoctorCheck).ToArray();
        CheckStatus status = mapped.Select(c => c.Status).OrderByDescending(StatusRank).First();
        string detail = string.Join("  ", mapped.Select(c => c.Detail));
        string? hint = mapped.Select(c => c.Fix).FirstOrDefault(h => !string.IsNullOrWhiteSpace(h));
        return new GameCheck("Vision hardware — Elgato Game Capture 4K Pro", status, detail, hint);
    }

    private static int StatusRank(CheckStatus status) => status switch
    {
        CheckStatus.Blocker => 3,
        CheckStatus.Warn => 2,
        CheckStatus.Info => 1,
        _ => 0
    };

    internal static GameCheck MapDoctorCheck(DoctorCheck check)
    {
        var status = check.Status switch
        {
            DoctorStatus.Ok => CheckStatus.Ok,
            DoctorStatus.Missing => CheckStatus.Blocker,
            DoctorStatus.Warn => CheckStatus.Warn,
            DoctorStatus.Hardware when check.Component.StartsWith("Capture-card", StringComparison.OrdinalIgnoreCase)
                => CheckStatus.Blocker,
            DoctorStatus.Hardware => CheckStatus.Info,
            _ => CheckStatus.Info
        };

        // Vision/OCR bots cannot operate without ffmpeg even though the generic doctor permits degraded use.
        if (check.Component.StartsWith("FFmpeg", StringComparison.OrdinalIgnoreCase) && check.Status != DoctorStatus.Ok)
            status = CheckStatus.Blocker;

        return new GameCheck(check.Component, status, check.Detail, check.FixHint);
    }
}
