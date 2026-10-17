using System.Diagnostics;

namespace DnsBlocker.Core.Diagnostics;

/// <summary>Best-effort working-set snapshots for sync RSS logging on the hub/DC.</summary>
public static class ProcessMemory
{
    public static long WorkingSetBytes
    {
        get
        {
            try
            {
                using var p = Process.GetCurrentProcess();
                p.Refresh();
                return p.WorkingSet64;
            }
            catch
            {
                return 0;
            }
        }
    }

    public static string FormatWorkingSet() => FormatBytes(WorkingSetBytes);

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "?";
        const double mb = 1024d * 1024d;
        if (bytes < mb)
            return $"{bytes / 1024d:0} KB";
        return $"{bytes / mb:0.0} MB";
    }

    /// <summary>
    /// Release LOH / gen2 pressure after large HashSet drops (6M-domain sync on DCs).
    /// </summary>
    public static void TrimAfterLargeDrop()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: true);
        }
        catch
        {
            // ignore
        }
    }
}
