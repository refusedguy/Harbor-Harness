using System.Collections.Immutable;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
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
        ChatAppMsg.ReopenTab => ReopenTab(state),
        ChatAppMsg.CycleNextUnreadTab => CycleUnreadTab(state, forward: true),
        ChatAppMsg.CyclePreviousUnreadTab => CycleUnreadTab(state, forward: false),
        ChatAppMsg.ActivateTabSlot s => ActivateTabSlot(state, s.Slot),
        ChatAppMsg.MarkTabUnread m => ReduceResult.NoOp(MarkTabUnread(state, m.SessionId, m.IsError)),
        ChatAppMsg.MarkTabRead m => ReduceResult.NoOp(MarkTabRead(state, m.SessionId)),
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

    // ── agent-event fold (lives in AgentReducer, #95) ──────────────────────────
    //
    // Thin forwarders: the transitions live in AgentReducer as pure folds over
    // Chat. These stay so every existing call site, test and doc reference keeps
    // resolving with identical behaviour.

    /// <summary>Agent-event transition — implemented in <see cref="AgentReducer" />.</summary>
    public static UiState Reduce(UiState state, AgentEvent @event) =>
        AgentReducer.Reduce(state, @event);

    private static ReduceResult OnAgentEnded(UiState state, ChatAppMsg.AgentEnded msg) =>
        AgentReducer.OnAgentEnded(state, msg);

    private static UiState FlushPending(UiState state) =>
        AgentReducer.FlushPending(state);

    private static SessionStatus HostClosedRun(ChatDomainState chat, string? reportedStatus) =>
        AgentReducer.HostClosedRun(chat, reportedStatus);

    // ── session hydration (lives in SessionReducer, #95) ───────────────────
    //
    // Thin forwarder: the transition lives in SessionReducer as a pure fold
    // over Chat. This stays so every existing call site, test and doc reference
    // keeps resolving with identical behaviour.

    /// <summary>Session hydration — implemented in <see cref="SessionReducer" />.</summary>
    private static UiState HydrateSession(UiState state, ChatAppMsg.HydrateSession h) =>
        SessionReducer.HydrateSession(state, h);

    // ── chat-owned key arms ────────────────────────────────────────────────

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
            case ChatAction.ReopenTab:
                return ReopenTab(state);
            case ChatAction.NextUnreadTab:
                return CycleUnreadTab(state, forward: true);
            case ChatAction.PreviousUnreadTab:
                return CycleUnreadTab(state, forward: false);
            case ChatAction.ActivateTabSlot:
                return ActivateSlotFromKey(state, k.Pressed);
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
    ///     first (same as <see cref="AgentReducer.OnMessageEnd" />) so the abort does not eat
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

    // ── tab-strip transitions (#388/#390/#1173) ─────────────────────────────
    //
    // Thin forwarders: the transitions live in TabStripReducer (#95) as
    // pure folds over Chat.TabStrip. These stay so every existing call
    // site, test and doc reference keeps resolving with identical behaviour.

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static ReduceResult OpenTab(UiState state, SessionTab tab) =>
        TabStripReducer.OpenTab(state, tab);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static ReduceResult ActivateTab(UiState state, SessionId sessionId) =>
        TabStripReducer.ActivateTab(state, sessionId);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static ReduceResult CloseTab(UiState state, SessionId sessionId) =>
        TabStripReducer.CloseTab(state, sessionId);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static ReduceResult CloseOtherTabs(UiState state, SessionId keep) =>
        TabStripReducer.CloseOtherTabs(state, keep);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static ReduceResult CloseTabsToRight(UiState state, SessionId from) =>
        TabStripReducer.CloseTabsToRight(state, from);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static UiState PinTab(UiState state, SessionId sessionId, bool pinned) =>
        TabStripReducer.PinTab(state, sessionId, pinned);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static UiState ReorderTab(UiState state, SessionId sessionId, int toIndex) =>
        TabStripReducer.ReorderTab(state, sessionId, toIndex);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static ReduceResult CycleNextTab(UiState state) =>
        TabStripReducer.CycleNextTab(state);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static ReduceResult CyclePreviousTab(UiState state) =>
        TabStripReducer.CyclePreviousTab(state);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static ImmutableArray<ClosedTab> PushClosed(ImmutableArray<ClosedTab> stack, SessionTab tab, int index) =>
        TabStripReducer.PushClosed(stack, tab, index);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static ImmutableArray<ClosedTab> PruneClosed(ImmutableArray<ClosedTab> stack, SessionId sessionId) =>
        TabStripReducer.PruneClosed(stack, sessionId);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static ReduceResult ReopenTab(UiState state) =>
        TabStripReducer.ReopenTab(state);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static ReduceResult CycleUnreadTab(UiState state, bool forward) =>
        TabStripReducer.CycleUnreadTab(state, forward);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static ReduceResult ActivateTabSlot(UiState state, int slot) =>
        TabStripReducer.ActivateTabSlot(state, slot);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static ReduceResult ActivateSlotFromKey(UiState state, UiKey key) =>
        TabStripReducer.ActivateSlotFromKey(state, key);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static UiState MarkTabUnread(UiState state, SessionId sessionId, bool isError) =>
        TabStripReducer.MarkTabUnread(state, sessionId, isError);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static UiState MarkTabRead(UiState state, SessionId sessionId) =>
        TabStripReducer.MarkTabRead(state, sessionId);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static ReduceResult NextTabFromKey(UiState state) =>
        TabStripReducer.NextTabFromKey(state);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static ReduceResult PreviousTabFromKey(UiState state) =>
        TabStripReducer.PreviousTabFromKey(state);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static ReduceResult CloseFocusedTab(UiState state) =>
        TabStripReducer.CloseFocusedTab(state);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static ReduceResult RequestOpenTab(UiState state) =>
        TabStripReducer.RequestOpenTab(state);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static ReduceResult MoveFocusedTab(UiState state, int delta) =>
        TabStripReducer.MoveFocusedTab(state, delta);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static ReduceResult HydrateTabStrip(UiState state, ChatAppMsg.HydrateTabStrip h) =>
        TabStripReducer.HydrateTabStrip(state, h);

    /// <summary>Tab-strip transition — implemented in <see cref="TabStripReducer" />.</summary>
    public static SessionId? TabNeighbourAfterClose(ImmutableArray<SessionTab> remaining, int removedIndex) =>
        TabStripReducer.TabNeighbourAfterClose(remaining, removedIndex);

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
