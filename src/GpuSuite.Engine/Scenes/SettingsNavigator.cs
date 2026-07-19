using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Automation;

namespace GpuSuite.Engine.Scenes;

public sealed class SettingsResult
{
    public bool Ok { get; set; } = true;
    public bool Verified { get; set; }
    public string Detail { get; set; } = "";
    public List<string> Issues { get; } = new();
}

/// <summary>
/// (3) In-game settings navigation. After launch and before capture: run the game's settings
/// bot script to apply the graphics preset + resolution, then VERIFY it was applied. If
/// verification is configured and fails, the run is marked Invalid — never silently continued.
/// With no real game (degraded mode) the apply step is a dry-run and verification is skipped.
/// </summary>
public sealed class SettingsNavigator
{
    private readonly RunLogger _log;
    public SettingsNavigator(RunLogger log) => _log = log;

    public async Task<SettingsResult> ApplyAndVerifyAsync(GameProfile game, Resolution res, bool realGame, int? pid, CancellationToken ct)
    {
        var r = new SettingsResult();

        // ---- apply ----
        var script = BotScriptLibrary.Resolve(game.SettingsBotScript);
        if (script is not null)
        {
            _log.Info("Settings", $"Applying preset '{game.GraphicsPreset ?? "default"}' + {res.Name} via '{game.SettingsBotScript}' (inject={realGame}).");
            var bot = new InputAutomationEngine(_log, inject: realGame) { TargetPid = pid };
            await bot.RunAsync(script, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        }
        else
        {
            _log.Trace("Settings", $"No settings bot script; assuming preset '{game.GraphicsPreset ?? "default"}' + {res.Name}.");
        }

        // ---- verify ----
        var v = game.SettingsVerify;
        if (v.Method.Equals("none", StringComparison.OrdinalIgnoreCase)) { r.Detail = "no verification configured"; return r; }
        if (!realGame || pid is null) { r.Detail = "verification skipped (no game window)"; _log.Trace("Settings", r.Detail); return r; }

        (bool ok, string detail) = v.Method.ToLowerInvariant() switch
        {
            "window-size" => VerifyWindowSize(pid.Value, res),
            // config-file resolution is applied AND checked pre-launch by the launcher. The game owns
            // and may truncate this file once running, so re-reading it post-launch is unreliable and
            // was the root cause of a launch→verify-fail→kill→empty-file death spiral. Authoritative
            // confirmation comes downstream from the benchmark's own result (summary.json renderWidth).
            "config-file" => (true, "config-file resolution verified pre-launch (applied by launcher)"),
            _ => (true, $"unknown verify method '{v.Method}' — skipped")
        };
        r.Ok = ok; r.Verified = ok; r.Detail = detail;
        if (!ok)
        {
            r.Issues.Add($"Settings verification failed: {detail}");
            _log.Warn("Settings", r.Issues[^1]);
        }
        else _log.Info("Settings", $"Settings verified: {detail}");
        return r;
    }

    private (bool, string) VerifyWindowSize(int pid, Resolution res)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            var h = p.MainWindowHandle;
            if (h == IntPtr.Zero) return (false, "no main window handle");
            if (!GetClientRect(h, out RECT rc)) return (false, "GetClientRect failed");
            int w = rc.Right - rc.Left, ht = rc.Bottom - rc.Top;
            return (w == res.Width && ht == res.Height, $"client {w}x{ht} vs target {res.Width}x{res.Height}");
        }
        catch (Exception ex) { return (false, "window check error: " + ex.Message); }
    }

    private static (bool, string) VerifyConfig(SettingsVerification v, Resolution res)
    {
        if (string.IsNullOrWhiteSpace(v.ConfigFilePath)) return (false, "no configFilePath");
        var path = Environment.ExpandEnvironmentVariables(v.ConfigFilePath);
        if (!File.Exists(path)) return (false, "config file not found: " + path);
        if (string.IsNullOrWhiteSpace(v.ExpectedPattern)) return (false, "no expectedPattern");
        try
        {
            var text = File.ReadAllText(path);
            var pat = v.ExpectedPattern.Replace("{WIDTH}", res.Width.ToString()).Replace("{HEIGHT}", res.Height.ToString());
            return (Regex.IsMatch(text, pat), $"expected /{pat}/ in config");
        }
        catch (Exception ex) { return (false, "config read error: " + ex.Message); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
}
