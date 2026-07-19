namespace GpuSuite.Core.Models;

/// <summary>A test resolution, e.g. 1080p = 1920x1080.</summary>
public sealed class Resolution
{
    public string Name { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }

    public Resolution() { }

    public Resolution(string name, int width, int height)
    {
        Name = name;
        Width = width;
        Height = height;
    }

    public string Label => $"{Width}x{Height}";
    public override string ToString() => $"{Name} ({Width}x{Height})";

    public static readonly Resolution Fhd = new("1080p", 1920, 1080);
    public static readonly Resolution Qhd = new("1440p", 2560, 1440);
    public static readonly Resolution Uhd = new("4K", 3840, 2160);

    public static Resolution? FromName(string name) => name.ToLowerInvariant() switch
    {
        "1080p" or "fhd" or "1920x1080" => Fhd,
        "1440p" or "qhd" or "2560x1440" => Qhd,
        "4k" or "uhd" or "2160p" or "3840x2160" => Uhd,
        _ => null
    };
}

/// <summary>How a scene is driven.</summary>
public enum SceneKind
{
    /// <summary>The game's built-in benchmark runs and exits on its own.</summary>
    BuiltInBenchmark,
    /// <summary>A custom playable scene driven by the bot/input engine.</summary>
    BotDriven,
    /// <summary>A fixed-duration manual/idle capture window (no input automation).</summary>
    FixedWindow
}

/// <summary>Where a measurement value originated.</summary>
public enum DataSourceMode
{
    /// <summary>Real hardware/process capture.</summary>
    Live,
    /// <summary>Synthetic data because the source was unavailable (degraded mode).</summary>
    Synthetic,
    /// <summary>Replayed from a previously recorded file.</summary>
    Replay
}

/// <summary>Result of validating a single run.</summary>
public enum RunVerdict
{
    Valid,
    Outlier,
    Invalid
}
