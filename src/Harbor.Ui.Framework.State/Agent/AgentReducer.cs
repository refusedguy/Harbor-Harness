using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     Agent-event transitions of the chat reducer: the <c>AgentEvent → UiState</c>
///     fold (run start, streaming deltas, tool cards, costs, compaction notes and
///     the terminal verdict) plus the effect host's run-end fold — pure folds over
///     <c>Chat</c>, extracted verbatim from <see cref="ChatAppReducer" /> (#95).
///     The host runs the effects; nothing here touches I/O or DI.
/// </summary>
/// <remarks>
///     <see cref="ChatAppReducer" /> keeps a thin forwarder over every entry point
///     that stays referenced, so existing call sites, tests and doc references keep
///     resolving to the same signatures with the same behaviour.
/// </remarks>
public static class AgentReducer
{
    // ── agent-event reduction ──────────────────────────────────────────────

    /// <summary>
    ///     Apply an agent event to the UI state, returning the next immutable snapshot.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="AgentStartEvent" /> and <see cref="AgentEndEvent" /> both
    ///         snapshot <see cref="ChatDomainState.IsAgentRunning" /> into
    ///         <see cref="ChatDomainState.WasRunning" /> before flipping it, so
    ///         renderers can detect the rising edge
    ///         (<c>IsAgentRunning &amp;&amp; !WasRunning</c>) without keeping local
    ///         mutable state. A new run also pins scroll to the live tail so
    ///         streaming output is always visible (§FP-005 TEA fix).
    ///     </para>
    /// </remarks>
    public static UiState Reduce(UiState state, AgentEvent @event) => @event switch
    {
        AgentStartEvent ase => OnAgentStart(state, ase),
        MessageStartEvent => OnMessageStart(state),
        MessageUpdateEvent mu => OnMessageUpdate(state, mu),
        MessageEndEvent => OnMessageEnd(state),
// The transcript line is the DISPLAY text; Chat.ToolCalls is the STRUCTURE (#680).
        // Both are written here so a renderer can read the call instead of parsing
        // the line it is about to draw. The diff payload is derived once, here,
        // rather than once per rendering path.
        ToolExecutionStartEvent tes => state
            .PutToolCall(ToolCallSnapshot.Start(
                tes.ToolCallId, tes.ToolName, tes.Glyph, EmptyPayloadLiteral(tes.Args) ?? tes.Args.GetRawText()))
            .AddLine(ChatRole.Tool, FormatToolStart(tes), tes.ToolCallId),
        ToolExecutionEndEvent tee => state
            .CompleteToolCall(
                tee.ToolCallId,
                tee.IsError ? ToolCallState.Error : ToolCallState.Success,
                FormatToolResultPreview(tee))
            .AddLine(ChatRole.ToolResult, FormatToolEnd(tee), tee.ToolCallId),
        SessionStatsEvent ss => OnSessionStats(state, ss),
        CompactionStartedEvent => state with { Chat = state.Chat with { Status = "compacting" } },
        CompactionCompletedEvent cc => OnCompactionCompleted(state, cc),
        // #773: the third arm of the lifecycle, and the only one whose absence
        // left a LABEL lying. CompactionBehavior publishes this and returns
        // TruncationFallback: true — the turn continues on a truncated history.
        // "error" would be a worse lie than the one this fixes: AgentEndEvent
        // deliberately preserves an "error" status past the end of a run
        // (`Status == "error" ? "error" : "idle"`), so it would repaint a run
        // that completed as a failed one. "running" is the truth, and the
        // system line is what keeps the truncation from being silent.
        // SessionStatus is deliberately NOT touched — #687 decides it on the
        // transition that establishes it, and the run has not ended.
        CompactionFailedEvent cf => OnCompactionFailed(state, cf),
        AgentErrorEvent err => state
            .AddLine(ChatRole.Error, err.Message)
            .WithStatus("error")
            .WithSessionStatus(SessionStatus.Error),
        AgentEndEvent end => OnAgentEnd(state, end),
        _ => state
    };

    // ── event handlers ─────────────────────────────────────────────────────

    private static UiState OnAgentStart(UiState state, AgentStartEvent ase)
    {
        // Rising-edge trigger: snapshot the prior IsAgentRunning and pin scroll to
        // live tail so the user immediately sees streaming output. This used to be a
        // local `_wasRunning` / `_scroll = 0` mutation in ChatScreen (§FP-005).
        var next = state with
        {
            Chat = state.Chat with
            {
                Status = "running",
                IsAgentRunning = true,
                WasRunning = state.Chat.IsAgentRunning,
                SessionStatus = SessionStatus.Working
            },
            Ui = state.Ui with { ScrollOffset = 0 }
        };
        if (next.Chat.Lines.Length != 0)
            return next;

        // Empty store: replay history so a late attach shows the full
        // transcript, mirroring the live rendering of each role. No dedup:
        // replay runs only on the empty store, and submit is suppressed
        // while running, so the echo cannot double — a tail-match would risk
        // eating a legitimate repeated prompt.
        foreach (var m in ase.Messages)
        {
            next = ReplayMessage(next, m);
        }

        return next;
    }

    /// <summary>
    ///     Folds one history message into the transcript. #461: the per-role
    ///     dispatch lives in <see cref="ReplayMessageVisitor" />, so a new
    ///     <see cref="AgentMessage" /> subtype can no longer be skipped in silence
    ///     by a <c>_ =&gt; state</c> arm.
    /// </summary>
    private static UiState ReplayMessage(UiState state, AgentMessage m) =>
        new ReplayMessageVisitor(state).Accept(m);

    private sealed class ReplayMessageVisitor : AgentMessageVisitor<UiState>
    {
        private readonly ReplayPartsVisitor _parts;
        private UiState _state;

        internal ReplayMessageVisitor(UiState state)
        {
            _state = state;
            _parts = new ReplayPartsVisitor(state);
        }

        public override UiState Visit(UserMessage message) => _state.AddLine(ChatRole.User, message.Content);

        public override UiState Visit(AssistantMessage message)
        {
            _parts.Seed(_state);
            _state = _parts.Walk(message.Parts);
            return _state;
        }

        public override UiState Visit(ToolResultMessage message)
        {
            var results = message.Results;
            for (int i = 0; i < results.Count; i++)
            {
                _state = _state.AddLine(ChatRole.ToolResult, results[i].Output, results[i].ToolCallId);
            }

            return _state;
        }
    }

    /// <summary>
    ///     Part-level arm of the history replay (#461). The whitespace guards and
    ///     the per-role line mapping are byte-identical to the switch this
    ///     replaced — only the dispatch moved.
    /// </summary>
    private sealed class ReplayPartsVisitor : ContentPartVisitor<UiState>
    {
        private UiState _state;

        internal ReplayPartsVisitor(UiState state) => _state = state;

        internal void Seed(UiState state) => _state = state;

        public override UiState Visit(TextPart part)
        {
            if (!string.IsNullOrWhiteSpace(part.Text))
                _state = _state.AddLine(ChatRole.Assistant, part.Text);
            return _state;
        }

        public override UiState Visit(ThinkingPart part)
        {
            if (!string.IsNullOrWhiteSpace(part.Text))
                _state = _state.AddLine(ChatRole.Thinking, part.Text);
            return _state;
        }

        public override UiState Visit(ToolCallPart part)
        {
            _state = _state.AddLine(ChatRole.Tool, $"→ {part.ToolName}", part.Id);
            return _state;
        }

        /// <summary>
        ///     Binary attachments are rendered by the renderer that owns the card
        ///     (CellForge's image card); the chat line list stays text-only. The
        ///     old switch had no file arm at all — it is now an explicit decision.
        /// </summary>
        public override UiState Visit(FilePart part) => _state;
    }

    private static UiState OnMessageStart(UiState state) =>
        state with
        {
            Chat = state.Chat with
            {
                Status = "running",
                IsAgentRunning = true,
                IsStreaming = true,
                SessionStatus = SessionStatus.Working,
                Active = ActiveMessage.Empty,
                PendingStreamText = ChunkedBuffer.Empty,
                PendingStreamThink = ChunkedBuffer.Empty
            }
        };

    private static UiState OnMessageUpdate(UiState state, MessageUpdateEvent mu) => mu.LlmEvent switch
    {
        TextDeltaEvent td => WithTextDelta(state, td.Delta),
        ThinkingDeltaEvent thd => WithThinkingDelta(state, thd.Delta),
// The card appears the moment the model NAMES the tool, before execution — the
        // same two-phase lifecycle the transcript already models (a start line,
        // then a start-with-args line). Before #680 the UI scraped the name off
        // this display line to build the card; now the reducer publishes a
        // name-only placeholder and ToolExecutionStartEvent upgrades it with the
        // arguments, the glyph and the diff payload (#680).
        ToolCallStartEvent tcs => FlushPending(state)
            .PutToolCall(ToolCallSnapshot.Named(tcs.Id, tcs.ToolName))
            .AddLine(ChatRole.Tool, $"→ {tcs.ToolName}", tcs.Id),
        StepFinishEvent sf => NoteRequestSize(FlushPending(state), sf.Usage),
        _ => state
    };

    /// <summary>
    ///     Records what the context OCCUPIES, from the request the provider just
    ///     accepted (#651). <c>Usage.InputTokens</c> is that request's full input
    ///     — the system prompt and the whole history, re-read every turn — so
    ///     summing it is the bill and never a window; this is the one figure that
    ///     answers "how full is my context", and it is the same one the ctx bar
    ///     reads (#630). Money is untouched: the paid totals arrive on
    ///     <see cref="SessionStatsEvent" /> and are the core's (#653).
    /// </summary>
    private static UiState NoteRequestSize(UiState state, Usage? usage) =>
        usage is null
            ? state
            : state with
            {
                Chat = state.Chat with
                {
                    Cost = state.Chat.Cost with { ContextTokens = usage.InputTokens }
                }
            };

    /// <summary>
    ///     Append a text delta to the chunked pending buffer and rebuild the
    ///     synced <see cref="ActiveMessage.TextBuffer" /> string only when
    ///     <see cref="StreamingSync.ShouldFlush" /> demands it.
    /// </summary>
    private static UiState WithTextDelta(UiState state, string delta)
    {
        if (string.IsNullOrEmpty(delta))
            return state;

        ChunkedBuffer pending = state.Chat.PendingStreamText.Append(delta);
        if (!StreamingSync.ShouldFlush(state.Chat.Active.TextBuffer.Length, pending.Length))
            return state with { Chat = state.Chat with { PendingStreamText = pending } };

        string full = StreamingSync.Concat(state.Chat.Active.TextBuffer, pending);
        return state with
        {
            Chat = state.Chat with
            {
                Active = state.Chat.Active with { TextBuffer = full },
                PendingStreamText = ChunkedBuffer.Empty
            }
        };
    }

    /// <summary>Thinking-delta counterpart of <see cref="WithTextDelta" />.</summary>
    private static UiState WithThinkingDelta(UiState state, string delta)
    {
        if (string.IsNullOrEmpty(delta))
            return state;

        ChunkedBuffer pending = state.Chat.PendingStreamThink.Append(delta);
        if (!StreamingSync.ShouldFlush(state.Chat.Active.ThinkBuffer.Length, pending.Length))
            return state with { Chat = state.Chat with { PendingStreamThink = pending } };

        string full = StreamingSync.Concat(state.Chat.Active.ThinkBuffer, pending);
        return state with
        {
            Chat = state.Chat with
            {
                Active = state.Chat.Active with { ThinkBuffer = full },
                PendingStreamThink = ChunkedBuffer.Empty
            }
        };
    }

    /// <summary>
    ///     Materialize any pending chunks into the synced
    ///     <see cref="ChatDomainState.Active" /> buffer strings so pause points
    ///     (tool calls, step finish, message end) observe the complete text.
    /// </summary>
    internal static UiState FlushPending(UiState state)
    {
        if (state.Chat.PendingStreamText.Length == 0 && state.Chat.PendingStreamThink.Length == 0)
            return state;

        return state with
        {
            Chat = state.Chat with
            {
                Active = state.Chat.Active with
                {
                    TextBuffer = StreamingSync.Concat(state.Chat.Active.TextBuffer, state.Chat.PendingStreamText),
                    ThinkBuffer = StreamingSync.Concat(state.Chat.Active.ThinkBuffer, state.Chat.PendingStreamThink)
                },
                PendingStreamText = ChunkedBuffer.Empty,
                PendingStreamThink = ChunkedBuffer.Empty
            }
        };
    }

    /// <summary>
    ///     Adopt the session totals the core published — tokens AND cost — in one
    ///     assignment.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         #653: these are absolute session-cumulative totals, not a delta, so
    ///         they are ASSIGNED. Folding a per-step delta here is what used to
    ///         make the status bar's money a second opinion about a bill the core
    ///         already knew; the cost also arrives with the core's own answer to
    ///         "is this model's price published at all", so an unpriced model
    ///         renders "—" instead of a fabricated "$0.0000".
    ///     </para>
    ///     <para>
    ///         No session check here on purpose. A session's events reach its own
    ///         store (see <c>UiEventRouter</c> / <c>SessionManager</c>), and a
    ///         sub-agent run publishes no totals of its own — it propagates its
    ///         cost into the parent record instead. A reducer-level "is this my
    ///         session?" filter would duplicate that isolation with a heuristic
    ///         that silently drops the real number the moment the two ids
    ///         disagree.
    ///     </para>
    ///     <para>
    ///         #651: the occupied context carried over from the last
    ///         <see cref="StepFinishEvent" /> is PRESERVED, not cleared. These
    ///         totals describe the whole session; the request size describes one
    ///         request, and only a new request may replace it. Rebuilding the
    ///         snapshot from the totals alone would drop the context figure back
    ///         to "unknown" at the end of every single turn — the moment the
    ///         cell most needs it.
    ///     </para>
    /// </remarks>
    private static UiState OnSessionStats(UiState state, SessionStatsEvent stats) => state with
    {
        Chat = state.Chat with
        {
            Cost = new CostSnapshot(
                stats.Metadata.TokensInput,
                stats.Metadata.TokensOutput,
                stats.Metadata.Cost,
                !stats.Metadata.IsCostKnown,
                state.Chat.Cost.ContextTokens)
        }
    };

    private static UiState OnMessageEnd(UiState state)
    {
        var next = FlushPending(state);
        if (!string.IsNullOrEmpty(next.Chat.Active.ThinkBuffer))
            next = next.AddLine(ChatRole.Thinking, next.Chat.Active.ThinkBuffer.Trim());
        if (!string.IsNullOrEmpty(next.Chat.Active.TextBuffer))
            next = next.AddLine(ChatRole.Assistant, next.Chat.Active.TextBuffer.Trim());
        return next with
        {
            Chat = next.Chat with
            {
                IsStreaming = false,
                Active = ActiveMessage.Empty,
                PendingStreamText = ChunkedBuffer.Empty,
                PendingStreamThink = ChunkedBuffer.Empty
            }
        };
    }

    private static UiState OnCompactionCompleted(UiState state, CompactionCompletedEvent cc) =>
        state
            .AddLine(ChatRole.System,
                $"compacted: pruned {cc.PrunedMessageCount} msgs, saved ~{cc.TokensSaved} tokens")
            .WithStatus("running");

    /// <summary>
    ///     #773 — a failed compaction is a degradation, not a failed run. The
    ///     core engaged the truncation fallback and continues the turn on a
    ///     shortened history, so the status returns to "running" and the
    ///     transcript says what happened instead. Shaped exactly like
    ///     <see cref="OnCompactionCompleted" />: one system line, one status.
    /// </summary>
    private static UiState OnCompactionFailed(UiState state, CompactionFailedEvent cf) =>
        state
            .AddLine(ChatRole.System,
                $"compaction failed: {cf.Error} — continuing on truncated history")
            .WithStatus("running");

    // ── formatting helpers (escaping) ─────────────────────────────────────

    private static string FormatToolStart(ToolExecutionStartEvent tes)
    {
        string args = EmptyPayloadLiteral(tes.Args) ?? tes.Args.GetRawText();
        return string.IsNullOrEmpty(args) || args == "{}"
            ? $"→ {tes.ToolName}"
            : $"→ {tes.ToolName}  {args}";
    }

    private static string FormatToolEnd(ToolExecutionEndEvent tee)
    {
        string label = tee.IsError ? "✗" : "✓";
        return $"{label} {FormatToolResultPreview(tee)}";
    }

    /// <summary>
    ///     The bare result text, budgeted to what a tool card shows. Split out of
    ///     <see cref="FormatToolEnd" /> because the status glyph belongs to the
    ///     transcript line only — the structured snapshot carries the result
    ///     without it, so a renderer no longer strips a "✓ "/"✗ " prefix back off
    ///     a string to recover the result (#680).
    /// </summary>
    private static string FormatToolResultPreview(ToolExecutionEndEvent tee)
    {
        string output = tee.Result.Output ?? string.Empty;
        string preview = output.Length > 600 ? output[..600] + "..." : output;
        return preview.Trim();
    }

    private static UiState WithStatus(this UiState state, string status) =>
        state with { Chat = state.Chat with { Status = status } };

    /// <summary>Empty object/array payload without materializing
    /// <c>GetRawText</c> (struct-enumerator probe, zero allocation).
    /// Same probe as the AnsiPlain/Nick twins (#1134 slice 1) — the store is
    /// the third consumer of per-tool-start args, and the literals keep both
    /// filters behavior-identical (<c>ToolCallSnapshot.Start</c> maps
    /// <c>"{}"</c> to empty; <c>FormatToolStart</c> hides <c>"{}"</c>).
    /// Returns null for anything non-empty.</summary>
    private static string? EmptyPayloadLiteral(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Array && args.GetArrayLength() == 0)
        {
            return "[]";
        }

        if (args.ValueKind == JsonValueKind.Object && !args.EnumerateObject().MoveNext())
        {
            return "{}";
        }

        return null;
    }

    /// <summary>
    ///     Fold the core's terminal fact into state (#687, #1024): the status
    ///     the core decided, plus — when a limit ended the run — the transcript
    ///     line that says so. Without the line a capped run would read as a
    ///     silent <see cref="SessionStatus.Done" />.
    /// </summary>
    private static UiState OnAgentEnd(UiState state, AgentEndEvent end)
    {
        var next = state with
        {
            Chat = state.Chat with
            {
                // Mirror OnAgentEnded: a preceding AgentErrorEvent leaves
                // "error" behind; a blind reset to idle would repaint a
                // failed run as a clean finish.
                Status = state.Chat.Status == "error" ? "error" : "idle",
                IsAgentRunning = false,
                WasRunning = state.Chat.IsAgentRunning,
                IsStreaming = false,
                Active = ActiveMessage.Empty,
                PendingStreamText = ChunkedBuffer.Empty,
                PendingStreamThink = ChunkedBuffer.Empty,
                // The core's own terminal fact, read once here (#687). The
                // projection reads this field; it does not re-ask which role
                // the transcript's last line had.
                SessionStatus = CoreEndedRun(state.Chat, end)
            }
        };
        // #1024: a limit stop is announced, never silent. The verdict stays
        // Done — the loop reached its terminal event without failing, and
        // SessionStatus has no limit member to reach for — but the transcript
        // names the kind, so a ceiling never reads as finished work.
        return end.Limit is { } limit
            ? next.AddLine(ChatRole.System, LimitNotice(limit))
            : next;
    }

    /// <summary>
    ///     The transcript line a limit stop leaves behind (#1024). Kind
    ///     specific: a clock stop that told the user to raise MaxSteps would
    ///     send them tuning the wrong ceiling.
    /// </summary>
    private static string LimitNotice(RunLimitKind limit) => limit switch
    {
        RunLimitKind.Timeout => "limit reached: wall-clock budget elapsed — work is incomplete.",
        RunLimitKind.MaxSteps => "limit reached: step budget exhausted — work is incomplete.",
        // #404: the budget members must name their own ceiling — the `_`
        // fallback below used to read "step budget", which would send a
        // spend-capped user tuning MaxSteps (the #1024 wrong-ceiling trap).
        RunLimitKind.MaxTokens => "limit reached: token budget exhausted — work is incomplete.",
        RunLimitKind.MaxCost => "limit reached: spend budget exhausted — work is incomplete.",
        RunLimitKind.MaxOutputBytes => "limit reached: output-size budget exhausted — work is incomplete.",
        _ => "limit reached: run budget exhausted — work is incomplete.",
    };

    /// <summary>
    ///     The status a run the CORE closed out carries (#687). This is the one
    ///     place that answer is produced, and everything it reads is the core's
    ///     own statement about the run:
    ///     <list type="bullet">
    ///         <item>
    ///             <c>AgentEndEvent.Cancelled</c> — the agent published the abort
    ///             terminal (<c>AgentLoop</c> emits it on the cancel path).
    ///         </item>
    ///         <item>
    ///             a preceding <c>AgentErrorEvent</c>, already folded into
    ///             <c>Chat.Status</c> — a run that failed is not done, whatever
    ///             role its last transcript line had.
    ///         </item>
    ///         <item>
    ///             otherwise the run finished cleanly — which INCLUDES a run cut
    ///             short by a limit (#1024): the loop reached its terminal event
    ///             without failing, and a limit is neither a cancel nor an
    ///             error, so it must read as neither <c>Aborted</c> nor
    ///             <c>Error</c>. The limit itself is announced in the transcript
    ///             by <see cref="OnAgentEnd" />, not by this verdict.
    ///         </item>
    ///     </list>
    /// </summary>
    /// <param name="chat">The chat state as of the terminal event.</param>
    /// <param name="end">The core's terminal event (cancel and limit read off it).</param>
    private static SessionStatus CoreEndedRun(ChatDomainState chat, AgentEndEvent end) =>
        end.Cancelled ? SessionStatus.Aborted
        : chat.Status == "error" ? SessionStatus.Error
        : SessionStatus.Done;

    /// <summary>
    ///     The status after a HOST-driven terminal message —
    ///     <see cref="ChatAppMsg.AgentEnded" /> and
    ///     <see cref="ChatAppMsg.StatusChanged" /> from <c>TuiEffectHost</c>
    ///     (#687).
    ///     <para>
    ///         The host only ever DECIDES a failure. By the time its terminal
    ///         message lands, a run that finished has already published
    ///         <c>AgentEndEvent</c>, so treating the host's message as a second
    ///         verdict would downgrade that <see cref="SessionStatus.Done" /> back
    ///         to <see cref="SessionStatus.Idle" /> one message later. A run the
    ///         host closes out while still marked working never reported a
    ///         terminal at all, and <see cref="SessionStatus.Idle" /> — nothing
    ///         running, nothing succeeded — is the honest reading of that.
    ///     </para>
    /// </summary>
    /// <param name="chat">The chat state as of the host's message.</param>
    /// <param name="reportedStatus">The status string the host reported, if any.</param>
    internal static SessionStatus HostClosedRun(ChatDomainState chat, string? reportedStatus) =>
        string.Equals(reportedStatus, "error", StringComparison.OrdinalIgnoreCase)
            ? SessionStatus.Error
            : chat.SessionStatus == SessionStatus.Working
                ? SessionStatus.Idle
                : chat.SessionStatus;

    /// <summary>Set the domain status alongside a status-string change (#687).</summary>
    private static UiState WithSessionStatus(this UiState state, SessionStatus status) =>
        state with { Chat = state.Chat with { SessionStatus = status } };

    /// <summary>
    ///     Run-end fold for the effect host. A null
    ///     <see cref="ChatAppMsg.AgentEnded.Status" /> keeps a previously set
    ///     <c>"error"</c> status (so a failed run does not get repainted as a
    ///     clean finish by the host's finally block) and otherwise falls back to
    ///     <c>"idle"</c>. The domain status follows
    ///     <see cref="HostClosedRun" /> — the host closes a run out, it does not
    ///     re-judge it (#687).
    /// </summary>
    internal static ReduceResult OnAgentEnded(UiState state, ChatAppMsg.AgentEnded msg)
    {
        var next = state with
        {
            Chat = state.Chat with
            {
                IsAgentRunning = false,
                IsStreaming = false,
                Active = ActiveMessage.Empty,
                Status = msg.Status ?? (state.Chat.Status == "error" ? "error" : "idle"),
                SessionStatus = msg.Error is null
                    ? HostClosedRun(state.Chat, msg.Status)
                    : SessionStatus.Error
            }
        };
        return msg.Error is null
            ? ReduceResult.NoOp(next)
            : ReduceResult.NoOp(next.AddLine(ChatRole.Error, msg.Error));
    }
}
