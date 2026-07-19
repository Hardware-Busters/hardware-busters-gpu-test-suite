using System.Diagnostics;

namespace GpuSuite.App.Services;

/// <summary>Opens only reviewed public links. Support links must never be assembled from user input.</summary>
public sealed class ExternalLinkService
{
    public const string PatreonUrl = "https://www.patreon.com/hardwarebusters";
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        PatreonUrl
    };

    public static bool IsAllowed(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && Allowed.Contains(uri.AbsoluteUri);

    public bool Open(string url)
    {
        if (!IsAllowed(url)) return false;
        try { return Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }) is not null; }
        catch { return false; }
    }
}
