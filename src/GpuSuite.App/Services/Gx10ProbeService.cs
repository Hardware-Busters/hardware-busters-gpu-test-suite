using GpuSuite.Core.Remote;

namespace GpuSuite.App.Services;

/// <summary>
/// Probes the lab GX10 / GB10 box (its Ollama endpoint) off the UI thread so the Settings screen can show
/// its live statistics and decide whether the "GX10" vision-compute option is selectable. Read-only — it
/// only asks what the box is already serving; it never loads, pulls, or unloads a model. Mirrors the CLI
/// `gpusuite gx10` verb.
/// </summary>
public sealed class Gx10ProbeService
{
    private readonly Workspace _ws;
    public Gx10ProbeService(Workspace ws) => _ws = ws;

    /// <summary>Probe the configured endpoint (settings.gx10Endpoint). Never throws — failure ⇒ Reachable=false.</summary>
    public Task<Gx10Status> ProbeAsync(CancellationToken ct = default)
        => new Gx10Client(_ws.Config.Gx10Endpoint).ProbeAsync(ct, 6000);
}
