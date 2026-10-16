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
    public static string SecretsDirectory => Path.Combine(DataDirectory, "secrets");
    public static string PreparedTargetsPath => Path.Combine(DataDirectory, "prepared-targets.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(SecretsDirectory);
    }
}
