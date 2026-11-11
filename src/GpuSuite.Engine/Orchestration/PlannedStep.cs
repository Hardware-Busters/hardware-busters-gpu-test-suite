namespace GpuSuite.Engine.Orchestration;

/// <summary>
/// One planned benchmark cell. <paramref name="Key"/> identifies it, <paramref name="Definition"/> also says how it is executed
/// (so a changed repeat count changes the plan), and <paramref name="Label"/> is the text a person reads in a checklist.
/// </summary>
internal sealed record PlannedStep(string Key, string GameId, string Definition, string Label)
{
    /// <summary>The one label format of a step, scrubbed because profile names are free text.</summary>
    internal static string LabelOf(string gameName, string sceneName, string variantId, string resolutionName)
        => MethodTextScrubber.Scrub($"{gameName} / {sceneName} / {variantId} / {resolutionName}");
}