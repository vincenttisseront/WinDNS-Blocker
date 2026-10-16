namespace DnsBlocker.Core.Paths;

public static class AppPaths
{
    public const string PipeName = "DnsBlocker";

    public static string DataDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                return Path.Combine(programData, "DnsBlocker");
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".dnsblocker");
        }
    }

    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");
    public static string StateDbPath => Path.Combine(DataDirectory, "state.db");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// Written when the first-run wizard finishes. Shared contract with install/repair tooling.
    /// </summary>
    public static string SetupCompleteFlagPath => Path.Combine(DataDirectory, "setup-complete.flag");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    public static void MarkSetupComplete()
    {
        EnsureDirectories();
        File.WriteAllText(SetupCompleteFlagPath,
            $"completedUtc={DateTimeOffset.UtcNow:O}{Environment.NewLine}");
    }

    public static void ClearSetupComplete()
    {
        try
        {
            if (File.Exists(SetupCompleteFlagPath))
                File.Delete(SetupCompleteFlagPath);
        }
        catch
        {
            // ignore
        }
    }

    public static bool SetupCompleteFlagExists() => File.Exists(SetupCompleteFlagPath);
}
