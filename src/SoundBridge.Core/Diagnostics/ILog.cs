namespace SoundBridge.Core.Diagnostics;

public enum LogLevel
{
    Info,
    Warn,
    Error,
}

/// <summary>Diagnostic sink. Implementations must be safe to call from any thread and must never throw.</summary>
public interface ILog
{
    void Write(LogLevel level, string category, string message);
}

public static class LogExtensions
{
    public static void Info(this ILog log, string category, string message) => log.Write(LogLevel.Info, category, message);

    public static void Warn(this ILog log, string category, string message) => log.Write(LogLevel.Warn, category, message);

    public static void Error(this ILog log, string category, string message) => log.Write(LogLevel.Error, category, message);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    public void Write(LogLevel level, string category, string message)
    {
    }
}
