using Harbor.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
namespace Harbor.App.Avalonia.Hosting;
/// <summary>
///     Serilog configuration for the Avalonia app — writes to BOTH file
///     (<c>~/.harbor/logs/harbor-avalonia-{timestamp}.log</c> at Debug
///     level) and console (stderr at Warning level — Avalonia doesn't use
///     stdout for UI). Extracted from <c>AppHost</c> so the logging setup
///     can be unit-tested in isolation.
/// </summary>
internal static class LoggingConfiguration
{
    /// <summary>
    ///     Configure Serilog logging on the host builder. Clears existing
    ///     providers, wires Serilog as the singleton logger, and cleans up
    ///     old log files (keeps the 50 most recent).
    /// </summary>
    /// <param name="builder">The host builder to configure.</param>
    public static void Configure(HostApplicationBuilder builder)
    {
        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string logDir = Path.Combine(homeDir, ".harbor", "logs");

        // Use Serilog for all logging — writes to BOTH file and console.
        var serilogLogger = LoggerSetup.Create(
            "avalonia",
            logDir,
            LogEventLevel.Warning); // file captures everything

        // Clean up old logs (keep the 50 most recent).
        LoggerSetup.CleanupOldLogs(logDir);

        // Replace .NET logging with Serilog.
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(serilogLogger, true);
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
    }

    /// <summary>
    ///     Create a bootstrap Serilog logger factory for use BEFORE the host
    ///     is built (e.g. for the eager config stores + registries). The
    ///     returned factory is intentionally NOT disposed — its loggers are
    ///     passed to long-lived singletons (ToolRegistry, ProviderRegistry,
    ///     InMemoryMcpRegistry) constructed eagerly below. Disposing the
    ///     factory at end-of-method would silence those singletons.
    /// </summary>
    /// <returns>A <see cref="ILoggerFactory" /> that lives for the process lifetime.</returns>
    public static ILoggerFactory CreateBootstrapLoggerFactory()
    {
        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var serilogLogger = LoggerSetup.Create(
            "avalonia",
            Path.Combine(homeDir, ".harbor", "logs"),
            LogEventLevel.Warning);
        return new SerilogLoggerFactory(serilogLogger, true);
    }
}
