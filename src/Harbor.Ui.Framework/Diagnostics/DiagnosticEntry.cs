using Microsoft.Extensions.Logging;

namespace Harbor.Ui.Framework.Diagnostics;

/// <summary>
///     One immutable log entry held by <see cref="IDiagnosticsPanel" />.
/// </summary>
/// <param name="Timestamp">UTC timestamp the entry was recorded.</param>
/// <param name="Level">The log level (Trace..Critical).</param>
/// <param name="Category">Logger category (typically the calling type's full name).</param>
/// <param name="Message">Formatted, exception-free log message.</param>
public sealed record DiagnosticEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message);
