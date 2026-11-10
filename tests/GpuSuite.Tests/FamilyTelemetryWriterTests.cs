using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using GpuSuite.Measurement.Monitoring.FamilyTransport;
using Xunit;

namespace GpuSuite.Tests;

/// <summary>Tests the wire behaviour of the stopgap writer by reading frames the way the family reader does.</summary>
public sealed class FamilyTelemetryWriterTests
{
    // DELETE | READ_CONTROL | WRITE_DAC | WRITE_OWNER. Windows narrows the requested mask to each object type's own
    // all-access value (0xF001F for a section, 0x1F0001 for a mutex), so full control is checked by what they share.
    private const int StandardRightsRequired = 0xF0000;

    private static string NewAppId() => "t" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public void APublishedFrameSatisfiesEveryRuleOfTheFamilyReader()
    {
        string appId = NewAppId();
        using var writer = new FamilyTelemetryWriter(appId, "1.2.3");

        Assert.True(writer.TryPublish(new { answer = 42 }));

        var envelope = FamilyFrameReader.Read(appId).Envelope;
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(appId, envelope.GetProperty("appId").GetString());
        Assert.Equal("1.2.3", envelope.GetProperty("appVersion").GetString());
        Assert.Equal(Environment.ProcessId, envelope.GetProperty("pid").GetInt32());
        Assert.Equal(writer.InstanceId, envelope.GetProperty("instanceId").GetString());
        Assert.Equal(1, envelope.GetProperty("sequence").GetInt64());
        Assert.Equal(200, envelope.GetProperty("publishIntervalMs").GetInt32());
        Assert.True(envelope.GetProperty("online").GetBoolean());
        Assert.Equal(42, envelope.GetProperty("data").GetProperty("answer").GetInt32());
    }

    [Fact]
    public void ThePublicationTimeIsTheCurrentTime()
    {
        string appId = NewAppId();
        using var writer = new FamilyTelemetryWriter(appId, "1.0.0");

        var before = DateTime.UtcNow;
        writer.TryPublish(new { });
        var after = DateTime.UtcNow;

        Assert.InRange(new DateTime(FamilyFrameReader.Read(appId).Ticks, DateTimeKind.Utc), before, after);
    }

    [Fact]
    public void TheSequenceAdvancesOnEverySuccessfulPublication()
    {
        string appId = NewAppId();
        using var writer = new FamilyTelemetryWriter(appId, "1.0.0");

        for (int expected = 1; expected <= 3; expected++)
        {
            Assert.True(writer.TryPublish(new { }));
            Assert.Equal(expected, writer.Sequence);
            Assert.Equal(expected, FamilyFrameReader.Read(appId).Sequence);
        }
    }

    [Fact]
    public void OnlineFalseIsPublishedWhenAsked()
    {
        string appId = NewAppId();
        using var writer = new FamilyTelemetryWriter(appId, "1.0.0");

        writer.TryPublish(new { }, online: false);

        Assert.False(FamilyFrameReader.Read(appId).Online);
    }

    [Fact]
    public void NamesFollowTheFamilyConvention()
    {
        string sid = FamilyFrameReader.CurrentUserSid;

        Assert.Equal($@"Local\Cybenetics.Faganas.{sid}.gpu.Telemetry.v1", FamilyTelemetryWriter.MappingName("gpu"));
        Assert.Equal($@"Local\Cybenetics.Faganas.{sid}.gpu.Telemetry.v1.Frame", FamilyTelemetryWriter.FrameMutexName("gpu"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("A1")]
    [InlineData("1abc")]
    [InlineData("has-dash")]
    [InlineData("abcdefghijklmnopqrstuvwxy")]
    public void AnInvalidAppIdIsRefused(string appId)
    {
        Assert.Throws<ArgumentException>(() => FamilyTelemetryWriter.MappingName(appId));
        Assert.Throws<ArgumentException>(() => new FamilyTelemetryWriter(appId, "1.0.0"));
    }

    [Theory]
    [InlineData("gpu")]
    [InlineData("ab")]
    [InlineData("a1b2c3d4e5f6g7h8i9j0k1l2")]
    public void AValidAppIdIsAccepted(string appId)
    {
        Assert.EndsWith($".{appId}.Telemetry.v1", FamilyTelemetryWriter.MappingName(appId));
    }

    [Fact]
    public void ASecondWriterInTheSameProcessIsRefusedAndDoesNotReleaseTheFirstLease()
    {
        string appId = NewAppId();
        using var first = new FamilyTelemetryWriter(appId, "1.0.0");

        Assert.Throws<InvalidOperationException>(() => new FamilyTelemetryWriter(appId, "1.0.0"));
        Assert.Throws<InvalidOperationException>(() => new FamilyTelemetryWriter(appId, "1.0.0"));
    }

    [Fact]
    public void AnotherProcessHoldingTheWriterLeaseRefusesUsAndLeavesNoLeaseBehind()
    {
        string appId = NewAppId();
        using var otherProcess = new Semaphore(1, 1, FamilyTelemetryWriter.MappingName(appId) + ".Writer");
        Assert.True(otherProcess.WaitOne(0));

        Assert.Throws<InvalidOperationException>(() => new FamilyTelemetryWriter(appId, "1.0.0"));

        otherProcess.Release();
        using var writer = new FamilyTelemetryWriter(appId, "1.0.0");
        Assert.True(writer.TryPublish(new { }));
    }

    [Fact]
    public void AnotherWriterCanStartOnceTheFirstIsDisposed()
    {
        string appId = NewAppId();
        new FamilyTelemetryWriter(appId, "1.0.0").Dispose();

        using var second = new FamilyTelemetryWriter(appId, "2.0.0");

        Assert.True(second.TryPublish(new { }));
    }

    [Fact]
    public async Task ABusyFrameIsSkippedWithoutWaitingAndTheSequenceDoesNotAdvance()
    {
        string appId = NewAppId();
        using var writer = new FamilyTelemetryWriter(appId, "1.0.0");
        writer.TryPublish(new { });
        var held = new TaskCompletionSource();
        using var release = new ManualResetEventSlim();
        // A mutex belongs to the thread that took it, so the whole hold-and-release runs on one dedicated thread.
        var reader = Task.Factory.StartNew(() =>
        {
            try
            {
                using var mutex = Mutex.OpenExisting(FamilyTelemetryWriter.FrameMutexName(appId));
                mutex.WaitOne();
                held.SetResult();
                release.Wait();
                mutex.ReleaseMutex();
            }
            catch (Exception ex)
            {
                held.TrySetException(ex);
                throw;
            }
        }, TaskCreationOptions.LongRunning);

        bool published;
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            published = await Task.Run(() => writer.TryPublish(new { })).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
        }
        await reader.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(published);
        Assert.NotNull(writer.LastError);
        Assert.Equal(1, writer.Sequence);
        Assert.Equal(1, FamilyFrameReader.Read(appId).Sequence);
        Assert.True(writer.TryPublish(new { }));
        Assert.Equal(2, FamilyFrameReader.Read(appId).Sequence);
    }

    [Fact]
    public void AnOversizedPayloadIsRefusedAndThePreviousFrameSurvives()
    {
        string appId = NewAppId();
        using var writer = new FamilyTelemetryWriter(appId, "1.0.0");
        writer.TryPublish(new { kept = true });

        bool published = writer.TryPublish(new string('x', 1_100_000));

        Assert.False(published);
        Assert.NotNull(writer.LastError);
        Assert.Equal(1, writer.Sequence);
        var frame = FamilyFrameReader.Read(appId);
        Assert.Equal(1, frame.Sequence);
        Assert.True(frame.Data.GetProperty("kept").GetBoolean());
    }

    [Fact]
    public void PublishingAfterDisposeIsRefused()
    {
        string appId = NewAppId();
        var writer = new FamilyTelemetryWriter(appId, "1.0.0");
        writer.Dispose();

        Assert.False(writer.TryPublish(new { }));
        Assert.NotNull(writer.LastError);
    }

    [Fact]
    public void TheMappingIsLimitedToTheCurrentUser()
    {
        string appId = NewAppId();
        using var writer = new FamilyTelemetryWriter(appId, "1.0.0");
        using var mapping = MemoryMappedFile.OpenExisting(FamilyFrameReader.MappingName(appId), MemoryMappedFileRights.ReadPermissions);

        AssertLimitedToCurrentUser(mapping.SafeMemoryMappedFileHandle);
    }

    [Fact]
    public void TheMutexAndTheLeaseAreLimitedToTheCurrentUser()
    {
        string name = $@"Local\GpuSuite.Tests.{Guid.NewGuid():N}";
        using var mutex = FamilyTelemetryAccess.CreateMutex(name + ".Frame");
        using var semaphore = FamilyTelemetryAccess.CreateSemaphore(name + ".Writer");

        AssertLimitedToCurrentUser(mutex.SafeWaitHandle);
        AssertLimitedToCurrentUser(semaphore.SafeWaitHandle);
    }

    private static void AssertLimitedToCurrentUser(SafeHandle handle)
    {
        var acl = new KernelObjectAcl(handle);

        Assert.True(acl.AreAccessRulesProtected);
        var rule = Assert.Single(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<AccessRule<Rights>>());
        Assert.Equal(FamilyFrameReader.CurrentUserSid, ((SecurityIdentifier)rule.IdentityReference).Value);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(StandardRightsRequired, (int)rule.Rights & StandardRightsRequired);
    }

    private enum Rights { None = 0 }

    private sealed class KernelObjectAcl(SafeHandle handle)
        : NativeObjectSecurity(false, ResourceType.KernelObject, handle, AccessControlSections.Access)
    {
        public override Type AccessRightType => typeof(Rights);
        public override Type AccessRuleType => typeof(AccessRule<Rights>);
        public override Type AuditRuleType => typeof(AuditRule<Rights>);

        public override AccessRule AccessRuleFactory(IdentityReference identityReference, int accessMask, bool isInherited,
            InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AccessControlType type)
            => new AccessRule<Rights>(identityReference, (Rights)accessMask, type);

        public override AuditRule AuditRuleFactory(IdentityReference identityReference, int accessMask, bool isInherited,
            InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AuditFlags flags)
            => new AuditRule<Rights>(identityReference, (Rights)accessMask, flags);
    }
}
