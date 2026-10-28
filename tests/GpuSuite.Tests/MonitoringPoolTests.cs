using System.Text.Json;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Models;
using GpuSuite.Measurement.Monitoring;
using Xunit;

namespace GpuSuite.Tests;

[CollectionDefinition("Monitoring", DisableParallelization = true)]
public sealed class MonitoringCollection { }

[Collection("Monitoring")]
public sealed class MonitoringPoolTests
{
    private static async Task<JsonElement> WaitFor(Func<JsonElement, bool> predicate)
    {
        var until = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < until)
        {
            var text = MonitoringPool.ReadJson(Environment.ProcessId);
            if (text is not null)
            {
                using var json = JsonDocument.Parse(text);
                try { if (predicate(json.RootElement)) return json.RootElement.Clone(); }
                catch (InvalidOperationException) { /* The initial page may precede source registration. */ }
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("Monitoring worker did not publish the expected state.");
    }

    private static JsonElement Channel(JsonElement root, string kind) =>
        root.GetProperty("channels").EnumerateArray().Single(c => c.GetProperty("kind").GetString() == kind);

    [Fact]
    public async Task BurstPublishesLatestWithoutInventingAcquisitionOnHeartbeat()
    {
        using var pool = new MonitoringPool();
        using var source = new SampleMonitor("power", "test synthetic", DataSourceMode.Synthetic);
        for (int i = 1; i <= 500; i++) source.Commit(new PowerSample { TimeSec = i, GpuTotalW = i }, i);
        var first = await WaitFor(r => Channel(r, "power").GetProperty("sequence").GetInt64() == 500);
        var firstChannel = Channel(first, "power");
        var second = await WaitFor(r => r.GetProperty("publishedUtc").GetDateTimeOffset() > first.GetProperty("publishedUtc").GetDateTimeOffset());
        Assert.Equal(500, Channel(second, "power").GetProperty("sequence").GetInt64());
        Assert.Equal(firstChannel.GetProperty("receivedUtc").GetString(), Channel(second, "power").GetProperty("receivedUtc").GetString());
        Assert.True(firstChannel.GetProperty("simulation").GetBoolean());
        Assert.Equal(JsonValueKind.Null, firstChannel.GetProperty("acquiredUtc").ValueKind);
    }

    [Fact]
    public async Task StoppedAndSupersededSourcesCannotRepublish()
    {
        using var pool = new MonitoringPool();
        using var old = new SampleMonitor("frames", "old", DataSourceMode.Synthetic);
        using var next = new SampleMonitor("frames", "next", DataSourceMode.Replay);
        old.Commit(new FrameSample(1, 10), 1);
        next.Commit(new FrameSample(2, 20), 2);
        old.Dispose();
        next.Dispose();
        next.Commit(new FrameSample(3, 30), 3);
        var snapshot = await WaitFor(r => Channel(r, "frames").GetProperty("status").GetString() == "stopped");
        var channel = Channel(snapshot, "frames");
        Assert.Equal("next", channel.GetProperty("source").GetString());
        Assert.Equal(1, channel.GetProperty("sequence").GetInt64());
        Assert.False(channel.GetProperty("fresh").GetBoolean());
        Assert.Equal("Replay", channel.GetProperty("mode").GetString());
    }

    [Fact]
    public void ObserverFailuresDoNotEscapeOrSuppressLaterObservers()
    {
        int calls = 0;
        Action bad = () => throw new InvalidOperationException("observer failed");
        Action good = () => calls++;
        MonitoringState.Changed += bad;
        MonitoringState.Changed += good;
        try { using var source = new SampleMonitor("telemetry", "test", DataSourceMode.Live); source.Commit(new TelemetrySample(), 0); }
        finally { MonitoringState.Changed -= bad; MonitoringState.Changed -= good; }
        Assert.Equal(3, calls); // register, committed sample, stop
        using var logger = new RunLogger(null, false);
        logger.EntryLogged += _ => throw new InvalidOperationException("view failed");
        logger.Error("Capture", "sensitive command must not be shared");
        Assert.Equal("Capture", MonitoringState.LastEvent!.Stage);
    }

    [Fact]
    public void UncommittedOperationExitIsNotSuccess()
    {
        using (new MonitoringOperationScope(new("preparing", "test"))) { }
        Assert.Equal("interrupted", MonitoringState.Read().Operation.State);
        Assert.Null(MonitoringState.Read().Operation.Verdict);
        using (var op = new MonitoringOperationScope(new("capturing"))) op.Complete("Invalid");
        Assert.Equal("Invalid", MonitoringState.Read().Operation.Verdict);
    }

    [Fact]
    public async Task UnknownAndNonFiniteReadingsRemainNull()
    {
        using var pool = new MonitoringPool();
        using var source = new SampleMonitor("telemetry", "test", DataSourceMode.Live);
        source.Commit(new TelemetrySample { TimeSec = 3, GpuTempC = double.NaN }, 3);
        var snapshot = await WaitFor(r => Channel(r, "telemetry").GetProperty("sequence").GetInt64() == 1);
        var sample = Channel(snapshot, "telemetry").GetProperty("sample");
        Assert.Equal(JsonValueKind.Null, sample.GetProperty("gpuTempC").ValueKind);
        Assert.Equal(JsonValueKind.Null, sample.GetProperty("gpuVramTempC").ValueKind);
    }

    [Fact]
    public async Task DisposePublishesOfflineAndDetachesFutureUpdates()
    {
        var pool = new MonitoringPool();
        using var source = new SampleMonitor("power", "test", DataSourceMode.Synthetic);
        source.Commit(new PowerSample { TimeSec = 1, GpuTotalW = 50 }, 1);
        await WaitFor(r => Channel(r, "power").GetProperty("sequence").GetInt64() == 1);
        // Holding a reader keeps the final offline page alive after the producer closes its handles.
        using var retainedReader = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(
            MonitoringPool.Name(Environment.ProcessId), System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read);
        pool.Dispose();
        var offline = await WaitFor(r => !r.GetProperty("online").GetBoolean());
        source.Commit(new PowerSample { TimeSec = 2, GpuTotalW = 60 }, 2);
        Assert.Equal(offline.GetRawText(), MonitoringPool.ReadJson(Environment.ProcessId));
        pool.Dispose(); // idempotent
    }

    [Fact]
    public async Task ChangeArrivingDuringSerializationIsRetained()
    {
        using var pool = new MonitoringPool();
        using var source = new SampleMonitor("power", "test", DataSourceMode.Synthetic);
        source.Commit(new SampleWithCallback(() => source.Commit(new PowerSample { TimeSec = 2, GpuTotalW = 75 }, 2)), 1);
        var snapshot = await WaitFor(r => Channel(r, "power").GetProperty("sequence").GetInt64() == 2);
        Assert.Equal(75, Channel(snapshot, "power").GetProperty("sample").GetProperty("gpuTotalW").GetDouble());
    }

    public sealed class SampleWithCallback(Action callback)
    {
        public double Reading { get { callback(); return 10; } }
    }

    [Fact]
    public async Task HeartbeatDoesNotKeepAnOldSourceFresh()
    {
        using var pool = new MonitoringPool();
        using var source = new SampleMonitor("frames", "test", DataSourceMode.Live);
        source.Commit(new FrameSample(1, 10), 1);
        await WaitFor(r => Channel(r, "frames").GetProperty("fresh").GetBoolean());
        var stale = await WaitFor(r => !Channel(r, "frames").GetProperty("fresh").GetBoolean() &&
            Channel(r, "frames").GetProperty("sourceAgeMs").GetDouble() > 2000);
        Assert.Equal(1, Channel(stale, "frames").GetProperty("sequence").GetInt64());
    }

    [Fact]
    public async Task SerializationFailureCannotBreakProducersAndRecoversOnNextCommit()
    {
        using var pool = new MonitoringPool();
        using var source = new SampleMonitor("power", "test", DataSourceMode.Synthetic);
        source.Commit(new SampleWithCallback(() => throw new InvalidOperationException("serializer fixture")), 1);
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (pool.LastError is null && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.NotNull(pool.LastError);
        source.Commit(new PowerSample { TimeSec = 2, GpuTotalW = 40 }, 2);
        await WaitFor(r => Channel(r, "power").GetProperty("sequence").GetInt64() == 2);
        Assert.Null(pool.LastError);
    }
}
