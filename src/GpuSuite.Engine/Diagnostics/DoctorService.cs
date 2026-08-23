using System.Diagnostics;
using System.IO.Ports;
using GpuSuite.Core.Config;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Remote;
using GpuSuite.Core.Security;
using GpuSuite.Engine.Automation;
using GpuSuite.Engine.Profiles;
using GpuSuite.Engine.Vision;

namespace GpuSuite.Engine.Diagnostics;

/// <summary>Severity of a single environment check.</summary>
public enum DoctorStatus
{
    /// <summary>Present and working.</summary>
    Ok,
    /// <summary>Present-or-absent external HARDWARE (capture card, Powenetics PMD) — cannot be auto-installed;
    /// absence is reported but not treated as a failure (a bench may legitimately lack it).</summary>
    Hardware,
    /// <summary>A software dependency that is missing but OPTIONAL for the immediate task (degraded mode works).</summary>
    Warn,
    /// <summary>A required software dependency is missing — the suite cannot do its core job until it is installed.</summary>
    Missing,
}

/// <summary>
/// The component names <see cref="DoctorService"/> emits, as constants rather than repeated literals.
///
/// These are a CONTRACT, not labels: the App's full pre-flight screen looks rows up by name to build the
/// operator-facing measurement chain, and <c>FullPreflightService.MapDoctorCheck</c> escalates a
/// <see cref="DoctorStatus.Hardware"/> row to a hard Blocker only when its name starts with
/// <see cref="CaptureCardPrefix"/>. While both sides held their own string literals that coupling was
/// invisible and it failed OPEN — renaming a capture-card check would have quietly demoted an unqualified
/// or absent card from Blocker to Info, so a bench that cannot capture would report zero blockers.
/// Referencing these from both sides makes any rename a compile error instead.
/// </summary>
public static class DoctorComponents
{
    public const string PresentMon = "PresentMon (FPS / frametime)";
    public const string Rtss = "RTSS / RivaTuner (FPS / frametime fallback)";
    public const string Ffmpeg = "FFmpeg (capture-card vision)";
    public const string CaptureCardModel = CaptureCardPrefix + " model (full automation)";
    public const string CaptureCardStream = CaptureCardPrefix + " vision stream (FFmpeg)";

    /// <summary>Shared prefix of every capture-card row; drives the Hardware-to-Blocker escalation.</summary>
    public const string CaptureCardPrefix = "Capture-card";
}

/// <summary>One environment check result. <see cref="FixCommand"/>, when set, is a self-contained command line
/// (run via the OS shell) that installs/repairs the dependency — executed only by <c>doctor --fix</c>.</summary>
public sealed record DoctorCheck(
    string Component,
    DoctorStatus Status,
    string Detail,
    string? FixHint = null,
    string? FixCommand = null);

/// <summary>
/// "Is this machine ready to run the suite?" — probes every dependency the suite relies on (runtimes, the
/// frame-capture stack, the virtual gamepad, the local LLM + its model, and the optional bench hardware) and
/// reports each with a clear status + a fix. The <c>doctor --fix</c> path can auto-install the software ones
/// (ffmpeg, Ollama + model, ViGEmBus, the .NET runtime) via winget / ollama. Detection is READ-ONLY and never
/// touches firewall/security/system settings; only the explicit <c>--fix</c> run installs anything.
///
/// Designed for "run this on another machine": one command tells the operator exactly what to install.
/// </summary>
public sealed class DoctorService
{
    private readonly SuiteConfig _cfg;
    private readonly RunLogger _log;

    public DoctorService(SuiteConfig cfg, RunLogger log) { _cfg = cfg; _log = log; }

    /// <summary>Run every check. <paramref name="llmModel"/> is the Ollama model the nav supervisor expects.</summary>
    public async Task<List<DoctorCheck>> RunAsync(
        string llmModel,
        CancellationToken ct,
        FrameCapturePreflightPlan? framePlan = null)
    {
        // Full pre-flight supplies the enabled roster. The stand-alone doctor remains conservative and
        // derives a generic plan from the selected global backend.
        var plan = framePlan ?? GenericFramePlan(_cfg.FrameProvider);
        var presentMon = CheckPresentMon(plan);
        var rtss = CheckRtss(plan, presentMon.Status == DoctorStatus.Ok);
        var checks = new List<DoctorCheck>
        {
            await CheckDotnetRuntimeAsync(ct),
            await CheckFfmpegAsync(ct),
            presentMon,
            rtss,
            CheckViGEm(),
            await CheckOllamaAsync(llmModel, ct),
            await CheckGx10Async(ct),
            CheckPowenetics(),
            CheckProfiles(),
        };
        checks.AddRange(await CheckCaptureCardsAsync(ct));
        return checks;
    }

    // ---------------- individual checks ----------------

    private async Task<DoctorCheck> CheckDotnetRuntimeAsync(CancellationToken ct)
    {
        var (code, outp, _) = await RunProcAsync("dotnet", "--list-runtimes", 8000, ct);
        if (code != 0)
            // The CLI itself is running on .NET, so the runtime IS present even if the `dotnet` launcher isn't on PATH.
            return new(".NET 9 desktop runtime", DoctorStatus.Ok,
                "dotnet CLI not on PATH, but the suite is running — runtime present (likely self-contained or app-local).");
        bool has9 = outp.Split('\n').Any(l => l.Contains("Microsoft.WindowsDesktop.App 9.", StringComparison.OrdinalIgnoreCase));
        if (has9) return new(".NET 9 desktop runtime", DoctorStatus.Ok, "Microsoft.WindowsDesktop.App 9.x present.");
        return new(".NET 9 desktop runtime", DoctorStatus.Ok,
            "the suite is running, so a usable runtime is present; no standalone WindowsDesktop 9.x listed by `dotnet`.",
            FixHint: "If deploying to a fresh machine, install the .NET 9 Desktop Runtime.",
            FixCommand: "winget install --id Microsoft.DotNet.DesktopRuntime.9 -e --accept-source-agreements --accept-package-agreements");
    }

    /// <summary>
    /// One budget for every `ffmpeg -version` probe in this file. The two call sites used to differ
    /// (8000 ms here, 5000 ms in the capture-card check), so a cold or AV-scanned ffmpeg could pass the
    /// generous probe and fail the strict one — reporting "FFmpeg Ok" beside capture-card blockers that
    /// said FFmpeg was unavailable, in the same pass. Re-running warm made it "go away", which reads as
    /// flakiness rather than a fixed-budget artefact.
    /// </summary>
    private const int FfmpegProbeTimeoutMs = 8000;

    private async Task<DoctorCheck> CheckFfmpegAsync(CancellationToken ct)
    {
        string exe = ResolveFfmpegExe();
        var (code, outp, _) = await RunProcAsync(exe, "-version", FfmpegProbeTimeoutMs, ct);
        if (code == 0)
        {
            var first = outp.Split('\n').FirstOrDefault()?.Trim() ?? "ffmpeg";
            return new(DoctorComponents.Ffmpeg, DoctorStatus.Ok, first);
        }
        return new(DoctorComponents.Ffmpeg, DoctorStatus.Warn,
            $"not found ({(string.IsNullOrWhiteSpace(_cfg.FfmpegPath) ? "not on PATH" : ResolveFfmpegExe())}).",
            FixHint: "Required for the supported Elgato vision input: install ffmpeg or set settings.ffmpegPath, then re-run full pre-flight. This is separate from PresentMon/RTSS FPS measurement.",
            FixCommand: "winget install --id Gyan.FFmpeg -e --accept-source-agreements --accept-package-agreements");
    }

    private DoctorCheck CheckPresentMon(FrameCapturePreflightPlan plan)
    {
        var p = ResolveAppPath(_cfg.PresentMonPath);
        if (p is not null)
            return new(DoctorComponents.PresentMon, DoctorStatus.Ok, p);
        return new(DoctorComponents.PresentMon, plan.RequiresPresentMon ? DoctorStatus.Missing : DoctorStatus.Warn,
            $"not found at '{_cfg.PresentMonPath}'.",
            FixHint: plan.RequiresPresentMon
                ? "Required by the enabled roster (including RTSS-forbidden titles): copy tools/ alongside gpusuite.exe or set settings.presentMonPath. FPS / frametime validation will otherwise fail safe."
                : "No enabled title currently requires PresentMon, but it is the preferred FPS / frametime backend. Copy tools/ alongside gpusuite.exe or set settings.presentMonPath.");
    }

    /// <summary>
    /// Resolve a configured tool path the way the RUNTIME does — trimmed and with environment variables
    /// expanded (see <c>CaptureCardGrabber</c>'s constructor). Checking the raw string instead meant a path
    /// such as <c>%ProgramFiles%\...\RTSS.exe</c> failed here and produced a hard pre-flight blocker on a
    /// bench that would have run perfectly.
    /// </summary>
    private static string ResolveConfiguredPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "" : Environment.ExpandEnvironmentVariables(path.Trim());

    /// <summary>The ffmpeg to probe: the configured path resolved as the runtime resolves it, else PATH.</summary>
    private string ResolveFfmpegExe()
    {
        string configured = ResolveConfiguredPath(_cfg.FfmpegPath);
        return configured.Length > 0 ? configured : "ffmpeg";
    }

    private DoctorCheck CheckRtss(FrameCapturePreflightPlan plan, bool presentMonAvailable)
    {
        string configuredRtss = ResolveConfiguredPath(_cfg.RtssExePath);
        var candidates = new[]
        {
            configuredRtss,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RivaTuner Statistics Server", "RTSS.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RivaTuner Statistics Server", "RTSS.exe"),
        };
        var found = candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
        bool running = Process.GetProcessesByName("RTSS").Length > 0;
        bool configuredForLazyStart = configuredRtss.Length > 0 && File.Exists(configuredRtss);
        if (running || configuredForLazyStart)
            return new(DoctorComponents.Rtss, DoctorStatus.Ok,
                running ? "RTSS is running (it will still remain closed for RTSS-forbidden titles)." : $"configured for lazy start: {_cfg.RtssExePath} (full pre-flight does not start it).");
        // Do not let a global OSD setting turn RTSS into a requirement: it is a global hook and stays lazy
        // until a compatible game needs it. Ratchet-style profiles are covered by RequiresPresentMon.
        bool needed = plan.RequiresRtss || (plan.MayUseRtssFallback && !presentMonAvailable);
        string state = found is null ? "not installed / not running."
            : $"installed at {found}, but settings.rtssExePath is not configured and RTSS is not running.";
        return new(DoctorComponents.Rtss, needed ? DoctorStatus.Missing : DoctorStatus.Warn,
            state,
            FixHint: needed
                ? "Required by the enabled roster or its auto fallback: install RivaTuner Statistics Server and set settings.rtssExePath. It is started only at a compatible game boundary; do not use it to bypass an RTSS-forbidden profile."
                : "Not required by the enabled roster. Install/configure it only for RTSS-pinned games or the optional live OSD; full pre-flight will not start it.");
    }

    private DoctorCheck CheckViGEm()
    {
        string sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "ViGEmBus.sys");
        bool sysPresent = File.Exists(sys);
        try
        {
            using var pad = Gamepad.Create(_log);
            if (pad.Connected)
                return new("ViGEmBus (virtual gamepad)", DoctorStatus.Ok, "virtual Xbox 360 pad created — gamepad bots can drive RE-Engine / pad-only games.");
            return new("ViGEmBus (virtual gamepad)", DoctorStatus.Missing,
                $"driver {(sysPresent ? "present but a pad could not be created" : "not installed")} ({pad.Unavailable}).",
                FixHint: "Needed for pad-driven games (RE Requiem, Ratchet, Black Myth, Forza, MSFS). The installer needs Administrator (the suite will NOT self-elevate).",
                FixCommand: "winget install --id Nefarius.ViGEmBus -e --accept-source-agreements --accept-package-agreements");
        }
        catch (Exception ex)
        {
            return new("ViGEmBus (virtual gamepad)", DoctorStatus.Missing, $"probe error: {ex.Message}",
                FixHint: "Install ViGEmBus (Administrator required).",
                FixCommand: "winget install --id Nefarius.ViGEmBus -e --accept-source-agreements --accept-package-agreements");
        }
    }

    private async Task<DoctorCheck> CheckOllamaAsync(string model, CancellationToken ct)
    {
        var (vcode, _, _) = await RunProcAsync("ollama", "--version", 8000, ct);
        if (vcode != 0)
            return new($"Ollama + LLM model ({model})", DoctorStatus.Warn,
                "Ollama not installed — the smart-bot LLM nav supervisor falls back to deterministic recovery only.",
                FixHint: "Optional but recommended: the local model recovers smart-bot nav when it hits an unknown screen. Runs on CPU; no API key.",
                FixCommand: $"winget install --id Ollama.Ollama -e --accept-source-agreements --accept-package-agreements && ollama pull {model}");

        var (lcode, lout, lerr) = await RunProcAsync("ollama", "list", 12000, ct);
        if (lcode != 0)
            return new($"Ollama + LLM model ({model})", DoctorStatus.Warn,
                $"Ollama installed but the daemon did not respond ({(lerr.Trim().Length > 0 ? lerr.Trim() : "is `ollama serve` running?")}).",
                FixHint: "Start the Ollama service (it normally auto-starts), then re-run doctor.",
                FixCommand: $"ollama pull {model}");

        bool hasModel = lout.Split('\n').Any(l => LineHasModel(l, model));
        if (hasModel)
            return new($"Ollama + LLM model ({model})", DoctorStatus.Ok, $"installed and '{model}' is pulled.");
        return new($"Ollama + LLM model ({model})", DoctorStatus.Warn,
            $"Ollama installed but model '{model}' is not pulled.",
            FixHint: "Pull the model so the smart-bot LLM supervisor can engage (also via: gpusuite llm-update).",
            FixCommand: $"ollama pull {model}");
    }

    private async Task<DoctorCheck> CheckGx10Async(CancellationToken ct)
    {
        string endpoint = Gx10Client.Normalize(_cfg.Gx10Endpoint);
        string compute = (_cfg.VisionCompute ?? "auto").Trim().ToLowerInvariant();
        if (compute == "openai")
        {
            bool configured = !string.IsNullOrWhiteSpace(ApiKeyStore.ReadOrEnvironment("openai", "OPENAI_API_KEY"));
            return new("OpenAI vision API", configured ? DoctorStatus.Ok : DoctorStatus.Warn,
                configured ? $"configured for model '{_cfg.OpenAiVisionModel}' (key is kept in the Windows user environment)."
                           : "OPENAI_API_KEY is not set; the suite will fall back to local vision.",
                FixHint: configured ? null : "Set OPENAI_API_KEY in your Windows user environment, then restart GPU Test Suite.");
        }
        if (compute == "anthropic")
        {
            bool configured = !string.IsNullOrWhiteSpace(ApiKeyStore.ReadOrEnvironment("anthropic", "ANTHROPIC_API_KEY"));
            return new("Anthropic vision API", configured ? DoctorStatus.Ok : DoctorStatus.Warn,
                configured ? $"configured for model '{_cfg.AnthropicVisionModel}' (key is kept in the Windows user environment)."
                           : "ANTHROPIC_API_KEY is not set; the suite will fall back to local vision.",
                FixHint: configured ? null : "Set ANTHROPIC_API_KEY in your Windows user environment, then restart GPU Test Suite.");
        }
        var status = await new Gx10Client(_cfg.Gx10Endpoint).ProbeAsync(ct, 4000);
        if (status.Reachable)
        {
            bool hasVision = status.HasModel(_cfg.NavSupervisorVisionModel);
            return new($"Custom AI server ({endpoint})", hasVision ? DoctorStatus.Ok : DoctorStatus.Warn,
                $"{status.Summary()}" + (hasVision ? $" — vision model '{_cfg.NavSupervisorVisionModel}' present." : $" — but vision model '{_cfg.NavSupervisorVisionModel}' NOT pulled."),
                FixHint: hasVision ? null : $"Pull the vision model on the box so visionCompute=gx10 works: ollama pull {_cfg.NavSupervisorVisionModel}");
        }
        // Not reachable: only an issue when the operator asked for the GX10; auto can use an opted-in cloud
        // provider (or the local GPU) without making the private lab box a requirement.
        bool wanted = compute == "gx10";
        bool openAiFallback = !string.IsNullOrWhiteSpace(ApiKeyStore.ReadOrEnvironment("openai", "OPENAI_API_KEY"));
        bool anthropicFallback = !string.IsNullOrWhiteSpace(ApiKeyStore.ReadOrEnvironment("anthropic", "ANTHROPIC_API_KEY"));
        string fallback = openAiFallback ? "the OpenAI API will handle vision navigation"
            : anthropicFallback ? "the Anthropic API will handle vision navigation"
            : "the LOCAL GPU will handle the vision model";
        return new($"Custom AI server ({endpoint})", wanted ? DoctorStatus.Warn : DoctorStatus.Hardware,
            $"offline ({status.Error}) — {fallback}" + (wanted ? " (visionCompute=gx10 was requested)." : "."),
            FixHint: "Optional: bring a Custom AI server online (Ollama serving on the LAN) to run the vision model off-bench. Check it in Settings.");
    }

    private async Task<IReadOnlyList<DoctorCheck>> CheckCaptureCardsAsync(CancellationToken ct)
    {
        // Needs ffmpeg to enumerate DirectShow devices; skip cleanly if ffmpeg is absent.
        // Same executable resolution and the same budget as CheckFfmpegAsync — see FfmpegProbeTimeoutMs.
        string exe = ResolveFfmpegExe();
        var (probe, _, _) = await RunProcAsync(exe, "-version", FfmpegProbeTimeoutMs, ct);
        if (probe != 0)
            return
            [
                new(DoctorComponents.CaptureCardModel, DoctorStatus.Hardware,
                    "not checked — FFmpeg is unavailable, so DirectShow devices cannot be enumerated.",
                    "Install/configure FFmpeg first, then re-run full pre-flight. The supported workflow requires the exact Elgato 4K Pro."),
                new(DoctorComponents.CaptureCardStream, DoctorStatus.Hardware,
                    "not checked — FFmpeg is unavailable.",
                    "Install/configure FFmpeg first. This is the vision/OCR input, not the FPS / frametime backend.")
            ];
        try
        {
            var grabber = new CaptureCardGrabber(_cfg.FfmpegPath, _cfg.CaptureCardDevice, _log);
            var devices = await grabber.ListVideoDevicesAsync(ct);
            if (devices.Count == 0)
                return
                [
                    new(DoctorComponents.CaptureCardModel, DoctorStatus.Hardware,
                        "no DirectShow video devices found — is the Elgato Game Capture 4K Pro connected and powered?",
                        $"The full supported workflow requires the exact DirectShow name \"{CaptureCardSupportPolicy.QualifiedDeviceName}\"."),
                    new(DoctorComponents.CaptureCardStream, DoctorStatus.Hardware,
                        "not checked because no DirectShow video device was enumerated.")
                ];
            var qualification = CaptureCardSupportPolicy.Evaluate(_cfg.CaptureCardDevice, devices);
            if (qualification.IsQualified)
            {
                var stream = await ProbeQualifiedCaptureStreamAsync(grabber, ct);
                return
                [
                    new(DoctorComponents.CaptureCardModel, DoctorStatus.Ok, qualification.Detail),
                    stream
                ];
            }
            return
            [
                new(DoctorComponents.CaptureCardModel, DoctorStatus.Hardware,
                    qualification.Detail,
                    FixHint: $"Set settings.captureCardDevice to exactly \"{CaptureCardSupportPolicy.QualifiedDeviceName}\" and confirm it appears in `gpusuite grab --list`. Other cards remain available for diagnostics but are not qualified for the full automated workflow."),
                new(DoctorComponents.CaptureCardStream, DoctorStatus.Hardware,
                    "not probed until the exact qualified Elgato 4K Pro is configured and enumerated.",
                    "This probe briefly consumes frames to FFmpeg's null sink; no image is written or retained, and it never launches a game.")
            ];
        }
        catch (Exception ex)
        {
            return
            [
                new(DoctorComponents.CaptureCardModel, DoctorStatus.Hardware, $"enumeration error: {ex.Message}"),
                new(DoctorComponents.CaptureCardStream, DoctorStatus.Hardware, "not probed because capture-card enumeration failed.")
            ];
        }
    }

    private async Task<DoctorCheck> ProbeQualifiedCaptureStreamAsync(CaptureCardGrabber grabber, CancellationToken ct)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(TimeSpan.FromSeconds(8));
        bool live = await grabber.ProbeVideoAsync(frames: 2, ct: probeCts.Token).ConfigureAwait(false);
        // Only claim a timeout when it actually timed out. ProbeVideoAsync also returns false when the device
        // is held by another app (OBS), when there is no input signal, and when ffmpeg exits non-zero
        // immediately — reporting all of those as "did not complete within 8 seconds" sends the operator
        // hunting a stall that never happened. ffmpeg's own stderr explains which it was, but the App's
        // pre-flight passes a RunLogger that writes nowhere, so it is not visible here; the run log is the
        // place to look, and that plumbing is a wider change than this check.
        bool timedOut = probeCts.IsCancellationRequested;
        return live
            ? new(DoctorComponents.CaptureCardStream, DoctorStatus.Ok,
                "qualified Elgato bounded stream probe passed; no image retained.")
            : new(DoctorComponents.CaptureCardStream, DoctorStatus.Hardware,
                timedOut
                    ? "qualified Elgato was enumerated but its bounded stream probe did not complete within 8 seconds."
                    : "qualified Elgato was enumerated but its bounded stream probe failed — typically the device is already open in another application, there is no input signal, or ffmpeg rejected the device. See the run log for ffmpeg's exact error.",
                "Check the HDMI source/cable, power, input signal, that nothing else holds the card, and the 60 Hz validated bench path; then re-run full pre-flight. This is vision/OCR input, not FPS / frametime capture.");
    }

    private static FrameCapturePreflightPlan GenericFramePlan(string? globalProvider)
    {
        string global = (globalProvider ?? "auto").Trim().ToLowerInvariant();
        return global switch
        {
            "rtss" => new(true, RequiresPresentMon: false, RequiresRtss: true, MayUseRtssFallback: false,
                PresentMonGames: Array.Empty<string>(), RtssGames: ["current global configuration"], AutoGames: Array.Empty<string>()),
            "presentmon" => new(true, RequiresPresentMon: true, RequiresRtss: false, MayUseRtssFallback: false,
                PresentMonGames: ["current global configuration"], RtssGames: Array.Empty<string>(), AutoGames: Array.Empty<string>()),
            _ => new(true, RequiresPresentMon: false, RequiresRtss: false, MayUseRtssFallback: true,
                PresentMonGames: Array.Empty<string>(), RtssGames: Array.Empty<string>(), AutoGames: ["current global configuration"])
        };
    }

    private DoctorCheck CheckPowenetics()
    {
        // READ-ONLY: just list serial ports (no init bytes written to unknown devices).
        string[] ports;
        try { ports = SerialPort.GetPortNames(); } catch { ports = Array.Empty<string>(); }
        string want = _cfg.PoweneticsComPort;
        if (!string.IsNullOrWhiteSpace(want))
        {
            bool present = ports.Any(p => p.Equals(want, StringComparison.OrdinalIgnoreCase));
            return new("Powenetics PMD (power)", DoctorStatus.Hardware,
                present ? $"configured port {want} is present (run `gpusuite probe-powenetics` to confirm it streams)."
                        : $"configured port {want} NOT present. Ports seen: {(ports.Length == 0 ? "none" : string.Join(", ", ports))}.",
                FixHint: present ? null : "Re-plug the PMD and update settings.poweneticsComPort (ports drift across reconnects). Power falls back to LHM automatically if absent.");
        }
        return new("Powenetics PMD (power)", DoctorStatus.Hardware,
            ports.Length == 0 ? "no serial ports; no PMD configured — power will use the LHM fallback."
                              : $"no port configured (ports seen: {string.Join(", ", ports)}). Power uses the LHM fallback unless set.",
            FixHint: "Optional: set settings.poweneticsComPort + run `gpusuite probe-powenetics` for real per-rail watts. Without it, LHM board power is used.");
    }

    private DoctorCheck CheckProfiles()
    {
        if (!Directory.Exists(_cfg.ProfilesDir))
            return new("Game profiles", DoctorStatus.Missing, $"profiles directory '{_cfg.ProfilesDir}' not found.",
                FixHint: "Copy the profiles/ directory alongside gpusuite.exe (it ships with the suite).");
        var manager = new ProfilePackManager(_cfg.ProfilesDir);
        var packs = manager.Discover();
        int n = manager.GetActiveProfileFiles().Count;
        if (n == 0)
            return new("Game profiles", DoctorStatus.Missing, $"no profiles in '{_cfg.ProfilesDir}'.",
                FixHint: "Copy the profiles/ directory alongside gpusuite.exe.");
        int problems = packs.Count(p => p.Errors.Count > 0 || !p.IsCompatible);
        int active = packs.Count(p => p.IsUsable);
        return new("Game profiles", problems == 0 ? DoctorStatus.Ok : DoctorStatus.Warn,
            $"{n} profile(s) from {active} active pack(s) in '{_cfg.ProfilesDir}'" +
            (problems > 0 ? $"; {problems} pack(s) invalid or incompatible." : "."),
            FixHint: problems > 0 ? "Open Profile Packs, review validation findings, then update, disable, or remove the affected pack." : null);
    }

    // ---------------- helpers ----------------

    /// <summary>True if an `ollama list` line names <paramref name="model"/> (handles the implicit :latest tag).</summary>
    private static bool LineHasModel(string line, string model)
    {
        var name = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Equals(model, StringComparison.OrdinalIgnoreCase)) return true;
        // "qwen2.5:7b" listed vs "qwen2.5" requested, or vice-versa (":latest" elided).
        string baseWant = model.Split(':')[0];
        string baseHave = name.Split(':')[0];
        return baseHave.Equals(baseWant, StringComparison.OrdinalIgnoreCase) &&
               (model.Contains(':') ? name.Equals(model, StringComparison.OrdinalIgnoreCase) : true);
    }

    /// <summary>Resolve a possibly-relative tool path against cwd and the app base dir; null if not found.</summary>
    private static string? ResolveAppPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (File.Exists(path)) return Path.GetFullPath(path);
        var baseDir = Path.Combine(AppContext.BaseDirectory, path);
        if (File.Exists(baseDir)) return baseDir;
        return null;
    }

    /// <summary>Run a process, capture output, bounded by <paramref name="timeoutMs"/>. Never throws —
    /// a missing executable returns a negative code so callers treat it as "not installed".</summary>
    internal static async Task<(int code, string stdout, string stderr)> RunProcAsync(string file, string args, int timeoutMs, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return (-1, "", "failed to start");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (-2, "", "timed out"); }
            string so = await p.StandardOutput.ReadToEndAsync();
            string se = await p.StandardError.ReadToEndAsync();
            return (p.ExitCode, so, se);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }
}
