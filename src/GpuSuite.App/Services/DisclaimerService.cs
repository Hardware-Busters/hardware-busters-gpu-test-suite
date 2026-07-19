using System.Globalization;
using System.IO;
using System.Windows;

namespace GpuSuite.App.Services;

/// <summary>
/// Owns the versioned first-run disclaimer acknowledgement.  The acknowledgement is deliberately
/// stored outside the portable workspace so changing benchmark folders does not repeatedly prompt
/// the same Windows user.  Changing <see cref="CurrentVersion"/> presents revised terms again.
/// </summary>
public sealed class DisclaimerService
{
    public const string CurrentVersion = "2026-07-19-v3";

    private readonly string _acceptancePath;
    private readonly Func<(bool Accepted, bool Remember)> _prompt;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<string> _warning;

    public DisclaimerService()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Hardware Busters",
                "GpuTestSuite",
                "disclaimer-acknowledgement.txt"),
            PromptForAcknowledgement,
            () => DateTime.UtcNow,
            ShowPersistenceWarning)
    {
    }

    /// <summary>Test seam for acknowledgement storage, prompt, time, and non-fatal save warnings.</summary>
    internal DisclaimerService(
        string acceptancePath,
        Func<(bool Accepted, bool Remember)> prompt,
        Func<DateTime> utcNow,
        Action<string> warning)
    {
        _acceptancePath = acceptancePath;
        _prompt = prompt;
        _utcNow = utcNow;
        _warning = warning;
    }

    public bool IsCurrentVersionAcknowledged()
    {
        try
        {
            return File.Exists(_acceptancePath) && File.ReadLines(_acceptancePath)
                .Any(line => string.Equals(line.Trim(), $"Version={CurrentVersion}", StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Shows the blocking first-run notice when this version has not been acknowledged.</summary>
    public bool EnsureAcknowledged()
    {
        if (IsCurrentVersionAcknowledged()) return true;

        var prompt = _prompt();
        if (!prompt.Accepted) return false;

        // The acknowledgement permits this launch.  Persistence is a separate, explicit choice:
        // leaving the option clear presents the notice again on the next launch.
        if (!prompt.Remember) return true;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_acceptancePath)!);
            File.WriteAllText(_acceptancePath,
                $"Version={CurrentVersion}{Environment.NewLine}" +
                $"AcknowledgedUtc={_utcNow().ToString("O", CultureInfo.InvariantCulture)}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            // Do not trap a user after they have explicitly acknowledged the notice.  Explain that
            // it could not be remembered, so it may be shown again on the next launch.
            _warning(ex.Message);
        }

        return true;
    }

    /// <summary>Opens the same complete notice for review from the title bar or Help.</summary>
    public void ShowForReview(Window? owner)
    {
        var dialog = new DisclaimerWindow(requireAcknowledgement: false);
        if (owner is not null)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        dialog.ShowDialog();
    }

    private static (bool Accepted, bool Remember) PromptForAcknowledgement()
    {
        var dialog = new DisclaimerWindow(requireAcknowledgement: true);
        return (dialog.ShowDialog() == true, dialog.DontShowAgain);
    }

    private static void ShowPersistenceWarning(string errorMessage)
    {
        MessageBox.Show(
            $"Your acknowledgement could not be saved and the disclaimer may appear again next time.\n\n{errorMessage}",
            "Hardware Busters GPU Test Suite disclaimer",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
