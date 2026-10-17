using System.Diagnostics;

namespace DnsBlocker.Core.Diagnostics;

/// <summary>Best-effort working-set snapshots and sync priority helpers for DC-friendly sync.</summary>
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
    /// Release LOH / gen2 pressure after large HashSet drops (multi-million domain sync on DCs).
    /// </summary>
    public static void TrimAfterLargeDrop(bool blocking = false)
    {
        try
        {
            GC.Collect(
                GC.MaxGeneration,
                blocking ? GCCollectionMode.Forced : GCCollectionMode.Optimized,
                blocking,
                compacting: true);
            if (blocking)
                GC.WaitForPendingFinalizers();
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Drop process priority for the duration of a long sync so DNS/AD keep CPU on a DC.
    /// Restores the previous class when disposed (best-effort).
    /// </summary>
    public static IDisposable? EnterBelowNormalPriority()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            var previous = process.PriorityClass;
            if (previous == ProcessPriorityClass.BelowNormal
                || previous == ProcessPriorityClass.Idle)
            {
                return null;
            }

            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            return new PriorityRestore(process, previous);
        }
        catch
        {
            return null;
        }
    }

    private sealed class PriorityRestore : IDisposable
    {
        private Process? _process;
        private readonly ProcessPriorityClass _previous;

        public PriorityRestore(Process process, ProcessPriorityClass previous)
        {
            _process = process;
            _previous = previous;
        }

        public void Dispose()
        {
            var process = Interlocked.Exchange(ref _process, null);
            if (process is null)
                return;
            try
            {
                if (!process.HasExited)
                    process.PriorityClass = _previous;
            }
            catch
            {
                // ignore
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
