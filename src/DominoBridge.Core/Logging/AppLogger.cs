namespace DominoBridge.Core.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss.fff} [{Level}] {Message}";
}

public interface IAppLogger
{
    void Log(LogLevel level, string message);
}

public static class LoggerExtensions
{
    public static void Debug(this IAppLogger l, string m) => l.Log(LogLevel.Debug, m);
    public static void Info(this IAppLogger l, string m) => l.Log(LogLevel.Info, m);
    public static void Warn(this IAppLogger l, string m) => l.Log(LogLevel.Warn, m);
    public static void Error(this IAppLogger l, string m) => l.Log(LogLevel.Error, m);
}

public sealed class NullLogger : IAppLogger
{
    public static readonly NullLogger Instance = new();
    public void Log(LogLevel level, string message) { }
}

/// <summary>Raises an event for the UI and appends to a daily log file (if a directory is given).</summary>
public sealed class AppLogger : IAppLogger
{
    private readonly string? _dir;
    private readonly object _gate = new();

    public AppLogger(string? logDirectory = null)
    {
        _dir = logDirectory;
        if (_dir != null) { try { Directory.CreateDirectory(_dir); } catch { _dir = null; } }
    }

    public event Action<LogEntry>? EntryLogged;

    public void Log(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        if (_dir != null)
        {
            try
            {
                lock (_gate)
                    File.AppendAllText(Path.Combine(_dir, $"bridge-{entry.Time:yyyyMMdd}.log"), entry + Environment.NewLine);
            }
            catch { /* logging must never break printing */ }
        }
        EntryLogged?.Invoke(entry);
    }
}
