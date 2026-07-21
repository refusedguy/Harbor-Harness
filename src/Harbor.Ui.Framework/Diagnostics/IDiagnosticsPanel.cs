using Microsoft.Extensions.Logging;
namespace Harbor.Ui.Framework.Diagnostics;
/// <summary>
///     In-memory sink for log entries that should be surfaced inside an interactive
///     TUI as a diagnostics panel (F12). Backed by a fixed-capacity ring buffer so
///     memory usage stays bounded regardless of how chatty the running agent is.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists:</b> when an interactive TUI renderer (SpectreTUI,
///         Termina, Terminal.Gui, RazorConsole) takes over the alternate screen
///         buffer, console-bound <c>ILogger</c> output corrupts the rendered frame.
///         The console logger is therefore disabled for interactive TUIs (see
///         <c>HostBuilder.ConfigureLogging</c>); log entries are routed here instead
///         and displayed on-demand via F12.
///     </para>
///     <para>
///         <b>Thread safety:</b> implementations MUST be safe for concurrent
///         <see cref="Log" /> calls from any thread (loggers are called from
///         thread-pool threads, agent threads, and the UI thread). The default
///         <see cref="InMemoryDiagnosticsPanel" /> uses a single lock around the
///         ring buffer.
///     </para>
///     <para>
///         <b>Allocation policy:</b> the buffer holds at most
///         <see cref="InMemoryDiagnosticsPanel.DefaultCapacity" /> entries; older
///         entries are evicted FIFO. <see cref="GetRecent" /> returns a defensive
///         copy so renderers may iterate without synchronization.
///     </para>
/// </remarks>
public interface IDiagnosticsPanel
{
    /// <summary>
    ///     Append a log entry to the ring buffer. Safe to call from any thread.
    /// </summary>
    /// <param name="level">The log level (Trace, Debug, Info, Warning, Error, Critical).</param>
    /// <param name="category">Logger category name (typically the calling type's full name).</param>
    /// <param name="message">Formatted log message (already exception-free).</param>
    public void Log(LogLevel level, string category, string message);

    /// <summary>
    ///     Return up to <paramref name="max" /> most-recent entries, oldest-first.
    /// </summary>
    /// <param name="max">Maximum entries to return. Defaults to 100.</param>
    /// <returns>A defensive copy of the recent entries; never <see langword="null" />.</returns>
    public IReadOnlyList<DiagnosticEntry> GetRecent(int max = 100);

    /// <summary>
    ///     Drop all buffered entries. Called when the user clears the panel.
    /// </summary>
    public void Clear();
}
