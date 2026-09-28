using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Streaming;

/// <summary>
/// Tool-card lifecycle extracted from <see cref="ChatScreenBridge"/> (#172):
/// live card tracking, completion with duration + unified diff, result image
/// attachments, expand/collapse routing and args shaping. Render-thread only;
/// the bridge owns the clock and mirrors the last frame tick into
/// <see cref="NowMs"/>.
/// </summary>
internal sealed class ToolCardTracker
{
    private readonly ChatTimelinePanel _panel;
    private readonly Dictionary<string, ToolCard> _cards = new(StringComparer.Ordinal);
    private readonly Queue<ChatScreenBridge.InlineImage> _pendingImages = new();

    /// <summary>Last frame-loop tick (mirrors the bridge clock).</summary>
    public long NowMs { get; set; }

    public ToolCardTracker(ChatTimelinePanel panel)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
    }

    private sealed class ToolCard
    {
        public required ToolCallBlock Block { get; init; }
        public long StartedMs { get; set; } = long.MinValue;
    }

    /// <summary>Tool name carrying a sub-agent run ([UX5] #265).</summary>
    internal const string TaskToolName = "task";

    internal static bool IsTaskTool(string toolName) =>
        string.Equals(toolName, TaskToolName, StringComparison.Ordinal);

    private const int MaxTranscriptChars = 8000;
    private const int MaxTranscriptLines = 200;
    private const string TranscriptTruncatedMarker = "…[transcript truncated]";
    private const string TranscriptSeparator = "\n───\n";

    /// <summary>Live display state of one running <c>task</c> card ([UX5] #265):
    /// child-tool tallies for the finish suffix, the currently running child
    /// (or retry) for the live header suffix, and the buffered child
    /// transcript revealed on expand. The child run itself is untouched —
    /// this only observes bus events.</summary>
    private sealed class TaskState
    {
        public int CompletedChildCalls;
        public readonly Dictionary<string, ChildRun> Running = new(StringComparer.Ordinal);
        public string LastChildTool = string.Empty;
        public int? RetryAttempt;
        public int? RetryMax;
        public readonly StringBuilder Transcript = new();
        public int TranscriptLines;
        public bool TranscriptTruncated;
        public readonly StringBuilder PendingText = new();
    }

    private sealed record ChildRun(string ToolName, string Summary, long StartedMs);

    private readonly Dictionary<string, TaskState> _tasks = new(StringComparer.Ordinal);

    /// <summary>Returns the live card, creating and appending it on first sight.
    /// With <paramref name="restampStarted"/> the start timestamp is refreshed
    /// even for an existing card (ToolExecutionStart arrives after
    /// ToolCallStart, and durations measure from execution).</summary>
    public void EnsureCard(string id, string toolName, string? argsSummary, string? argsFull = null, bool restampStarted = false)
    {
        if (_cards.TryGetValue(id, out var existing))
        {
            if (restampStarted)
            {
                existing.StartedMs = NowMs;
            }

            return;
        }

        var block = new ToolCallBlock(new ToolCallInfo(id, toolName, argsSummary ?? string.Empty, ArgsFull: argsFull));
        if (IsTaskTool(toolName))
        {
            // [UX5] #265 + PRIM1d #294: a task card is always exactly one
            // collapsed line — the live suffix in the header carries progress,
            // the full child transcript waits in the expanded body. Collapse
            // state lives on ICollapsibleChatBlock (zero budget = pure
            // one-liner, see ClampedBodyLineCount).
            ICollapsibleChatBlock collapsible = block;
            collapsible.MaxBodyLines = 0;
            _tasks.TryAdd(id, new TaskState());
        }
        else if (ReadGroupBlock.IsReadOnlyTool(toolName))
        {
            // [UX3] #263: consecutive read-only context tools coalesce into
            // one "gathered context" group line (opencode pattern) instead of
            // N cards. Single reads stay plain ToolCallBlocks.
            EnsureReadCard(id, block);
            return;
        }

        _panel.Timeline.Append(block);
        _panel.Timeline.MarkLastDirty();
        var card = new ToolCard { Block = block, StartedMs = NowMs };
        _cards[id] = card;
    }

    // ── Read group ([UX3] #263) ────────────────────────────────────────────
    // Consecutive read-only context tools (read/glob/grep/ls/tree/ripgrep)
    // share one "gathered context" group line: the second adjacent read-only
    // card converts the pair into a ReadGroupBlock (in-place Replace — no
    // timeline removal needed), further adjacent ones extend it. Any other
    // block (tool card, text, gate) breaks the run. Members stay tracked in
    // _cards by id, so completion, durations and toggle routing work unchanged.

    private int _readGroupSeq;

    private string NewReadGroupId() => $"readgroup-{_readGroupSeq++}";

    private void EnsureReadCard(string id, ToolCallBlock block)
    {
        var tl = _panel.Timeline;
        if (tl.Count > 0)
        {
            var last = tl.BlockAt(tl.Count - 1);
            if (last is ReadGroupBlock group)
            {
                group.AddMember(block);
                _cards[id] = new ToolCard { Block = block, StartedMs = NowMs };
                tl.MarkLastDirty();
                return;
            }

            if (last is ToolCallBlock lastCard && ReadGroupBlock.IsReadOnlyTool(lastCard.Info.ToolName))
            {
                var merged = new ReadGroupBlock(NewReadGroupId());
                merged.AddMember(lastCard);
                merged.AddMember(block);
                tl.Replace(last, merged);
                _cards[id] = new ToolCard { Block = block, StartedMs = NowMs };
                tl.MarkLastDirty();
                return;
            }
        }

        tl.Append(block);
        tl.MarkLastDirty();
        _cards[id] = new ToolCard { Block = block, StartedMs = NowMs };
    }

    /// <summary>Agent-level error rendered through the same collapsible
    /// card component as tool calls: error-glyph header with the short
    /// blurb, collapsed by default, Enter/click expands the full text.
    /// No <see cref="ToolExecutionEndEvent" /> is fabricated — completion
    /// lands directly on the block.</summary>
    public void CompleteError(string id, string toolName, string summary, string output)
    {
        EnsureCard(id, toolName, summary, argsFull: output);
        if (!_cards.TryGetValue(id, out var card))
        {
            return;
        }

        // [UX5] #265: a failed task keeps its transcript + tally, collapsed.
        if (_tasks.TryGetValue(id, out var taskState))
        {
            FinishTaskCard(id, taskState, output, isError: true, duration: TimeSpan.Zero, diffText: null);
            return;
        }

        card.Block.Complete(new ToolResultBody(output, true, TimeSpan.Zero));
        // PRIM1d #294: error cards collapse through the ICollapsibleChatBlock
        // mixin (default budget) — Enter/click expands the full text.
        ICollapsibleChatBlock errorCard = card.Block;
        errorCard.SetExpanded(false);
        _cards.Remove(id);
        _panel.Timeline.MarkLastDirty();
    }

    public void CompleteCard(ToolExecutionEndEvent e)
    {
        // [UX5] #265: the task's own end composes the transcript + tally.
        if (_tasks.TryGetValue(e.ToolCallId, out var taskState))
        {
            FinishTaskCard(e.ToolCallId, taskState, e.Result.Output, e.Result.IsError, duration: null, diffText: TryExtractDiff(e.Result));
            return;
        }

        if (!_cards.TryGetValue(e.ToolCallId, out var card))
        {
            return;
        }

        long startedAt = card.StartedMs > long.MinValue ? card.StartedMs : NowMs;
        long durationMs = Math.Max(0, NowMs - startedAt);
        card.Block.Complete(new ToolResultBody(
            e.Result.Output,
            e.Result.IsError,
            TimeSpan.FromMilliseconds(durationMs),
            TryExtractDiff(e.Result)));
        _cards.Remove(e.ToolCallId);

        // Вложенные изображения результата — карточки в таймлайне следом за тулом.
        if (e.Result.Attachments is { Count: > 0 } attachments)
        {
            foreach (var att in attachments)
            {
                if (att.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    AppendImageCard(att.Path, att.MimeType, att.Data.Length, att.Data);
                }
            }
        }

        _panel.Timeline.MarkLastDirty();
    }

    public void AppendImageCard(string path, string mime, long sizeBytes, byte[]? data)
    {
        _panel.Timeline.Append(new ImageBlock(path, mime, sizeBytes, data));
        if (data is { Length: > 0 })
        {
            // Inline-image hand-off (osc-sprint §1337): the host frame loop
            // drains payloads and emits them through the terminal backend —
            // the tracker itself never touches I/O.
            _pendingImages.Enqueue(new ChatScreenBridge.InlineImage(path, mime, data));
        }
    }

    /// <summary>Dequeues the next image attachment awaiting inline emission
    /// (kitty APC / OSC 1337 per terminal capability); false when drained.</summary>
    public bool TryTakePendingImage(out ChatScreenBridge.InlineImage image) => _pendingImages.TryDequeue(out image!);

    // ── Task cards ([UX5] #265) ────────────────────────────────────────────

    /// <summary>True while a <c>task</c> card is live on the feed.</summary>
    public bool TryGetRunningTask(out string taskCallId)
    {
        foreach (var (id, card) in _cards)
        {
            if (IsTaskTool(card.Block.Info.ToolName))
            {
                taskCallId = id;
                return true;
            }
        }

        taskCallId = string.Empty;
        return false;
    }

    /// <summary>True when <paramref name="id"/> already owns a timeline card
    /// (lets the bridge tell a retrying parent tool apart from a retrying
    /// child tool — children never open their own cards).</summary>
    public bool IsKnownCard(string id) => _cards.ContainsKey(id);

    /// <summary>Child sub-agent session announced itself — never a timeline
    /// block of its own, just a transcript header line on the task card.</summary>
    public void NoteSubagentStart(string taskId, string sessionId)
    {
        if (_tasks.TryGetValue(taskId, out var ts))
        {
            AppendTranscript(ts, $"◇ sub-agent {ShortId(sessionId)} started");
            RefreshTaskSuffix(taskId, ts);
        }
    }

    /// <summary>A child tool started inside the task window: no card of its
    /// own — it becomes the live header suffix and (on end) one transcript
    /// line.</summary>
    public void NoteTaskChildStart(string taskId, string childId, string toolName, string? summary)
    {
        if (!_tasks.TryGetValue(taskId, out var ts))
        {
            return;
        }

        FlushPendingText(ts);
        ts.Running[childId] = new ChildRun(toolName, summary ?? string.Empty, NowMs);
        ts.LastChildTool = toolName;
        ts.RetryAttempt = null;
        ts.RetryMax = null;
        RefreshTaskSuffix(taskId, ts);
    }

    /// <summary>Closes one child transcript line (<c>⚙ tool args → ok · 12ms</c>).
    /// False when the child was never tracked — the caller then falls back to
    /// the ordinary card path.</summary>
    public bool NoteTaskChildEnd(string taskId, string childId, bool isError)
    {
        if (!_tasks.TryGetValue(taskId, out var ts) || !ts.Running.Remove(childId, out var run))
        {
            return false;
        }

        FlushPendingText(ts);
        long durationMs = Math.Max(0, NowMs - run.StartedMs);
        string outcome = isError ? "error" : "ok";
        string duration = ToolResultBody.FormatDuration(TimeSpan.FromMilliseconds(durationMs));
        AppendTranscript(ts, string.IsNullOrEmpty(run.Summary)
            ? $"⚙ {run.ToolName} → {outcome} · {duration}"
            : $"⚙ {run.ToolName} {run.Summary} → {outcome} · {duration}");
        ts.CompletedChildCalls++;
        ts.RetryAttempt = null;
        ts.RetryMax = null;
        RefreshTaskSuffix(taskId, ts);
        return true;
    }

    /// <summary>A retry scheduled inside the task window — red suffix
    /// (<c>↻ read 1/3</c>) plus a transcript marker.</summary>
    public void NoteTaskRetry(string taskId, int attempt, int maxAttempts)
    {
        if (!_tasks.TryGetValue(taskId, out var ts))
        {
            return;
        }

        ts.RetryAttempt = attempt;
        ts.RetryMax = maxAttempts;
        AppendTranscript(ts, ts.LastChildTool.Length > 0
            ? $"↻ retry {attempt}/{maxAttempts} {ts.LastChildTool}"
            : $"↻ retry {attempt}/{maxAttempts}");
        RefreshTaskSuffix(taskId, ts);
    }

    /// <summary>Buffers one child text delta for the expand transcript —
    /// flushed as a paragraph on the next child boundary.</summary>
    public void AppendTaskText(string taskId, string text)
    {
        if (string.IsNullOrEmpty(text) || !_tasks.TryGetValue(taskId, out var ts) || ts.TranscriptTruncated)
        {
            return;
        }

        int room = MaxTranscriptChars - ts.Transcript.Length - ts.PendingText.Length;
        if (room <= 0)
        {
            MarkTranscriptTruncated(ts);
            return;
        }

        ts.PendingText.Append(text.Length <= room ? text : text[..room]);
        if (text.Length > room)
        {
            MarkTranscriptTruncated(ts);
        }
    }

    /// <summary>Flushes buffered child text as one transcript paragraph.</summary>
    public void FlushTaskText(string taskId)
    {
        if (_tasks.TryGetValue(taskId, out var ts))
        {
            FlushPendingText(ts);
            RefreshTaskSuffix(taskId, ts);
        }
    }

    private void FinishTaskCard(string taskId, TaskState ts, string output, bool isError, TimeSpan? duration, string? diffText)
    {
        if (!_cards.TryGetValue(taskId, out var card))
        {
            _tasks.Remove(taskId);
            return;
        }

        FlushPendingText(ts);
        TimeSpan elapsed = duration ?? (card.StartedMs > long.MinValue
            ? TimeSpan.FromMilliseconds(Math.Max(0, NowMs - card.StartedMs))
            : TimeSpan.Zero);
        string body = ts.Transcript.Length > 0
            ? ts.Transcript.ToString().TrimEnd('\n') + TranscriptSeparator + output
            : output;
        card.Block.Complete(new ToolResultBody(body, isError, elapsed, diffText));
        card.Block.LiveSuffix = ts.CompletedChildCalls > 0
            ? $"· {ts.CompletedChildCalls} toolcall{(ts.CompletedChildCalls == 1 ? string.Empty : "s")}"
            : null;
        card.Block.LiveSuffixIsError = false;
        // PRIM1d #294: finished task cards collapse through the mixin —
        // transcript + tally wait in the expanded body.
        ICollapsibleChatBlock taskCard = card.Block;
        taskCard.SetExpanded(false);
        _cards.Remove(taskId);
        _tasks.Remove(taskId);
        _panel.Timeline.MarkLastDirty();
    }

    private void RefreshTaskSuffix(string taskId, TaskState ts)
    {
        if (!_cards.TryGetValue(taskId, out var card))
        {
            return;
        }

        if (ts.RetryAttempt is { } attempt && ts.RetryMax is { } max)
        {
            string tool = ts.LastChildTool.Length > 0 ? ts.LastChildTool : TaskToolName;
            card.Block.LiveSuffix = $"↻ {tool} {attempt}/{max}";
            card.Block.LiveSuffixIsError = true;
        }
        else if (ts.Running.Count == 1)
        {
            var run = ts.Running.Values.First();
            card.Block.LiveSuffix = string.IsNullOrEmpty(run.Summary)
                ? $"› {run.ToolName}"
                : $"› {run.ToolName} {TrimSuffix(run.Summary, 32)}";
            card.Block.LiveSuffixIsError = false;
        }
        else if (ts.Running.Count > 1)
        {
            card.Block.LiveSuffix = $"› {ts.LastChildTool} +{ts.Running.Count - 1}";
            card.Block.LiveSuffixIsError = false;
        }
        else if (ts.CompletedChildCalls > 0)
        {
            card.Block.LiveSuffix = $"· {ts.CompletedChildCalls} toolcall{(ts.CompletedChildCalls == 1 ? string.Empty : "s")}";
            card.Block.LiveSuffixIsError = false;
        }
        else
        {
            card.Block.LiveSuffix = null;
            card.Block.LiveSuffixIsError = false;
        }

        _panel.Timeline.MarkLastDirty();
    }

    private static void FlushPendingText(TaskState ts)
    {
        if (ts.PendingText.Length == 0)
        {
            return;
        }

        string text = ts.PendingText.ToString().Trim();
        ts.PendingText.Clear();
        if (text.Length > 0)
        {
            AppendTranscript(ts, text);
        }
    }

    private static void AppendTranscript(TaskState ts, string line)
    {
        if (ts.TranscriptTruncated)
        {
            return;
        }

        if (ts.TranscriptLines >= MaxTranscriptLines || ts.Transcript.Length + line.Length + 1 > MaxTranscriptChars)
        {
            MarkTranscriptTruncated(ts);
            return;
        }

        ts.Transcript.AppendLine(line);
        ts.TranscriptLines++;
    }

    private static void MarkTranscriptTruncated(TaskState ts)
    {
        if (ts.TranscriptTruncated)
        {
            return;
        }

        ts.TranscriptTruncated = true;
        ts.Transcript.AppendLine(TranscriptTruncatedMarker);
        ts.TranscriptLines++;
    }

    private static string TrimSuffix(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        return text[..(max - 1)] + "…";
    }

    private static string ShortId(string sessionId) =>
        sessionId.Length <= 8 ? sessionId : sessionId[..8];

    /// <summary>Typed-ish diff extraction (widgets §5): tools that attach a
    /// unified diff in Metadata win; otherwise a raw diff-shaped Output is
    /// used verbatim. No heuristics beyond shape checks.</summary>
    internal static string? TryExtractDiff(ToolResult result)
    {
        if (result.Metadata is string meta && UnifiedDiffParser.LooksLikeDiff(meta))
        {
            return meta;
        }

        return UnifiedDiffParser.LooksLikeDiff(result.Output) ? result.Output : null;
    }

    internal static string Summarize(JsonElement args)
    {
        if (args.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            return string.Empty;
        }

        var raw = args.GetRawText().Replace("\n", " ", StringComparison.Ordinal).Replace("  ", " ", StringComparison.Ordinal);
        return raw.Length <= 48 ? raw : raw[..47] + "…";
    }

    /// <summary>Complete single-line args payload for the expanded card row (bounded for eviction accounting).</summary>
    internal static string FullArgs(JsonElement args)
    {
        if (args.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            return string.Empty;
        }

        var raw = args.GetRawText().Replace("\n", " ", StringComparison.Ordinal);
        const int cap = 2000;
        return raw.Length <= cap ? raw : raw[..(cap - 1)] + "…";
    }

    /// <summary>
    /// Toggles the expanded state of one tool card by id (feed Enter path).
    /// Returns false when no card carries <paramref name="toolCallId"/>.
    /// </summary>
    public bool ToggleToolCard(string toolCallId)
    {
        if (string.IsNullOrEmpty(toolCallId))
        {
            return false;
        }

        var tl = _panel.Timeline;
        for (int i = 0; i < tl.Count; i++)
        {
            if (tl.BlockAt(i) is ToolCallBlock card
                && string.Equals(card.Info.Id, toolCallId, StringComparison.Ordinal))
            {
                card.ToggleExpanded();
                tl.MarkLastDirty();
                return true;
            }

            // [UX3] #263: a read group toggles by its own id or any member id.
            if (tl.BlockAt(i) is ReadGroupBlock group
                && (string.Equals(group.Id, toolCallId, StringComparison.Ordinal) || group.ContainsMember(toolCallId)))
            {
                group.ToggleExpanded();
                tl.MarkLastDirty();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Routes a plain Enter press to the newest tool card on the feed (toggles
    /// expand/collapse). Hosts call this after approval routing and before the
    /// composer so feed-Enter expands cards while composer-Enter still submits
    /// — ordering stays host-side. Returns false when the key is not a plain
    /// Enter press or the feed holds no tool card.
    /// </summary>
    public bool TryRouteToolCardKey(in KeyEvent key)
    {
        if (key.EventType is not (KeyEventType.Press or KeyEventType.Repeat)
            || key.Key != KeyCode.Enter
            || key.Modifiers != KeyModifiers.None)
        {
            return false;
        }

        var tl = _panel.Timeline;
        for (int i = tl.Count - 1; i >= 0; i--)
        {
            var block = tl.BlockAt(i);
            // [UX3] #263: the read group expands like any tool card (no policy
            // change — the same newest-card Enter toggles it).
            if (block is ToolCallBlock or ReadGroupBlock)
            {
                ((ICollapsibleChatBlock)block).ToggleExpanded();
                tl.MarkLastDirty();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Routes a left-button press/click on a tool-card header to
    /// expand/collapse (mirrors approval-click routing).
    /// Returns false when the click lands outside every card header —
    /// callers keep normal scroll/selection behavior.
    /// </summary>
    public bool TryRouteToolCardClick(in Input.MouseEvent mouse)
    {
        if (mouse.Type is not (Input.MouseEventType.Press or Input.MouseEventType.Click)
            || mouse.Button != Input.MouseButton.Left)
        {
            return false;
        }

        var tl = _panel.Timeline;
        for (int i = tl.Count - 1; i >= 0; i--)
        {
            var block = tl.BlockAt(i);
            // [UX3] #263: group headers toggle like card headers.
            bool hit = block switch
            {
                ToolCallBlock card => card.TryHitHeader(mouse.Column, mouse.Row),
                ReadGroupBlock group => group.TryHitHeader(mouse.Column, mouse.Row),
                _ => false,
            };
            if (hit)
            {
                ((ICollapsibleChatBlock)block).ToggleExpanded();
                tl.MarkLastDirty();
                return true;
            }
        }

        return false;
    }
}
