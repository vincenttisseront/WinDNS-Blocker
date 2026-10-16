using System.IO;
using System.IO.Pipes;
using System.ServiceProcess;
using DnsBlocker.Core.Paths;
using DnsBlocker.UI.Localization;

namespace DnsBlocker.UI;

/// <summary>
/// In-process equivalent of doctor.ps1 for the Setup GUI (no PowerShell CLI).
/// </summary>
internal static class InstallHealth
{
    public const string ServiceName = "DnsBlocker";
    public const string PipeName = "DnsBlocker";

    public sealed record CheckItem(string Id, bool Ok, bool Warning, string Message);

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
            ? new CheckItem("service_exe", true, false, Loc.F("Health.SvcPresent", svcExe))
            : new CheckItem("service_exe", false, false, Loc.F("Health.SvcMissing", svcExe)));

        items.Add(hasUi
            ? new CheckItem("ui_exe", true, false, Loc.F("Health.UiPresent", uiExe))
            : new CheckItem("ui_exe", false, false, Loc.F("Health.UiMissing", uiExe)));

        if (File.Exists(hostFxr) || File.Exists(Path.Combine(root, "coreclr.dll")))
        {
            items.Add(new CheckItem("runtime", true, false, Loc.T("Health.RuntimeEmbedded")));
        }
        else if (!hasSvc)
        {
            items.Add(new CheckItem("runtime", false, true, Loc.T("Health.RuntimeSparse")));
        }
        else
        {
            items.Add(new CheckItem("runtime", false, true, Loc.T("Health.RuntimeMissing")));
        }

        AppPaths.EnsureDirectories();
        AppPaths.TryHardenDataDirectoryAcl();
        items.Add(new CheckItem("programdata", true, false,
            Loc.F("Health.DataOk", AppPaths.DataDirectory)));

        var probe = ServiceProcessHelper.Probe();
        if (!probe.Exists)
        {
            var suffix = hasSvc
                ? Loc.T("Health.SvcNotRegistered.Repair")
                : Loc.T("Health.SvcNotRegistered.Reinstall");
            items.Add(new CheckItem("service", false, false,
                Loc.F("Health.SvcNotRegistered", suffix)));
        }
        else if (!probe.Running)
        {
            items.Add(new CheckItem("service", false, false,
                Loc.F("Health.SvcStopped", probe.Detail)));
        }
        else
        {
            items.Add(new CheckItem("service", true, false, Loc.T("Health.SvcRunning")));
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
            items.Add(new CheckItem("pipe", true, false, Loc.T("Health.PipeOk")));
        }
        else if (probe.Running)
        {
            items.Add(new CheckItem("pipe", false, false, Loc.T("Health.PipeMissingRunning")));
        }
        else
        {
            items.Add(new CheckItem("pipe", true, true, Loc.T("Health.PipeMissingStopped")));
        }

        var sparse = !hasSvc || !hasUi;
        var healthy = hasSvc && hasUi && probe.Exists && probe.Running &&
                      items.Where(i => i.Id is "service_exe" or "ui_exe" or "service").All(i => i.Ok);

        return new Report(root, items, healthy, sparse, probe.Exists, probe.Running);
    }

    public static string? TryStartService()
    {
        return ServiceProcessHelper.TryStart(TimeSpan.FromSeconds(45));
    }

    public static string? TryRegisterService(string installRoot)
    {
        if (!OperatingSystem.IsWindows())
            return Loc.T("Health.RegisterWindowsOnly");

        var svcExe = Path.Combine(installRoot, "DnsBlocker.Service.exe");
        if (!File.Exists(svcExe))
            return Loc.T("Health.RegisterMissingBin");

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
            return Loc.F("Health.RegisterRead", ex.Message);
        }

        try
        {
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
                return Loc.T("Health.RegisterUac");
            p.WaitForExit(60000);
            if (p.ExitCode != 0)
                return Loc.F("Health.RegisterScFail", p.ExitCode);

            TryStartService();
            return null;
        }
        catch (Exception ex)
        {
            return Loc.F("Health.RegisterScError", ex.Message);
        }
    }
}
