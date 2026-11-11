using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace GpuSuite.Tests;

internal sealed record FamilyFrame(long Sequence, long Ticks, string Json, JsonElement Envelope)
{
    public JsonElement Data => Envelope.GetProperty("data");
    public bool Online => Envelope.GetProperty("online").GetBoolean();
}

/// <summary>
/// Independent C# port of Read-Frame in the family reader (Tools/Read-FaganasTelemetry.ps1). Tests read frames through
/// this, never through the writer's own types, so they keep proving the wire format after the writer is swapped.
/// </summary>
internal static class FamilyFrameReader
{
    private const int HeaderSize = 128;
    private const int MaxPayloadBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string CurrentUserSid { get; } = ReadCurrentUserSid();

    public static string MappingName(string appId) => $@"Local\Cybenetics.Faganas.{CurrentUserSid}.{appId}.Telemetry.v1";

    private static string ReadCurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }

    public static MemoryMappedFile Open(string appId) =>
        MemoryMappedFile.OpenExisting(MappingName(appId), MemoryMappedFileRights.Read);

    /// <summary>Reads under the frame mutex and applies every header and envelope rule the family reader applies.</summary>
    public static FamilyFrame Read(string appId)
    {
        using var map = Open(appId);
        using var view = CreateView(map);
        using var mutex = Mutex.OpenExisting(MappingName(appId) + ".Frame");
        bool held;
        try { held = mutex.WaitOne(100); }
        catch (AbandonedMutexException) { held = true; }
        if (!held) throw new InvalidDataException("Telemetry frame is busy.");
        try { return Parse(view, appId); }
        finally { mutex.ReleaseMutex(); }
    }

    /// <summary>Validates a page a reader still holds after its writer is gone (there is no mutex left to take).</summary>
    public static FamilyFrame ReadRetained(MemoryMappedFile map, string appId)
    {
        using var view = CreateView(map);
        return Parse(view, appId);
    }

    private static MemoryMappedViewAccessor CreateView(MemoryMappedFile map) =>
        map.CreateViewAccessor(0, HeaderSize + MaxPayloadBytes, MemoryMappedFileAccess.Read);

    private static FamilyFrame Parse(MemoryMappedViewAccessor view, string appId)
    {
        byte[] magic = new byte[8];
        view.ReadArray(0, magic, 0, magic.Length);
        if (Encoding.ASCII.GetString(magic) != "FGTEL001" || view.ReadInt32(8) != 1)
            throw new InvalidDataException("Unknown telemetry schema.");

        int length = view.ReadInt32(12);
        if (length <= 0 || length > MaxPayloadBytes) throw new InvalidDataException("Incomplete or oversized telemetry frame.");
        long sequence = view.ReadInt64(16);
        long ticks = view.ReadInt64(24);
        if (sequence <= 0 || ticks <= 0 || ticks > DateTime.MaxValue.Ticks)
            throw new InvalidDataException("Invalid frame identity or time.");

        byte[] payload = new byte[length];
        byte[] expected = new byte[32];
        view.ReadArray(HeaderSize, payload, 0, length);
        view.ReadArray(40, expected, 0, expected.Length);
        if (!SHA256.HashData(payload).AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("Telemetry checksum mismatch.");

        string json = StrictUtf8.GetString(payload);
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        DateTime published = DateTimeOffset.Parse(root.GetProperty("publishedUtc").GetString()!, CultureInfo.InvariantCulture).UtcDateTime;
        bool coherent = root.GetProperty("schemaVersion").GetInt32() == 1
            && root.GetProperty("appId").GetString() == appId
            && root.GetProperty("sequence").GetInt64() == sequence
            && (root.GetProperty("online").ValueKind is JsonValueKind.True or JsonValueKind.False)
            && published.Ticks == ticks;
        if (!coherent) throw new InvalidDataException("Telemetry envelope/header mismatch.");
        return new FamilyFrame(sequence, ticks, json, root.Clone());
    }
}
