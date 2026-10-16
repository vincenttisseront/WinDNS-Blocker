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

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    /// <summary>
    /// Best-effort ACL so interactive users can read logs while SYSTEM retains full control.
    /// Safe to call repeatedly; no-ops on non-Windows or if icacls is unavailable.
    /// </summary>
    public static void TryHardenDataDirectoryAcl()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            EnsureDirectories();
            // SIDs work on FR/EN Server (BUILTIN\Administrators often fails as Administrateurs).
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "icacls.exe",
                Arguments = $"\"{DataDirectory}\" /grant *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch
        {
            // ignore — doctor.ps1 can fix ACLs later
        }
    }
}
