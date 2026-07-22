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
    public async Task<List<DoctorCheck>> RunAsync(string llmModel, CancellationToken ct)
    {
        var checks = new List<DoctorCheck>
        {
            await CheckDotnetRuntimeAsync(ct),
            await CheckFfmpegAsync(ct),
            CheckPresentMon(),
            CheckRtss(),
            CheckViGEm(),
            await CheckOllamaAsync(llmModel, ct),
            await CheckGx10Async(ct),
            await CheckCaptureCardAsync(ct),
            CheckPowenetics(),
            CheckProfiles(),
        };
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

    private async Task<DoctorCheck> CheckFfmpegAsync(CancellationToken ct)
    {
        string exe = string.IsNullOrWhiteSpace(_cfg.FfmpegPath) ? "ffmpeg" : _cfg.FfmpegPath;
        var (code, outp, _) = await RunProcAsync(exe, "-version", 8000, ct);
        if (code == 0)
        {
            var first = outp.Split('\n').FirstOrDefault()?.Trim() ?? "ffmpeg";
            return new("ffmpeg (capture-card grabs)", DoctorStatus.Ok, first);
        }
        return new("ffmpeg (capture-card grabs)", DoctorStatus.Warn,
            $"not found ({(string.IsNullOrWhiteSpace(_cfg.FfmpegPath) ? "not on PATH" : _cfg.FfmpegPath)}).",
            FixHint: "Needed for `grab` / vision-nav OCR off the capture card. Set settings.ffmpegPath if installed elsewhere.",
            FixCommand: "winget install --id Gyan.FFmpeg -e --accept-source-agreements --accept-package-agreements");
    }

    private DoctorCheck CheckPresentMon()
    {
        var p = ResolveAppPath(_cfg.PresentMonPath);
        if (p is not null)
            return new("PresentMon (frame capture)", DoctorStatus.Ok, p);
        return new("PresentMon (frame capture)", DoctorStatus.Missing,
            $"not found at '{_cfg.PresentMonPath}'.",
            FixHint: "PresentMon ships in the suite's tools/ folder — copy the tools/ directory alongside gpusuite.exe, or set settings.presentMonPath. (No package install; it's a bundled binary.)");
    }

    private DoctorCheck CheckRtss()
    {
        var candidates = new[]
        {
            _cfg.RtssExePath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RivaTuner Statistics Server", "RTSS.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RivaTuner Statistics Server", "RTSS.exe"),
        };
        var found = candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
        bool running = Process.GetProcessesByName("RTSS").Length > 0;
        if (found is not null || running)
            return new("RTSS / RivaTuner (rtss capture + OSD)", DoctorStatus.Ok,
                running ? "RTSS is running." : $"installed: {found}");
        // Only an issue when the rtss backend or the OSD is in use.
        bool needed = _cfg.FrameProvider.Equals("rtss", StringComparison.OrdinalIgnoreCase)
                      || _cfg.FrameProvider.Equals("auto", StringComparison.OrdinalIgnoreCase) || _cfg.Osd;
        return new("RTSS / RivaTuner (rtss capture + OSD)", needed ? DoctorStatus.Missing : DoctorStatus.Warn,
            "not installed / not running.",
            FixHint: "Install RivaTuner Statistics Server (ships with MSI Afterburner or the Powenetics V2 kit). Set settings.rtssExePath so the suite can auto-start it. Required for the rtss frame backend and the live OSD.");
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

    private async Task<DoctorCheck> CheckCaptureCardAsync(CancellationToken ct)
    {
        // Needs ffmpeg to enumerate DirectShow devices; skip cleanly if ffmpeg is absent.
        string exe = string.IsNullOrWhiteSpace(_cfg.FfmpegPath) ? "ffmpeg" : _cfg.FfmpegPath;
        var (probe, _, _) = await RunProcAsync(exe, "-version", 5000, ct);
        if (probe != 0)
            return new("Capture card (Elgato/HDMI)", DoctorStatus.Hardware, "skipped — ffmpeg not available to enumerate devices.");
        try
        {
            var grabber = new CaptureCardGrabber(_cfg.FfmpegPath, _cfg.CaptureCardDevice, _log);
            var devices = await grabber.ListVideoDevicesAsync();
            if (devices.Count == 0)
                return new("Capture card (Elgato/HDMI)", DoctorStatus.Hardware, "no DirectShow video devices found — is the capture card connected?");
            var qualification = CaptureCardSupportPolicy.Evaluate(_cfg.CaptureCardDevice, devices);
            if (qualification.IsQualified)
                return new("Capture card (Elgato/HDMI)", DoctorStatus.Ok, qualification.Detail);
            return new("Capture card (Elgato/HDMI)", DoctorStatus.Hardware,
                qualification.Detail,
                FixHint: $"Set settings.captureCardDevice to exactly \"{CaptureCardSupportPolicy.QualifiedDeviceName}\" and confirm it appears in `gpusuite grab --list`. Other cards remain available for diagnostics but are not qualified for the full automated workflow.");
        }
        catch (Exception ex)
        {
            return new("Capture card (Elgato/HDMI)", DoctorStatus.Hardware, $"enumeration error: {ex.Message}");
        }
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
