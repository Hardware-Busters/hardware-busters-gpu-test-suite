namespace GpuSuite.Measurement.Monitoring.FamilyTransport;

/// <summary>Writes one application's frames to the family shared-memory pool; the only seam the publisher depends on.</summary>
public interface IFamilyFrameWriter : IDisposable
{
    TimeSpan PublishInterval { get; }
    long Sequence { get; }
    string? LastError { get; }
    bool TryPublish(object data, bool online = true);
}
