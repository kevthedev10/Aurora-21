using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Helpers;

namespace Aurora21.UltimateTeam.Server.Hosting;

/// <summary>
/// Appends log lines (time, level, message; no category) to a single UTF-8 file, the runtime's <c>logs/ultimateteam.log</c>.
/// Lines are written by a background writer, so request threads never wait on disk I/O or a shared lock; the
/// time stamp is taken when a line is logged, and disposal drains the queue.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly BackgroundLineWriter _writer;
    private readonly FileLogger _logger;

    /// <param name="maxBytes">Rotate to <c>{path}.1</c> before the file would exceed this size; 0 never rotates.</param>
    public FileLoggerProvider(string path, long maxBytes = 0)
    {
        _writer = new BackgroundLineWriter(path, maxBytes);
        _logger = new FileLogger(this);
    }

    /// <summary>Every category writes to the same file in the same format.</summary>
    public ILogger CreateLogger(string categoryName) => _logger;

    public void Dispose() => _writer.Dispose();

    internal void Write(LogLevel level, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss,fff", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(LevelName(level))
            .Append(' ')
            .Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }
        _writer.WriteLine(line.ToString(), important: level >= LogLevel.Warning);
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRITICAL",
        _ => "INFO",
    };

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;

        public FileLogger(FileLoggerProvider provider) => _provider = provider;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                _provider.Write(logLevel, formatter(state, exception), exception);
            }
        }
    }
}
