using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Automation;
using GpuSuite.Engine.Diagnostics;
using GpuSuite.Engine.Display;
using GpuSuite.Engine.Launch;
using GpuSuite.Engine.Vision;

namespace GpuSuite.App.Services;

public sealed record RosterSmokeProgress(int Index, int Total, string GameName, string Phase);

public sealed class RosterSmokeItem
{
    public required string GameId { get; init; }
    public required string GameName { get; init; }
    public required string Store { get; init; }
    public SmokeFrameVerdict Verdict { get; set; }
    public string Detail { get; set; } = "";
    public string EvidencePath { get; set; } = "";
    public string OcrText { get; set; } = "";
    public double ElapsedSeconds { get; set; }
}

public sealed class RosterSmokeReport
{
    public DateTime StartedUtc { get; init; }
    public DateTime FinishedUtc { get; set; }
    public bool Cancelled { get; set; }
    public List<RosterSmokeItem> Games { get; } = new();
    public int ClearCount => Games.Count(g => g.Verdict == SmokeFrameVerdict.Clear);
    public int WarningCount => Games.Count(g => g.Verdict == SmokeFrameVerdict.Warning);
    public int BlockedCount => Games.Count(g => g.Verdict == SmokeFrameVerdict.Blocked);
}

public sealed record RosterSmokeOutcome(RosterSmokeReport Report, string ReportPath, string LogPath);

/// <summary>Launches each selected game once, proves its process and captured output are alive, checks
/// the visible frame for common update/login/first-run blockers, saves evidence, then closes the game.</summary>
public sealed class RosterSmokeService
{
    private readonly Workspace _ws;
    public RosterSmokeService(Workspace ws) => _ws = ws;

    public Task<RosterSmokeOutcome> RunAsync(IReadOnlyList<GameProfile> games, Action<TimelineEntry> onLog,
        Action<RosterSmokeProgress> onProgress, CancellationToken ct) => Task.Run(async () =>
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string root = Path.Combine(_ws.ResultsDir, "Smoke", "roster_" + stamp);
        Directory.CreateDirectory(root);
        string logPath = Path.Combine(root, "smoke.log");
        string reportPath = Path.Combine(root, "smoke-report.json");
        using var log = new RunLogger(logPath, echoToConsole: false, minimumFileLevel: LogLevel.Trace);
        log.EntryLogged += onLog;
        var report = new RosterSmokeReport { StartedUtc = DateTime.UtcNow };
        try
        {
            await LauncherSessionPrimer.EnsureRunningAsync(games, ct).ConfigureAwait(false);
            var catalog = new GpuSuite.Engine.Discovery.LauncherDiscovery().DiscoverAll();
            var readiness = new GameReadinessChecker(_ws.ProfilesDir);
            var launcher = new GameLauncher(log, _ws.Config.SimulateLaunch, attachToRunning: false,
                _ws.Config.MaxRefreshHz, simulateOnFailure: false, _ws.ProfilesDir);
            var grabber = new CaptureCardGrabber(_ws.Config.FfmpegPath, _ws.Config.CaptureCardDevice, log);
            var reader = new ScreenReader(grabber, log);

            for (int i = 0; i < games.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var game = games[i];
                var sw = Stopwatch.StartNew();
                var item = new RosterSmokeItem
                {
                    GameId = game.Id,
                    GameName = game.Name,
                    Store = game.Launch.Store ?? "",
                    Verdict = SmokeFrameVerdict.Blocked
                };
                report.Games.Add(item);
                LaunchResult? launch = null;
                RefreshGuard? refreshGuard = null;
                InputAutomationEngine? focusEngine = null;
                onProgress(new(i + 1, games.Count, game.Name, "Checking just-in-time readiness"));
                try
                {
                    var ready = readiness.CheckOne(game, catalog);
                    var blockers = ready.Checks.Where(c => c.Status == CheckStatus.Blocker).ToList();
                    if (blockers.Count > 0)
                    {
                        item.Detail = "Pre-flight blocker: " + string.Join("; ", blockers.Select(b => b.Detail));
                        log.Error("Smoke", $"{game.Name}: {item.Detail}");
                        continue;
                    }

                    onProgress(new(i + 1, games.Count, game.Name, "Launching"));
                    log.Info("Smoke", $"[{i + 1}/{games.Count}] Launching {game.Name}.");
                    launch = launcher.Launch(game);
                    if (!launch.Launched || launch.Pid is not int pid)
                    {
                        item.Detail = launch.Detail.Length > 0 ? launch.Detail : "The game process was not resolved.";
                        log.Error("Smoke", $"{game.Name}: {item.Detail}");
                        continue;
                    }

                    if (_ws.Config.EnforceRefreshCap)
                    {
                        refreshGuard = RefreshGuard.Start(_ws.Config.MaxRefreshHz, _ws.Config.RefreshGuardPollMs, hz =>
                        {
                            if (!_ws.Config.KillGameOnRefreshViolation)
                            {
                                log.Warn("RefreshGuard", $"{game.Name} drove the display to {hz}Hz; configured to log only.");
                                return;
                            }
                            try
                            {
                                using var offender = Process.GetProcessById(pid);
                                if (!offender.HasExited) offender.Kill(true);
                                log.Error("RefreshGuard", $"Killed {game.Name} pid {pid}: {hz}Hz exceeded the {_ws.Config.MaxRefreshHz}Hz smoke-test cap.");
                            }
                            catch (Exception ex) { log.Warn("RefreshGuard", "Unable to terminate the over-cap game: " + ex.Message); }
                        }, log, ct);
                    }

                    onProgress(new(i + 1, games.Count, game.Name, "Waiting for the game window and foreground ownership"));
                    await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                    if (!ProcessAlive(pid))
                    {
                        item.Detail = $"Game process {pid} exited before the smoke capture.";
                        log.Error("Smoke", $"{game.Name}: {item.Detail}");
                        continue;
                    }

                    // Process-alive is not visual proof: Xbox/launchers can leave the game alive behind the
                    // desktop. Reuse the benchmark bot's proven foreground mechanism and require PID ownership
                    // before trusting any capture-card OCR as evidence for this game.
                    focusEngine = new InputAutomationEngine(log, inject: true) { TargetPid = pid, StrictForeground = false };
                    var focusDeadline = DateTime.UtcNow.AddSeconds(60);
                    bool foreground = false;
                    while (DateTime.UtcNow < focusDeadline && ProcessAlive(pid))
                    {
                        if (focusEngine.TryForegroundTargetForRead()) { foreground = true; break; }
                        await Task.Delay(2000, ct).ConfigureAwait(false);
                    }
                    if (!foreground)
                    {
                        item.Detail = "The process launched but its game window could not be made foreground within 60 seconds; visual evidence would be untrustworthy.";
                        log.Error("Smoke", $"{game.Name}: {item.Detail}");
                        continue;
                    }

                    onProgress(new(i + 1, games.Count, game.Name, "Verifying capture sync and waiting for a readable game frame"));
                    bool synced = await CaptureSyncGuard.EnsureSyncedAsync(reader, _ws.Config.MaxRefreshHz, log, ct,
                        maxNudges: 3, context: $"roster-smoke {game.Id}").ConfigureAwait(false);
                    if (!ProcessAlive(pid))
                    {
                        item.Detail = $"Game process {pid} exited while capture sync was being verified.";
                        log.Error("Smoke", $"{game.Name}: {item.Detail}");
                        continue;
                    }
                    if (!synced)
                    {
                        item.Detail = "The game launched, but capture-card sync could not be restored after three display-mode nudges.";
                        log.Error("Smoke", $"{game.Name}: {item.Detail}");
                        continue;
                    }

                    string safeId = string.Concat(game.Id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
                    string png = Path.Combine(root, $"{i + 1:D2}_{safeId}.png");
                    item.EvidencePath = png;
                    OcrFrame? frame = null;
                    var readableDeadline = DateTime.UtcNow.AddSeconds(60);
                    do
                    {
                        if (!ProcessAlive(pid)) break;
                        if (!focusEngine.TryForegroundTargetForRead())
                        {
                            await Task.Delay(2000, ct).ConfigureAwait(false);
                            continue;
                        }
                        if (await grabber.GrabAsync(png, 18, ct).ConfigureAwait(false))
                            frame = await reader.OcrImageAsync(png, ct).ConfigureAwait(false);
                        if (frame is { Lines.Count: > 0 }) break;
                        if (DateTime.UtcNow < readableDeadline)
                        {
                            log.Info("Smoke", $"{game.Name}: foreground frame is still textless/loading; waiting for readable evidence.");
                            await Task.Delay(5000, ct).ConfigureAwait(false);
                        }
                    } while (DateTime.UtcNow < readableDeadline);

                    if (!File.Exists(png))
                    {
                        item.Detail = "The foreground game stayed alive, but the capture card produced no evidence frame.";
                        log.Error("Smoke", $"{game.Name}: {item.Detail}");
                        continue;
                    }
                    item.OcrText = frame is null ? "" : string.Join(" | ", frame.Lines.Select(l => l.Text));
                    var finding = SmokeFrameClassifier.Classify(frame);
                    item.Verdict = finding.Verdict;
                    item.Detail = finding.Detail;
                    if (finding.Verdict == SmokeFrameVerdict.Blocked) log.Error("Smoke", $"{game.Name}: {finding.Detail}");
                    else if (finding.Verdict == SmokeFrameVerdict.Warning) log.Warn("Smoke", $"{game.Name}: {finding.Detail}");
                    else log.Info("Smoke", $"{game.Name}: clear — {finding.Detail}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    item.Verdict = SmokeFrameVerdict.Blocked;
                    item.Detail = ex.Message;
                    log.Error("Smoke", $"{game.Name}: unexpected smoke failure — {ex}");
                }
                finally
                {
                    item.ElapsedSeconds = sw.Elapsed.TotalSeconds;
                    focusEngine?.Dispose();
                    refreshGuard?.Dispose();
                    if (launch is not null) launcher.Cleanup(game, launch);
                }
            }

            report.FinishedUtc = DateTime.UtcNow;
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            log.Info("Smoke", $"Roster smoke complete: {report.ClearCount} clear, {report.WarningCount} warning, {report.BlockedCount} blocked. Report: {reportPath}");
            return new RosterSmokeOutcome(report, reportPath, logPath);
        }
        catch (OperationCanceledException)
        {
            report.Cancelled = true;
            report.FinishedUtc = DateTime.UtcNow;
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            log.Warn("Smoke", $"Roster smoke cancelled after {report.Games.Count}/{games.Count} game(s); partial report saved: {reportPath}");
            throw;
        }
        finally { log.EntryLogged -= onLog; }
    }, ct);

    private static bool ProcessAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch { return false; }
    }
}
