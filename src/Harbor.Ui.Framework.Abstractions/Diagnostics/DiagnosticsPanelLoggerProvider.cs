using Microsoft.Extensions.Logging;
namespace Harbor.Ui.Framework.Diagnostics;
/// <summary>
///     <see cref="ILoggerProvider" /> that bridges every <c>ILogger</c> created by
///     the host's <c>ILoggerFactory</c> into the shared <see cref="IDiagnosticsPanel" />
///     singleton. Register this provider alongside <see cref="InMemoryDiagnosticsPanel" />
///     in <c>HostBuilder.ConfigureLogging</c> when an interactive TUI renderer is
///     active; the console logger is disabled in that case so the alt-screen buffer
///     is not corrupted by interleaved log lines.
/// </summary>
/// <remarks>
///     <para>
///         <b>Filtering:</b> the provider itself does not filter by category or
///         level — the underlying <see cref="IDiagnosticsPanel" /> keeps a bounded
///         ring buffer so noise is auto-trimmed. Use
///         <c>ILoggerFactory.AddFilter&lt;DiagnosticsPanelLoggerProvider&gt;(...)</c>
///         for category- or level-based filtering.
///     </para>
///     <para>
///         <b>Lifetime:</b> the provider owns no resources and is safe to dispose
///         multiple times. The <see cref="IDiagnosticsPanel" /> is shared (singleton)
///         and outlives the provider — disposing the provider does NOT clear the
///         panel.
///     </para>
/// </remarks>
public sealed class DiagnosticsPanelLoggerProvider : ILoggerProvider
{
    private int _disposed;

    /// <summary>
    ///     Construct a provider that forwards to the supplied
    ///     <paramref name="panel" />.
    /// </summary>
    /// <param name="panel">The shared diagnostics panel. Must not be <see langword="null" />.</param>
    public DiagnosticsPanelLoggerProvider(IDiagnosticsPanel panel)
    {
        Panel = panel ?? throw new ArgumentNullException(nameof(panel));
    }

    /// <summary>
    ///     The underlying panel. Exposed so renderers can resolve the same instance
    ///     via the provider (in addition to resolving it directly from DI).
    /// </summary>
    public IDiagnosticsPanel Panel
    {
        get;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        new DiagnosticsPanelLogger(Panel, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        // Idempotent — Microsoft.Extensions.Logging may dispose us via the factory
        // and via the host's service-provider dispose pass.
        Interlocked.Exchange(ref _disposed, 1);
    }
}

/// <summary>
///     Concrete <see cref="ILogger" /> that funnels every
///     <see cref="Log{TState}" /> call into the owning
///     <see cref="DiagnosticsPanelLoggerProvider" />'s panel. Stateless beyond the
///     category name — safe to hand out one per logger request.
/// </summary>
public sealed class DiagnosticsPanelLogger : ILogger
{
    private readonly string _category;
    private readonly IDiagnosticsPanel _panel;

    /// <summary>
    ///     Construct a logger that tags every forwarded entry with
    ///     <paramref name="category" />.
    /// </summary>
    public DiagnosticsPanelLogger(IDiagnosticsPanel panel, string category)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _category = category ?? string.Empty;
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => NullScope.Instance;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;
        if (formatter is null)
            return;

        string message = formatter(state, exception);
        if (exception is not null)
            message = string.IsNullOrEmpty(message)
                ? exception.ToString()
                : message + " | " + exception.GetType().Name + ": " + exception.Message;

        _panel.Log(logLevel, _category, message);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
///     Extension helpers for wiring <see cref="DiagnosticsPanelLoggerProvider" />
///     into an <c>ILoggerFactory</c>. The caller is expected to call
///     <c>builder.Logging.AddProvider(new DiagnosticsPanelLoggerProvider(panel))</c>
///     directly when wiring through <c>ILoggingBuilder</c>.
/// </summary>
public static class DiagnosticsPanelLoggerExtensions
{
    /// <summary>
    ///     Register <see cref="DiagnosticsPanelLoggerProvider" /> against the supplied
    ///     <paramref name="panel" /> on the logger factory. Safe to call multiple times.
    /// </summary>
    public static ILoggerFactory AddDiagnosticsPanel(this ILoggerFactory factory, IDiagnosticsPanel panel)
    {
        factory.AddProvider(new DiagnosticsPanelLoggerProvider(panel));
        return factory;
    }
}
