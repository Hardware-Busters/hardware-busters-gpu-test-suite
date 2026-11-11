using System.Text.RegularExpressions;

namespace GpuSuite.Engine.Orchestration;

/// <summary>Removes file-system paths and the Windows user name from free text before it is published to monitoring readers.</summary>
internal static class MethodTextScrubber
{
    private const string PathMarker = "[path]";
    private const string UserMarker = "[user]";

    // An absolute path runs to the next quote or line end, because folder names may contain spaces.
    private static readonly Regex AbsolutePath = new(@"(?:[A-Za-z]:[\\/]|\\\\)[^'""<>|\r\n]*", RegexOptions.CultureInvariant);

    private static readonly Regex? UserName = string.IsNullOrWhiteSpace(Environment.UserName)
        ? null
        : CreateUserNamePattern(Environment.UserName);

    // Windows machine and managed service account names can end in '$'.
    internal static Regex CreateUserNamePattern(string userName) =>
        new(@"(?<![\w$])" + Regex.Escape(userName) + @"(?![\w$])",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static string Scrub(string text)
    {
        string withoutPaths = AbsolutePath.Replace(text, PathMarker);
        return UserName?.Replace(withoutPaths, UserMarker) ?? withoutPaths;
    }
}
