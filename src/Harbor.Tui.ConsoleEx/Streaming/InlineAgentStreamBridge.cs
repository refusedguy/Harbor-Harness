using Harbor.Abstractions.Events;
using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Streaming;

/// <summary>
/// Bridges the agent event bus to the inline renderer (CE-1 З.4): text deltas
/// stream through <see cref="StreamBlock"/> (StreamingSync flush policy +
/// CommitTickPacer hysteresis), finalized content commits above the
/// scrollback via <see cref="InlineSession"/>, the composer stays live below.
///
/// ConsoleEx never touches AgentLoop directly — the event bus is the seam.
/// </summary>
public sealed class InlineAgentStreamBridge : IDisposable
{
    private readonly IEventBus _bus;
    private readonly InlineSession _session;
    private readonly ComposerController _composer;
    private StreamBlock? _stream;

    public InlineAgentStreamBridge(
        IEventBus bus,
        AnsiWriter writer,
        InlineSession session,
        ComposerController composer)
    {
        _bus = bus;
        _writer = writer;
        _session = session;
        _composer = composer;
        Subscription = bus.Subscribe(HandleEvent);
    }

    private readonly AnsiWriter _writer;
    private readonly List<string> _revealed = new();
    private long _lastNowMs;

    /// <summary>Terminal width used for wrapping (updated on resize).</summary>
    public int Width { get; set; } = 80;

    public IDisposable Subscription { get; }

    /// <summary>Prompt buffer passthrough for input handling before rendering.</summary>
    public PromptBuffer Prompt => _composer.Buffer;

    // ── Event side ─────────────────────────────────────────────────────────

    private ValueTask HandleEvent(AgentEvent evt, CancellationToken ct)
    {
        switch (evt)
        {
            case MessageStartEvent:
                _stream = new StreamBlock(Environment.TickCount64 & 0x7FFFFFFF);
                break;

            case MessageUpdateEvent update when update.LlmEvent is TextDeltaEvent delta:
                _stream?.AppendDelta(delta.Delta);
                break;

            case MessageEndEvent:
                FinishStream();
                break;

            case ToolExecutionStartEvent tool:
                FinishStream();
                CommitLine($"⚙ {tool.ToolName}", StyleAttr.Dim);
                break;

            case AgentErrorEvent error:
                FinishStream();
                CommitLine($"! {error.Message}", StyleAttr.Bold);
                break;
        }

        return ValueTask.CompletedTask;
    }

    // ── Stream lifecycle ───────────────────────────────────────────────────

    /// <summary>Advances pacing by one tick and repaints the live region.</summary>
    public void Tick(long nowMs)
    {
        _lastNowMs = nowMs;
        if (_stream is not null)
        {
            _revealed.AddRange(_stream.Tick(nowMs));
        }
    }

    /// <summary>Finalizes the current stream block into a committed block.</summary>
    public void FinishStream()
    {
        if (_stream is null)
        {
            return;
        }

        _stream.Complete();
        if (_stream.QueuedDepth > 0)
        {
            // Finalized blocks reveal everything in one pass (BatchAll).
            Tick(_lastNowMs);
        }

        var full = BuildStreamText();
        if (full.Length > 0)
        {
            _session.EraseLiveRegion();
            _session.WriteFinalizedBlock(full, Width);
        }

        _stream = null;
        _revealed.Clear();
    }

    // ── Painting ───────────────────────────────────────────────────────────

    /// <summary>
    /// Repaints the live region: revealed stream lines + partial tail above,
    /// composer below. Returns rows occupied (also recorded in the session).
    /// The terminal cursor ends at the prompt caret — inline mode parks it by
    /// construction, no absolute addressing needed.
    /// </summary>
    public int RenderLiveRegion(string? placeholder = null)
    {
        _session.EraseLiveRegion();
        _session.SetLiveLines(0);

        var text = BuildStreamText();
        var lines = new List<string>(64);
        if (text.Length > 0)
        {
            TextWrap.WrapDocument(text, Width, lines);
        }

        foreach (var line in lines)
        {
            _writer.WriteText(line);
            _writer.WriteLineBreak();
        }

        int promptColumn = PromptRenderer.Render(_writer, _composer.Buffer, Width, placeholder);

        int totalRows = lines.Count + Math.Max(1, CountPromptLines());
        _session.SetLiveLines(totalRows);
        return totalRows;
    }

    /// <summary>Flushes buffered bytes to the backend (end of interaction tick).</summary>
    public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
        _writer.FlushAsync(cancellationToken);

    private string BuildStreamText()
    {
        if (_stream is null)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var line in _revealed)
        {
            sb.Append(line).Append('\n');
        }

        var tail = _stream.PartialTail();
        if (!tail.IsEmpty)
        {
            sb.Append(tail);
        }

        return sb.ToString();
    }

    private int CountPromptLines()
    {
        var text = _composer.Buffer.SnapshotText();
        int lines = 1;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    private void CommitLine(string text, StyleAttr attrs)
    {
        _session.EraseLiveRegion();
        _session.WriteFinalizedBlock(text, Width, new CellStyle(attrs: attrs));
    }

    public void Dispose() => Subscription.Dispose();
}
