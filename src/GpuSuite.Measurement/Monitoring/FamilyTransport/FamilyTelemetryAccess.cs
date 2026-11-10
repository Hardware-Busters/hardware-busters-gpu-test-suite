using System.IO.MemoryMappedFiles;
using System.Security.Principal;

namespace GpuSuite.Measurement.Monitoring.FamilyTransport;

internal enum TelemetryKernelRights { FullControl = 0x1FFFFF }

internal static class FamilyTelemetryAccess
{
    internal static SecurityIdentifier CurrentUser { get; } = ReadCurrentUser();

    internal static Mutex CreateMutex(string name)
    {
        var mutex = new Mutex(false, name, out bool created);
        try
        {
            if (created) CurrentUserOnlySecurity<TelemetryKernelRights>.For(CurrentUser).ApplyTo(mutex.SafeWaitHandle);
            return mutex;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    internal static Semaphore CreateSemaphore(string name)
    {
        var semaphore = new Semaphore(1, 1, name, out bool created);
        try
        {
            if (created) CurrentUserOnlySecurity<TelemetryKernelRights>.For(CurrentUser).ApplyTo(semaphore.SafeWaitHandle);
            return semaphore;
        }
        catch
        {
            semaphore.Dispose();
            throw;
        }
    }

    internal static MemoryMappedFile CreateSection(string name, long capacity)
    {
        MemoryMappedFile section;
        try { section = MemoryMappedFile.CreateNew(name, capacity, MemoryMappedFileAccess.ReadWrite); }
        catch (IOException)
        {
            return MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
        }
        try
        {
            CurrentUserOnlySecurity<TelemetryKernelRights>.For(CurrentUser).ApplyTo(section.SafeMemoryMappedFileHandle);
            return section;
        }
        catch
        {
            section.Dispose();
            throw;
        }
    }

    internal static bool TryEnter(Mutex mutex, int timeoutMs)
    {
        try { return mutex.WaitOne(timeoutMs); }
        catch (AbandonedMutexException) { return true; }
    }

    private static SecurityIdentifier ReadCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("The Windows identity has no user SID.");
    }
}
