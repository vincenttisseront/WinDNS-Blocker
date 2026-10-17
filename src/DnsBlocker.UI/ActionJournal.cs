using System.IO;
using DnsBlocker.Core.Paths;

namespace DnsBlocker.UI;

/// <summary>
/// Records user-facing actions for the Logs tab and a durable ui-actions.log file.
/// </summary>
public sealed class ActionJournal
{
    private readonly object _gate = new();
    private readonly List<string> _lines = [];
    private readonly string _logFile;
    private const int MaxLines = 500;

    public ActionJournal(string? logFile = null)
    {
        AppPaths.EnsureDirectories();
        _logFile = logFile ?? Path.Combine(AppPaths.LogDirectory, "ui-actions.log");
    }

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
                return _lines.ToList();
        }
    }

    public void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [UI] {message}";
        lock (_gate)
        {
            _lines.Add(line);
            while (_lines.Count > MaxLines)
                _lines.RemoveAt(0);
            try
            {
                File.AppendAllText(_logFile, line + Environment.NewLine);
            }
            catch
            {
                // UI must not crash if ProgramData is not writable
            }
        }
    }

    /// <summary>
    /// Builds Logs tab text: action journal first, then optional service log lines.
    /// </summary>
    public string Render(IEnumerable<string>? serviceLogs = null)
    {
        lock (_gate)
        {
            var parts = new List<string>
            {
                "=== Actions UI ==="
            };
            if (_lines.Count == 0)
                parts.Add("(aucune action encore)");
            else
                parts.AddRange(_lines);

            var svc = serviceLogs?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? [];
            if (svc.Count > 0)
            {
                parts.Add("");
                parts.Add("=== Service ===");
                parts.AddRange(svc);
            }

            return string.Join(Environment.NewLine, parts);
        }
    }
}
