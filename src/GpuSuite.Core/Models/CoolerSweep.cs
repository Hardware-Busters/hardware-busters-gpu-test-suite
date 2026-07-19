namespace GpuSuite.Core.Models;

/// <summary>
/// Result of a noise- and power-normalized GPU cooler evaluation (TechPowerUp-style "Cooler Performance").
/// The test holds the fan at each noise-normalized speed (the user's 25/30/35/40 dBA @ 1 m points) and, at
/// each, sweeps an exact GPU heat load (power) recording the steady on-die temperatures — giving a FAMILY of
/// temperature-vs-power curves (one per dBA) plus a single-point summary at the card's reference TDP.
/// </summary>
public sealed class CoolerSweepResult
{
    public string GpuName { get; set; } = "Unknown GPU";
    public string Vendor { get; set; } = "Unknown";
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Reference heat load (watts) for the single-point bars — typically the card's reference TDP.</summary>
    public double ReferencePowerW { get; set; }
    /// <summary>NVIDIA graphics clock the load was locked to (MHz), if clock-lock was applied; null otherwise.</summary>
    public int? ClockLockMhz { get; set; }
    /// <summary>True if the fan was set programmatically (GpuFanController); false if the operator set it manually.</summary>
    public bool FanAutoControlled { get; set; }
    /// <summary>Operator note documenting the noise calibration (meter, distance, manual fan setup, etc.).</summary>
    public string FanNote { get; set; } = "";
    /// <summary>Power feedback source used to hold each step (Powenetics / LHM board power / synthetic).</summary>
    public string PowerSource { get; set; } = "";
    /// <summary>Room/ambient temperature (°C) the operator recorded, for context (0 = not provided).</summary>
    public double AmbientC { get; set; }

    /// <summary>One curve per noise-normalized fan speed.</summary>
    public List<CoolerNoiseLevel> Levels { get; set; } = new();
}

/// <summary>One noise-normalized fan setting (e.g. 35 dBA @ 1 m) and its temperature-vs-power curve.</summary>
public sealed class CoolerNoiseLevel
{
    /// <summary>Target noise this fan speed was calibrated to emit (dBA), e.g. 25 / 30 / 35 / 40.</summary>
    public double TargetDba { get; set; }
    /// <summary>Fan duty (%) that produces <see cref="TargetDba"/> — from the operator's one-time calibration.</summary>
    public double FanPercent { get; set; }
    /// <summary>Observed fan RPM held during this level (documents the actual speed behind the dBA figure).</summary>
    public double? FanRpm { get; set; }

    /// <summary>The temperature-vs-power steps measured at this fan speed.</summary>
    public List<CoolerStep> Steps { get; set; } = new();

    /// <summary>GPU temperature at <see cref="CoolerSweepResult.ReferencePowerW"/> (interpolated from the steps).</summary>
    public double? RefGpuTempC { get; set; }
    /// <summary>Memory temperature at the reference power (interpolated), where the card reports it.</summary>
    public double? RefMemTempC { get; set; }
}

/// <summary>One soaked power step: the steady-state thermal reading at a fixed heat load.</summary>
public sealed class CoolerStep
{
    /// <summary>Commanded heat load (watts).</summary>
    public double TargetW { get; set; }
    /// <summary>Mean board power actually held over the steady window (watts).</summary>
    public double? AchievedW { get; set; }

    public double? GpuTempC { get; set; }
    public double? GpuHotspotC { get; set; }
    public double? MemTempC { get; set; }
    public double? FanRpm { get; set; }
    public double? GpuClockMhz { get; set; }

    /// <summary>Seconds spent soaking this step (until the temp slope settled or the max-soak timeout).</summary>
    public double SoakSeconds { get; set; }
    /// <summary>True if the GPU-temperature slope fell below the settle threshold; false if max-soak timed out.</summary>
    public bool Settled { get; set; }
}
