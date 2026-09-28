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
        _panel.Timeline.Append(block);
        _panel.Timeline.MarkLastDirty();
        var card = new ToolCard { Block = block, StartedMs = NowMs };
        _cards[id] = card;
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

        card.Block.Complete(new ToolResultBody(output, true, TimeSpan.Zero));
        card.Block.SetExpanded(false);
        _cards.Remove(id);
        _panel.Timeline.MarkLastDirty();
    }

    public void CompleteCard(ToolExecutionEndEvent e)
    {
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
            if (tl.BlockAt(i) is ToolCallBlock card)
            {
                card.ToggleExpanded();
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
            if (tl.BlockAt(i) is ToolCallBlock card && card.TryHitHeader(mouse.Column, mouse.Row))
            {
                card.ToggleExpanded();
                tl.MarkLastDirty();
                return true;
            }
        }

        return false;
    }
}
