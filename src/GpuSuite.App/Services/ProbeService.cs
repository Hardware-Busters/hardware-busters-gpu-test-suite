using GpuSuite.Measurement;
using GpuSuite.Core.Models;

namespace GpuSuite.App.Services;

/// <summary>Result of a hardware probe (the same data the CLI `probe` verb prints).</summary>
public sealed record ProbeResult(
    string GpuName,
    string CpuName,
    IReadOnlyList<string> Lines,
    string PreferredGpu,
    string PowerSource,
    PowerMeasurementMetadata EffectivePowerMeasurement,
    string FrameProvider,
    string PoweneticsComPort,
    bool Osd);

/// <summary>
/// Probes the measurement sources (frames / power / telemetry) and detects the GPU/CPU under test,
/// off the UI thread. Wraps <see cref="MeasurementFactory.ProbeAll"/> — exactly what `gpusuite probe`
/// runs, which honors PoweneticsAutoDetect=false + an empty COM port (it never scans serial ports).
/// </summary>
public sealed class ProbeService
{
    private readonly Workspace _ws;
    public ProbeService(Workspace ws) => _ws = ws;

    public Task<ProbeResult> ProbeAsync() => Task.Run(() =>
    {
        var cfg = _ws.Config;
        using var factory = new MeasurementFactory(cfg);
        var lines = factory.ProbeAll().ToList();
        var effectivePower = factory.SelectPowerProvider().Measurement;
        return new ProbeResult(
            factory.DetectedGpuName,
            factory.DetectedCpuName,
            lines,
            cfg.PreferredGpu,
            cfg.PowerSource,
            new PowerMeasurementMetadata
            {
                SchemaVersion = effectivePower.SchemaVersion,
                Kind = effectivePower.Kind,
                Scope = effectivePower.Scope,
                EffectiveSampleHz = effectivePower.EffectiveSampleHz,
                HasPerRailData = effectivePower.HasPerRailData,
                HardwareBustersVerifiedPowerEligible = effectivePower.HardwareBustersVerifiedPowerEligible,
                LegacyInferred = effectivePower.LegacyInferred,
                QualificationNote = effectivePower.QualificationNote
            },
            cfg.FrameProvider,
            cfg.PoweneticsComPort,
            cfg.Osd);
    });
}
