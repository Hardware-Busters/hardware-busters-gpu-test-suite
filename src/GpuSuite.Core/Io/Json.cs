using System.Text.Json;
using System.Text.Json.Serialization;

namespace GpuSuite.Core.Io;

/// <summary>Centralized JSON options + load/save helpers used for all structured files.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
    }

    public static T? Load<T>(string path)
        => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : default;

    public static string ToString<T>(T value) => JsonSerializer.Serialize(value, Options);
}
