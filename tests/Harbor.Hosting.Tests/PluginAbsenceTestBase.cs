using Harbor.Abstractions.Tools;
using Harbor.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Hosting.Tests;

/// <summary>
///     Shared fixture for the plugin-absence suites (issue #1055): the CLI must
///     start fast and work fully with no plugin dirs, no host binary and no
///     network. The invariant is the same everywhere — compose a plugin-less
///     harbor dir under an empty working directory and read the bootstrap log
///     to see how the plugin route reported itself; what varies is the
///     temp-root prefix, so that is what this base parameterises.
///     <para>
///         It lives here and NOT in <c>Harbor.TestKit</c>: it composes
///         <c>Harbor.Hosting</c>, and a TestKit-hosted base would push the
///         hosting assembly into the closure of every TestKit consumer (the
///         epic's rule 3). A base in one test project referenced by another
///         would be worse still — a cycle.
///     </para>
/// </summary>
public abstract class PluginAbsenceTestBase
{
    /// <summary>
    ///     Temp-root prefix for this suite — one per suite, so two suites never
    ///     share a directory (they run in the same process).
    /// </summary>
    protected abstract string TempDirPrefix { get; }

    /// <summary>A harbor dir that is deliberately NOT created (no plugin dirs).</summary>
    protected string TempHarborDir() =>
        Path.Combine(Path.GetTempPath(), $"{TempDirPrefix}-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Composes the hosting stack over a memory store, capturing the bootstrap log.</summary>
    protected static ServiceProvider Compose(string harborDir, CaptureLoggerFactory capture)
    {
        var services = new ServiceCollection();
        services.AddHarbor(new HarborComposeOptions
        {
            HarborDir = harborDir,
            DefaultStorageBackend = "memory",
            BootstrapLoggerFactory = () => capture,
        });
        return services.BuildServiceProvider();
    }

    /// <summary>Runs the body with an empty working directory as the project scope.</summary>
    protected IDisposable WithTempCwd()
    {
        string previous = Directory.GetCurrentDirectory();
        string temp = Path.Combine(Path.GetTempPath(), $"{TempDirPrefix}-cwd", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Directory.SetCurrentDirectory(temp);
        return new RestoreCwd(previous, temp);
    }

    private sealed class RestoreCwd : IDisposable
    {
        private readonly string _previous;
        private readonly string _temp;
        public RestoreCwd(string previous, string temp)
        {
            _previous = previous;
            _temp = temp;
        }

        public void Dispose()
        {
            Directory.SetCurrentDirectory(_previous);
            try
            {
                Directory.Delete(_temp, true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    protected sealed record LogEntry(LogLevel Level, string Message);

    protected sealed class CaptureLogger : ILogger
    {
        private readonly List<LogEntry> _entries;
        public CaptureLogger(List<LogEntry> entries)
        {
            _entries = entries;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
            }
        }
    }

    protected sealed class CaptureLoggerFactory : ILoggerFactory
    {
        private readonly List<LogEntry> _entries = new();
        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToArray();
                }
            }
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(_entries);

        public void Dispose()
        {
        }
    }
}
