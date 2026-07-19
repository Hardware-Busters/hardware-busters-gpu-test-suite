using System.Runtime.InteropServices;
using System.Text;

namespace GpuSuite.Core.Security;

/// <summary>
/// Stores API keys in the current Windows user's Credential Manager vault.  Credentials are never serialized to
/// settings.json, copied into reports, or written to run logs.  Environment variables remain a supported fallback
/// for headless/managed deployments.
/// </summary>
public static class ApiKeyStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const string Prefix = "GpuTestSuite/";

    public static string? Read(string provider)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!CredRead(Target(provider), CredTypeGeneric, 0, out var ptr)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(ptr);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }
        finally { CredFree(ptr); }
    }

    public static bool Save(string provider, string secret)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(secret)) return false;
        var bytes = Encoding.Unicode.GetBytes(secret);
        IntPtr blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = Target(provider),
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = "GpuTestSuite"
            };
            return CredWrite(ref credential, 0);
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public static bool Delete(string provider) => !OperatingSystem.IsWindows() || CredDelete(Target(provider), CredTypeGeneric, 0);

    public static string? ReadOrEnvironment(string provider, string environmentVariable)
        => Read(provider) ?? Environment.GetEnvironmentVariable(environmentVariable);

    public static bool IsStored(string provider) => !string.IsNullOrWhiteSpace(Read(provider));

    private static string Target(string provider) => Prefix + provider.Trim().ToLowerInvariant();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite([In] ref Credential credential, uint flags);
    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);
    [DllImport("Advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);
}
