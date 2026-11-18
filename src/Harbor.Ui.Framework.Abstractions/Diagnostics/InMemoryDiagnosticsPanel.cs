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
///         <see cref="object" /> lock. Entries live in a fixed-size array with
///         a moving head, so writes are O(1) and <see cref="GetRecent" /> copies
///         only the requested tail in O(take), not the whole buffer.
///     </para>
/// </remarks>
public sealed class InMemoryDiagnosticsPanel : IDiagnosticsPanel
{
    /// <summary>Default ring-buffer capacity. Trade-off: enough history for post-mortem, bounded memory.</summary>
    public const int DefaultCapacity = 1000;

    // Ring buffer: _head is the oldest slot, _count the live entry count.
    // A wrapped write advances the head, so the newest entry always occupies
    // (_head + _count - 1) % Capacity.
    private readonly DiagnosticEntry?[] _buffer;
    private int _head;
    private int _count;

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
        _buffer = new DiagnosticEntry?[capacity];
    }

    /// <summary>Current entry count (mainly for diagnostics / tests).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
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
            if (_count == Capacity)
            {
                // Full: overwrite the oldest slot and move the head forward.
                _buffer[_head] = entry;
                _head = (_head + 1) % Capacity;
            }
            else
            {
                _buffer[(_head + _count) % Capacity] = entry;
                _count++;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DiagnosticEntry> GetRecent(int max = 100)
    {
        if (max <= 0)
            return Array.Empty<DiagnosticEntry>();

        // Per-paint path: the logs panel rebuilds every frame asking for at
        // most ~50 rows out of a 1000-entry ring. Read the tail by index
        // instead of walking all Capacity entries under the gate.
        // Oldest-first within the window, as before.
        lock (_gate)
        {
            if (_count == 0)
                return Array.Empty<DiagnosticEntry>();
            int take = Math.Min(max, _count);
            int start = (_head + _count - take) % Capacity;
            var result = new DiagnosticEntry[take];
            for (int i = 0; i < take; i++)
            {
                result[i] = _buffer[(start + i) % Capacity]!;
            }

            return result;
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_buffer);
            _head = 0;
            _count = 0;
        }
    }
}
