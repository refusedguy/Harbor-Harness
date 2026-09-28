using System.Text;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Streaming;

/// <summary>
/// Markdown/thinking stream coalescing extracted from <see cref="ChatScreenBridge"/>
/// (#172): pacer-gated line queue, incoming buffers, live stream slots and the
/// per-message assistant-header meta. Render-thread only; the bridge owns the
/// clock and mirrors the last frame tick into <see cref="NowMs"/>.
/// </summary>
internal sealed class StreamCoalescer
{
    private readonly ChatTimelinePanel _panel;
    private readonly StatusViewModel _status;
    private readonly CommitTickPacer _pacer = new();
    private readonly Queue<PendingLine> _pending = new();
    private readonly StringBuilder _incoming = new();
    private readonly StringBuilder _streamSource = new();
    private StreamingMarkdownBlock? _stream;
    private StreamingThinkingBlock? _thinkStream;
    private readonly StringBuilder _thinkingIncoming = new();

    // Per-message meta for the assistant header (crush-style footer info):
    // stream start tick + accumulated step usage, reset on every start.
    private long _msgStartTick;
    private int _msgTokensIn;
    private int _msgTokensOut;

    /// <summary>Last frame-loop tick (mirrors the bridge clock).</summary>
    public long NowMs { get; set; }

    /// <summary>
    /// Characters handed to the newline scan since the last
    /// <see cref="StartStream"/> — the measurement seam for #489. The scan is
    /// delta-local, so this tracks streamed bytes, never accumulated buffer
    /// length; a regression back to a buffer rescan shows up as an
    /// O(L·D) blowup here. Reset per stream so callers can compare runs.
    /// </summary>
    public int NewlineScanChars { get; private set; }

    public StreamCoalescer(ChatTimelinePanel panel, StatusViewModel status)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _status = status ?? throw new ArgumentNullException(nameof(status));
    }

    private readonly record struct PendingLine(string Text, long AtMs);

    /// <summary>Pacer-gated reveal of queued source lines (Smooth = one line per
    /// tick, CatchUp = batch drain).</summary>
    public void DrainPaced(long nowMs)
    {
        NowMs = nowMs;
        if (_stream is null || _pending.Count == 0)
        {
            return;
        }

        var oldestAge = TimeSpan.FromMilliseconds(nowMs - _pending.Peek().AtMs);
        var plan = _pacer.Decide(new QueueSnapshot(_pending.Count, oldestAge), nowMs);
        int take = plan == DrainPlanKind.BatchAll ? _pending.Count : Math.Min(1, _pending.Count);

        while (take-- > 0)
        {
            PushToStream(_pending.Dequeue().Text);
        }

        // #465: name the block that actually grew. MarkLastDirty() forced a
        // viewport-wide full scan on EVERY paced tick — the single largest
        // unnecessary cost in the renderer — while the damage is only the
        // stream block's own suffix.
        _panel.Timeline.MarkDirty(_stream);
    }

    private void PushToStream(string text)
    {
        if (_stream is null)
        {
            return;
        }

        _stream.Push(text);
        _streamSource.Append(text);
    }

    public void StartStream()
    {
        _incoming.Clear();
        _streamSource.Clear();
        _pending.Clear();
        NewlineScanChars = 0;
        _msgStartTick = NowMs;
        _msgTokensIn = 0;
        _msgTokensOut = 0;
        var fresh = new StreamingMarkdownBlock();
        if (_stream is null)
        {
            _stream = fresh;
            _panel.Timeline.Append(_stream);
        }
        else
        {
            // Re-attempt without MessageEnd (retry path): swap the live slot
            // instead of appending — the superseded partial never duplicates.
            _panel.Timeline.Replace(_stream, fresh);
            _stream = fresh;
        }

        _status.Phase = AgentPhase.Thinking;
        _status.Mode = StatusBarMode.Running;
    }

    /// <summary>Deltas land in the incoming buffer; complete source lines join
    /// the paced queue (codex MarkdownStreamCollector pattern).</summary>
    public void Incoming(string delta)
    {
        if (_stream is null)
        {
            StartStream();
        }

        // #489: `_incoming` only ever holds the text AFTER the last '\n' (the
        // drain below keeps that tail, everything else is enqueued), so a line
        // break can only arrive inside `delta` itself. Scanning the accumulated
        // buffer instead re-walked the whole partial line on every delta — the
        // buffer holds ONE line, so a streamed paragraph with no newline yet
        // (the common case for an answer) was O(L) per delta, O(L·D) per line.
        // The delta-local scan is O(len(delta)); the invariant is pinned by
        // StreamCoalescerScanTests (a '\n' in any later chunk still drains).
        NewlineScanChars += delta.Length;
        bool lineCompleted = delta.AsSpan().IndexOf('\n') >= 0;

        _incoming.Append(delta);
        if (!lineCompleted)
        {
            return; // hot path: partial-line deltas allocate nothing
        }

        var rest = _incoming.ToString();
        _incoming.Clear();

        int consumed = 0;
        while (true)
        {
            int nl = rest.IndexOf('\n', consumed);
            if (nl < 0)
            {
                break;
            }

            var segment = rest.Substring(consumed, nl - consumed + 1);
            _pending.Enqueue(new PendingLine(segment, NowMs));
            consumed = nl + 1;
        }

        if (consumed < rest.Length)
        {
            _incoming.Append(rest.AsSpan(consumed));
        }
    }

    public void StartThinkingStream()
    {
        _thinkingIncoming.Clear();
        var fresh = new StreamingThinkingBlock();
        if (_thinkStream is null)
        {
            _thinkStream = fresh;
            _panel.Timeline.Append(_thinkStream);
        }
        else
        {
            // Same retry swap as StartStream: one live thinking slot, no dupes.
            _panel.Timeline.Replace(_thinkStream, fresh);
            _thinkStream = fresh;
        }
    }

    public void IncomingThinking(string delta)
    {
        if (_thinkStream is null)
        {
            StartThinkingStream();
        }

        // #489: same delta-local scan as Incoming — `_thinkingIncoming` is
        // newline-free on entry by the same construction, so a rescan of the
        // accumulated buffer could only ever re-find what we already drained.
        NewlineScanChars += delta.Length;
        bool lineCompleted = delta.AsSpan().IndexOf('\n') >= 0;

        _thinkingIncoming.Append(delta);
        if (!lineCompleted)
        {
            return; // hot path: partial-line deltas allocate nothing
        }

        var rest = _thinkingIncoming.ToString();
        _thinkingIncoming.Clear();

        int consumed = 0;
        while (true)
        {
            int nl = rest.IndexOf('\n', consumed);
            if (nl < 0)
            {
                break;
            }

            var segment = rest.Substring(consumed, nl - consumed + 1);
            _thinkStream!.Append(segment);
            consumed = nl + 1;
        }

        if (consumed < rest.Length)
        {
            _thinkingIncoming.Append(rest.AsSpan(consumed));
        }

        // #465: the thinking block is the one that grew — narrow rect, not a
        // viewport-wide full scan on every delta.
        if (_thinkStream is not null)
        {
            _panel.Timeline.MarkDirty(_thinkStream);
        }
    }

    public void FinishThinkingStream()
    {
        if (_thinkStream is null)
        {
            return;
        }

        if (_thinkingIncoming.Length > 0)
        {
            _thinkStream.Append(_thinkingIncoming.ToString());
            _thinkingIncoming.Clear();
        }

        var text = _thinkStream.RawText();
        if (!string.IsNullOrEmpty(text))
        {
            var final = new ThinkingBlock(text);

            // [UX4] #264: default-collapsed policy — the finalized block
            // starts collapsed like the stream; only an explicit user expand
            // mid-stream carries over (all three stages).
            if (_thinkStream.IsFullyExpanded)
            {
                final.CycleExpand();
                final.CycleExpand();
            }
            else if (_thinkStream.IsExpanded)
            {
                final.SetExpanded(true);
            }

            _panel.Timeline.Replace(_thinkStream, final);
        }

        _thinkStream = null;
    }

    /// <summary>Commits the finished assistant message over the stream slot.</summary>
    public void FinishStream()
    {
        if (_stream is null)
        {
            return;
        }

        FlushStreamNow();
        _stream.Complete();
        if (_streamSource.Length > 0)
        {
            _panel.Timeline.Replace(_stream, new AssistantMarkdownBlock(_streamSource.ToString(), BuildAssistantHeader()));
        }

        if (_thinkStream is not null)
        {
            FinishThinkingStream();
        }

        _stream = null;
        _pending.Clear();
    }

    /// <summary>Assistant header meta (crush-style): model · duration · tokens.</summary>
    private string? BuildAssistantHeader()
    {
        string model = _status.Model;
        long durMs = _msgStartTick > 0 ? Math.Max(0, NowMs - _msgStartTick) : 0;
        bool hasMeta = durMs > 0 || _msgTokensIn > 0 || _msgTokensOut > 0;
        if (string.IsNullOrEmpty(model) && !hasMeta)
        {
            return null;
        }

        var sb = new StringBuilder("● ");
        sb.Append(string.IsNullOrEmpty(model) ? "assistant" : model);
        if (durMs > 0)
        {
            sb.Append(" · ");
            sb.Append(FormatDuration(durMs));
        }

        if (_msgTokensIn > 0 || _msgTokensOut > 0)
        {
            sb.Append(" · ");
            sb.Append(SideBarView.FormatTokens(_msgTokensIn));
            sb.Append('↑');
            sb.Append(' ');
            sb.Append(SideBarView.FormatTokens(_msgTokensOut));
            sb.Append('↓');
        }

        return sb.ToString();
    }

    /// <summary>Model-only header for replayed blocks (no duration/usage known).</summary>
    public string? ModelHeader() =>
        string.IsNullOrWhiteSpace(_status.Model) ? null : "● " + _status.Model;

    private static string FormatDuration(long ms)
    {
        long s = ms / 1000;
        return s < 60 ? $"{s}s" : $"{s / 60}m{s % 60:00}s";
    }

    /// <summary>Bypasses pacing: everything buffered becomes visible at once.</summary>
    public void FlushStreamNow()
    {
        if (_incoming.Length > 0)
        {
            PushToStream(_incoming.ToString());
            _incoming.Clear();
        }

        if (_thinkingIncoming.Length > 0 && _thinkStream is not null)
        {
            _thinkStream.Append(_thinkingIncoming.ToString());
            _thinkingIncoming.Clear();
        }

        while (_pending.Count > 0)
        {
            PushToStream(_pending.Dequeue().Text);
        }

        if (_stream is not null)
        {
            _panel.Timeline.MarkDirty(_stream);
        }
    }

    /// <summary>Accumulates per-step token usage for the assistant header.</summary>
    public void AddStepUsage(int inputTokens, int outputTokens)
    {
        _msgTokensIn += inputTokens;
        _msgTokensOut += outputTokens;
    }
}
