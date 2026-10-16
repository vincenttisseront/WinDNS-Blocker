namespace DnsBlocker.Core.Paths;

/// <summary>
/// Shared handoff between the post-install Setup assistant and the main UI
/// (and any parallel first-run wizard worker).
/// </summary>
public static class FirstRunHandoff
{
    public const string ArgSetup = "/setup";
    public const string ArgFirstRun = "/firstrun";

    /// <summary>Presence of this file means the UI should open the guided initial configuration.</summary>
    public static string PendingFlagPath => Path.Combine(AppPaths.DataDirectory, "firstrun.pending");

    public static bool IsPending()
    {
        try
        {
            return File.Exists(PendingFlagPath);
        }
        catch
        {
            return false;
        }
    }

    public static void MarkPending(string source)
    {
        AppPaths.EnsureDirectories();
        var payload =
            "{\n" +
            "  \"source\": \"" + Escape(source) + "\",\n" +
            "  \"createdAt\": \"" + DateTimeOffset.UtcNow.ToString("o") + "\"\n" +
            "}\n";
        File.WriteAllText(PendingFlagPath, payload);
    }

    public static void ClearPending()
    {
        try
        {
            if (File.Exists(PendingFlagPath))
                File.Delete(PendingFlagPath);
        }
        catch
        {
            // ignore — UI can still proceed
        }
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
