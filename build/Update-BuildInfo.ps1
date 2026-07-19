param([Parameter(Mandatory)][string]$OutputFile)
$content = @'
namespace GpuSuite;
internal static class BuildInfo
{
    public const string ProductName = "Hardware Busters GPU Test Suite";
    public const string Version = "0.0.0-public";
    public const int Revision = 0;
    public const string BuildTimestamp = "public-export";
    public const string CommitHash = "public-main";
    public const string DisplayVersion = "Hardware Busters GPU Test Suite v0.0.0-public";
    public const string InformationalVersion = "0.0.0-public+main";
}
'@
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputFile) | Out-Null
[IO.File]::WriteAllText($OutputFile, $content, [Text.UTF8Encoding]::new($false))