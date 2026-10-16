using System.Collections.Concurrent;
using DnsBlocker.Core.Paths;

namespace DnsBlocker.Core.Logging;

public sealed class FileLogBuffer
{
    private readonly ConcurrentQueue<string> _recent = new();
    private readonly int _capacity;
    private readonly string _logFile;
    private readonly object _writeGate = new();

    public FileLogBuffer(int capacity = 200, string? logFile = null)
    {
        _capacity = capacity;
        AppPaths.EnsureDirectories();
        _logFile = logFile ?? Path.Combine(AppPaths.LogDirectory, "dnsblocker.log");
    }

    public void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}";
        _recent.Enqueue(line);
        while (_recent.Count > _capacity && _recent.TryDequeue(out _))
        {
        }

        lock (_writeGate)
        {
            File.AppendAllText(_logFile, line + Environment.NewLine);
        }
    }

    public IReadOnlyList<string> GetRecent(int count = 100)
    {
        return _recent.Reverse().Take(count).Reverse().ToList();
    }
}
