using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Rendering.Markdown;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Streaming;

/// <summary>
/// Alt-screen counterpart of <see cref="InlineAgentStreamBridge"/> (CE-3 W2.4):
/// feeds agent events into the <see cref="ChatTimelinePanel"/> feed instead of
/// the inline scrollback. Deltas pass through the CE-1
/// <see cref="CommitTickPacer"/> — completed source lines queue up and reveal
/// at typing rate in Smooth mode or burst in CatchUp (widgets §3.4), so token
/// storms never outpace frames. The event bus remains the only seam: no
/// direct AgentLoop coupling.
///
/// Event → block map:
///   AgentStart            → history replay (UserBlock / AssistantMarkdownBlock)
///   MessageStart          → live StreamingMarkdownBlock appended
///   TextDelta             → pacer-gated pushes into the live block
///   ToolCallStart         → ToolCallBlock(Running)
///   ToolExecutionStart    → args summary + start timestamp
///   ToolExecutionEnd      → Ok/Error + duration (+ unified-diff body when present)
///   MessageEnd            → committed AssistantMarkdownBlock replaces the stream slot
///   AgentError/AgentEnd   → SystemBlock notice, footer back to Idle
/// </summary>
public sealed class ChatScreenBridge : IDisposable
{
    private readonly IEventBus _bus;
    private readonly ChatTimelinePanel _panel;
    private readonly StatusViewModel _status;
    private readonly CommitTickPacer _pacer = new();
    private readonly Queue<PendingLine> _pending = new();
    private readonly Dictionary<string, ToolCard> _cards = new(StringComparer.Ordinal);
    private readonly StringBuilder _incoming = new();
    private readonly StringBuilder _streamSource = new();
    private StreamingMarkdownBlock? _stream;
    private StreamingThinkingBlock? _thinkStream;
    private readonly StringBuilder _thinkingIncoming = new();
    private long _nowMs;

    private readonly HashSet<string> _displayedMessageIds = new();

    /// <summary>AgentErrorEvent seen since the last AgentStart — decides
    /// whether AgentEnd flags the run as errored or succeeded (mascot moods).</summary>
    private bool _runHadError;

    private readonly record struct PendingLine(string Text, long AtMs);

    private sealed class ToolCard
    {
        public required ToolCallBlock Block { get; init; }
        public long StartedMs { get; set; } = long.MinValue;
    }

    public ChatScreenBridge(IEventBus bus, ChatTimelinePanel panel, StatusViewModel status, bool autoSubscribe = true)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _status = status ?? throw new ArgumentNullException(nameof(status));
        // Auto-subscribe suits fire-and-forget hosts (CE-3 tests). A driven
        // host (CellForge REPL frame loop) passes false and pumps events via
        // <see cref="AcceptAsync"/> so all timeline mutation stays on the
        // render thread — zero cross-thread access to the block list.
        Subscription = autoSubscribe ? bus.Subscribe(HandleEvent) : NoSubscription.Instance;
    }

    public IDisposable Subscription { get; }

    /// <summary>
    ///     Loop-driven entry point: process one real bus event on the caller's
    ///     (render) thread. Pair with <c>autoSubscribe: false</c>.
    /// </summary>
    public ValueTask AcceptAsync(AgentEvent evt, CancellationToken ct = default) => HandleEvent(evt, ct);

    private sealed class NoSubscription : IDisposable
    {
        public static readonly NoSubscription Instance = new();
        public void Dispose()
        {
        }
    }

    /// <summary>Monotonic clock injection point (frame pipeline calls each tick).</summary>
    public void Tick(long nowMs)
    {
        _nowMs = nowMs;
        DrainGateQueue();
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

        _panel.Timeline.MarkLastDirty();
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

    private ValueTask HandleEvent(AgentEvent evt, CancellationToken ct)
    {
        switch (evt)
        {
            case AgentStartEvent started:
                ReplayHistory(started.Messages);
                _runHadError = false;
                _status.Phase = AgentPhase.Auto;
                _status.Mode = StatusBarMode.Running;
                break;

            case MessageStartEvent:
                StartStream();
                break;

            case MessageUpdateEvent update:
                switch (update.LlmEvent)
                {
                    case TextDeltaEvent delta:
                        Incoming(delta.Delta);
                        break;
                    case ThinkingStartEvent _:
                        StartThinkingStream();
                        break;
                    case ThinkingDeltaEvent delta:
                        IncomingThinking(delta.Delta);
                        break;
                    case ThinkingEndEvent _:
                        FinishThinkingStream();
                        break;
                    case ToolCallStartEvent callStart:
                        EnsureCard(callStart.Id, callStart.ToolName, argsSummary: null);
                        break;
                }

                break;

            case ToolExecutionStartEvent execStart:
                {
                    var card = EnsureCard(execStart.ToolCallId, execStart.ToolName, Summarize(execStart.Args));
                    card.StartedMs = _nowMs;
                    _status.Phase = AgentPhase.ToolCall;
                    _status.Mode = StatusBarMode.Running;
                    break;
                }

            case ToolExecutionEndEvent execEnd:
                CompleteCard(execEnd);
                break;

            case MessageEndEvent:
                FinishStream();
                break;

            case CompactionStartedEvent:
                AppendSystem("compacting history…");
                _status.Mode = StatusBarMode.Compacting;
                break;

            case CompactionCompletedEvent:
                AppendSystem("history compacted");
                _status.Mode = StatusBarMode.Running;
                break;

            case SessionStatsEvent stats:
                _status.SetUsage(stats.Metadata.TokensInput, stats.Metadata.TokensOutput, stats.Metadata.Cost);
                break;

            case AgentErrorEvent error:
                FlushStreamNow();
                AppendSystem("! " + error.Message);
                _runHadError = true;
                _status.Phase = AgentPhase.Errored;
                _status.SignalMascot(MascotReaction.ErrorBlink);
                _status.Mode = StatusBarMode.Idle;
                break;

            case AgentEndEvent:
                FlushStreamNow();
                _status.Phase = _runHadError ? AgentPhase.Errored : AgentPhase.Succeeded;
                if (!_runHadError)
                {
                    _status.SignalMascot(MascotReaction.SuccessBounce);
                }

                _status.Mode = StatusBarMode.Idle;
                break;
        }

        return ValueTask.CompletedTask;
    }

    // ── Stream lifecycle ───────────────────────────────────────────────────

    internal void ReplayHistory(IReadOnlyList<AgentMessage> messages)
    {
        bool lastBlockIsMatchingUser = false;
        if (_panel.Timeline.Count > 0 && messages.Count > 0 && messages[^1] is UserMessage lastUm)
        {
            var last = _panel.Timeline.BlockAt(_panel.Timeline.Count - 1);
            lastBlockIsMatchingUser = IsUserBlockMatching(last, lastUm.Content);
        }

        for (int i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Id is not null && _displayedMessageIds.Contains(message.Id))
                continue;

            bool isLastLocallyEchoedUser = i == messages.Count - 1
                && message is UserMessage
                && lastBlockIsMatchingUser;

            if (isLastLocallyEchoedUser)
            {
                if (message.Id is not null)
                    _displayedMessageIds.Add(message.Id);
                continue;
            }

            AppendHistoryMessage(message);
            if (message.Id is not null)
                _displayedMessageIds.Add(message.Id);
        }
    }

    private static bool IsUserBlockMatching(IChatBlock block, string expectedContent)
    {
        if (block is not UserBlock ub) return false;
        ReadOnlySpan<char> s1 = ub.RawText().AsSpan().Trim();
        if (s1.StartsWith("›")) s1 = s1[1..].TrimStart();
        ReadOnlySpan<char> s2 = expectedContent.AsSpan().Trim();
        if (s2.StartsWith("›")) s2 = s2[1..].TrimStart();
        return s1.SequenceEqual(s2);
    }

    public void ResetMessageTracking() => _displayedMessageIds.Clear();

    private void AppendHistoryMessage(AgentMessage message)
    {
        switch (message)
        {
            case UserMessage user:
                _panel.Timeline.Append(new UserBlock(user.Content));
                break;

            case AssistantMessage assistant:
                var text = new StringBuilder();
                foreach (var part in assistant.Parts)
                {
                    switch (part)
                    {
                        case TextPart tp:
                            text.AppendLine(tp.Text);
                            break;
                        case FilePart { MimeType: var mime } file when mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                            // Порядок карточек = порядку частей: накопленный
                            // текст коммитится перед изображением.
                            if (text.Length > 0)
                            {
                                _panel.Timeline.Append(new AssistantMarkdownBlock(text.ToString()));
                                text.Clear();
                            }

                            AppendImageCard(file.Path, mime, file.SizeBytes, file.Data);
                            break;
                    }
                }

                if (text.Length > 0)
                {
                    _panel.Timeline.Append(new AssistantMarkdownBlock(text.ToString()));
                }

                break;
        }
    }

    private void StartStream()
    {
        _incoming.Clear();
        _streamSource.Clear();
        _pending.Clear();
        _stream = new StreamingMarkdownBlock();
        _panel.Timeline.Append(_stream);
        _status.Phase = AgentPhase.Thinking;
        _status.Mode = StatusBarMode.Running;
    }

    /// <summary>Deltas land in the incoming buffer; complete source lines join
    /// the paced queue (codex MarkdownStreamCollector pattern).</summary>
    internal void Incoming(string delta)
    {
        if (_stream is null)
        {
            StartStream();
        }

        _incoming.Append(delta);
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
            _pending.Enqueue(new PendingLine(segment, _nowMs));
            consumed = nl + 1;
        }

        if (consumed < rest.Length)
        {
            _incoming.Append(rest.AsSpan(consumed));
        }
    }

    private void StartThinkingStream()
    {
        _thinkingIncoming.Clear();
        _thinkStream = new StreamingThinkingBlock();
        _panel.Timeline.Append(_thinkStream);
    }

    private void IncomingThinking(string delta)
    {
        if (_thinkStream is null)
        {
            StartThinkingStream();
        }

        _thinkingIncoming.Append(delta);
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

        _panel.Timeline.MarkLastDirty();
    }

    private void FinishThinkingStream()
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
            _panel.Timeline.Replace(_thinkStream, new ThinkingBlock(text));
        }

        _thinkStream = null;
    }

    /// <summary>Commits the finished assistant message over the stream slot.</summary>
    private void FinishStream()
    {
        if (_stream is null)
        {
            return;
        }

        FlushStreamNow();
        _stream.Complete();
        if (_streamSource.Length > 0)
        {
            _panel.Timeline.Replace(_stream, new AssistantMarkdownBlock(_streamSource.ToString()));
        }

        if (_thinkStream is not null)
        {
            FinishThinkingStream();
        }

        _stream = null;
        _pending.Clear();
    }

    /// <summary>Bypasses pacing: everything buffered becomes visible at once.</summary>
    internal void FlushStreamNow()
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
            _panel.Timeline.MarkLastDirty();
        }
    }

    /// <summary>Render-thread drain of gates requested off-thread; every queued
    /// gate lands on the timeline and joins the pending queue in arrival order.</summary>
    private void DrainGateQueue()
    {
        bool appended = false;
        while (_gateQueue.TryDequeue(out var gate))
        {
            _panel.Timeline.Append(gate);
            EnqueuePendingGate(gate);
            appended = true;
        }

        if (appended)
        {
            _panel.Timeline.MarkLastDirty();
        }
    }

    // ── Tool cards ─────────────────────────────────────────────────────────

    private ToolCard EnsureCard(string id, string toolName, string? argsSummary)
    {
        if (_cards.TryGetValue(id, out var existing))
        {
            return existing;
        }

        var block = new ToolCallBlock(new ToolCallInfo(id, toolName, argsSummary ?? string.Empty));
        _panel.Timeline.Append(block);
        _panel.Timeline.MarkLastDirty();
        var card = new ToolCard { Block = block, StartedMs = _nowMs };
        _cards[id] = card;
        return card;
    }

    private void CompleteCard(ToolExecutionEndEvent e)
    {
        if (!_cards.TryGetValue(e.ToolCallId, out var card))
        {
            return;
        }

        long startedAt = card.StartedMs > long.MinValue ? card.StartedMs : _nowMs;
        long durationMs = Math.Max(0, _nowMs - startedAt);
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

    private void AppendImageCard(string path, string mime, long sizeBytes, byte[]? data)
    {
        _panel.Timeline.Append(new ImageBlock(path, mime, sizeBytes, data));
        if (data is { Length: > 0 })
        {
            // Inline-image hand-off (osc-sprint §1337): the host frame loop
            // drains payloads and emits them through the terminal backend —
            // the bridge itself never touches I/O.
            _pendingImages.Enqueue(new InlineImage(path, mime, data));
        }
    }

    /// <summary>One drained attachment for the inline-image pipeline.</summary>
    public readonly record struct InlineImage(string Path, string MimeType, byte[] Data);

    private readonly Queue<InlineImage> _pendingImages = new();

    /// <summary>Dequeues the next image attachment awaiting inline emission
    /// (kitty APC / OSC 1337 per terminal capability); false when drained.</summary>
    public bool TryTakePendingImage(out InlineImage image) => _pendingImages.TryDequeue(out image!);

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

    /// <summary>Host-driven notice into the timeline (slash-command output,
    /// submit errors). Rendered as a system line and flagged dirty.</summary>
    public void AppendSystemLine(string text)
    {
        AppendSystem(text);
        _panel.Timeline.MarkLastDirty();
    }

    private void AppendSystem(string text) =>
        _panel.Timeline.Append(new SystemBlock(text));

    // ── Approval gates ─────────────────────────────────────────────────────

    /// <summary>Bound on simultaneously pending gates; overflow auto-denies the
    /// oldest so its host-side await always wakes and no unreachable
    /// <c>IsPending</c> block survives.</summary>
    private const int MaxPendingGates = 8;

    /// <summary>Undecided <see cref="ApprovalGateView" />s in arrival order —
    /// the front one owns key/click routing (hotfix: a single slot turned every
    /// earlier gate into a zombie the user could never answer).</summary>
    private readonly Queue<ApprovalGateView> _pendingGates = new();

    /// <summary>Gates posted off the render thread (tool-execution context), drained by <see cref="Tick" />.</summary>
    private readonly ConcurrentQueue<ApprovalGateView> _gateQueue = new();

    /// <summary>
    /// Thread-safe approval request for the agent-loop side of the seam:
    /// creates a gate the caller can await via <c>DecisionRecorded</c>, and
    /// enqueues it so the frame loop appends it onto the timeline on its next
    /// tick — all list mutation stays on the render thread.
    /// </summary>
    public ApprovalGateView RequestApprovalGate(string toolName, string detail)
    {
        var gate = new ApprovalGateView(toolName, detail);
        _gateQueue.Enqueue(gate);
        _status.SignalMascot(MascotReaction.ApprovalWiggle);
        return gate;
    }

    /// <summary>
    /// Appends a permission gate to the timeline and arms it at the tail of
    /// the pending queue. Every queued gate stays interactable in arrival
    /// order — the front one is answered first; deciding it exposes the next.
    /// </summary>
    public ApprovalGateView BeginApprovalGate(string toolName, string detail)
    {
        var gate = new ApprovalGateView(toolName, detail);
        _panel.Timeline.Append(gate);
        EnqueuePendingGate(gate);
        _panel.Timeline.MarkLastDirty();
        _status.SignalMascot(MascotReaction.ApprovalWiggle);
        return gate;
    }

    /// <summary>Appends to the pending queue, auto-denying the oldest gate on
    /// overflow (the bound keeps both the queue and host-side waiters finite).</summary>
    private void EnqueuePendingGate(ApprovalGateView gate)
    {
        _pendingGates.Enqueue(gate);
        while (_pendingGates.Count > MaxPendingGates)
        {
            _ = _pendingGates.Dequeue().TryDecide(ApprovalChoice.Deny);
        }
    }

    /// <summary>Drops gates resolved off the routing path (e.g. host called
    /// <see cref="Widgets.ApprovalGateView.TryDecide" /> directly).</summary>
    private void PruneResolvedGates()
    {
        while (_pendingGates.Count > 0 && !_pendingGates.Peek().IsPending)
        {
            _ = _pendingGates.Dequeue();
        }
    }

    /// <summary>
    /// Routes one key event to the OLDEST pending gate BEFORE composer input.
    /// Consumed keys always wake the frame pipeline (decision stamps repaint).
    /// Returns false while no gate is armed or the key is not one of y/n/a/
    /// Enter/Escape — callers fall through to normal routing.
    /// </summary>
    public bool TryRouteApprovalKey(in KeyEvent key)
    {
        PruneResolvedGates();
        if (_pendingGates.Count == 0)
        {
            return false;
        }

        var gate = _pendingGates.Peek();
        if (!gate.HandleKey(key))
        {
            return false;
        }

        if (!gate.IsPending)
        {
            _ = _pendingGates.Dequeue();
        }

        _panel.Timeline.MarkLastDirty();
        return true;
    }

    /// <summary>
    /// Routes a left-button press/click to the OLDEST pending gate's hint-row
    /// buttons (see <see cref="Widgets.ApprovalGateView.TryHitDecision" />).
    /// Returns false when no gate is armed or the click lands outside its
    /// decision zones — callers keep normal scroll/routing behavior.
    /// </summary>
    public bool TryRouteApprovalClick(in Input.MouseEvent mouse)
    {
        PruneResolvedGates();
        if (_pendingGates.Count == 0)
        {
            return false;
        }

        var gate = _pendingGates.Peek();
        if (mouse.Type is not (Input.MouseEventType.Press or Input.MouseEventType.Click)
            || mouse.Button != Input.MouseButton.Left)
        {
            return false;
        }

        if (gate.TryHitDecision(mouse.Column, mouse.Row) is not { } choice
            || !gate.TryDecide(choice))
        {
            return false;
        }

        if (!gate.IsPending)
        {
            _ = _pendingGates.Dequeue();
        }

        _panel.Timeline.MarkLastDirty();
        return true;
    }

    public void Dispose() => Subscription.Dispose();

    public void RouteDiffNavigation(DiffPreviewViewModel diffVm, ChatAction action)
    {
        if (diffVm is null) return;
        switch (action)
        {
            case ChatAction.ScrollDownLine:
                diffVm.NextDiffCommand.Execute(null);
                break;
            case ChatAction.ScrollUpLine:
                diffVm.PreviousDiffCommand.Execute(null);
                break;
        }
    }
}
