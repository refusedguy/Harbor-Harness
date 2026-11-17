using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Tui.CellForge.Mascot;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework;
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

    /// <summary>
    ///     #1494: one reusable LlmEvent dispatcher. A fresh visitor per delta
    ///     would put an allocation on the streaming hot path, so the instance is
    ///     owned by the bridge and shared by every MessageUpdateEvent.
    /// </summary>
    private readonly LlmEventVisitor _llmEvents;

    /// <summary>
    ///     #1485: the render path's single AgentEvent dispatcher. Created once
    ///     per bridge, like <see cref="_llmEvents"/>, so the streaming hot path
    ///     stays allocation-free.
    /// </summary>
    private readonly AgentEventDispatcher _agentEvents;

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

    /// <summary>[UX5] #265: owning session of the current parent run, pinned
    /// on every non-subagent <see cref="AgentStartEvent"/> (and refreshed on
    /// <see cref="SessionChangedEvent"/>). Any message/turn event from another
    /// session while a <c>task</c> card runs is child traffic: it feeds the
    /// task transcript and never the parent feed.</summary>
    private string? _parentSessionId;

    /// <summary>AgentErrorEvent seen since the last AgentStart — decides
    /// whether AgentEnd flags the run as errored or succeeded (mascot moods).</summary>
    private bool _runHadError;

    private int _errorCardSeq;

    private string NewErrorCardId() => $"err-{_errorCardSeq++}";

    /// <summary>One-line header blurb for an error card (the full text lives
    /// in the collapsed body, expandable like any tool card).</summary>
    internal static string ErrorBlurb(string message)
    {
        int nl = message.IndexOf('\n');
        string first = nl >= 0 ? message[..nl] : message;
        return first.Length <= 200 ? first : first[..200] + "…";
    }

    public ChatScreenBridge(
        IEventBus bus,
        ChatTimelinePanel panel,
        StatusViewModel status,
        bool autoSubscribe = true,
        IApprovalCoordinator? coordinator = null)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(bus);
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _status = status ?? throw new ArgumentNullException(nameof(status));
        _streams = new StreamCoalescer(panel, status);
        _llmEvents = new BridgeLlmEventVisitor(this);
        _agentEvents = new AgentEventDispatcher(this);
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
        // #1485: the one traversal over the AgentEvent union on the render path.
        // Per-event control flow (child routing, history replay, status/gates)
        // lives in the dispatcher; the nested LlmEvent dispatch stays with the
        // #1494 visitor owned by this bridge.
        _agentEvents.Accept(evt);
        return ValueTask.CompletedTask;
    }

    // ── AgentEvent dispatch (Visitor, #1485) ──────────────────────────────────

    /// <summary>
    ///     The single traversal over the <see cref="AgentEvent" /> union on the
    ///     render path. Every member is named, and <see cref="Accept" /> is the
    ///     only place that branches on the runtime type. A new kind cannot ride
    ///     along silently: each walker must declare what it means for it, and an
    ///     unrecognised runtime type is refused by <see cref="VisitUnknown" />.
    /// </summary>
    /// <remarks>
    ///     Mirrors <see cref="LlmEventVisitor" /> (#1494). This is a dispatch
    ///     helper inside the render path, not a new TUI extension axis (#555 stays
    ///     frozen). One instance per bridge: a reference-type switch plus virtual
    ///     calls, no delegates, no dictionary and no per-event allocation on the
    ///     streaming hot path.
    /// </remarks>
    internal abstract class AgentEventVisitor
    {
        public abstract void Visit(AgentStartEvent e);
        public abstract void Visit(TurnStartEvent e);
        public abstract void Visit(MessageStartEvent e);
        public abstract void Visit(MessageUpdateEvent e);
        public abstract void Visit(MessageEndEvent e);
        public abstract void Visit(ToolExecutionStartEvent e);
        public abstract void Visit(ToolExecutionUpdateEvent e);
        public abstract void Visit(ToolExecutionEndEvent e);
        public abstract void Visit(TurnEndEvent e);
        public abstract void Visit(AgentEndEvent e);
        public abstract void Visit(AgentErrorEvent e);
        public abstract void Visit(PluginBlockedEvent e);
        public abstract void Visit(CompactionStartedEvent e);
        public abstract void Visit(CompactionCompletedEvent e);
        public abstract void Visit(CompactionFailedEvent e);
        public abstract void Visit(SessionStatsEvent e);
        public abstract void Visit(SessionChangedEvent e);

        /// <summary>
        ///     Handle an event whose runtime type is outside the closed union.
        ///     Throws by default; a walker may override to report it, never to
        ///     drop it without a trace.
        /// </summary>
        protected virtual void VisitUnknown(AgentEvent e) =>
            throw new NotSupportedException(
                $"{GetType().Name} has no arm for agent event '{e.GetType().Name}'. "
                + "A new AgentEvent subtype must ship with a Visit arm on AgentEventVisitor, "
                + "an arm in Accept, and an explicit decision in every walker.");

        /// <summary>Dispatch one event to the arm for its kind.</summary>
        public void Accept(AgentEvent e)
        {
            switch (e)
            {
                case AgentStartEvent agentStart:
                    Visit(agentStart);
                    return;
                case TurnStartEvent turnStart:
                    Visit(turnStart);
                    return;
                case MessageStartEvent msgStart:
                    Visit(msgStart);
                    return;
                case MessageUpdateEvent msgUpdate:
                    Visit(msgUpdate);
                    return;
                case MessageEndEvent msgEnd:
                    Visit(msgEnd);
                    return;
                case ToolExecutionStartEvent toolStart:
                    Visit(toolStart);
                    return;
                case ToolExecutionUpdateEvent toolUpdate:
                    Visit(toolUpdate);
                    return;
                case ToolExecutionEndEvent toolEnd:
                    Visit(toolEnd);
                    return;
                case TurnEndEvent turnEnd:
                    Visit(turnEnd);
                    return;
                case AgentEndEvent agentEnd:
                    Visit(agentEnd);
                    return;
                case AgentErrorEvent agentError:
                    Visit(agentError);
                    return;
                case PluginBlockedEvent pluginBlocked:
                    Visit(pluginBlocked);
                    return;
                case CompactionStartedEvent compactionStarted:
                    Visit(compactionStarted);
                    return;
                case CompactionCompletedEvent compactionCompleted:
                    Visit(compactionCompleted);
                    return;
                case CompactionFailedEvent compactionFailed:
                    Visit(compactionFailed);
                    return;
                case SessionStatsEvent sessionStats:
                    Visit(sessionStats);
                    return;
                case SessionChangedEvent sessionChanged:
                    Visit(sessionChanged);
                    return;
            }

            VisitUnknown(e);
        }
    }

    /// <summary>
    ///     Render-path walker for <see cref="AgentEvent" /> (#1485). Every arm
    ///     is the old HandleEvent case body verbatim, with bridge state reached
    ///     through the owner. Child-traffic gates, history replay, retry and
    ///     compaction/status wiring are unchanged.
    /// </summary>
    private sealed class AgentEventDispatcher(ChatScreenBridge bridge) : AgentEventVisitor
    {
        public override void Visit(AgentStartEvent started)
        {
            // [UX5] #265: a child sub-agent announces on the same bus —
            // it never replays into the parent timeline; its session just
            // opens a transcript header on the running task card.
            if (started.Kind == SessionKind.Subagent)
            {
                if (bridge._cards.TryGetRunningTask(out var subTaskId))
                {
                    bridge._cards.NoteSubagentStart(subTaskId, started.SessionId);
                }

                return;
            }

            bridge._parentSessionId = started.SessionId;
            bridge.ReplayHistory(started.Messages);
            bridge._context.RememberContextWindow(started.Model);
            bridge._runHadError = false;
            bridge._status.Phase = AgentPhase.Auto;
            bridge._status.Mode = StatusBarMode.Running;
        }

        /// <summary>The bridge draws nothing for a turn start; the parent's
        /// status line is already in Running from AgentStart. The old switch
        /// had no arm here either.</summary>
        public override void Visit(TurnStartEvent started) { }

        public override void Visit(MessageStartEvent started)
        {
            // [UX5] #265: child text never streams into the parent feed.
            if (bridge.IsForeignChild(started.Message.SessionId))
            {
                return;
            }

            bridge.MarkSeen(started.Message);
            bridge._streams.StartStream();
        }

        public override void Visit(MessageUpdateEvent update)
        {
            // [UX5] #265: child deltas buffer into the task transcript.
            if (bridge.IsForeignChild(update.Partial.SessionId))
            {
                bridge.RouteChildUpdate(update);
                return;
            }

            // #1494: the nested LlmEvent dispatch stays with the shared visitor.
            bridge._llmEvents.Accept(update.LlmEvent);
        }

        public override void Visit(MessageEndEvent ended)
        {
            // [UX5] #265: a child message close flushes its buffered text
            // as one transcript paragraph — never a parent stream block.
            if (bridge.IsForeignChild(ended.Message.SessionId))
            {
                if (bridge._cards.TryGetRunningTask(out var textTaskId))
                {
                    bridge._cards.FlushTaskText(textTaskId);
                }

                return;
            }

            bridge.MarkSeen(ended.Message);
            bridge._streams.FinishStream();
        }

        public override void Visit(ToolExecutionStartEvent execStart)
        {
            // [UX5] #265: while a task card runs, non-task executions
            // are its child tools — they feed the live suffix and the
            // transcript instead of opening their own cards.
            if (bridge._cards.TryGetRunningTask(out var ownerTaskId)
                && !ToolCardTracker.IsTaskTool(execStart.ToolName))
            {
                bridge._cards.NoteTaskChildStart(ownerTaskId, execStart.ToolCallId, execStart.ToolName, ChatScreenBridge.Summarize(execStart.Args));
                return;
            }

            bridge._cards.EnsureCard(execStart.ToolCallId, execStart.ToolName, execStart.Args, restampStarted: true);
            bridge._status.Phase = AgentPhase.ToolCall;
            bridge._status.Mode = StatusBarMode.Running;
        }

        public override void Visit(ToolExecutionUpdateEvent update)
        {
            if (update.RetryAttempt is null || update.RetryMaxAttempts is null)
            {
                // Ordinary progress updates carry no retry fraction; the old
                // guarded switch arm did not run for them either.
                return;
            }

            // #76: tool-retry mirror — render-only. Precomputed once per change
            // (never interpolated per frame); cleared on ToolExecutionEndEvent.
            bridge._status.Retry = RetryCountdown.Line(
                update.RetryAttempt.Value,
                update.RetryMaxAttempts.Value,
                Math.Max(0, (int)Math.Ceiling(update.RetryBackoffSeconds ?? 0)));
            bridge._toolRetryShown = true;

            // [UX5] #265: retries also surface on the task suffix in red —
            // the task's own id, or any id without a card (live children
            // never open their own cards) while a task card runs.
            if (bridge._cards.TryGetRunningTask(out var retryTaskId)
                && (string.Equals(update.ToolCallId, retryTaskId, StringComparison.Ordinal)
                    || !bridge._cards.IsKnownCard(update.ToolCallId)))
            {
                bridge._cards.NoteTaskRetry(retryTaskId, update.RetryAttempt.Value, update.RetryMaxAttempts.Value);
            }
        }

        public override void Visit(ToolExecutionEndEvent execEnd)
        {
            // [UX5] #265: a live child end only closes its transcript line.
            // Only the task's own end completes the card.
            if (bridge._cards.TryGetRunningTask(out var finishedOwnerId)
                && !string.Equals(execEnd.ToolCallId, finishedOwnerId, StringComparison.Ordinal)
                && bridge._cards.NoteTaskChildEnd(finishedOwnerId, execEnd.ToolCallId, execEnd.IsError))
            {
                return;
            }

            bridge._cards.CompleteCard(execEnd);
            if (bridge._toolRetryShown)
            {
                bridge._status.Retry = null;
                bridge._toolRetryShown = false;
            }
        }

        public override void Visit(TurnEndEvent turnEnd)
        {
            // [UX5] #265: child turns never mark parent history.
            if (bridge.IsForeignTurn(turnEnd))
            {
                return;
            }

            bridge.MarkSeen(turnEnd.AssistantMessage);
            foreach (var toolResults in turnEnd.ToolResults)
            {
                bridge.MarkSeen(toolResults);
            }
        }

        public override void Visit(AgentEndEvent agentEnd)
        {
            // [UX5] #265: the child run's AgentEnd must not idle the
            // parent footer, bounce the mascot, or flush parent streams.
            if (bridge.IsForeignAgentEnd(agentEnd))
            {
                return;
            }

            foreach (var message in agentEnd.NewMessages)
            {
                bridge.MarkSeen(message);
            }

            bridge._streams.FlushStreamNow();

            // #567: a cancelled run publishes AgentEndEvent(Cancelled: true)
            // and never a ToolExecutionEndEvent for the call it was aborting,
            // so the in-flight card kept its running glyph and spun forever.
            // Sweep the open calls into an explicit terminal state; before the
            // enum collapse there was no such state and no way to say "stopped".
            if (agentEnd.Cancelled)
            {
                bridge._cards.StopRunningCalls(ToolCallState.Cancelled, "cancelled by user");
                bridge._status.Phase = AgentPhase.Errored;
                bridge._status.SignalMascot(MascotReaction.ErrorBlink);
                bridge._status.Mode = StatusBarMode.Idle;
                return;
            }

            bridge._status.Phase = bridge._runHadError ? AgentPhase.Errored : AgentPhase.Succeeded;
            if (!bridge._runHadError)
            {
                bridge._status.SignalMascot(MascotReaction.SuccessBounce);
            }

            bridge._status.Mode = StatusBarMode.Idle;
        }

        public override void Visit(AgentErrorEvent error)
        {
            bridge._streams.FlushStreamNow();
            bridge._cards.CompleteError(bridge.NewErrorCardId(), "error", ChatScreenBridge.ErrorBlurb(error.Message), error.Message);
            bridge._runHadError = true;
            bridge._status.Phase = AgentPhase.Errored;
            bridge._status.SignalMascot(MascotReaction.ErrorBlink);
            bridge._status.Mode = StatusBarMode.Idle;
        }

        /// <summary>Sandbox enforcement is audited elsewhere; the feed has no
        /// card for it, matching the old switch's missing arm.</summary>
        public override void Visit(PluginBlockedEvent blocked) { }

        public override void Visit(CompactionStartedEvent started)
        {
            bridge.AppendSystem("compacting history…");
            bridge._status.Mode = StatusBarMode.Compacting;
        }

        public override void Visit(CompactionCompletedEvent completed)
        {
            bridge.AppendSystem("history compacted");
            bridge._status.Mode = StatusBarMode.Running;
        }

        // #840: the third member of a family the old switch declared twice,
        // and the arm that was missing is the expensive one — CompactionBehavior
        // calls its truncation fallback irreversible and then continues the
        // run, so nothing downstream of this event would have told the user.
        //
        // NOT a duplicate of #839. That fix added a ChatRole.System line to
        // UiState.Chat.Lines, and this timeline never reads it:
        // ChatScreenProjector (issue #1202; reached via
        // CellForgeTuiRenderer.ProjectStateIntoWidgets) hands the store's
        // UiState to Status.ProjectedState and the sidebar and nothing else.
        // The store projection feeds the desktop app transcript.
        // This ring is what HARBOR_TUI=cellforge paints.
        public override void Visit(CompactionFailedEvent failed)
        {
            bridge.AppendSystem(CompactionLifecycleLines.Failed(failed.Error));
            bridge._status.Mode = StatusBarMode.Running;
        }

        public override void Visit(SessionStatsEvent stats)
        {
            // #623: the ctx segment is NOT fed from here. These totals are
            // session-cumulative spend (SessionMetadata.AddUsage), so
            // rendering them as occupancy pinned the bar at 100% by turn 4
            // on a 128k window while the actual request never grew.
            // StepFinishEvent.Usage.InputTokens is the request just sent.
            // #651: the token cell takes the SAME figure for the same reason
            // — the sum is the bill, and a cell that reported it answered
            // "what am I paying" with a number that read as "how full am I".
            // DisplayedInputTokens keeps the total as the fallback for a
            // session whose totals arrive with no request behind them.
            // #653: the cost is the core's, and null (→ "—") is the core's
            // own answer for a model that publishes no price — this bridge
            // used to never receive this event at all, so its footer showed
            // no cost while the projection showed a fabricated one.
            bridge._status.SetUsage(
                ContextUsage.DisplayedInputTokens(
                    bridge._context.LastRequestTokens,
                    stats.Metadata.TokensInput),
                stats.Metadata.TokensOutput,
                stats.Metadata.IsCostKnown ? stats.Metadata.Cost : null);
        }

        public override void Visit(SessionChangedEvent changed)
        {
            // [UX5] #265: keep the child-traffic filter aligned when the
            // user switches sessions.
            bridge._parentSessionId = changed.SessionId;
        }
    }

    /// <summary>
    ///     Live-stream half of the #1494 dispatcher. Only the arms that paint
    ///     something do work; the rest are declared empty so a new LlmEvent
    ///     cannot be silently adopted by this walker.
    /// </summary>
    private sealed class BridgeLlmEventVisitor(ChatScreenBridge bridge) : LlmEventVisitor
    {
        public override void Visit(TextStartEvent e) { }

        public override void Visit(TextDeltaEvent e) => bridge._streams.Incoming(e.Delta);

        public override void Visit(TextEndEvent e) { }

        public override void Visit(ThinkingStartEvent e) => bridge._streams.StartThinkingStream();

        public override void Visit(ThinkingDeltaEvent e) => bridge._streams.IncomingThinking(e.Delta);

        public override void Visit(ThinkingEndEvent e) => bridge._streams.FinishThinkingStream();

        public override void Visit(ToolCallStartEvent e) =>
            bridge._cards.EnsureCard(e.Id, e.ToolName, argsSummary: null);

        public override void Visit(ToolCallDeltaEvent e) { }

        public override void Visit(ToolCallEndEvent e) { }

        public override void Visit(StepStartEvent e) { }

        public override void Visit(StepFinishEvent e)
        {
            var usage = e.Usage;
            if (usage is null)
            {
                return;
            }

            bridge._streams.AddStepUsage(usage.InputTokens, usage.OutputTokens);
            // #623: prompt tokens of the request just sent = the context
            // window's real occupancy.
            bridge._context.NoteRequestSize(usage.InputTokens);
        }

        public override void Visit(FinishEvent e) { }

        public override void Visit(ErrorEvent e) { }
    }

    /// <summary>[UX5] #265: true for traffic from a non-parent session while a
    /// <c>task</c> card runs. Gated on a known parent (late attach degrades to
    /// the legacy pass-through) and on a live task (no task, no children).</summary>
    private bool IsForeignChild(string? sessionId) =>
        _parentSessionId is not null
        && sessionId is not null
        && !string.Equals(sessionId, _parentSessionId, StringComparison.Ordinal)
        && _cards.TryGetRunningTask(out _);

    private bool IsForeignTurn(TurnEndEvent turnEnd) =>
        turnEnd.SessionId is not null && IsForeignChild(turnEnd.SessionId);

    private bool IsForeignAgentEnd(AgentEndEvent agentEnd)
    {
        if (agentEnd.NewMessages.Count == 0 || _parentSessionId is null)
        {
            return false;
        }

        if (!_cards.TryGetRunningTask(out _))
        {
            return false;
        }

        foreach (var message in agentEnd.NewMessages)
        {
            if (string.Equals(message.SessionId, _parentSessionId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>[UX5] #265: foreign (child-session) LLM traffic never touches
    /// the parent stream — text deltas buffer into the running task card for
    /// its expand view, tool announcements are dropped (the execution pair
    /// carries the transcript line), child token usage never rewrites the
    /// parent footer.</summary>
    private void RouteChildUpdate(MessageUpdateEvent update)
    {
        if (!_cards.TryGetRunningTask(out var taskId))
        {
            return;
        }

        if (update.LlmEvent is TextDeltaEvent text)
        {
            _cards.AppendTaskText(taskId, text.Delta);
        }
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

    /// <summary>
    ///     Replays one history message into the timeline. #461: the per-kind
    ///     dispatch lives in <see cref="HistoryVisitor" />, so a new
    ///     <see cref="AgentMessage" /> or <see cref="ContentPart" /> subtype is a
    ///     build break here instead of a part that quietly never paints.
    /// </summary>
    private void AppendHistoryMessage(AgentMessage message) =>
        new HistoryVisitor(this).Accept(message);

    private sealed class HistoryVisitor(ChatScreenBridge bridge) : AgentMessageVisitor<HistoryVisitor>
    {
        public override HistoryVisitor Visit(UserMessage message)
        {
            bridge._panel.Timeline.Append(new UserBlock(message.Content));
            return this;
        }

        public override HistoryVisitor Visit(AssistantMessage message)
        {
            var parts = new HistoryPartVisitor(bridge);
            parts.Walk(message.Parts);
            parts.Flush();
            return this;
        }

        /// <summary>
        ///     Tool results are already in the transcript as their own tool lines
        ///     (see the live-render path); replaying them here would double them.
        ///     The old switch had no arm for this role — now it says so.
        /// </summary>
        public override HistoryVisitor Visit(ToolResultMessage message) => this;
    }

    /// <summary>
    ///     Per-part timeline shape for a replayed assistant turn. The commit
    ///     order — text block, then image cards, in part order — is what the
    ///     golden baselines lock in, and it is unchanged.
    /// </summary>
    private sealed class HistoryPartVisitor(ChatScreenBridge bridge) : ContentPartVisitor<HistoryPartVisitor>
    {
        private readonly StringBuilder _text = new();

        /// <summary>Commits the trailing text block, if any is still buffered.</summary>
        internal void Flush()
        {
            if (_text.Length > 0)
            {
                bridge._panel.Timeline.Append(new AssistantMarkdownBlock(_text.ToString(), bridge._streams.ModelHeader()));
            }
        }

        public override HistoryPartVisitor Visit(TextPart part)
        {
            _text.AppendLine(part.Text);
            return this;
        }

        public override HistoryPartVisitor Visit(FilePart part)
        {
            string mime = part.MimeType;
            if (!MimeTypes.IsImage(mime))
            {
                // Non-image files have no timeline card; the old switch skipped
                // them here too. Explicit so a new kind cannot ride along.
                return this;
            }

            // Порядок карточек = порядку частей: накопленный
            // текст коммитится перед изображением.
            if (_text.Length > 0)
            {
                bridge._panel.Timeline.Append(new AssistantMarkdownBlock(_text.ToString(), bridge._streams.ModelHeader()));
                _text.Clear();
            }

            bridge._cards.AppendImageCard(part.Path, mime, part.SizeBytes, part.Data);
            return this;
        }

        /// <summary>
        ///     Reasoning and tool calls are streamed live as their own blocks, so
        ///     the replayed history stays text + image only. The old switch had no
        ///     arms for them at all.
        /// </summary>
        public override HistoryPartVisitor Visit(ThinkingPart part) => this;

        /// <inheritdoc cref="Visit(ThinkingPart)" />
        public override HistoryPartVisitor Visit(ToolCallPart part) => this;
    }

    // ── Stream compatibility shims (internals now owned by StreamCoalescer) ──

    /// <summary>Deltas land in the incoming buffer; complete source lines join
    /// the paced queue (codex MarkdownStreamCollector pattern).</summary>
    internal void Incoming(string delta) => _streams.Incoming(delta);

    /// <summary>Bypasses pacing: everything buffered becomes visible at once.</summary>
    internal void FlushStreamNow() => _streams.FlushStreamNow();

    /// <summary>Queued-prompt dock-counter mirror (O7 #1176): the composer owns
    /// the queue, the coalescer paints the count — this shim is the only path
    /// between them, mirroring the stream shims above.</summary>
    public void SetQueuedCount(int count) => _streams.SetQueuedCount(count);

    /// <summary>Render-side busy signal for submit-while-busy routing.</summary>
    public bool IsStreaming => _streams.IsStreaming;

    // ── Tool-card compatibility shims (internals now owned by ToolCardTracker) ──

    /// <summary>One drained attachment for the inline-image pipeline.</summary>
    public readonly record struct InlineImage(string Path, string MimeType, byte[] Data);

    /// <summary>Dequeues the next image attachment awaiting inline emission
    /// (kitty APC / OSC 1337 per terminal capability); false when drained.</summary>
    public bool TryTakePendingImage(out InlineImage image) => _cards.TryTakePendingImage(out image);

    /// <summary>
    ///     Host-driven image card into the timeline (issue #400: the annotated
    ///     copy the markup overlay baked). Same card the attachment path
    ///     appends, so the saved PNG renders with its real dimensions.
    /// </summary>
    public void AppendImageCard(string path, string mime, long sizeBytes, byte[]? data) =>
        _cards.AppendImageCard(path, mime, sizeBytes, data);

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
    /// Routes a plain Enter/Space press to the newest collapsible block on the
    /// feed ([UX2] #262: single expand gesture — Enter/click/space — everywhere).
    /// Hosts call this after approval routing and before the
    /// composer so feed-Enter expands blocks while composer-Enter still submits
    /// — ordering stays host-side. Returns false when the key is not a plain
    /// Enter/Space press or the feed holds no collapsible block.
    /// </summary>
    public bool TryRouteToolCardKey(in KeyEvent key) => _cards.TryRouteToolCardKey(key);

    /// <summary>
    /// Routes a plain Enter press to the newest image block so the host can open
    /// the fullscreen zoom viewer (KILLER_FEATURES §2.7 Feature 12, issue #387).
    /// Returns the block to show, or null when the feed holds no image row.
    /// </summary>
    public ImageBlock? TryOpenImageViewer(in KeyEvent key) => _cards.TryOpenImageViewer(key);

    /// <summary>
    /// Routes a left-button press/click on a collapsible block's header to
    /// expand/collapse ([UX2] #262; mirrors <see cref="TryRouteApprovalClick"/>).
    /// Returns false when the click lands outside every collapsible header —
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
