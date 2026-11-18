using Harbor.Abstractions.Events;
using Harbor.Terminal.Abstractions;
using Harbor.Tui.CellForge.Rendering;
// #436: the finalized-block sink is engine-typed (InlineSession); the attrs
// flowing in here are the Rendering vocabulary, converted value-identically
// (both StyleAttr enums share the ushort layout member-for-member).
using EngineCells = Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Streaming;

/// <summary>
/// Bridges the agent event bus to the inline renderer (CE-1 З.4): text deltas
/// stream through <see cref="StreamBlock"/> (StreamingSync flush policy +
/// CommitTickPacer hysteresis), finalized content commits above the
/// scrollback via <see cref="InlineSession"/>, the composer stays live below.
///
/// CellForge never touches AgentLoop directly — the event bus is the seam.
/// </summary>
public sealed class InlineAgentStreamBridge : IDisposable
{
    private readonly IEventBus _bus;
    private readonly InlineSession _session;
    private readonly ComposerController _composer;
    // #2651: one walker per bridge, created once. No per-event allocation on
    // the streaming hot path. Both unions keep a single traversal in the UI.
    private readonly InlineStreamEventVisitor _events;
    private readonly InlineStreamLlmEventVisitor _llmEvents;
    private StreamBlock? _stream;

    /// <summary>
    ///     #2358: the bus handle is owned by this bridge through the same
    ///     <see cref="EventSubscription"/> primitive the alt-screen bridge uses.
    ///     It lives exactly as long as the bridge and is released on Dispose,
    ///     so tearing down the inline view cannot leave a live handler behind.
    /// </summary>
    private readonly EventSubscription _subscription;

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
        _llmEvents = new InlineStreamLlmEventVisitor(this);
        _events = new InlineStreamEventVisitor(this);
        _subscription = new EventSubscription(bus, HandleEvent);
        Subscription = _subscription.Subscription;
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
        // #2651: the shared visitor is the only traversal over either union on
        // this path. A new AgentEvent/LlmEvent member cannot be dropped in
        // silence: it must get an explicit arm, and an unknown runtime kind is
        // refused by VisitUnknown. Each arm below keeps its old switch body.
        _events.Accept(evt);
        return ValueTask.CompletedTask;
    }

    // ── Event dispatch (visitors, #2651) ────────────────────────────────────

    /// <summary>
    ///     Inline half of the <see cref="ChatScreenBridge.AgentEventVisitor" />
    ///     dispatch. Every union member is declared explicitly; only six change
    ///     inline state, the rest are inert. <c>VisitUnknown</c> is inherited
    ///     and throws, so an unrecognised kind is never silently ignored.
    /// </summary>
    private sealed class InlineStreamEventVisitor(InlineAgentStreamBridge bridge)
        : ChatScreenBridge.AgentEventVisitor
    {
        public override void Visit(AgentStartEvent e) { }
        public override void Visit(TurnStartEvent e) { }

        public override void Visit(MessageStartEvent e) =>
            bridge._stream = new StreamBlock(Environment.TickCount64 & 0x7FFFFFFF);

        public override void Visit(MessageUpdateEvent e) =>
            bridge._llmEvents.Accept(e.LlmEvent);

        public override void Visit(MessageEndEvent e) =>
            bridge.FinishStream();

        public override void Visit(ToolExecutionStartEvent e)
        {
            bridge.FinishStream();
            bridge.CommitLine($"⚙ {e.ToolName}", StyleAttr.Dim);
        }

        public override void Visit(ToolExecutionUpdateEvent e) { }
        public override void Visit(ToolExecutionEndEvent e) { }
        public override void Visit(TurnEndEvent e) { }
        public override void Visit(AgentEndEvent e) { }

        public override void Visit(AgentErrorEvent e)
        {
            bridge.FinishStream();
            bridge.CommitLine($"! {e.Message}", StyleAttr.Bold);
        }

        public override void Visit(PluginBlockedEvent e) { }
        public override void Visit(CompactionStartedEvent e) { }
        public override void Visit(CompactionCompletedEvent e) { }
        public override void Visit(CompactionFailedEvent e) { }
        public override void Visit(SessionStatsEvent e) { }
        public override void Visit(SessionChangedEvent e) { }
    }

    /// <summary>
    ///     Inline half of the <see cref="LlmEventVisitor" /> (#1494) dispatch:
    ///     text and thinking deltas feed the live stream, every other kind is
    ///     inert. <c>VisitUnknown</c> is inherited and throws.
    /// </summary>
    private sealed class InlineStreamLlmEventVisitor(InlineAgentStreamBridge bridge) : LlmEventVisitor
    {
        public override void Visit(TextStartEvent e) { }
        public override void Visit(TextDeltaEvent e) => bridge._stream?.AppendDelta(e.Delta);
        public override void Visit(TextEndEvent e) { }
        public override void Visit(ThinkingStartEvent e) { }

        // O10 #1179: thinking accumulates for the collapsed one-liner committed
        // on finish — never interleaved with the answer.
        public override void Visit(ThinkingDeltaEvent e) => bridge._stream?.AppendThinking(e.Delta);

        public override void Visit(ThinkingEndEvent e) => bridge._stream?.CompleteThinking();
        public override void Visit(ToolCallStartEvent e) { }
        public override void Visit(ToolCallDeltaEvent e) { }
        public override void Visit(ToolCallEndEvent e) { }
        public override void Visit(StepStartEvent e) { }
        public override void Visit(StepFinishEvent e) { }
        public override void Visit(FinishEvent e) { }
        public override void Visit(ErrorEvent e) { }
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

        // O10 #1179: collapsed thinking — one summary + duration line above
        // the answer (opencode ReasoningPart); nothing streams inline.
        if (_stream.ThinkBuffer.Length > 0)
        {
            CommitLine(_stream.ThinkHeaderLine, StyleAttr.Dim | StyleAttr.Italic);
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
    /// Repaints the live region: collapsed reasoning line (while thinking
    /// streams) + revealed stream lines + partial tail above, composer below.
    /// Returns rows occupied (also recorded in the session).
    /// The terminal cursor ends at the prompt caret — inline mode parks it by
    /// construction, no absolute addressing needed.
    /// </summary>
    public int RenderLiveRegion(string? placeholder = null)
    {
        _session.EraseLiveRegion();
        _session.SetLiveLines(0);

        // Reasoning folds to one dim line while it streams (#1171); the
        // transcript layer owns the finished text, so the line hides on
        // ThinkingEnd/Complete instead of lingering.
        string thinking = _stream?.LiveThinkingLabel() ?? string.Empty;
        if (thinking.Length > 0)
        {
            _writer.WriteStyledText(thinking, new EngineCells.CellStyle(attrs: EngineCells.StyleAttr.Dim));
            _writer.WriteLineBreak();
        }

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

        int totalRows = lines.Count + (thinking.Length > 0 ? 1 : 0) + Math.Max(1, CountPromptLines());
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
        _session.WriteFinalizedBlock(text, Width, new EngineCells.CellStyle(attrs: (EngineCells.StyleAttr)attrs));
    }

    public void Dispose() => _subscription.Dispose();
}
