namespace GpuSuite.Authoring;

/// <summary>Non-throwing gate shared by Author Studio command, transition, and deferred-checkpoint paths.</summary>
public static class AuthoringEditCommitCoordinator
{
    public enum InputSurface { Editor, DocumentSelector, Tab, Button, Other }
    public static bool TryCommit(IEnumerable<Func<bool>> committers, Action<string> reportFailure)
    {
        foreach (var committer in committers)
        {
            try
            {
                if (!committer())
                {
                    reportFailure("Complete or correct the highlighted editor field before continuing.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                reportFailure("Author Studio could not commit the current editor field: " + ex.Message);
                return false;
            }
        }
        return true;
    }

    public static bool IsScalarBindingValid(bool bindingHasError, bool validationHasError)
        => !bindingHasError && !validationHasError;

    public static bool RequiresOutboundPointerCommit(InputSurface surface)
        => surface is InputSurface.DocumentSelector or InputSurface.Tab or InputSurface.Button;

    public static bool RequiresOutboundKeyCommit(InputSurface surface, string key)
        => surface != InputSurface.Editor
           && (key.Equals("Tab", StringComparison.OrdinalIgnoreCase)
               || (surface is InputSurface.DocumentSelector or InputSurface.Tab
                   && key is "Enter" or "Left" or "Right" or "Up" or "Down"));
}
