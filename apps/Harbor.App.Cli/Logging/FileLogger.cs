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
    private readonly Func<LogLevel, bool> _filter;
    private readonly StreamWriter _writer;

    public FileLoggerProvider(LogLevel minLevel)
    {
        string exeDir = AppContext.BaseDirectory;
        string logsDir = Path.Combine(exeDir, "logs");
        Directory.CreateDirectory(logsDir);

        // One log file per calendar day, appended across runs so a crash/restart in
        // the same day keeps all history in a single file.
        string stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        FilePath = Path.Combine(logsDir, $"harbor_{stamp}.log");
        _filter = level => level >= minLevel;
        _writer = new StreamWriter(
            new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read),
            leaveOpen: false)
        {
            AutoFlush = true
        };

        _writer.WriteLine($"=== Harbor log started {DateTimeOffset.Now:O} (level>={minLevel}) ===");
        _writer.WriteLine($"exe: {exeDir}");
    }

    public string FilePath
    {
        get;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName, _filter);

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

    internal void Write(LogLevel level, string category, string message)
    {
        string line = $"{DateTimeOffset.Now:HH:mm:ss.fff} [{level,-11}] {category}: {message}";
        _writer.WriteLine(line);
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly Func<LogLevel, bool> _filter;
        private readonly FileLoggerProvider _provider;

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

            string msg = formatter(state, exception);
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
