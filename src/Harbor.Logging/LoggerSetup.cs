using Serilog;
using Serilog.Events;
using Serilog.Core;

namespace Harbor.Logging;

/// <summary>
///     Centralized Serilog configuration — shared by CLI, Avalonia, and all desktop apps.
///     Writes to:
///     - File: ~/.harbor/logs/harbor-{appPrefix}-{timestamp}.log (rolling, keep 50)
///     - Console: colored, filtered by app mode (verbose in Debug, info in Release)
///     - Optional diagnostics panel: via ILogger → IDiagnosticsPanel bridge
/// </summary>
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

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
        var logFile = Path.Combine(logDir, $"harbor-{appPrefix}-{timestamp}.log");

        var loggerConfig = new LoggerConfiguration()
            .Enrich.WithProperty("App", appPrefix)
            .Enrich.WithProperty("PID", Environment.ProcessId);

        // File sink — always Debug level, rolling by file count
        loggerConfig = loggerConfig.WriteTo.Async(a => a.File(
            logFile,
            fileLevel,
            outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u4}] [{ThreadId,3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
            shared: true,
            rollingInterval: RollingInterval.Infinite,
            retainedFileCountLimit: 50));

        // Console sink — colored, filtered
        loggerConfig = loggerConfig.WriteTo.Async(a => a.Console(
            consoleLevel,
            outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

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
                try { f.Delete(); } catch { /* best-effort cleanup, ignore errors */ }
            }
        }
        catch { /* best-effort cleanup, ignore errors */ }
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
            var source = logEvent.Properties.TryGetValue("SourceContext", out var sc)
                ? sc.ToString().Trim('"')
                : "";
            var message = logEvent.RenderMessage();
            _callback(logEvent.Level, source, message);
        }
        catch { /* best-effort cleanup, ignore errors */ }
    }
}
