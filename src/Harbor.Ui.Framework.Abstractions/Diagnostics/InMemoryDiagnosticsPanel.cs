using Microsoft.Extensions.Logging;
namespace Harbor.Ui.Framework.Diagnostics;
/// <summary>
///     Default <see cref="IDiagnosticsPanel" /> implementation: a fixed-capacity
///     ring buffer that keeps the last <see cref="DefaultCapacity" /> log entries.
/// </summary>
/// <remarks>
///     <para>
///         Registered as a singleton in <c>HostBuilder</c> whenever an interactive
///         TUI renderer is selected. The <c>DiagnosticsPanelLoggerProvider</c>
///         forwards every <c>ILogger.Log&lt;TState&gt;</c> call into this buffer.
///     </para>
///     <para>
///         <b>Thread safety:</b> all public methods take a single
///         <see cref="object" /> lock. The buffer is small (1000 entries by default)
///         and writes are O(1) amortized (we reuse the underlying array via a
///         <see cref="Queue{T}" />, evicting the head when full).
///     </para>
/// </remarks>
public sealed class InMemoryDiagnosticsPanel : IDiagnosticsPanel
{
    /// <summary>Default ring-buffer capacity. Trade-off: enough history for post-mortem, bounded memory.</summary>
    public const int DefaultCapacity = 1000;
    private readonly Queue<DiagnosticEntry> _entries = new(DefaultCapacity);

    private readonly object _gate = new();

    /// <summary>
    ///     Construct a panel with the supplied capacity (defaults to
    ///     <see cref="DefaultCapacity" />).
    /// </summary>
    /// <param name="capacity">Maximum number of entries kept. Must be &gt; 0.</param>
    public InMemoryDiagnosticsPanel(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            capacity = DefaultCapacity;
        Capacity = capacity;
    }

    /// <summary>Current entry count (mainly for diagnostics / tests).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Maximum number of entries the buffer keeps.</summary>
    public int Capacity
    {
        get;
    }

    /// <inheritdoc />
    public void Log(LogLevel level, string category, string message)
    {
        if (string.IsNullOrEmpty(category))
            category = string.Empty;
        if (message is null)
            message = string.Empty;

        var entry = new DiagnosticEntry(DateTimeOffset.UtcNow, level, category, message);
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DiagnosticEntry> GetRecent(int max = 100)
    {
        if (max <= 0)
            return Array.Empty<DiagnosticEntry>();

        DiagnosticEntry[] snapshot;
        lock (_gate)
        {
            if (_entries.Count == 0)
                return Array.Empty<DiagnosticEntry>();
            snapshot = _entries.ToArray();
        }

        // The user wants the most-recent N entries, oldest-first within that window.
        int start = Math.Max(0, snapshot.Length - max);
        int count = snapshot.Length - start;
        if (start == 0 && count == snapshot.Length)
            return snapshot;
        var result = new DiagnosticEntry[count];
        Array.Copy(snapshot, start, result, 0, count);
        return result;
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }
}
