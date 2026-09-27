using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Tui.CellForge.Widgets;
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
/// Thin coordinator since #172: streaming markdown/thinking lives in
/// <see cref="StreamCoalescer"/>, tool cards in <see cref="ToolCardTracker"/>,
/// the context-window segment in <see cref="ContextSegmentSync"/> and the bus
/// subscription in <see cref="EventSubscription"/>. This class owns event
/// dispatch, history replay, approval gates and mascot wiring.
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
    private readonly ChatTimelinePanel _panel;
    private readonly StatusViewModel _status;
    private readonly StreamCoalescer _streams;
    private readonly ToolCardTracker _cards;
    private readonly ContextSegmentSync _context;
    private readonly EventSubscription _subscription;

    /// <summary>Bound on simultaneously pending gates; overflow auto-denies the
    /// oldest so its host-side await always wakes and no unreachable
    /// <c>IsPending</c> block survives.</summary>
    private readonly ApprovalGateRouter _gates;

    /// <summary>
    ///     #76: true while the tool-retry mirror owns <see cref="StatusViewModel.Retry"/>.
    ///     The bridge clears the slot on <see cref="ToolExecutionEndEvent"/> only when it
    ///     set it — a stream-retry line owned by the REPL pipeline (same singleton) is
    ///     never clobbered. Render-only: the dispatcher owns the schedule.
    /// </summary>
    private bool _toolRetryShown;

    private readonly HashSet<string> _displayedMessageIds = new();

    /// <summary>AgentErrorEvent seen since the last AgentStart — decides
    /// whether AgentEnd flags the run as errored or succeeded (mascot moods).</summary>
    private bool _runHadError;

    public ChatScreenBridge(
        IEventBus bus,
        ChatTimelinePanel panel,
        StatusViewModel status,
        bool autoSubscribe = true,
        IApprovalCoordinator? coordinator = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _status = status ?? throw new ArgumentNullException(nameof(status));
        _streams = new StreamCoalescer(panel, status);
        _cards = new ToolCardTracker(panel);
        _context = new ContextSegmentSync(status);
        _gates = new ApprovalGateRouter(panel, status) { Coordinator = coordinator };
        // Auto-subscribe suits fire-and-forget hosts (CE-3 tests). A driven
        // host (CellForge REPL frame loop) passes false and pumps events via
        // <see cref="AcceptAsync"/> so all timeline mutation stays on the
        // render thread — zero cross-thread access to the block list.
        _subscription = new EventSubscription(bus, HandleEvent, autoSubscribe);
        Subscription = _subscription.Subscription;
    }

    public IDisposable Subscription { get; }

    /// <summary>
    ///     Loop-driven entry point: process one real bus event on the caller's
    ///     (render) thread. Pair with <c>autoSubscribe: false</c>.
    /// </summary>
    public ValueTask AcceptAsync(AgentEvent evt, CancellationToken ct = default) => HandleEvent(evt, ct);

    /// <summary>Monotonic clock injection point (frame pipeline calls each tick).
    /// Render-thread drain of gates requested off-thread; every queued gate
    /// lands on the timeline in arrival order.</summary>
    public void Tick(long nowMs)
    {
        _gates.DrainQueued();
        _cards.NowMs = nowMs;
        _streams.DrainPaced(nowMs);
    }

    private ValueTask HandleEvent(AgentEvent evt, CancellationToken ct)
    {
        switch (evt)
        {
            case AgentStartEvent started:
                ReplayHistory(started.Messages);
                _context.RememberContextWindow(started.Model);
                _runHadError = false;
                _status.Phase = AgentPhase.Auto;
                _status.Mode = StatusBarMode.Running;
                break;

            case MessageStartEvent started:
                MarkSeen(started.Message);
                _streams.StartStream();
                break;

            case MessageUpdateEvent update:
                switch (update.LlmEvent)
                {
                    case TextDeltaEvent delta:
                        _streams.Incoming(delta.Delta);
                        break;
                    case ThinkingStartEvent _:
                        _streams.StartThinkingStream();
                        break;
                    case ThinkingDeltaEvent delta:
                        _streams.IncomingThinking(delta.Delta);
                        break;
                    case ThinkingEndEvent _:
                        _streams.FinishThinkingStream();
                        break;
                    case ToolCallStartEvent callStart:
                        _cards.EnsureCard(callStart.Id, callStart.ToolName, argsSummary: null);
                        break;
                    case StepFinishEvent sf when sf.Usage is not null:
                        _streams.AddStepUsage(sf.Usage.InputTokens, sf.Usage.OutputTokens);
                        break;
                }

                break;

            case ToolExecutionStartEvent execStart:
                {
                    _cards.EnsureCard(execStart.ToolCallId, execStart.ToolName, Summarize(execStart.Args), FullArgs(execStart.Args), restampStarted: true);
                    _status.Phase = AgentPhase.ToolCall;
                    _status.Mode = StatusBarMode.Running;
                    break;
                }

            case ToolExecutionUpdateEvent { RetryAttempt: not null, RetryMaxAttempts: not null } retry:
                // #76: tool-retry mirror — render-only. Precomputed once per change
                // (never interpolated per frame); cleared on ToolExecutionEndEvent.
                _status.Retry = RetryCountdown.Line(
                    retry.RetryAttempt.Value,
                    retry.RetryMaxAttempts.Value,
                    Math.Max(0, (int)Math.Ceiling(retry.RetryBackoffSeconds ?? 0)));
                _toolRetryShown = true;
                break;

            case ToolExecutionEndEvent execEnd:
                _cards.CompleteCard(execEnd);
                if (_toolRetryShown)
                {
                    _status.Retry = null;
                    _toolRetryShown = false;
                }

                break;

            case MessageEndEvent ended:
                MarkSeen(ended.Message);
                _streams.FinishStream();
                break;

            case TurnEndEvent turnEnd:
                MarkSeen(turnEnd.AssistantMessage);
                foreach (var toolResults in turnEnd.ToolResults)
                {
                    MarkSeen(toolResults);
                }

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
                _context.NoteUsage(stats.Metadata.TokensInput, stats.Metadata.TokensOutput);
                break;

            case AgentErrorEvent error:
                _streams.FlushStreamNow();
                AppendSystem("! " + error.Message);
                _runHadError = true;
                _status.Phase = AgentPhase.Errored;
                _status.SignalMascot(MascotReaction.ErrorBlink);
                _status.Mode = StatusBarMode.Idle;
                break;

            case AgentEndEvent agentEnd:
                foreach (var message in agentEnd.NewMessages)
                {
                    MarkSeen(message);
                }

                _streams.FlushStreamNow();
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

    // ── History replay ───────────────────────────────────────────────────────

    /// <summary>Replays persisted history into the timeline (session switch).</summary>
    public void ReplayHistory(IReadOnlyList<AgentMessage> messages)
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

    /// <summary>Settled-message marker: every message that already owns a
    /// timeline block (live stream slot or committed form) is recorded, so a
    /// later <see cref="AgentStartEvent"/> replay never re-appends it — each
    /// message paints exactly once per timeline lifetime.
    /// <see cref="ResetMessageTracking"/> re-arms on session switch/new
    /// session, where the timeline is cleared alongside.</summary>
    private void MarkSeen(AgentMessage message)
    {
        if (message.Id is not null)
        {
            _displayedMessageIds.Add(message.Id);
        }
    }

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
                                _panel.Timeline.Append(new AssistantMarkdownBlock(text.ToString(), _streams.ModelHeader()));
                                text.Clear();
                            }

                            _cards.AppendImageCard(file.Path, mime, file.SizeBytes, file.Data);
                            break;
                    }
                }

                if (text.Length > 0)
                {
                    _panel.Timeline.Append(new AssistantMarkdownBlock(text.ToString(), _streams.ModelHeader()));
                }

                break;
        }
    }

    // ── Stream compatibility shims (internals now owned by StreamCoalescer) ──

    /// <summary>Deltas land in the incoming buffer; complete source lines join
    /// the paced queue (codex MarkdownStreamCollector pattern).</summary>
    internal void Incoming(string delta) => _streams.Incoming(delta);

    /// <summary>Bypasses pacing: everything buffered becomes visible at once.</summary>
    internal void FlushStreamNow() => _streams.FlushStreamNow();

    // ── Tool-card compatibility shims (internals now owned by ToolCardTracker) ──

    /// <summary>One drained attachment for the inline-image pipeline.</summary>
    public readonly record struct InlineImage(string Path, string MimeType, byte[] Data);

    /// <summary>Dequeues the next image attachment awaiting inline emission
    /// (kitty APC / OSC 1337 per terminal capability); false when drained.</summary>
    public bool TryTakePendingImage(out InlineImage image) => _cards.TryTakePendingImage(out image);

    /// <summary>Typed-ish diff extraction (widgets §5): tools that attach a
    /// unified diff in Metadata win; otherwise a raw diff-shaped Output is
    /// used verbatim. No heuristics beyond shape checks.</summary>
    internal static string? TryExtractDiff(ToolResult result) => ToolCardTracker.TryExtractDiff(result);

    internal static string Summarize(JsonElement args) => ToolCardTracker.Summarize(args);

    /// <summary>Complete single-line args payload for the expanded card row (bounded for eviction accounting).</summary>
    internal static string FullArgs(JsonElement args) => ToolCardTracker.FullArgs(args);

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

    /// <summary>Approval gates live in <see cref="ApprovalGateRouter"/> —
    /// the bridge keeps the public surface (tests + permission asker).</summary>
    public ApprovalGateView RequestApprovalGate(string toolName, string detail, string? invocationId = null, int generation = 1) =>
        _gates.RequestApprovalGate(toolName, detail, invocationId, generation);

    public ApprovalGateView BeginApprovalGate(string toolName, string detail, string? invocationId = null, int generation = 1) =>
        _gates.BeginApprovalGate(toolName, detail, invocationId, generation);

    public bool TryRouteApprovalKey(in KeyEvent key) => _gates.TryRouteApprovalKey(key);

    public bool TryRouteApprovalClick(in Input.MouseEvent mouse) => _gates.TryRouteApprovalClick(mouse);

    // ── Tool cards ─────────────────────────────────────────────────────────

    /// <summary>
    /// Toggles the expanded state of one tool card by id (feed Enter path).
    /// Returns false when no card carries <paramref name="toolCallId"/>.
    /// </summary>
    public bool ToggleToolCard(string toolCallId) => _cards.ToggleToolCard(toolCallId);

    /// <summary>
    /// Routes a plain Enter press to the newest tool card on the feed (toggles
    /// expand/collapse). Hosts call this after approval routing and before the
    /// composer so feed-Enter expands cards while composer-Enter still submits
    /// — ordering stays host-side. Returns false when the key is not a plain
    /// Enter press or the feed holds no tool card.
    /// </summary>
    public bool TryRouteToolCardKey(in KeyEvent key) => _cards.TryRouteToolCardKey(key);

    /// <summary>
    /// Routes a left-button press/click on a tool-card header to
    /// expand/collapse (mirrors <see cref="TryRouteApprovalClick"/>).
    /// Returns false when the click lands outside every card header —
    /// callers keep normal scroll/selection behavior.
    /// </summary>
    public bool TryRouteToolCardClick(in Input.MouseEvent mouse) => _cards.TryRouteToolCardClick(mouse);

    /// <summary>
    /// Optional TEA store passthrough (epic C contour): when set, the gate
    /// router steps diff navigation through the store instead of executing
    /// the diff view-model commands directly. See
    /// <see cref="ApprovalGateRouter.Store"/>.
    /// </summary>
    public UiStore? Store
    {
        get => _gates.Store;
        set => _gates.Store = value;
    }

    private MascotPanel? _mascotPanel;
    private StatusPanel? _statusPanel;

    /// <summary>
    /// Wires the mascot hosts for <see cref="IsMascotAnimating"/> (#170): the
    /// panel cat and the footer cat each own a <c>MascotDirector</c>, and the
    /// heartbeat must outlive Idle while either still owes motion. Called once
    /// by the frame-loop owner (the runner); the bridge never paints from them.
    /// </summary>
    public void TrackMascot(MascotPanel? panel, StatusPanel? status)
    {
        _mascotPanel = panel;
        _statusPanel = status;
    }

    /// <summary>
    /// True while either mascot director still owes motion (a reaction is armed
    /// or the mood latch is live). The runner keeps the 80 ms heartbeat on Idle
    /// frames while this holds, so the error/success sequence plays out and the
    /// mascot revives by itself. Allocation-free.
    /// </summary>
    public bool IsMascotAnimating =>
        (_mascotPanel?.IsMascotAnimating ?? false) || (_statusPanel?.IsMascotAnimating ?? false);

    public void Dispose() => _subscription.Dispose();

    public void RouteDiffNavigation(DiffPreviewViewModel diffVm, ChatAction action) =>
        _gates.RouteDiffNavigation(diffVm, action);
}
