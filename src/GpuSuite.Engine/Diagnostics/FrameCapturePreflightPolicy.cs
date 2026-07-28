using GpuSuite.Core.Models;

namespace GpuSuite.Engine.Diagnostics;

/// <summary>
/// Describes the real FPS/frametime backends that the currently enabled roster can use. This is deliberately
/// separate from the FFmpeg/Elgato vision path: a capture card is never a frame-timing source.
/// </summary>
public sealed record FrameCapturePreflightPlan(
    bool HasEnabledGames,
    bool RequiresPresentMon,
    bool RequiresRtss,
    bool MayUseRtssFallback,
    IReadOnlyList<string> PresentMonGames,
    IReadOnlyList<string> RtssGames,
    IReadOnlyList<string> AutoGames)
{
    public static FrameCapturePreflightPlan Build(IEnumerable<GameProfile> profiles, string? globalFrameProvider)
    {
        var presentMonGames = new List<string>();
        var rtssGames = new List<string>();
        var autoGames = new List<string>();
        string global = Normalize(globalFrameProvider);

        foreach (var profile in profiles.Where(p => p.Enabled))
        {
            string target = Describe(profile);
            string profileProvider = Normalize(profile.FrameProvider);

            // This ordering intentionally mirrors MeasurementFactory.PrepareFrameBackend / FrameProviderPolicy.
            if (profile.ForbidRtss)
                presentMonGames.Add(target);
            else if (profile.LateRtss)
                rtssGames.Add(target);
            else if (global == "rtss" || profileProvider == "rtss")
                rtssGames.Add(target);
            else if (profileProvider == "presentmon")
                presentMonGames.Add(target);
            else if (global == "presentmon")
                presentMonGames.Add(target);
            else
                autoGames.Add(target);
        }

        return new FrameCapturePreflightPlan(
            HasEnabledGames: presentMonGames.Count + rtssGames.Count + autoGames.Count > 0,
            RequiresPresentMon: presentMonGames.Count > 0,
            RequiresRtss: rtssGames.Count > 0,
            MayUseRtssFallback: autoGames.Count > 0,
            PresentMonGames: presentMonGames,
            RtssGames: rtssGames,
            AutoGames: autoGames);
    }

    public FrameCapturePreflightVerdict Evaluate(bool presentMonAvailable, bool rtssAvailable, bool syntheticFramesForced)
    {
        if (!HasEnabledGames)
            return new(true, "No enabled games require an FPS / frametime backend.");
        if (syntheticFramesForced)
            return new(true, "Synthetic frames are explicitly forced; real FPS / frametime capture is not being claimed.");
        if (RequiresPresentMon && !presentMonAvailable)
            return new(false, $"PresentMon is required by: {Format(PresentMonGames)}.");
        if (RequiresRtss && !rtssAvailable)
            return new(false, $"RTSS is required by: {Format(RtssGames)}. It remains lazy and starts only at the relevant game boundary.");
        if (MayUseRtssFallback && !presentMonAvailable && !rtssAvailable)
            return new(false, $"Auto-selected games have neither PresentMon nor RTSS available: {Format(AutoGames)}.");

        var available = new List<string>();
        if (presentMonAvailable) available.Add("PresentMon");
        if (rtssAvailable) available.Add("RTSS");
        return new(true, $"FPS / frametime backend ready: {string.Join(" + ", available)}. " +
                         $"PresentMon: {Format(PresentMonGames)}; RTSS: {Format(RtssGames)}; auto: {Format(AutoGames)}.");
    }

    private static string Describe(GameProfile profile) =>
        string.IsNullOrWhiteSpace(profile.Name) ? profile.Id : $"{profile.Id} ({profile.Name})";

    private static string Format(IReadOnlyList<string> games) => games.Count == 0 ? "none" : string.Join(", ", games);

    private static string Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "presentmon" => "presentmon",
        "rtss" => "rtss",
        _ => "auto"
    };
}

public sealed record FrameCapturePreflightVerdict(bool IsReady, string Detail);
