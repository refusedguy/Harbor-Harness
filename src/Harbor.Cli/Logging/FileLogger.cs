using Microsoft.Extensions.Logging;
namespace Harbor.Cli.Logging;

/// <summary>
///     Minimal file logger provider. Writes all log entries to a dated file in a
///     <c>logs</c> directory next to the entry assembly. Avoids extra dependencies
///     (and AOT-unfriendly reflection) while giving full visibility into the
///     agent pipeline from startup to completion.
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly Func<LogLevel, bool> _filter;
    private readonly StreamWriter _writer;

    public FileLoggerProvider(LogLevel minLevel)
    {
        var exeDir = AppContext.BaseDirectory;
        var logsDir = Path.Combine(exeDir, "logs");
        Directory.CreateDirectory(logsDir);

        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd_HHmmss");
        _filePath = Path.Combine(logsDir, $"harbor_{stamp}.log");
        _filter = level => level >= minLevel;
        _writer = new StreamWriter(
            new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.Read),
            leaveOpen: false)
        {
            AutoFlush = true
        };

        _writer.WriteLine($"=== Harbor log started {DateTimeOffset.Now:O} (level>={minLevel}) ===");
        _writer.WriteLine($"exe: {exeDir}");
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName, _filter);

    public string FilePath => _filePath;

    internal void Write(LogLevel level, string category, string message)
    {
        var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} [{level,-11}] {category}: {message}";
        _writer.WriteLine(line);
    }

    public void Dispose()
    {
        try
        {
            _writer.WriteLine($"=== Harbor log ended {DateTimeOffset.Now:O} ===");
            _writer.Dispose();
        }
        catch
        {
            // best-effort
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;
        private readonly Func<LogLevel, bool> _filter;

        public FileLogger(FileLoggerProvider provider, string category, Func<LogLevel, bool> filter)
        {
            _provider = provider;
            _category = category;
            _filter = filter;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => _filter(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var msg = formatter(state, exception);
            if (exception is not null)
                msg += $"{Environment.NewLine}{exception}";
            _provider.Write(logLevel, _category, msg);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
