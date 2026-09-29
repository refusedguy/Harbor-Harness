using Serilog;
using Serilog.Core;
using Serilog.Events;
namespace Harbor.Logging;
/// <summary>
///     Serilog configuration for the desktop apps — used by Avalonia
///     (<c>Harbor.App.Avalonia.Hosting.LoggingConfiguration</c>).
///     Writes to:
///     - File: ~/.harbor/logs/harbor-{appPrefix}-{timestamp}.log (rolling, keep 50)
///     - Console: colored, filtered by app mode (verbose in Debug, info in Release)
///     - Optional diagnostics panel: via ILogger → IDiagnosticsPanel bridge
/// </summary>
/// <remarks>
///     <para>
///         <b>This is NOT shared with the CLI, and the doc comment above used
///         to claim it was</b> ("shared by CLI, Avalonia, and all desktop
///         apps"). The CLI logs through its own hand-rolled
///         <c>ILoggerProvider</c> in <c>Harbor.App.Cli.Logging</c>, not through
///         Serilog.
///     </para>
///     <para>
///         <b>They are not two copies of one behaviour, so do not "fix" the
///         duplication by folding one into the other</b> (issue #558 found and
///         deleted a third, genuinely dead copy of the CLI's sink; see
///         <c>FileLogSinkOwnershipRule</c>). The two are app-disjoint — no
///         process loads both, so no log line can be written twice — and they
///         differ in ways a merge would change for every user: level model
///         (<c>LogEventLevel</c> Verbose..Fatal here vs <c>LogLevel</c>
///         Trace..None there), line format, exception rendering (this writes
///         the full <c>{Exception}</c> chain; the hand-rolled provider writes
///         one <c>Exception:</c> line plus a single <c>Inner:</c> line, so a
///         third-level inner exception is dropped), and retention owner.
///         Collapsing them is a rewrite of two working log formats, not a
///         deduplication.
///     </para>
///     <para>
///         One consequence that IS real and still open: both sinks sweep the
///         same <c>harbor-*.log</c> glob over <c>~/.harbor/logs</c>
///         (<see cref="CleanupOldLogs" /> here, <c>RollingLogCleaner</c> in the
///         CLI), so either can delete the other's files. Unchanged by #558 and
///         recorded in docs/ROADMAP.md rather than fixed silently.
///     </para>
/// </remarks>
public static class LoggerSetup
{
    /// <summary>
    ///     Create a Serilog logger configured for the Harbor app.
    /// </summary>
    /// <param name="appPrefix">App identifier for log filename (e.g. "cli", "avalonia").</param>
    /// <param name="logDir">Log directory (usually ~/.harbor/logs/).</param>
    /// <param name="consoleLevel">Minimum level for console output.</param>
    /// <param name="fileLevel">Minimum level for file output (always Debug for post-mortem).</param>
    /// <returns>Configured Serilog logger.</returns>
    public static ILogger Create(
        string appPrefix,
        string logDir,
        LogEventLevel consoleLevel = LogEventLevel.Information,
        LogEventLevel fileLevel = LogEventLevel.Debug)
    {
        Directory.CreateDirectory(logDir);

        string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
        string logFile = Path.Combine(logDir, $"harbor-{appPrefix}-{timestamp}.log");

        var loggerConfig = new LoggerConfiguration()
            .Enrich.WithProperty("App", appPrefix)
            .Enrich.WithProperty("PID", Environment.ProcessId);

        // File sink — always Debug level, rolling by file count
        loggerConfig = loggerConfig.WriteTo.Async(a => a.File(
            logFile,
            fileLevel,
            "{Timestamp:HH:mm:ss.fff} [{Level:u4}] [{ThreadId,3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
            shared: true,
            rollingInterval: RollingInterval.Infinite,
            retainedFileCountLimit: 50));

        // Console sink — colored, filtered
        loggerConfig = loggerConfig.WriteTo.Async(a => a.Console(
            consoleLevel,
            "{Timestamp:HH:mm:ss} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

        // Filter noisy categories
        loggerConfig = loggerConfig
            .MinimumLevel.Is(fileLevel)
            .Filter.ByExcluding(e => e.Properties.TryGetValue("SourceContext", out var sc)
                                     && sc.ToString().Contains("System.Net.Http")
                                     && e.Level < LogEventLevel.Warning)
            .Filter.ByExcluding(e => e.Properties.TryGetValue("SourceContext", out var sc)
                                     && sc.ToString().Contains("Microsoft.Extensions")
                                     && e.Level < LogEventLevel.Warning);

        var logger = loggerConfig.CreateLogger();

        // Write startup header
        logger.Information("=== Harbor {App} log started {Timestamp:O} ===", appPrefix, DateTime.UtcNow);
        logger.Information("=== Process ID: {PID} ===", Environment.ProcessId);
        logger.Information("=== .NET: {Runtime} ===", Environment.Version);
        logger.Information("=== OS: {OS} ===", Environment.OSVersion);
        logger.Information("=== Working dir: {Dir} ===", Environment.CurrentDirectory);
        logger.Information("=== File level: >= {FileLevel} ===", fileLevel);
        logger.Information("=== Console level: >= {ConsoleLevel} ===", consoleLevel);
        logger.Information("=== Args: {Args} ===", string.Join(" ", Environment.GetCommandLineArgs()));
        logger.Information(" ");

        return logger;
    }

    /// <summary>
    ///     Create a logger that ALSO writes to a diagnostics callback (for TUI/desktop panel).
    /// </summary>
    public static ILogger CreateWithDiagnostics(
        string appPrefix,
        string logDir,
        Action<LogEventLevel, string, string> diagnosticsCallback,
        LogEventLevel consoleLevel = LogEventLevel.Information,
        LogEventLevel fileLevel = LogEventLevel.Debug)
    {
        var baseLogger = Create(appPrefix, logDir, consoleLevel, fileLevel);

        // Wrap with a diagnostics sink
        return new LoggerConfiguration()
            .WriteTo.Logger(baseLogger)
            .WriteTo.Sink(new DiagnosticsSink(diagnosticsCallback))
            .CreateLogger();
    }

    /// <summary>
    ///     Clean up old log files beyond the retention limit.
    /// </summary>
    public static void CleanupOldLogs(string logDir, int maxFiles = 50)
    {
        try
        {
            var files = Directory.GetFiles(logDir, "harbor-*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(maxFiles)
                .ToList();

            foreach (var f in files)
            {
                try { f.Delete(); }
                catch
                { /* best-effort cleanup, ignore errors */
                }
            }
        }
        catch
        { /* best-effort cleanup, ignore errors */
        }
    }
}

/// <summary>
///     Custom Serilog sink that forwards log events to a diagnostics callback.
///     Used by TUI renderers and Avalonia diagnostics panel.
/// </summary>
internal sealed class DiagnosticsSink : ILogEventSink
{
    private readonly Action<LogEventLevel, string, string> _callback;

    public DiagnosticsSink(Action<LogEventLevel, string, string> callback)
    {
        _callback = callback;
    }

    public void Emit(LogEvent logEvent)
    {
        try
        {
            string source = logEvent.Properties.TryGetValue("SourceContext", out var sc)
                ? sc.ToString().Trim('"')
                : "";
            string message = logEvent.RenderMessage();
            _callback(logEvent.Level, source, message);
        }
        catch
        { /* best-effort cleanup, ignore errors */
        }
    }
}
