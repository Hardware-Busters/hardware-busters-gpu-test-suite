using System.Globalization;

namespace GpuSuite.Measurement.Monitoring;

internal static class FamilyMethodValues
{
    internal const int IdentityLimit = 64;
    internal const int LabelLimit = 96;
    internal const int TextLimit = 240;
    internal const int ScalarMemberLimit = 16;
    internal const int ExtraMemberLimit = 32;
    internal const int PlanStepLimit = 256;
    private const int NameLimit = 32;

    internal static string? Text(string? value, int limit)
    {
        if (value is null) return null;
        string clean = string.Concat(value.Select(c => c <= '\u001f' ? ' ' : c));
        if (string.IsNullOrWhiteSpace(clean)) return null;
        if (clean.Length <= limit) return clean;
        int length = limit - 1;
        if (char.IsHighSurrogate(clean[length - 1]) && char.IsLowSurrogate(clean[length])) length--;
        return clean[..length] + "\u2026";
    }

    internal static string Timestamp(DateTime value) =>
        (value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc))
        .ToString("o", CultureInfo.InvariantCulture);

    internal static Dictionary<string, object?> Scalars(IReadOnlyDictionary<string, object?> source, int limit)
    {
        var result = new Dictionary<string, object?>();
        foreach (var entry in source)
        {
            if (!ValidName(entry.Key)) continue;
            result[entry.Key] = Scalar(entry.Value);
            if (result.Count == limit) break;
        }
        return result;
    }

    private static bool ValidName(string name) => name.Length is > 0 and <= NameLimit &&
        IsLetter(name[0]) && name.All(c => IsLetter(c) || c is >= '0' and <= '9' or '_');

    private static bool IsLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static object? Scalar(object? value) => value switch
    {
        string text => Text(text, TextLimit),
        bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal => value,
        float number when float.IsFinite(number) => number,
        double number when double.IsFinite(number) => number,
        _ => null
    };
}
