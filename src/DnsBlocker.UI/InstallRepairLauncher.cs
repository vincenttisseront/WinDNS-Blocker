using System.Diagnostics;
using System.IO;

namespace DnsBlocker.UI;

/// <summary>
/// Locates and launches a bundled repair helper under the product InstallRoot
/// (no raw CLI for the user). Names coordinated with the install worker:
/// Repair-DnsBlocker.exe / .cmd / .ps1, Verify-Install.cmd.
/// </summary>
internal static class InstallRepairLauncher
{
    public static string? InstallRoot
    {
        get
        {
            // UI lives in <InstallRoot>\UI\
            var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Directory.GetParent(baseDir);
            return parent?.FullName ?? baseDir;
        }
    }

    public static RepairHelperInfo? FindHelper()
    {
        var root = InstallRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return null;

        string[] candidates =
        [
            "Repair-DnsBlocker.exe",
            "Repair-DnsBlocker.cmd",
            "Repair.cmd",
            "Verify-Install.cmd",
            "Repair-DnsBlocker.ps1"
        ];

        foreach (var name in candidates)
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path))
                return new RepairHelperInfo(path, name);
        }

        return null;
    }

    /// <summary>Returns null on success start, or an error message.</summary>
    public static string? Launch(RepairHelperInfo helper)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                UseShellExecute = true,
                Verb = "runas", // repair needs elevation
                WorkingDirectory = Path.GetDirectoryName(helper.FullPath) ?? InstallRoot
            };

            if (helper.FullPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = "powershell.exe";
                psi.Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{helper.FullPath}\"";
            }
            else
            {
                psi.FileName = helper.FullPath;
            }

            var p = Process.Start(psi);
            return p is null ? "Impossible de démarrer l'assistant de réparation (UAC annulé ?)." : null;
        }
        catch (Exception ex)
        {
            return "Réparation : " + ex.Message;
        }
    }
}

internal readonly record struct RepairHelperInfo(string FullPath, string FileName);
