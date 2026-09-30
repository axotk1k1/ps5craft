using PS5Craft.Core;
using PS5Craft.Core.Abstractions;
using PS5Craft.Core.Models;

namespace PS5Craft.Infrastructure.Logging;

public sealed class LogService : ILogService
{
    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = new();
    private readonly ISettingsService _settings;

    public LogService(ISettingsService settings)
    {
        _settings = settings;
    }

    public event EventHandler<LogEntry>? EntryAdded;

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    public void Info(string message) => Add(LogLevel.Info, message);
    public void Warning(string message) => Add(LogLevel.Warning, message);
    public void Error(string message) => Add(LogLevel.Error, message);
    public void Success(string message) => Add(LogLevel.Success, message);

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    public async Task SaveAsync(string? path = null)
    {
        path ??= Path.Combine(_settings.LogsDirectory, $"ps5craft-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string text;
        lock (_gate)
        {
            text = string.Join(Environment.NewLine, _entries.Select(e => e.ToString()));
        }

        await File.WriteAllTextAsync(path, text).ConfigureAwait(false);
    }

    public void OpenLogFolder()
    {
        Directory.CreateDirectory(_settings.LogsDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _settings.LogsDirectory,
            UseShellExecute = true
        });
    }

    private void Add(LogLevel level, string message)
    {
        var entry = new LogEntry { Level = level, Message = message, Timestamp = DateTime.Now };
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > 20000)
            {
                _entries.RemoveRange(0, _entries.Count - 15000);
            }
        }

        EntryAdded?.Invoke(this, entry);
    }
}
