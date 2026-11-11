using System.Text.Json;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Models;
using GpuSuite.Measurement.Monitoring;
using GpuSuite.Measurement.Monitoring.FamilyTransport;
using Xunit;

namespace GpuSuite.Tests;

[Collection("Monitoring")]
public sealed class FamilyTelemetryPublisherTests
{
    private static string NewAppId() => "t" + Guid.NewGuid().ToString("N")[..12];

    private static async Task<FamilyFrame> WaitFor(string appId, Func<FamilyFrame, bool> predicate)
    {
        var until = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < until)
        {
            try
            {
                var frame = FamilyFrameReader.Read(appId);
                if (predicate(frame)) return frame;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or WaitHandleCannotBeOpenedException)
            {
                // The first page may not be committed yet.
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("The publisher did not publish the expected frame.");
    }

    private static double? PowerW(FamilyFrame frame) =>
        frame.Data.GetProperty("readings").GetProperty("gpuPowerW").GetProperty("value") is { ValueKind: JsonValueKind.Number } value
            ? value.GetDouble() : null;

    [Fact]
    public async Task AFrameIsPublishedOnlineWithTheVersionAndTheCommittedSample()
    {
        string appId = NewAppId();
        using var publisher = FamilyTelemetryPublisher.TryStart(appId, "1.2.3-test");
        Assert.NotNull(publisher);
        using var source = new SampleMonitor("power", "test source", DataSourceMode.Live);

        source.Commit(new PowerSample { TimeSec = 1, GpuTotalW = 150.25 }, 1);

        var frame = await WaitFor(appId, f => PowerW(f) == 150.25);
        Assert.True(frame.Online);
        Assert.Equal("faganas-method/v1", frame.Data.GetProperty("extension").GetProperty("method").GetProperty("schema").GetString());
        Assert.Equal("1.2.3-test", frame.Envelope.GetProperty("appVersion").GetString());
        Assert.Equal(Environment.ProcessId, frame.Envelope.GetProperty("pid").GetInt32());
        Assert.Equal("test source", frame.Data.GetProperty("readings").GetProperty("gpuPowerW").GetProperty("source").GetString());
    }

    [Fact]
    public async Task TheHeartbeatKeepsPublishingWhileTheStateIsUnchanged()
    {
        string appId = NewAppId();
        using var publisher = FamilyTelemetryPublisher.TryStart(appId, "1.0.0");
        Assert.NotNull(publisher);

        var first = await WaitFor(appId, _ => true);
        var next = await WaitFor(appId, f => f.Sequence > first.Sequence);

        Assert.True(next.Ticks > first.Ticks);
    }

    [Fact]
    public async Task ABurstOfCommitsEndsOnTheLatestValue()
    {
        string appId = NewAppId();
        using var publisher = FamilyTelemetryPublisher.TryStart(appId, "1.0.0");
        Assert.NotNull(publisher);
        using var source = new SampleMonitor("power", "test source", DataSourceMode.Live);

        for (int i = 1; i <= 500; i++) source.Commit(new PowerSample { TimeSec = i, GpuTotalW = 1000 + i }, i);

        var frame = await WaitFor(appId, f => PowerW(f) == 1500);
        Assert.Equal(500, frame.Data.GetProperty("sources").GetProperty("power").GetProperty("sequence").GetInt64());
    }

    [Fact]
    public async Task TheOperationAppearsInTheTestGroup()
    {
        string appId = NewAppId();
        using var publisher = FamilyTelemetryPublisher.TryStart(appId, "1.0.0");
        Assert.NotNull(publisher);

        MonitoringState.Operation(new MonitoringOperation("capturing", Game: "family-test-game", Resolution: "4K"));
        try
        {
            var frame = await WaitFor(appId, f => f.Data.GetProperty("test").GetProperty("game").GetString() == "family-test-game");
            Assert.Equal("capturing", frame.Data.GetProperty("test").GetProperty("state").GetString());
            Assert.Equal("4K", frame.Data.GetProperty("test").GetProperty("resolution").GetString());
        }
        finally
        {
            MonitoringState.Operation(new MonitoringOperation("idle"));
        }
    }

    [Fact]
    public async Task DisposeLeavesAFinalOfflineFrameForReadersThatStillHoldTheMapping()
    {
        string appId = NewAppId();
        using var publisher = FamilyTelemetryPublisher.TryStart(appId, "1.0.0");
        Assert.NotNull(publisher);
        var online = await WaitFor(appId, f => f.Online);
        using var retained = FamilyFrameReader.Open(appId);

        publisher.Dispose();

        var last = FamilyFrameReader.ReadRetained(retained, appId);
        Assert.False(last.Online);
        Assert.True(last.Sequence > online.Sequence);
    }

    [Fact]
    public async Task AFreshPublisherCanTakeOverAfterDisposeEvenWhileAReaderHoldsTheOldMapping()
    {
        string appId = NewAppId();
        using var first = FamilyTelemetryPublisher.TryStart(appId, "1.0.0");
        Assert.NotNull(first);
        await WaitFor(appId, f => f.Online);
        using var retained = FamilyFrameReader.Open(appId);
        first.Dispose();

        using var second = FamilyTelemetryPublisher.TryStart(appId, "2.0.0");

        Assert.NotNull(second);
        var frame = await WaitFor(appId, f => f.Online && f.Envelope.GetProperty("appVersion").GetString() == "2.0.0");
        Assert.True(frame.Online);
    }

    [Fact]
    public void ASecondPublisherForTheSameAppIdIsRefusedWhileTheFirstLives()
    {
        string appId = NewAppId();
        using var first = FamilyTelemetryPublisher.TryStart(appId, "1.0.0");
        Assert.NotNull(first);

        Assert.Null(FamilyTelemetryPublisher.TryStart(appId, "2.0.0"));
    }

    [Fact]
    public async Task RawLogMessagesAreNeverPublishedOnlyTheirStageAndSeverity()
    {
        string appId = NewAppId();
        using var publisher = FamilyTelemetryPublisher.TryStart(appId, "1.0.0");
        Assert.NotNull(publisher);
        using var log = new RunLogger(null, false);

        string drive = "C" + ":" + "\\"; // built at run time so that no drive path appears literally in this source file
        log.Info("family-test-stage", $"launching {drive}Users\\someone\\secret.txt token=abc123");

        var frame = await WaitFor(appId, f =>
            f.Data.GetProperty("extension").GetProperty("lastEvent") is { ValueKind: JsonValueKind.Object } e
            && e.GetProperty("stage").GetString() == "family-test-stage");
        Assert.DoesNotContain("secret.txt", frame.Json);
        Assert.DoesNotContain("token=abc123", frame.Json);
        Assert.Equal("Info", frame.Data.GetProperty("extension").GetProperty("lastEvent").GetProperty("severity").GetString());
    }

    [Fact]
    public async Task AFailingObserverElsewhereCannotStopPublication()
    {
        string appId = NewAppId();
        using var publisher = FamilyTelemetryPublisher.TryStart(appId, "1.0.0");
        Assert.NotNull(publisher);
        using var source = new SampleMonitor("power", "test source", DataSourceMode.Live);
        Action failing = () => throw new InvalidOperationException("observer failed");
        MonitoringState.Changed += failing;
        try
        {
            source.Commit(new PowerSample { TimeSec = 1, GpuTotalW = 321 }, 1);

            await WaitFor(appId, f => PowerW(f) == 321);
        }
        finally
        {
            MonitoringState.Changed -= failing;
        }
    }

    [Fact]
    public async Task AWriterThatThrowsNeverEscapesThePublisher()
    {
        var writer = new RecordingWriter(throwOnPublish: true);
        using var publisher = new FamilyTelemetryPublisher(writer);

        await Until(() => publisher.LastError is not null);

        Assert.Contains("writer exploded", publisher.LastError);
        publisher.Dispose();
        Assert.True(writer.Disposed);
    }

    [Fact]
    public async Task OneOfflineFrameIsWrittenBeforeTheWriterIsDisposed()
    {
        var writer = new RecordingWriter();
        using var publisher = new FamilyTelemetryPublisher(writer);
        await Until(() => writer.Frames.Count >= 3);

        publisher.Dispose();

        var frames = writer.Frames;
        Assert.False(frames[^1]);
        Assert.Single(frames, online => !online);
        Assert.Equal(frames.Count, writer.FramesWhenDisposed);
    }

    [Fact]
    public void APublisherWithoutAWriterIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new FamilyTelemetryPublisher(null!));
    }

    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(6);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The expected publication never happened.");
            await Task.Delay(10);
        }
    }

    private sealed class RecordingWriter(bool throwOnPublish = false) : IFamilyFrameWriter
    {
        private readonly object _gate = new();
        private readonly List<bool> _frames = [];

        public TimeSpan PublishInterval => TimeSpan.FromMilliseconds(20);
        public long Sequence => 0;
        public string? LastError => null;
        public bool Disposed { get; private set; }
        public int FramesWhenDisposed { get; private set; }
        public IReadOnlyList<bool> Frames { get { lock (_gate) return [.. _frames]; } }

        public bool TryPublish(object data, bool online = true)
        {
            lock (_gate) _frames.Add(online);
            return throwOnPublish ? throw new InvalidOperationException("writer exploded") : true;
        }

        public void Dispose()
        {
            lock (_gate) FramesWhenDisposed = _frames.Count;
            Disposed = true;
        }
    }
}
