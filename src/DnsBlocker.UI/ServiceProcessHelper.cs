using System.Diagnostics;
using System.ServiceProcess;

namespace DnsBlocker.UI;

/// <summary>
/// Best-effort helpers to detect / start the DnsBlocker Windows Service from the UI.
/// </summary>
internal static class ServiceProcessHelper
{
    public const string ServiceName = "DnsBlocker";

    public static ServiceProbeResult Probe()
    {
        if (!OperatingSystem.IsWindows())
            return new ServiceProbeResult(false, false, "Non-Windows host");

        try
        {
            using var sc = new ServiceController(ServiceName);
            var status = sc.Status;
            return new ServiceProbeResult(
                Exists: true,
                Running: status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending,
                Detail: status.ToString());
        }
        catch (InvalidOperationException ex)
        {
            return new ServiceProbeResult(false, false, "Service introuvable: " + ex.Message);
        }
        catch (Exception ex)
        {
            return new ServiceProbeResult(false, false, ex.Message);
        }
    }

    /// <summary>
    /// Tries to start the service. Returns null on success, error message otherwise.
    /// </summary>
    public static string? TryStart(TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows())
            return "Démarrage du service uniquement sous Windows.";

        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
                return null;

            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
            return null;
        }
        catch (InvalidOperationException)
        {
            // Fall back to elevated sc.exe — ServiceController.Start may need admin.
            return TryStartElevated(timeout);
        }
        catch (Exception ex)
        {
            var elevated = TryStartElevated(timeout);
            return elevated ?? ("Échec démarrage service: " + ex.Message);
        }
    }

    private static string? TryStartElevated(TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"start {ServiceName}",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi);
            if (p is null)
                return "Impossible de lancer sc.exe (UAC annulé ?).";
            p.WaitForExit((int)timeout.TotalMilliseconds);

            // Re-probe
            var probe = Probe();
            if (probe.Running)
                return null;
            return $"Service non démarré (sc exit {p.ExitCode}). {probe.Detail}";
        }
        catch (Exception ex)
        {
            return "Élévation / sc start échoué: " + ex.Message;
        }
    }
}

internal readonly record struct ServiceProbeResult(bool Exists, bool Running, string Detail);
