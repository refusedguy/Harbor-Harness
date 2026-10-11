using System.Collections.Immutable;
using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Ui.Framework.Panels;
namespace Harbor.Ui.Framework.State;

/// <summary>
///     Harbor chat extension: <c>AgentEvent → UiState</c> plus the
///     <see cref="ChatAppMsg" /> arms. Exposed to <see cref="AppReducer" /> (the
///     generic half) through <see cref="ChatAppReducerPlugin" />, so the generic
///     reducer is never forked to host the chat domain (#33/T4, #364).
/// </summary>
/// <remarks>
///     <para>
///         This is the only place that knows about agents, transcripts, streaming
///         and costs. It folds into <see cref="UiState.Chat" />; the few generic
///         writes it performs (pinning scroll on run start, resetting the input
///         box on submit) go through <see cref="UiState.Ui" /> explicitly.
///     </para>
///     <para>
///         <b>#653 — costs are folded, never computed.</b> The totals come from
///         the core, in <see cref="SessionStatsEvent" />; nothing in this reducer
///         turns tokens into money. The hand-rolled "$3/M in, $15/M out" formula
///         it used to carry charged a free model for 61.6k tokens — see
///         <c>tests/Harbor.Architecture.Tests/CostPricedInCoreRules.cs</c>.
///     </para>
///     <para>
///         Must never call into <c>IAgent</c> or perform I/O — side-effects are the
///         responsibility of <see cref="ITuiEffectRunner" /> (see
///         <see cref="ClassifySubmit" />).
///     </para>
/// </remarks>
public static class ChatAppReducer
{
    // ── extension surface (IAppReducerPlugin hook lives in ChatAppReducerPlugin) ──

    /// <summary>
    ///     Whether a prompt run is in flight. Queried by <see cref="AppReducer" />
    ///     so the generic input gate never has to know what "running" means.
    /// </summary>
    public static bool IsBusy(UiState state) => state.Chat.IsAgentRunning;

    /// <summary>
    ///     Claim the chat-owned arms. Returns <see langword="null" /> for every
    ///     message this reducer does not own so the generic pass runs.
    /// </summary>
    public static ReduceResult? ReduceMessage(UiState state, AppMsg msg) => msg switch
    {
        ChatAppMsg.Agent a => new ReduceResult(Reduce(state, a.Event), new TuiEffect.None()),
        ChatAppMsg.AgentStarted => new ReduceResult(state with
        {
            Chat = state.Chat with
            {
                Status = "running",
                IsAgentRunning = true,
                SessionStatus = SessionStatus.Working
            }
        }, new TuiEffect.None()),
        ChatAppMsg.AgentEnded ae => OnAgentEnded(state, ae),
        ChatAppMsg.StatusChanged sc => ReduceResult.NoOp(state with
        {
            Chat = state.Chat with
            {
                Status = sc.Status,
                SessionStatus = HostClosedRun(state.Chat, sc.Status)
            }
        }),
        ChatAppMsg.ConfigureRuntime cr => ReduceResult.NoOp(state with
        {
            Chat = state.Chat with { Model = cr.Model, Provider = cr.Provider, AgentName = cr.AgentName }
        }),
        ChatAppMsg.AppendLine al => ReduceResult.NoOp(state.AddLine(al.Role, al.Text, al.ToolCallId)),
        ChatAppMsg.HydrateSession h => ReduceResult.NoOp(HydrateSession(state, h)),
        ChatAppMsg.SyncSessions ss => ReduceResult.NoOp(state with
        {
            Chat = state.Chat with { Sessions = ss.Sessions, ActiveSessionId = ss.ActiveSessionId }
        }),
        // Replaces the snapshot outright — see ChatAppMsg.SyncDiagnostics.
        ChatAppMsg.SyncDiagnostics sd => ReduceResult.NoOp(state with
        {
            Chat = state.Chat with { Diagnostics = sd.Diagnostics }
        }),
        ChatAppMsg.OpenTab ot => OpenTab(state, ot.Tab),
        ChatAppMsg.ActivateTab at => ActivateTab(state, at.SessionId),
        ChatAppMsg.CloseTab ct => CloseTab(state, ct.SessionId),
        ChatAppMsg.CloseOtherTabs co => CloseOtherTabs(state, co.Keep),
        ChatAppMsg.CloseTabsToRight ctr => CloseTabsToRight(state, ctr.From),
        ChatAppMsg.PinTab pt => ReduceResult.NoOp(PinTab(state, pt.SessionId, pt.Pinned)),
        ChatAppMsg.ReorderTab ro => ReduceResult.NoOp(ReorderTab(state, ro.SessionId, ro.ToIndex)),
        ChatAppMsg.CycleNextTab => CycleNextTab(state),
        ChatAppMsg.CyclePreviousTab => CyclePreviousTab(state),
        ChatAppMsg.HydrateTabStrip ht => HydrateTabStrip(state, ht),
        ChatAppMsg.OpenMarkup om => ReduceResult.NoOp(state with
        {
            Chat = state.Chat with { Markup = MarkupOverlayState.Open(om.SourcePath, om.SourceName, om.SourceWidth, om.SourceHeight, om.ScrollOffset) }
        }),
        ChatAppMsg.CloseMarkup => ReduceResult.NoOp(state with
        {
            Chat = state.Chat with { Markup = MarkupOverlayState.Closed }
        }),
        ChatAppMsg.MarkupSelectTool st => WithMarkup(state, m => m with { ActiveTool = st.Tool, Error = string.Empty }),
        ChatAppMsg.MarkupMoveCursor mc => WithMarkup(state, m => m with { Cursor = m.Cursor.Shift(mc.Dx, mc.Dy) }),
        ChatAppMsg.MarkupPlace => WithMarkup(state, PlaceAtCursor),
        ChatAppMsg.MarkupSetPendingText pt => WithMarkup(state, m => m with
        {
            PendingText = pt.Text.Length <= MarkupOverlayState.MaxPendingTextLength
                ? pt.Text
                : pt.Text[..MarkupOverlayState.MaxPendingTextLength]
        }),
        ChatAppMsg.MarkupNudge mn => WithMarkup(state, m => m with { Model = m.Model.MoveSelected(mn.Dx, mn.Dy), Error = string.Empty }),
        ChatAppMsg.MarkupResize mr => WithMarkup(state, m => m with { Model = m.Model.ResizeSelected(mr.Dx, mr.Dy), Error = string.Empty }),
        ChatAppMsg.MarkupSelectNext => WithMarkup(state, m => m with { Model = m.Model.SelectNext() }),
        ChatAppMsg.MarkupSelectAt sa => WithMarkup(state, m => SelectAtPoint(m, NormalizedPoint.Create(sa.X, sa.Y))),
        ChatAppMsg.MarkupPressAt pa => WithMarkup(state, m => PressAtPoint(m, NormalizedPoint.Create(pa.X, pa.Y))),
        ChatAppMsg.MarkupDragTo dt => WithMarkup(state, m => DragToPoint(m, NormalizedPoint.Create(dt.X, dt.Y))),
        ChatAppMsg.MarkupReleaseAt ra => WithMarkup(state, m => ReleaseAtPoint(m, NormalizedPoint.Create(ra.X, ra.Y))),
        ChatAppMsg.MarkupDeleteSelected => WithMarkup(state, m => m with { Model = m.Model.Delete(), Error = string.Empty }),
        ChatAppMsg.MarkupUndo => WithMarkup(state, m => m with { Model = m.Model.UndoFrame() }),
        ChatAppMsg.MarkupRedo => WithMarkup(state, m => m with { Model = m.Model.RedoFrame() }),
        ChatAppMsg.MarkupSaved ms => WithMarkup(state, m => m with { SavedPath = ms.Path ?? string.Empty, Error = string.Empty }),
        ChatAppMsg.MarkupFailed mf => WithMarkup(state, m => m with { Error = string.IsNullOrWhiteSpace(mf.Error) ? "Save failed." : mf.Error }),

        // A clear-screen must not close the user's tabs — the strip is workspace
        // chrome, not transcript (#388), and UiState.ClearTranscript already
        // honours that for the key path.
        //
        // AppMsg.Reset needs the same rule, and it needs it HERE rather than in
        // After: the message documents itself as the clear-screen arm ("reset to
        // a fresh empty state, e.g. clear-screen") and the Avalonia host's Ctrl+L
        // dispatches exactly it, but the generic arm rebuilds a bare UiState and
        // the After hook only ever sees that already-reset state — never the one
        // that still held the tabs. Phase 1 is the last point where they are
        // reachable, so the chat half re-attaches them itself.
        //
        // ONLY the strip is carried over. Everything else must still reset, or
        // \"reset to a fresh empty state\" stops being true: the transcript, the
        // session chrome and the agent binding are all part of what a clear
        // is meant to drop.
        AppMsg.Reset => ReduceResult.NoOp(
            new UiState { Chat = ChatDomainState.Empty with { TabStrip = state.Chat.TabStrip } }),

        AppMsg.KeyInput k => OnKeyInput(state, k),
        _ => null
    };

    /// <summary>
    ///     Fold chat bookkeeping on top of a transition the generic reducer
    ///     already applied. Only <see cref="AppMsg.ScrollResetToTail" /> needs it:
    ///     the renderer emits that message on the rising edge of a run, so the
    ///     store must snapshot <see cref="ChatDomainState.WasRunning" /> to keep
    ///     the <c>IsAgentRunning &amp;&amp; !WasRunning</c> invariant intact.
    /// </summary>
    public static UiState After(UiState state, AppMsg msg) => msg switch
    {
        // Snapshot, don't force: fabricating WasRunning=true breaks the
        // rising-edge invariant (IsRunning && !WasRunning) that tells
        // renderers a run just started (e.g. to snap to tail).
        AppMsg.ScrollResetToTail => state with { Chat = state.Chat with { WasRunning = state.Chat.IsAgentRunning } },
        _ => state
    };

    /// <summary>
    ///     The composed entry point: generic arms plus the Harbor chat extension.
    ///     This is what <see cref="UiStore.Dispatch" /> calls; a non-chat host can
    ///     call <see cref="AppReducer.Update" /> without the extension instead.
    /// </summary>
    public static ReduceResult Update(UiState state, AppMsg msg) =>
        AppReducer.Update(state, msg, ChatAppReducerPlugin.Instance);

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
    private static UiState FlushPending(UiState state)
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
    private static SessionStatus HostClosedRun(ChatDomainState chat, string? reportedStatus) =>
        string.Equals(reportedStatus, "error", StringComparison.OrdinalIgnoreCase)
            ? SessionStatus.Error
            : chat.SessionStatus == SessionStatus.Working
                ? SessionStatus.Idle
                : chat.SessionStatus;

    /// <summary>Set the domain status alongside a status-string change (#687).</summary>
    private static UiState WithSessionStatus(this UiState state, SessionStatus status) =>
        state with { Chat = state.Chat with { SessionStatus = status } };

    // ── chat-owned message arms ────────────────────────────────────────────

    /// <summary>
    ///     Atomic session hydration (#89). Folds Reset + session-chrome bind +
    ///     history replay into one pure transition so the swap rides a single
    ///     store CAS: a concurrent background event applies strictly before
    ///     (superseded by the fresh state) or after (appended in order), never
    ///     interleaved mid-history.
    /// </summary>
    private static UiState HydrateSession(UiState state, ChatAppMsg.HydrateSession h)
    {
        var next = new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Model = h.Model,
                Provider = h.Provider,
                AgentName = h.AgentName,
                // The status the core persisted for this session, read rather
                // than re-derived from the replayed lines (#687) — the switch
                // path had the same defect the live path had.
                SessionStatus = h.Status,
                // The tab strip is workspace chrome, not transcript: hydrating a
                // session (i.e. switching to it) must not close the open tabs.
                TabStrip = state.Chat.TabStrip,
            },
        };
        foreach (var line in h.Lines)
            next = next.AddLine(line.Role, line.Text, line.ToolCallId);
        return next;
    }

    /// <summary>
    ///     Run-end fold for the effect host. A null
    ///     <see cref="ChatAppMsg.AgentEnded.Status" /> keeps a previously set
    ///     <c>"error"</c> status (so a failed run does not get repainted as a
    ///     clean finish by the host's finally block) and otherwise falls back to
    ///     <c>"idle"</c>. The domain status follows
    ///     <see cref="HostClosedRun" /> — the host closes a run out, it does not
    ///     re-judge it (#687).
    /// </summary>
    private static ReduceResult OnAgentEnded(UiState state, ChatAppMsg.AgentEnded msg)
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

    /// <summary>
    ///     The chat half of key handling. Returns <see langword="null" /> for
    ///     every action the generic reducer owns (scroll, focus, input editing,
    ///     panel keys) so <see cref="AppReducer" /> handles it.
    /// </summary>
    private static ReduceResult? OnKeyInput(UiState state, AppMsg.KeyInput k)
    {
        bool busy = IsBusy(state);
        switch (k.Action)
        {
            // Escape during a run is an abort (not a quit) — see keymap note below.
            case ChatAction.Quit when busy:
            case ChatAction.Abort:
                return TransitionAbort(state);

            case ChatAction.Quit:
                return new ReduceResult(state, new TuiEffect.QuitApp());

            case ChatAction.Submit:
            {
                if (busy)
                    return null;
                (var nextInput, string? submitted) = state.Ui.Input.Consume();
                var next = state.SetInput(nextInput);
                if (submitted is null)
                    return ReduceResult.NoOp(next);

                return ClassifySubmit(next, submitted);
            }

            case ChatAction.Clear:
                return busy ? null : ReduceResult.NoOp(state.ClearTranscript());

            // Tab-strip actions (#389). The tab model is chat-owned, so these are
            // claimed here and deliberately NOT gated on `busy`: switching or
            // closing a tab is chrome, not transcript work, and must stay
            // available while an agent runs — the effect is what the host
            // honours or refuses.
            case ChatAction.NextTab:
                return NextTabFromKey(state);
            case ChatAction.PreviousTab:
                return PreviousTabFromKey(state);
            case ChatAction.CloseTab:
                return CloseFocusedTab(state);
            case ChatAction.OpenTab:
                return RequestOpenTab(state);
            case ChatAction.MoveTabLeft:
                return MoveFocusedTab(state, -1);
            case ChatAction.MoveTabRight:
                return MoveFocusedTab(state, +1);

            default:
                return null;
        }
    }

    /// <summary>
    ///     Classify a submitted (already consumed) input line into the effect that
    ///     should run. Single source of truth shared by the reducer and the effect
    ///     host so exit words, slash commands, and prompts behave identically wherever
    ///     a line is submitted.
    /// </summary>
    private static ReduceResult ClassifySubmit(UiState state, string submitted)
    {
        string trimmed = submitted.Trim();
        if (ChatCommands.ExitWords.Contains(trimmed))
            return new ReduceResult(state, new TuiEffect.QuitApp());

        if (trimmed.StartsWith('/'))
            return new ReduceResult(state, new TuiEffect.RunSlash(trimmed));

        return new ReduceResult(
            state.AddLine(ChatRole.User, submitted),
            new TuiEffect.PromptAgent(submitted));
    }

    /// <summary>
    ///     Start an abort: emit a plain system note and the host effect that cancels
    ///     the running agent. Partially streamed text is folded into the transcript
    ///     first (same as <see cref="OnMessageEnd" />) so the abort does not eat
    ///     already-received content; only then are the buffers cleared. Colour is
    ///     the renderer's responsibility (driven by <see cref="ChatRole" />), so the
    ///     text here is markup-free.
    /// </summary>
    private static ReduceResult TransitionAbort(UiState state)
    {
        var next = FlushPending(state);
        if (!string.IsNullOrEmpty(next.Chat.Active.ThinkBuffer))
            next = next.AddLine(ChatRole.Thinking, next.Chat.Active.ThinkBuffer.Trim());
        if (!string.IsNullOrEmpty(next.Chat.Active.TextBuffer))
            next = next.AddLine(ChatRole.Assistant, next.Chat.Active.TextBuffer.Trim());
        // Two steps on purpose: folding the abort note into the transcript and
        // then clearing the buffers must not drop the note added a line above
        // (a single `x.AddLine(..) with { Chat = x.Chat with { .. } }` would
        // overwrite the fresh transcript with the pre-abort one).
        next = next.AddLine(ChatRole.System, "Aborted.");
        return new ReduceResult(
            next with { Chat = next.Chat with { IsStreaming = false, Active = ActiveMessage.Empty } },
            new TuiEffect.AbortAgent());
    }

    // ── tab transitions (#388, slice 1/3 — pure; the host runs the effect) ──

    /// <summary>
    ///     Open a tab at the right end of the strip and focus it, emitting
    ///     <see cref="TuiEffect.ActivateSession" /> so the host switches session.
    ///     <b>Idempotent:</b> a session that already has a tab is activated
    ///     instead of duplicated, and its position and fields are left alone.
    /// </summary>
    public static ReduceResult OpenTab(UiState state, SessionTab tab)
    {
        var strip = state.Chat.TabStrip;
        if (strip.Contains(tab.SessionId))
            return ActivateTab(state, tab.SessionId);

        var next = state with
        {
            Chat = state.Chat with
            {
                TabStrip = strip with { Tabs = strip.Tabs.Add(tab), ActiveTabId = tab.SessionId }
            }
        };
        return new ReduceResult(next, new TuiEffect.ActivateSession(tab.SessionId));
    }

    /// <summary>
    ///     Focus an already-open tab and ask the host to switch to its session.
    ///     Order is untouched (activating never reorders). Unknown session or
    ///     already-active tab → same state instance and no effect, so the store
    ///     does not bump the revision on a no-op.
    /// </summary>
    public static ReduceResult ActivateTab(UiState state, SessionId sessionId)
    {
        var strip = state.Chat.TabStrip;
        if (strip.IndexOf(sessionId) < 0)
            return ReduceResult.NoOp(state);
        if (SameSession(strip.ActiveTabId, sessionId))
            return ReduceResult.NoOp(state);

        var next = state with { Chat = state.Chat with { TabStrip = strip with { ActiveTabId = sessionId } } };
        return new ReduceResult(next, new TuiEffect.ActivateSession(sessionId));
    }

    /// <summary>
    ///     Close a tab, applying the neighbour rule
    ///     (<see cref="TabNeighbourAfterClose" />) when the active tab was the
    ///     one closed, and the panel-ownership rule
    ///     (<see cref="ReleaseOwnedPanels" />).
    /// </summary>
    public static ReduceResult CloseTab(UiState state, SessionId sessionId)
    {
        var strip = state.Chat.TabStrip;
        var tabs = strip.Tabs;
        int index = strip.IndexOf(sessionId);
        if (index < 0)
            return ReduceResult.NoOp(state);

        var remaining = tabs.RemoveAt(index);
        bool closedActive = SameSession(strip.ActiveTabId, sessionId);

        var released = ReleaseOwnedPanels(state, tabs[index], remaining);
        var activeId = closedActive ? TabNeighbourAfterClose(remaining, index) : strip.ActiveTabId;

        var next = released with
        {
            Chat = released.Chat with { TabStrip = strip with { Tabs = remaining, ActiveTabId = activeId } }
        };
        // The neighbour (if any) is the session the host must now open.
        TuiEffect effect = activeId is { } target && closedActive
            ? new TuiEffect.ActivateSession(target)
            : new TuiEffect.None();
        return new ReduceResult(next, effect);
    }

    /// <summary>
    ///     Close every tab except <paramref name="keep" /> and focus the
    ///     survivor. The survivor owns the focus unconditionally — it is the tab
    ///     the gesture was invoked on, so the strip must not end up pointing at a
    ///     tab that no longer exists. The effect fires only when the active tab
    ///     actually changed.
    /// </summary>
    /// <remarks>
    ///     Pinned tabs survive this gesture (#390): the survivor set is the
    ///     keep target plus every pinned tab, in tab order. An explicit
    ///     <see cref="CloseTab" /> still closes a pinned tab — pin guards bulk
    ///     gestures, not intent.
    /// </remarks>
    public static ReduceResult CloseOtherTabs(UiState state, SessionId keep)
    {
        var strip = state.Chat.TabStrip;
        var tabs = strip.Tabs;
        int keepIndex = strip.IndexOf(keep);
        if (keepIndex < 0 || tabs.Length == 1)
            return ReduceResult.NoOp(state);

        var survivors = ImmutableArray.CreateBuilder<SessionTab>(tabs.Length);
        for (int i = 0; i < tabs.Length; i++)
        {
            if (i == keepIndex || tabs[i].IsPinned)
                survivors.Add(tabs[i]);
        }

        var remaining = survivors.ToImmutable();
        if (remaining.Length == tabs.Length)
        {
            // Nothing would close — still honour the focus rule when the keep
            // target is not active, without touching order or panels.
            if (SameSession(strip.ActiveTabId, keep))
                return ReduceResult.NoOp(state);
            var focusOnly = state with
            {
                Chat = state.Chat with { TabStrip = strip with { ActiveTabId = keep } }
            };
            return new ReduceResult(focusOnly, new TuiEffect.ActivateSession(keep));
        }

        var released = state;
        for (int i = 0; i < tabs.Length; i++)
        {
            if (i != keepIndex && !tabs[i].IsPinned)
                released = ReleaseOwnedPanels(released, tabs[i], remaining);
        }

        var next = released with
        {
            Chat = released.Chat with { TabStrip = strip with { Tabs = remaining, ActiveTabId = keep } }
        };
        TuiEffect effect = SameSession(strip.ActiveTabId, keep)
            ? new TuiEffect.None()
            : new TuiEffect.ActivateSession(keep);
        return new ReduceResult(next, effect);
    }

    /// <summary>
    ///     Close every tab strictly to the right of <paramref name="from" /> and
    ///     focus <paramref name="from" />, mirroring
    ///     <see cref="CloseOtherTabs" />'s focus and effect rules. Closing the
    ///     last tab to the right is a no-op.
    /// </summary>
    /// <remarks>
    ///     Pinned tabs to the right survive (#390) — same rule as
    ///     <see cref="CloseOtherTabs" />. When every tab on the right is
    ///     pinned, nothing closes and the state is returned untouched.
    /// </remarks>
    public static ReduceResult CloseTabsToRight(UiState state, SessionId from)
    {
        var strip = state.Chat.TabStrip;
        var tabs = strip.Tabs;
        int index = strip.IndexOf(from);
        if (index < 0 || index == tabs.Length - 1)
            return ReduceResult.NoOp(state);

        bool closesAnything = false;
        for (int i = index + 1; i < tabs.Length; i++)
        {
            if (!tabs[i].IsPinned)
            {
                closesAnything = true;
                break;
            }
        }

        if (!closesAnything)
            return ReduceResult.NoOp(state);

        var survivors = ImmutableArray.CreateBuilder<SessionTab>(tabs.Length);
        for (int i = 0; i <= index; i++)
            survivors.Add(tabs[i]);
        for (int i = index + 1; i < tabs.Length; i++)
        {
            if (tabs[i].IsPinned)
                survivors.Add(tabs[i]);
        }

        var remaining = survivors.ToImmutable();
        var released = state;
        for (int i = index + 1; i < tabs.Length; i++)
        {
            if (!tabs[i].IsPinned)
                released = ReleaseOwnedPanels(released, tabs[i], remaining);
        }

        var next = released with
        {
            Chat = released.Chat with { TabStrip = strip with { Tabs = remaining, ActiveTabId = from } }
        };
        TuiEffect effect = SameSession(strip.ActiveTabId, from)
            ? new TuiEffect.None()
            : new TuiEffect.ActivateSession(from);
        return new ReduceResult(next, effect);
    }

    /// <summary>
    ///     Pin or unpin a tab. Flag only: pinning never reorders, so the tab the
    ///     user is looking at cannot jump under the cursor.
    /// </summary>
    public static UiState PinTab(UiState state, SessionId sessionId, bool pinned)
    {
        var strip = state.Chat.TabStrip;
        var tabs = strip.Tabs;
        int index = strip.IndexOf(sessionId);
        if (index < 0)
            return state;

        var tab = tabs[index];
        if (tab.IsPinned == pinned)
            return state;
        return state with
        {
            Chat = state.Chat with { TabStrip = strip with { Tabs = tabs.SetItem(index, tab with { IsPinned = pinned }) } }
        };
    }

    /// <summary>
    ///     Move a tab to <paramref name="toIndex" />, clamped to the current
    ///     range. Remove-then-insert lands the tab exactly at the clamped index
    ///     (later tabs shift left once), and the active tab never changes —
    ///     reordering is presentation, not focus.
    /// </summary>
    public static UiState ReorderTab(UiState state, SessionId sessionId, int toIndex)
    {
        var strip = state.Chat.TabStrip;
        var tabs = strip.Tabs;
        int from = strip.IndexOf(sessionId);
        if (from < 0 || tabs.Length < 2)
            return state;

        int to = Math.Clamp(toIndex, 0, tabs.Length - 1);
        if (to == from)
            return state;
        return state with
        {
            Chat = state.Chat with { TabStrip = strip with { Tabs = tabs.RemoveAt(from).Insert(to, tabs[from]) } }
        };
    }

    /// <summary>Focus the next tab in tab order (wraps around; no-op with fewer than two tabs).</summary>
    public static ReduceResult CycleNextTab(UiState state) => CycleTab(state, forward: true);

    /// <summary>Focus the previous tab in tab order (wraps around; no-op with fewer than two tabs).</summary>
    public static ReduceResult CyclePreviousTab(UiState state) => CycleTab(state, forward: false);

    // ── tab strip from the keyboard (#389) ──────────────────────────────────
    // ChatAction → tab-transition aliases. They exist so the keymap stays the
    // only key→meaning table (no shell-local branches) and every tab mutation
    // arrives through the one reducer path instead of a renderer's own
    // selection field.

    /// <summary>
    ///     <see cref="ChatAction.NextTab" /> — the <c>Ctrl+Tab</c> key path.
    ///     Delegates to <see cref="CycleNextTab" />, which already no-ops with
    ///     fewer than two tabs rather than re-emitting an activate effect for
    ///     the session already on screen.
    /// </summary>
    public static ReduceResult NextTabFromKey(UiState state) => CycleNextTab(state);

    /// <summary>
    ///     <see cref="ChatAction.PreviousTab" /> — the <c>Ctrl+Shift+Tab</c>
    ///     key path. Delegates to <see cref="CyclePreviousTab" />.
    /// </summary>
    public static ReduceResult PreviousTabFromKey(UiState state) => CyclePreviousTab(state);

    /// <summary>
    ///     <see cref="ChatAction.CloseTab" /> (<c>Ctrl+W</c>) — close the
    ///     <i>focused</i> tab, not the app.
    /// </summary>
    /// <remarks>
    ///     The distinction is the whole point of the binding, so it is enforced
    ///     here rather than in a shell: this method can only ever return
    ///     <see cref="TuiEffect.ActivateSession" /> or
    ///     <see cref="TuiEffect.None" /> — never
    ///     <see cref="TuiEffect.QuitApp" />. With no tab open it is a no-op, and
    ///     the explicit quit path (<see cref="ChatAction.Quit" />) is untouched.
    /// </remarks>
    public static ReduceResult CloseFocusedTab(UiState state)
    {
        if (state.Chat.TabStrip.ActiveTabId is not { } active)
            return ReduceResult.NoOp(state);

        return CloseTab(state, active);
    }

    /// <summary>
    ///     <see cref="ChatAction.OpenTab" /> (<c>Ctrl+T</c>) — ask the host to
    ///     open or switch a session. Pure state change: the reducer does not know
    ///     which session the user means (that is a picker decision), so it only
    ///     asks, and hosts without a picker wired ignore the effect.
    /// </summary>
    public static ReduceResult RequestOpenTab(UiState state) =>
        new(state, new TuiEffect.RequestOpenSession());

    /// <summary>
    ///     <see cref="ChatAction.MoveTabLeft" /> / <see cref="ChatAction.MoveTabRight" /> —
    ///     move the <i>focused</i> tab one step (<paramref name="delta" /> of -1/+1)
    ///     through the existing <see cref="ReorderTab" /> transition (#390).
    ///     Focus follows the tab (reorder never changes the active id), the
    ///     effect is always <see cref="TuiEffect.None" /> — moving is
    ///     presentation, not a session switch — and the edges are no-ops.
    /// </summary>
    public static ReduceResult MoveFocusedTab(UiState state, int delta)
    {
        if (state.Chat.TabStrip.ActiveTabId is not { } active)
            return ReduceResult.NoOp(state);

        var strip = state.Chat.TabStrip;
        int from = strip.IndexOf(active);
        if (from < 0)
            return ReduceResult.NoOp(state);

        int to = from + delta;
        if (to < 0 || to >= strip.Tabs.Length)
            return ReduceResult.NoOp(state);

        return ReduceResult.NoOp(ReorderTab(state, active, to));
    }

    /// <summary>
    ///     Restore the open-tab order + active tab from a persisted
    ///     <see cref="TabStripSnapshot" /> (#390, slice 3/3 — pure; the host
    ///     persists debounced and dispatches after first paint).
    /// </summary>
    /// <remarks>
    ///     Snapshot ids with no descriptor (sessions deleted since the persist)
    ///     are dropped with a single system note — one line no matter how many
    ///     vanished. A repeated id restores once (first occurrence wins).
    ///     Descriptors the snapshot does not order are appended at the end, so
    ///     a session the store knows is never lost to a stale payload. An
    ///     unknown active id falls back to the first restored tab; nothing
    ///     restored at all means an empty strip, no focus, no crash — the host
    ///     then opens the default tab through the existing open path.
    /// </remarks>
    public static ReduceResult HydrateTabStrip(UiState state, ChatAppMsg.HydrateTabStrip h)
    {
        var byId = new Dictionary<string, SessionTab>(StringComparer.Ordinal);
        foreach (var tab in h.Tabs)
        {
            if (!byId.ContainsKey(tab.SessionId.Value))
                byId[tab.SessionId.Value] = tab;
        }

        var restored = ImmutableArray.CreateBuilder<SessionTab>(byId.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in h.Snapshot.Order)
        {
            if (!seen.Add(id))
                continue;
            if (byId.TryGetValue(id, out var tab))
                restored.Add(tab);
        }

        foreach (var tab in h.Tabs)
        {
            if (seen.Add(tab.SessionId.Value))
                restored.Add(tab);
        }

        var tabs = restored.ToImmutable();
        int dropped = seen.Count - tabs.Length;

        SessionId? active = null;
        if (tabs.Length > 0)
        {
            active = tabs[0].SessionId;
            var want = h.Snapshot.ActiveSessionId;
            if (want is not null)
            {
                foreach (var tab in tabs)
                {
                    if (string.Equals(tab.SessionId.Value, want, StringComparison.Ordinal))
                    {
                        active = tab.SessionId;
                        break;
                    }
                }
            }
        }

        var next = state with
        {
            Chat = state.Chat with
            {
                TabStrip = state.Chat.TabStrip with { Tabs = tabs, ActiveTabId = active }
            }
        };

        if (dropped > 0)
        {
            next = next.AddLine(
                ChatRole.System,
                $"Restored {tabs.Length} tab(s); dropped {dropped} missing session(s).");
        }

        return ReduceResult.NoOp(next);
    }

    // ── screenshot-markup overlay (#400 slice 1/2) ──────────────────────────
    //
    // Every arm below is a pure fold over Chat.Markup and no-ops (returns the
    // input state) when the overlay is closed — keys never reach the agent
    // while it is open because the host stops at the markup barrier first
    // (ReplInputLoop checks Markup.IsOpen before the palette), not because
    // the reducer double-guesses the host.

    private static ReduceResult WithMarkup(UiState state, Func<MarkupOverlayState, MarkupOverlayState> fold)
    {
        var markup = state.Chat.Markup;
        if (!markup.IsOpen)
        {
            return ReduceResult.NoOp(state);
        }

        var next = fold(markup);
        return ReferenceEquals(next, markup)
            ? ReduceResult.NoOp(state)
            : ReduceResult.NoOp(state with { Chat = state.Chat with { Markup = next } });
    }

    private static MarkupOverlayState PlaceAtCursor(MarkupOverlayState markup)
    {
        MarkupAnnotationModel model = markup.ActiveTool switch
        {
            MarkupKind.Arrow => markup.Model.Add(
                MarkupKind.Arrow, markup.Cursor, markup.Cursor.Shift(0.15, 0.1)),
            MarkupKind.Rectangle => markup.Model.Add(
                MarkupKind.Rectangle, markup.Cursor.Shift(-0.075, -0.05), markup.Cursor.Shift(0.075, 0.05)),
            _ => string.IsNullOrEmpty(markup.PendingText)
                ? markup.Model
                : markup.Model.Add(MarkupKind.Text, markup.Cursor, markup.Cursor, text: markup.PendingText),
        };

        if (ReferenceEquals(model, markup.Model))
        {
            return markup;
        }

        var next = markup with { Model = model, Error = string.Empty };
        return markup.ActiveTool == MarkupKind.Text ? next with { PendingText = string.Empty } : next;
    }

    private static MarkupOverlayState SelectAtPoint(MarkupOverlayState markup, NormalizedPoint point)
    {
        int? hit = MarkupOverlayState.HitTest(markup.Model, point);
        return hit is { } id
            ? markup with { Model = markup.Model.Select(id) }
            : markup with { Cursor = point };
    }

    private static MarkupOverlayState PressAtPoint(MarkupOverlayState markup, NormalizedPoint point)
    {
        int? hit = MarkupOverlayState.HitTest(markup.Model, point);
        if (hit is { } id)
        {
            return markup with
            {
                Model = markup.Model.Select(id).Checkpoint(),
                Draft = new MarkupDraft(point, point, Moving: true),
            };
        }

        return markup with { Draft = new MarkupDraft(point, point, Moving: false), Cursor = point };
    }

    private static MarkupOverlayState DragToPoint(MarkupOverlayState markup, NormalizedPoint point)
    {
        if (markup.Draft is not { } draft)
        {
            return markup;
        }

        if (draft.Moving)
        {
            var moved = markup.Model.NudgeSelected(point.X - draft.Current.X, point.Y - draft.Current.Y);
            return markup with { Model = moved, Draft = draft with { Current = point } };
        }

        return markup with { Draft = draft with { Current = point } };
    }

    private static MarkupOverlayState ReleaseAtPoint(MarkupOverlayState markup, NormalizedPoint point)
    {
        if (markup.Draft is not { } draft)
        {
            return markup;
        }

        if (draft.Moving)
        {
            // The press recorded the checkpoint; the drags were transient, so
            // the whole gesture undoes in one step. A press+release without
            // motion still spent a frame — collapse it so empty drags leave
            // no undo trace.
            var model = draft.Current.Equals(draft.Anchor)
                ? markup.Model.UndoFrame()
                : markup.Model.NudgeSelected(point.X - draft.Current.X, point.Y - draft.Current.Y);
            return markup with { Model = model, Draft = null };
        }

        MarkupAnnotationModel model2 = markup.ActiveTool switch
        {
            MarkupKind.Text when string.IsNullOrEmpty(markup.PendingText) => markup.Model,
            MarkupKind.Text => markup.Model.Add(MarkupKind.Text, draft.Anchor, draft.Anchor, text: markup.PendingText),
            _ => markup.Model.Add(markup.ActiveTool, draft.Anchor, point),
        };

        if (ReferenceEquals(model2, markup.Model))
        {
            return markup with { Draft = null };
        }

        var next = markup with { Model = model2, Draft = null, Cursor = point, Error = string.Empty };
        return markup.ActiveTool == MarkupKind.Text ? next with { PendingText = string.Empty } : next;
    }

    /// <summary>
    ///     Neighbour rule for a closed tab — documented once, pinned by
    ///     <c>TabStripReducerTests</c>: after removing the tab at
    ///     <paramref name="removedIndex" />, focus lands on the tab that slid
    ///     into that slot (the one on its right); when the closed tab was last
    ///     there is nothing on the right, so focus falls back to the tab on its
    ///     left; with no tabs left nothing is focused.
    /// </summary>
    public static SessionId? TabNeighbourAfterClose(ImmutableArray<SessionTab> remaining, int removedIndex)
    {
        if (remaining.Length == 0)
            return null;
        int target = removedIndex < remaining.Length ? removedIndex : remaining.Length - 1;
        return remaining[target].SessionId;
    }

    private static ReduceResult CycleTab(UiState state, bool forward)
    {
        var strip = state.Chat.TabStrip;
        var tabs = strip.Tabs;
        if (tabs.Length < 2)
            return ReduceResult.NoOp(state);

        // No active tab yet → the first tab going forward, the last going back.
        int current = strip.ActiveTabId is { } active ? strip.IndexOf(active) : -1;
        int target = current < 0
            ? (forward ? 0 : tabs.Length - 1)
            : (forward ? current + 1 : current - 1 + tabs.Length) % tabs.Length;
        return ActivateTab(state, tabs[target].SessionId);
    }

    /// <summary>
    ///     Panel-ownership rule — documented once: a panel dies with the tab only
    ///     when that tab was its <i>sole</i> owner, in which case it goes
    ///     <see cref="TuiPanelState.Hidden" /> and gives up focus (chat takes it
    ///     back). A panel still listed by a surviving tab is <i>reassigned</i> to
    ///     the nearest surviving owner in tab order and keeps its state, because
    ///     panel state is global while ownership is per tab. Ids the host never
    ///     seeded into <see cref="UiState.PanelStates" /> are ignored.
    /// </summary>
    private static UiState ReleaseOwnedPanels(UiState state, SessionTab closed, ImmutableArray<SessionTab> survivors)
    {
        var owned = closed.PanelIds;
        if (owned.Length == 0 || state.Ui.PanelStates.Count == 0)
            return state;

        var panelStates = state.Ui.PanelStates;
        string? focused = state.Ui.FocusedPanelId;
        bool changed = false;

        for (int i = 0; i < owned.Length; i++)
        {
            string id = owned[i];
            if (string.IsNullOrEmpty(id) || !panelStates.ContainsKey(id))
                continue;
            if (IsOwnedBy(survivors, id))
                continue;

            if (panelStates[id] != TuiPanelState.Hidden)
            {
                panelStates = panelStates.SetItem(id, TuiPanelState.Hidden);
                changed = true;
            }
            if (string.Equals(focused, id, StringComparison.Ordinal))
            {
                focused = null;
                changed = true;
            }
        }

        if (!changed)
            return state;
        return state with { Ui = state.Ui with { PanelStates = panelStates, FocusedPanelId = focused } };
    }

    private static bool IsOwnedBy(ImmutableArray<SessionTab> tabs, string panelId)
    {
        for (int i = 0; i < tabs.Length; i++)
        {
            var owned = tabs[i].PanelIds;
            for (int j = 0; j < owned.Length; j++)
            {
                if (string.Equals(owned[j], panelId, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    ///     Session-id equality by value — <see cref="SessionId" /> is a
    ///     reference-typed value object, so <c>==</c> would compare references and
    ///     miss a tab built from an equal-but-distinct id.
    /// </summary>
    private static bool SameSession(SessionId? left, SessionId right) =>
        left is not null && string.Equals(left.Value, right.Value, StringComparison.Ordinal);
}

/// <summary>
///     <see cref="IAppReducerPlugin" /> adapter over the stateless
///     <see cref="ChatAppReducer" /> statics. This is the object
///     <see cref="AppReducer.Update" /> consults, so a host can add its own
///     <see cref="IAppReducerPlugin" /> without forking the generic reducer
///     (#33/T4, #364).
/// </summary>
public sealed class ChatAppReducerPlugin : IAppReducerPlugin
{
    /// <summary>The single shared instance (the adapter holds no state).</summary>
    public static readonly ChatAppReducerPlugin Instance = new();

    private ChatAppReducerPlugin()
    {
    }

    /// <inheritdoc />
    public ReduceResult? Reduce(UiState state, AppMsg msg) => ChatAppReducer.ReduceMessage(state, msg);

    /// <inheritdoc />
    public bool IsBusy(UiState state) => ChatAppReducer.IsBusy(state);

    /// <inheritdoc />
    public UiState After(UiState state, AppMsg msg) => ChatAppReducer.After(state, msg);
}
