using System.IO;
using System.Text;
using Clicky.Core;

namespace Clicky.Diagnostics;

/// <summary>
/// Tiny thread-safe file logger for debugging the offline pipeline. Writes
/// timestamped lines to a per-day file under %LOCALAPPDATA%\Clicky\logs. There's no
/// console for a tray app, so this file is the primary way to see what happened
/// (model startup, hotkey events, transcription/vision timings, errors).
/// </summary>
public static class ClickyLog
{
    private static readonly object WriteLock = new();
    private static string? _logFilePath;

    /// <summary>The folder logs are written to. Safe to open in Explorer for the user.</summary>
    public static string LogDirectory { get; } = Path.Combine(AppConfig.UserDataDirectory, "logs");

    /// <summary>Opens a fresh session marker in today's log file.</summary>
    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            _logFilePath = Path.Combine(LogDirectory, $"clicky-{DateTime.Now:yyyy-MM-dd}.log");

            var divider = new string('─', 60);
            Write("INFO", "App",
                $"\n{divider}\nClicky session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} " +
                $"(v{typeof(ClickyLog).Assembly.GetName().Version})\n{divider}");
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    public static void Info(string component, string message) => Write("INFO", component, message);

    public static void Warn(string component, string message) => Write("WARN", component, message);

    public static void Error(string component, string message, Exception? exception = null)
    {
        var detail = exception is null ? message : $"{message} :: {exception.GetType().Name}: {exception.Message}";
        Write("ERROR", component, detail);
        if (exception?.StackTrace is { } stackTrace)
        {
            Write("ERROR", component, stackTrace);
        }
    }

    private static void Write(string level, string component, string message)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{level,-5}] [{component}] {message}";
            lock (WriteLock)
            {
                _logFilePath ??= Path.Combine(LogDirectory, $"clicky-{DateTime.Now:yyyy-MM-dd}.log");
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            System.Diagnostics.Debug.WriteLine(line);
        }
        catch
        {
            // Never throw from the logger.
        }
    }
}
