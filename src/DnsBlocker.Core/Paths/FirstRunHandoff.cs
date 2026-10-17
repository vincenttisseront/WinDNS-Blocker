namespace DnsBlocker.Core.Paths;

/// <summary>
/// Install Setup assistant ↔ UI first-run wizard handoff.
/// Uses the PR #6 contract: <c>setup-complete.flag</c> + config <c>setupWizardCompleted</c>.
/// CLI args <c>/setup</c> (install health UI) and <c>/firstrun</c> (force config wizard).
/// </summary>
public static class FirstRunHandoff
{
    public const string ArgSetup = "/setup";
    public const string ArgFirstRun = "/firstrun";

    /// <summary>Legacy alias — prefer <see cref="AppPaths.SetupCompleteFlagPath"/>.</summary>
    public static string PendingFlagPath => AppPaths.SetupCompleteFlagPath;

    public static bool IsPending() => !AppPaths.SetupCompleteFlagExists();

    /// <summary>
    /// Ensure wizard will show: clear completion flag so SetupWizardGate opens.
    /// </summary>
    public static void MarkPending(string source)
    {
        AppPaths.EnsureDirectories();
        AppPaths.ClearSetupComplete();
        var note = Path.Combine(AppPaths.DataDirectory, "firstrun-handoff.log");
        try
        {
            File.AppendAllText(note,
                $"{DateTimeOffset.UtcNow:o} handoff source={source}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }

    public static void ClearPending() => AppPaths.MarkSetupComplete();
}
