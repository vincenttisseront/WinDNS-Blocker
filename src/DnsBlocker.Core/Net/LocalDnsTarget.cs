using System.Net;
using System.Net.Sockets;
using DnsBlocker.Core.Models;
using DnsBlocker.Core.Setup;
using Microsoft.Win32;

namespace DnsBlocker.Core.Net;

/// <summary>
/// Detects a local Windows DNS Server role/service and resolves how the hub
/// should name / talk to this machine as a DnsTargets entry (no WinRM).
/// </summary>
public static class LocalDnsTarget
{
    /// <summary>Test override: when set, <see cref="IsDnsServerRunning"/> returns this.</summary>
    public static Func<bool>? DnsServerRunningOverride { get; set; }

    /// <summary>Test override: when set, <see cref="GetPreferredLocalName"/> returns this.</summary>
    public static Func<string>? PreferredNameOverride { get; set; }

    /// <summary>Test override for local-name aliases used by <see cref="IsLocalTarget"/>.</summary>
    public static Func<IReadOnlyCollection<string>>? LocalAliasesOverride { get; set; }

    /// <summary>
    /// True when the Windows DNS Server service (name <c>DNS</c>) is installed and running.
    /// </summary>
    public static bool IsDnsServerRunning()
    {
        if (DnsServerRunningOverride is not null)
            return DnsServerRunningOverride();

        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\DNS");
            if (key is null)
                return false;

            // Prefer live state via sc.exe (no ServiceController package dependency).
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "query DNS",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
                return DnsServerKeyLooksInstalled(key);

            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            if (stdout.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
                return true;
            if (stdout.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)
                || stdout.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase))
                return false;

            return DnsServerKeyLooksInstalled(key);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Preferred DnsTargets entry for this machine: FQDN when available, else hostname.
    /// Never returns the scaffold-only literals <c>localhost</c> / <c>.</c> when a real name exists.
    /// </summary>
    public static string GetPreferredLocalName()
    {
        if (PreferredNameOverride is not null)
            return PreferredNameOverride().Trim();

        try
        {
            var fqdn = System.Net.Dns.GetHostEntry("localhost").HostName?.Trim().TrimEnd('.');
            if (!string.IsNullOrWhiteSpace(fqdn)
                && !fqdn.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return fqdn!;
        }
        catch
        {
            // fall through
        }

        var host = Environment.MachineName?.Trim() ?? "";
        var domain = MachineDomainDetector.GetDnsDomain();
        if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(domain))
        {
            if (!host.Contains('.', StringComparison.Ordinal))
                return host + "." + domain;
        }

        if (!string.IsNullOrWhiteSpace(host))
            return host;

        return "localhost";
    }

    /// <summary>
    /// True when <paramref name="target"/> refers to this machine for local PowerShell/CIM
    /// (omit <c>-ComputerName</c> / no WinRM). Recognizes loopback aliases, <c>.</c>,
    /// COMPUTERNAME, hostname, and FQDN.
    /// </summary>
    public static bool IsLocalTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return false;

        var raw = target.Trim();
        if (raw.Length == 0)
            return false;

        // Literal "." means local computer for -ComputerName — check before TrimEnd('.')
        if (raw is "." or "localhost" or "127.0.0.1" or "::1" or "(local)")
            return true;

        var t = raw.TrimEnd('.');
        if (t.Length == 0)
            return false;

        foreach (var alias in GetLocalAliases())
        {
            if (string.Equals(t, alias, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// On first-run / empty / scaffold-only DnsTargets, when a local DNS Server is running,
    /// replace targets with the preferred local computer DNS name.
    /// Does not rewrite farms that already contain a non-scaffold target.
    /// </summary>
    public static bool TryEnsureDefaultTargets(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!IsDnsServerRunning())
            return false;

        var targets = (config.DnsTargets ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToList();

        if (targets.Count > 0 && !targets.All(SetupWizardGate.IsLocalOnlyTarget))
            return false;

        var name = GetPreferredLocalName();
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // Already defaulted to this machine (e.g. FQDN) — nothing to do
        if (targets.Count == 1 && string.Equals(targets[0], name, StringComparison.OrdinalIgnoreCase))
            return false;

        config.DnsTargets = [name];
        return true;
    }

    /// <summary>
    /// Adds the preferred local name to DnsTargets if missing. Returns the name used.
    /// </summary>
    public static string EnsureInTargets(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var name = GetPreferredLocalName();
        config.DnsTargets ??= [];

        // Drop scaffold-only placeholders when adding a real local target
        config.DnsTargets = config.DnsTargets
            .Where(t => !string.IsNullOrWhiteSpace(t) && !SetupWizardGate.IsLocalOnlyTarget(t))
            .Select(t => t.Trim())
            .ToList();

        if (!config.DnsTargets.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
            config.DnsTargets.Insert(0, name);

        return name;
    }

    public static IReadOnlyCollection<string> GetLocalAliases()
    {
        if (LocalAliasesOverride is not null)
            return LocalAliasesOverride();

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".",
            "localhost",
            "127.0.0.1",
            "::1",
            "(local)"
        };

        try
        {
            var machine = Environment.MachineName;
            if (!string.IsNullOrWhiteSpace(machine))
                set.Add(machine.Trim());
        }
        catch
        {
            // ignore
        }

        try
        {
            var host = System.Net.Dns.GetHostName();
            if (!string.IsNullOrWhiteSpace(host))
                set.Add(host.Trim().TrimEnd('.'));
        }
        catch
        {
            // ignore
        }

        try
        {
            var fqdn = System.Net.Dns.GetHostEntry("localhost").HostName;
            if (!string.IsNullOrWhiteSpace(fqdn))
                set.Add(fqdn.Trim().TrimEnd('.'));
        }
        catch (SocketException)
        {
            // ignore
        }
        catch
        {
            // ignore
        }

        var domain = MachineDomainDetector.GetDnsDomain();
        if (!string.IsNullOrWhiteSpace(domain))
        {
            foreach (var existing in set.ToList())
            {
                if (!existing.Contains('.', StringComparison.Ordinal)
                    && existing is not ("." or "localhost" or "127.0.0.1" or "::1" or "(local)"))
                    set.Add(existing + "." + domain);
            }
        }

        return set;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool DnsServerKeyLooksInstalled(RegistryKey key)
    {
        // Start DWORD present ⇒ service registered (role installed)
        var start = key.GetValue("Start");
        return start is not null;
    }
}
