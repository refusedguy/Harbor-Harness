using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Harbor.Plugins.Host;

/// <summary>
///     Coalesces bursts of MCP tool-catalog changes into a single
///     <c>notifications/tools/list_changed</c> frame per quiet window.
/// </summary>
/// <remarks>
///     <para>
///         A plugin load or unload signals a change. When several changes land
///         inside one debounce window they collapse into one notification, so a
///         chattering catalog does not make the client refresh its registry on
///         every signal (#2228). The debounce clock is <see cref="TimeProvider" />
///         so the timing rule is testable without sleeping.
///     </para>
///     <para>
///         Every notification carries a monotonically increasing generation. A
///         client that receives a stale generation (for example a frame that was
///         queued before a newer refresh) can discard it. The generation is
///         included in <c>params</c>, which MCP leaves open for server-defined
///         notification payloads.
///     </para>
/// </remarks>
internal sealed class McpToolListChangeNotifier
{
    /// <summary>Default quiet window: 150 ms absorbs a plugin-load burst.</summary>
    internal static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(150);

    private const string Method = "notifications/tools/list_changed";

    private readonly object _gate = new();
    private readonly TimeSpan _debounce;
    private readonly TimeProvider _time;
    private long _generation;
    private DateTimeOffset _lastSignalAt;
    private bool _pending;

    /// <summary>
    ///     Create a notifier.
    /// </summary>
    /// <param name="debounce">Quiet window; <see cref="DefaultDebounce" /> when null.</param>
    /// <param name="timeProvider">Clock; <see cref="TimeProvider.System" /> when null.</param>
    public McpToolListChangeNotifier(TimeSpan? debounce = null, TimeProvider? timeProvider = null)
    {
        _debounce = debounce ?? DefaultDebounce;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The newest generation assigned to a change signal.</summary>
    public long CurrentGeneration
    {
        get { lock (_gate) return _generation; }
    }

    /// <summary>Record a tool-catalog change. Never blocks; safe from any thread.</summary>
    public void SignalChange()
    {
        lock (_gate)
        {
            _generation++;
            _lastSignalAt = _time.GetUtcNow();
            _pending = true;
        }
    }

    /// <summary>
    ///     Take the single notification that is due once the quiet window has
    ///     elapsed. Repeated signals inside the window are folded into the one
    ///     frame that is finally emitted, carrying the newest generation.
    /// </summary>
    public bool TryTake(out string? frame)
    {
        frame = null;
        lock (_gate)
        {
            if (!_pending)
                return false;
            if (_time.GetUtcNow() - _lastSignalAt < _debounce)
                return false;

            _pending = false;
            frame = BuildFrame(_generation);
            return true;
        }
    }

    /// <summary>
    ///     Build one NDJSON notification frame. A pooled buffer plus a
    ///     <see cref="Utf8JsonWriter" /> keeps the frame construction reflection-free.
    /// </summary>
    private static string BuildFrame(long generation)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", Method);
            writer.WriteStartObject("params");
            writer.WriteNumber("generation", generation);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }
}
