using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace AWFace.Api.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly IOptionsMonitor<FileLoggerOptions> _options;
    private readonly IWebHostEnvironment _environment;
    private readonly object _writeLock = new();

    public FileLoggerProvider(IOptionsMonitor<FileLoggerOptions> options, IWebHostEnvironment environment)
    {
        _options = options;
        _environment = environment;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new FileLogger(name, _options, _environment, _writeLock));
    }

    public void Dispose()
    {
        _loggers.Clear();
    }
}

internal sealed class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly IOptionsMonitor<FileLoggerOptions> _options;
    private readonly IWebHostEnvironment _environment;
    private readonly object _writeLock;

    public FileLogger(
        string categoryName,
        IOptionsMonitor<FileLoggerOptions> options,
        IWebHostEnvironment environment,
        object writeLock)
    {
        _categoryName = categoryName;
        _options = options;
        _environment = environment;
        _writeLock = writeLock;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel)
    {
        var options = _options.CurrentValue;
        return options.Enabled && logLevel != LogLevel.None && logLevel >= options.MinimumLevel;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (string.IsNullOrWhiteSpace(message) && exception is null)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} [{logLevel}] {_categoryName}";
        if (eventId.Id != 0)
        {
            line += $" ({eventId.Id}:{eventId.Name})";
        }
        line += $" - {message}";

        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        WriteLine(now, line);
    }

    private void WriteLine(DateTimeOffset now, string line)
    {
        var options = _options.CurrentValue;
        var directory = Path.IsPathRooted(options.Directory)
            ? options.Directory
            : Path.Combine(_environment.ContentRootPath, options.Directory);
        var path = Path.Combine(directory, $"{options.FileNamePrefix}-{now:yyyyMMdd}.log");

        lock (_writeLock)
        {
            System.IO.Directory.CreateDirectory(directory);
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}
