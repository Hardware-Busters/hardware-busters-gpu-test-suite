namespace GpuSuite.Load;

/// <summary>
/// A controllable GPU compute load. <see cref="Intensity"/> (0..1) is the single knob a closed-loop
/// power controller turns to drive the card to a target wattage; power responds monotonically to it.
/// </summary>
public interface IGpuLoad : IDisposable
{
    /// <summary>Name of the GPU the load runs on (the device under test).</summary>
    string GpuName { get; }

    /// <summary>0..1 workload intensity. Settable at any time; takes effect on the next dispatch tick.</summary>
    double Intensity { get; set; }

    bool Running { get; }

    void Start();
    void Stop();
}
