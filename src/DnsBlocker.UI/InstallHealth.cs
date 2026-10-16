using System.IO;
using System.IO.Pipes;
using System.ServiceProcess;
using DnsBlocker.Core.Paths;

namespace DnsBlocker.UI;

/// <summary>
/// In-process equivalent of doctor.ps1 for the Setup GUI (no PowerShell CLI).
/// </summary>
internal static class InstallHealth
{
    public const string ServiceName = "DnsBlocker";
    public const string PipeName = "DnsBlocker";

    public sealed record CheckItem(string Id, bool Ok, bool Warning, string MessageFr);

    public sealed record Report(
        string InstallRoot,
        IReadOnlyList<CheckItem> Items,
        bool Healthy,
        bool SparseInstall,
        bool ServiceRegistered,
        bool ServiceRunning);

    public static string ResolveInstallRoot()
    {
        var candidates = new List<string>();

        // Prefer directory of this UI exe (…\DnsBlocker\UI → parent)
        try
        {
            var uiDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Directory.GetParent(uiDir)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent))
                candidates.Add(parent);
        }
        catch
        {
            // ignore
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ProgramFiles")))
            candidates.Add(Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles")!, "DnsBlocker"));
        candidates.Add(@"C:\Program Files\DnsBlocker");

        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(Path.Combine(c, "DnsBlocker.Service.exe")))
                return c;
        }

        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(c))
                return c;
        }

        return @"C:\Program Files\DnsBlocker";
    }

    public static Report Run()
    {
        var root = ResolveInstallRoot();
        var items = new List<CheckItem>();
        var svcExe = Path.Combine(root, "DnsBlocker.Service.exe");
        var uiExe = Path.Combine(root, "UI", "DnsBlocker.UI.exe");
        var hostFxr = Path.Combine(root, "hostfxr.dll");

        var hasSvc = File.Exists(svcExe);
        var hasUi = File.Exists(uiExe);
        items.Add(hasSvc
            ? new CheckItem("service_exe", true, false, "Binaire service présent : " + svcExe)
            : new CheckItem("service_exe", false, false,
                "Binaire service manquant : " + svcExe +
                " — installation incomplète (réparez avec le bouton ci-dessous)."));

        items.Add(hasUi
            ? new CheckItem("ui_exe", true, false, "Binaire interface présent : " + uiExe)
            : new CheckItem("ui_exe", false, false, "Binaire interface manquant : " + uiExe));

        if (File.Exists(hostFxr) || File.Exists(Path.Combine(root, "coreclr.dll")))
        {
            items.Add(new CheckItem("runtime", true, false,
                "Runtime .NET embarqué (self-contained) — pas besoin d’installer .NET 8 séparément."));
        }
        else if (!hasSvc)
        {
            items.Add(new CheckItem("runtime", false, true,
                "Pas de runtime embarqué et pas d’exécutable service — installation MSI incomplète."));
        }
        else
        {
            items.Add(new CheckItem("runtime", false, true,
                "Pas de DLL runtime embarquées. Installez le runtime .NET 8 (Desktop pour l’UI)."));
        }

        AppPaths.EnsureDirectories();
        AppPaths.TryHardenDataDirectoryAcl();
        items.Add(new CheckItem("programdata", true, false,
            "Dossier données OK : " + AppPaths.DataDirectory + " (ACL via SID)"));

        var probe = ServiceProcessHelper.Probe();
        if (!probe.Exists)
        {
            items.Add(new CheckItem("service", false, false,
                "Le service Windows « DnsBlocker » n’est pas enregistré." +
                (hasSvc
                    ? " La réparation peut l’enregistrer."
                    : " Réinstallez / réparez d’abord les binaires.")));
        }
        else if (!probe.Running)
        {
            items.Add(new CheckItem("service", false, false,
                "Service enregistré mais arrêté (état : " + probe.Detail + ")."));
        }
        else
        {
            items.Add(new CheckItem("service", true, false,
                "Service DnsBlocker en cours d’exécution."));
        }

        var pipeOk = false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                client.Connect(400);
                pipeOk = client.IsConnected;
            }
        }
        catch
        {
            pipeOk = false;
        }

        if (pipeOk)
        {
            items.Add(new CheckItem("pipe", true, false, "Canal IPC nommé joignable."));
        }
        else if (probe.Running)
        {
            items.Add(new CheckItem("pipe", false, false,
                "Service démarré mais le canal IPC est absent — redémarrez le service ou consultez les journaux."));
        }
        else
        {
            items.Add(new CheckItem("pipe", true, true,
                "Canal IPC absent (normal si le service est arrêté)."));
        }

        var sparse = !hasSvc || !hasUi;
        var healthy = items.All(i => i.Ok || i.Warning) && hasSvc && hasUi && probe.Exists && probe.Running;
        // Warnings alone shouldn't fail if core is OK — recompute:
        healthy = hasSvc && hasUi && probe.Exists && probe.Running &&
                  items.Where(i => i.Id is "service_exe" or "ui_exe" or "service").All(i => i.Ok);

        return new Report(root, items, healthy, sparse, probe.Exists, probe.Running);
    }

    public static string? TryStartService()
    {
        var err = ServiceProcessHelper.TryStart(TimeSpan.FromSeconds(45));
        return err;
    }

    public static string? TryRegisterService(string installRoot)
    {
        if (!OperatingSystem.IsWindows())
            return "Enregistrement du service uniquement sous Windows.";

        var svcExe = Path.Combine(installRoot, "DnsBlocker.Service.exe");
        if (!File.Exists(svcExe))
            return "Impossible d’enregistrer le service : binaire manquant.";

        try
        {
            using var existing = new ServiceController(ServiceName);
            _ = existing.Status;
            return null; // already registered
        }
        catch (InvalidOperationException)
        {
            // create below
        }
        catch (Exception ex)
        {
            return "Lecture service : " + ex.Message;
        }

        try
        {
            // sc.exe requires a space after binPath= and quotes when the path has spaces.
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "create " + ServiceName + " binPath= \"" + svcExe +
                            "\" start= auto DisplayName= \"WinDNS Blocker Hub\"",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
                return "UAC annulé — enregistrement du service impossible.";
            p.WaitForExit(60000);
            if (p.ExitCode != 0)
                return "sc create a échoué (code " + p.ExitCode + ").";

            TryStartService();
            return null;
        }
        catch (Exception ex)
        {
            return "Échec sc create : " + ex.Message;
        }
    }
}
