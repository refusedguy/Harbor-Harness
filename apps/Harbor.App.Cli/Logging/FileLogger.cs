using Microsoft.Extensions.Logging;
namespace Harbor.Cli.Logging;

/// <summary>
///     File logger provider that writes every log entry (from <see cref="LogLevel.Trace"/>
///     upward by default) to a per-run, timestamped file under
///     <c>~/.harbor/logs/</c>. Each process gets its own file
///     (<c>harbor-{appPrefix}-{yyyy-MM-dd_HH-mm-ss}.log</c>) opened in
///     <see cref="FileMode.Append"/> — concurrent runs never overwrite each other
///     and a crash never wipes prior history.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a per-run file:</b> the previous implementation opened
///         <c>harbor_{date}.log</c> with <see cref="FileMode.OpenOrCreate"/> at
///         position 0. The first write of every new run overwrote the previous
///         run's header, and if the new run was shorter the file ended with
///         stale bytes from the previous run — the "log overwrites itself" bug.
///     </para>
///     <para>
///         <b>Thread safety:</b> every write is serialized through
///         <c>_writeLock</c>. The provider is safe to register on multiple
///         logger factories (Program.cs + HostBuilder) — the underlying
///         <see cref="StreamWriter"/> is shared and disposed exactly once via an
///         <c>Interlocked.Exchange</c> guard on <c>_disposed</c>.
///     </para>
///     <para>
///         <b>File level vs console level:</b> the file always captures down
///         to <see cref="LogLevel.Debug"/> (overridable via the
///         <c>fileLevel</c> ctor parameter) so a post-mortem has the full
///         picture even when the console is set to <see cref="LogLevel.Information"/>.
///     </para>
/// </remarks>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _appPrefix;
    private readonly string _logDir;
    private readonly LogLevel _fileLevel;
    private readonly object _writeLock = new();
    private readonly StreamWriter _writer;
    private int _disposed;

    /// <summary>
    ///     Create a provider that writes to <c>~/.harbor/logs/</c>.
    /// </summary>
    /// <param name="appPrefix">Short prefix embedded in the filename (e.g. "cli", "avalonia").</param>
    /// <param name="logDir">Absolute directory path. Created if missing.</param>
    /// <param name="fileLevel">Minimum level captured to the file. Defaults to <see cref="LogLevel.Debug"/>.</param>
    public FileLoggerProvider(string appPrefix, string logDir, LogLevel fileLevel = LogLevel.Debug)
    {
        _appPrefix = appPrefix;
        _logDir = logDir;
        _fileLevel = fileLevel;
        Directory.CreateDirectory(logDir);

        // Per-run filename: harbor-cli-2026-07-18_15-30-45.log
        // The seconds-resolution timestamp makes collisions effectively impossible
        // (two CLI invocations in the same second would still share the file
        // safely because we use FileMode.Append — they'd interleave, not overwrite).
        string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
        FilePath = Path.Combine(logDir, $"harbor-{appPrefix}-{stamp}.log");

        // FileMode.Append: opens existing OR creates new, position at end.
        // FileShare.Read: lets `harbor logs --follow` (tail -f) and external
        // editors open the file concurrently without blocking writes.
        var stream = new FileStream(
            FilePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: false);
        _writer = new StreamWriter(stream)
        {
            AutoFlush = true
        };

        WriteHeader();
    }

    /// <summary>
    ///     Absolute path to the per-run log file. Exposed so the
    ///     <c>harbor logs</c> command and tests can locate it.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    ///     Directory containing all Harbor log files.
    /// </summary>
    public string LogDirectory => _logDir;

    /// <summary>
    ///     App prefix used in the filename (e.g. "cli").
    /// </summary>
    public string AppPrefix => _appPrefix;

    /// <summary>
    ///     Minimum level captured to the file. Independent of the console level.
    /// </summary>
    public LogLevel FileLevel => _fileLevel;

    /// <summary>
    ///     Returns a logger for <paramref name="categoryName"/>. The same
    ///     underlying writer is shared across all categories — every log line
    ///     from every category lands in the same file in arrival order.
    /// </summary>
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>
    ///     Disposes the underlying writer. Safe to call multiple times —
    ///     Microsoft.Extensions.Logging calls this once per owning factory,
    ///     and the same provider instance is shared between Program.cs's
    ///     LoggerFactory and the host's, so the second call must be a no-op.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        try
        {
            lock (_writeLock)
            {
                _writer.WriteLine();
                _writer.WriteLine($"=== Harbor log ended {DateTimeOffset.UtcNow:O} ===");
                _writer.Dispose();
            }
        }
        catch
        {
            // best-effort during shutdown
        }
    }

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        if (level < _fileLevel)
            return;
        // 4-char level mnemonic: TRAC, DBUG, INFO, WARN, ERRO, CRIT, NONE
        string levelTag = level switch
        {
            LogLevel.Trace => "TRAC",
            LogLevel.Debug => "DBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERRO",
            LogLevel.Critical => "CRIT",
            LogLevel.None => "NONE",
            _ => level.ToString().ToUpperInvariant()
        };
        string timestamp = DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff");
        int thread = Environment.CurrentManagedThreadId;
        string line = $"{timestamp} [{levelTag}] [{thread,3}] {category}: {message}";

        lock (_writeLock)
        {
            _writer.WriteLine(line);
            if (exception is not null)
            {
                _writer.WriteLine($"    Exception: {exception.GetType().FullName}: {exception.Message}");
                if (exception.StackTrace is not null)
                    _writer.WriteLine(exception.StackTrace);
                if (exception.InnerException is not null)
                {
                    _writer.WriteLine($"    Inner: {exception.InnerException.GetType().FullName}: {exception.InnerException.Message}");
                }
            }
        }
    }

    private void WriteHeader()
    {
        string[] args = Environment.GetCommandLineArgs();
        string argsLine = args.Length == 0 ? "(none)" : string.Join(' ', args);
        lock (_writeLock)
        {
            _writer.WriteLine($"=== Harbor log started {DateTimeOffset.UtcNow:O} ===");
            _writer.WriteLine($"=== App prefix: {_appPrefix} ===");
            _writer.WriteLine($"=== Process ID: {Environment.ProcessId} ===");
            _writer.WriteLine($"=== .NET runtime: {Environment.Version} ===");
            _writer.WriteLine($"=== OS: {Environment.OSVersion} ===");
            _writer.WriteLine($"=== Machine: {Environment.MachineName} ===");
            _writer.WriteLine($"=== Working dir: {Environment.CurrentDirectory} ===");
            _writer.WriteLine($"=== File level: >= {_fileLevel} ===");
            _writer.WriteLine($"=== Args: {argsLine} ===");
            _writer.WriteLine();
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly FileLoggerProvider _provider;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider._fileLevel;

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
            _provider.Write(logLevel, _category, msg, exception);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
