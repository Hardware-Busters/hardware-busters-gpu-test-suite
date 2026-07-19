using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Diagnostics;

namespace GpuSuite.App.Converters;

/// <summary>Maps a <see cref="BenchmarkGameStatus"/> to its themed status brush.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is BenchmarkGameStatus s
            ? s switch
            {
                BenchmarkGameStatus.Ready => "StatusReadyBrush",
                BenchmarkGameStatus.NotInstalled => "StatusNotInstalledBrush",
                BenchmarkGameStatus.Invalid => "StatusInvalidBrush",
                BenchmarkGameStatus.Disabled => "StatusDisabledBrush",
                BenchmarkGameStatus.Installed => "StatusInstalledBrush",
                _ => "TextSecondaryBrush"
            }
            : "TextSecondaryBrush";
        return Application.Current?.TryFindResource(key) ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a pre-flight <see cref="CheckStatus"/> to its themed status brush (green/amber/red/blue/grey).</summary>
public sealed class CheckStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is CheckStatus s
            ? s switch
            {
                CheckStatus.Ok => "StatusReadyBrush",
                CheckStatus.Warn => "StatusNotInstalledBrush",
                CheckStatus.Blocker => "StatusInvalidBrush",
                CheckStatus.Info => "StatusInstalledBrush",
                CheckStatus.Skip => "StatusDisabledBrush",
                _ => "TextSecondaryBrush"
            }
            : "TextSecondaryBrush";
        return Application.Current?.TryFindResource(key) ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a pre-flight <see cref="CheckStatus"/> to a compact status glyph for the pip in front of a line.</summary>
public sealed class CheckStatusToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is CheckStatus s
            ? s switch
            {
                CheckStatus.Ok => "✔",       // ✔
                CheckStatus.Warn => "⚠",     // ⚠
                CheckStatus.Blocker => "✖",  // ✖
                CheckStatus.Info => "ℹ",     // ℹ
                CheckStatus.Skip => "–",     // –
                _ => "•"                      // •
            }
            : "•";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Shows the element when the value is "empty/false" and hides it otherwise — the inverse of a
/// truthiness test. Handles bool (false ⇒ Visible), int counts (0 ⇒ Visible), and null (⇒ Visible),
/// so it works for both "HasX" flags and ".Count" bindings.
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool truthy = value switch
        {
            bool b => b,
            int i => i > 0,
            _ => value is not null
        };
        return truthy ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Non-null/non-empty ⇒ Visible, else Collapsed.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool has = value is string str ? !string.IsNullOrWhiteSpace(str) : value is not null;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Renders a string-keyed dictionary (e.g. a variant's setting overrides) as "k=v, k=v".</summary>
public sealed class DictionaryToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IDictionary dict || dict.Count == 0) return "profile defaults";
        var parts = new List<string>();
        foreach (DictionaryEntry e in dict) parts.Add($"{e.Key}={e.Value}");
        return string.Join(", ", parts);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
