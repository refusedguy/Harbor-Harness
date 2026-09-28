using System.Collections.Immutable;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Ui.Framework.Panels;
namespace Harbor.Ui.Framework.State;
/// <summary>
///     Pure reducer: <c>(UiState, AgentEvent) → UiState</c>. This is the single
///     place that maps agent activity into UI state. Every interactive renderer
///     funnels its events through this — there is no per-renderer
///     <c>switch (AgentEvent)</c> anywhere.
/// </summary>
/// <remarks>
///     <para>
///         The method is allocation-light and reflection-free. Deltas append to
///         <see cref="UiState.Active" /> buffers; on <see cref="MessageEndEvent" />
///         the buffers are folded into <see cref="UiState.Lines" />. Token accounting
///         is accumulated in <see cref="CostSnapshot" /> via <see cref="EstimateCost" />.
///     </para>
///     <para>
///         Must never call into <c>IAgent</c> or perform I/O — side-effects are the
///         responsibility of <see cref="ITuiEffectRunner" /> (see <see cref="Classify" />).
///     </para>
/// </remarks>
public static class UiReducer
{
    /// <summary>Default per-million-token pricing (USD). Override via model table if available.</summary>
    private const decimal InputPricePerMillion = 3m;
    private const decimal OutputPricePerMillion = 15m;

    /// <summary>
    ///     Apply an agent event to the UI state, returning the next immutable snapshot.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="AgentStartEvent" /> and <see cref="AgentEndEvent" /> both
    ///         snapshot <see cref="UiState.IsAgentRunning" /> into
    ///         <see cref="UiState.WasRunning" /> before flipping it, so renderers can
    ///         detect the rising edge (<c>IsAgentRunning &amp;&amp; !WasRunning</c>)
    ///         without keeping local mutable state. A new run also pins scroll to the
    ///         live tail so streaming output is always visible (§FP-005 TEA fix).
    ///     </para>
    /// </remarks>
    public static UiState Reduce(UiState state, AgentEvent @event) => @event switch
    {
        AgentStartEvent ase => OnAgentStart(state, ase),
        MessageStartEvent => OnMessageStart(state),
        MessageUpdateEvent mu => OnMessageUpdate(state, mu),
        MessageEndEvent => OnMessageEnd(state),
        ToolExecutionStartEvent tes => state.AddLine(ChatRole.Tool, FormatToolStart(tes), tes.ToolCallId),
        ToolExecutionEndEvent tee => state.AddLine(ChatRole.ToolResult, FormatToolEnd(tee), tee.ToolCallId),
        CompactionStartedEvent => state with { Chat = state.Chat with { Status = "compacting" } },
        CompactionCompletedEvent cc => OnCompactionCompleted(state, cc),
        AgentErrorEvent err => state
            .AddLine(ChatRole.Error, err.Message)
            .WithStatus("error"),
        AgentEndEvent => state with
        {
            Chat = state.Chat with
            {
                // Mirror OnAgentEnded: a preceding AgentErrorEvent leaves
                // "error" behind; a blind reset to idle would repaint a
                // failed run as a clean finish.
                Status = state.Status == "error" ? "error" : "idle",
                IsAgentRunning = false,
                WasRunning = state.IsAgentRunning,
                IsStreaming = false,
                Active = ActiveMessage.Empty,
                PendingStreamText = ChunkedBuffer.Empty,
                PendingStreamThink = ChunkedBuffer.Empty
            }
        },
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
                WasRunning = state.IsAgentRunning
            },
            Ui = state.Ui with { ScrollOffset = 0 }
        };
        if (next.Lines.Length != 0)
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

    private static UiState ReplayMessage(UiState state, AgentMessage m) => m switch
    {
        UserMessage u => state.AddLine(ChatRole.User, u.Content),
        AssistantMessage a => ReplayAssistant(state, a),
        ToolResultMessage tr => ReplayResults(state, tr),
        _ => state
    };

    private static UiState ReplayAssistant(UiState state, AssistantMessage a)
    {
        foreach (var part in a.Parts)
        {
            switch (part)
            {
                case TextPart t when !string.IsNullOrWhiteSpace(t.Text):
                    state = state.AddLine(ChatRole.Assistant, t.Text);
                    break;
                case ThinkingPart th when !string.IsNullOrWhiteSpace(th.Text):
                    state = state.AddLine(ChatRole.Thinking, th.Text);
                    break;
                case ToolCallPart tc:
                    state = state.AddLine(ChatRole.Tool, $"→ {tc.ToolName}", tc.Id);
                    break;
            }
        }

        return state;
    }

    private static UiState ReplayResults(UiState state, ToolResultMessage tr)
    {
        foreach (var r in tr.Results)
        {
            state = state.AddLine(ChatRole.ToolResult, r.Output, r.ToolCallId);
        }

        return state;
    }

    private static UiState OnMessageStart(UiState state) =>
        state with
        {
            Chat = state.Chat with
            {
                Status = "running",
                IsAgentRunning = true,
                IsStreaming = true,
                Active = ActiveMessage.Empty,
                PendingStreamText = ChunkedBuffer.Empty,
                PendingStreamThink = ChunkedBuffer.Empty
            }
        };

    private static UiState OnMessageUpdate(UiState state, MessageUpdateEvent mu) => mu.LlmEvent switch
    {
        TextDeltaEvent td => WithTextDelta(state, td.Delta),
        ThinkingDeltaEvent thd => WithThinkingDelta(state, thd.Delta),
        ToolCallStartEvent tcs => FlushPending(state).AddLine(ChatRole.Tool, $"→ {tcs.ToolName}", tcs.Id),
        StepFinishEvent sf when sf.Usage is not null => OnStepFinish(FlushPending(state), sf.Usage),
        _ => state
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

        ChunkedBuffer pending = state.PendingStreamText.Append(delta);
        if (!StreamingSync.ShouldFlush(state.Active.TextBuffer.Length, pending.Length))
            return state with { Chat = state.Chat with { PendingStreamText = pending } };

        string full = StreamingSync.Concat(state.Active.TextBuffer, pending);
        return state with
        {
            Chat = state.Chat with
            {
                Active = state.Active with { TextBuffer = full },
                PendingStreamText = ChunkedBuffer.Empty
            }
        };
    }

    /// <summary>Thinking-delta counterpart of <see cref="WithTextDelta" />.</summary>
    private static UiState WithThinkingDelta(UiState state, string delta)
    {
        if (string.IsNullOrEmpty(delta))
            return state;

        ChunkedBuffer pending = state.PendingStreamThink.Append(delta);
        if (!StreamingSync.ShouldFlush(state.Active.ThinkBuffer.Length, pending.Length))
            return state with { Chat = state.Chat with { PendingStreamThink = pending } };

        string full = StreamingSync.Concat(state.Active.ThinkBuffer, pending);
        return state with
        {
            Chat = state.Chat with
            {
                Active = state.Active with { ThinkBuffer = full },
                PendingStreamThink = ChunkedBuffer.Empty
            }
        };
    }

    /// <summary>
    ///     Materialize any pending chunks into the synced
    ///     <see cref="UiState.Active" /> buffer strings so pause points
    ///     (tool calls, step finish, message end) observe the complete text.
    /// </summary>
    private static UiState FlushPending(UiState state)
    {
        if (state.PendingStreamText.Length == 0 && state.PendingStreamThink.Length == 0)
            return state;

        return state with
        {
            Chat = state.Chat with
            {
                Active = state.Active with
                {
                    TextBuffer = StreamingSync.Concat(state.Active.TextBuffer, state.PendingStreamText),
                    ThinkBuffer = StreamingSync.Concat(state.Active.ThinkBuffer, state.PendingStreamThink)
                },
                PendingStreamText = ChunkedBuffer.Empty,
                PendingStreamThink = ChunkedBuffer.Empty
            }
        };
    }

    private static UiState OnStepFinish(UiState state, Usage usage)
    {
        long nextIn = state.Cost.TokensIn + usage.InputTokens;
        long nextOut = state.Cost.TokensOut + usage.OutputTokens;
        return state with
        {
            Chat = state.Chat with
            {
                Cost = new CostSnapshot(
                    nextIn,
                    nextOut,
                    state.Cost.CostUsd + EstimateCost(usage.InputTokens, usage.OutputTokens))
            }
        };
    }

    private static UiState OnMessageEnd(UiState state)
    {
        var next = FlushPending(state);
        if (!string.IsNullOrEmpty(next.Active.ThinkBuffer))
            next = next.AddLine(ChatRole.Thinking, next.Active.ThinkBuffer.Trim());
        if (!string.IsNullOrEmpty(next.Active.TextBuffer))
            next = next.AddLine(ChatRole.Assistant, next.Active.TextBuffer.Trim());
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

    // ── formatting helpers (escaping) ─────────────────────────────────────

    private static string FormatToolStart(ToolExecutionStartEvent tes)
    {
        string args = tes.Args.GetRawText();
        return string.IsNullOrEmpty(args) || args == "{}"
            ? $"→ {tes.ToolName}"
            : $"→ {tes.ToolName}  {args}";
    }

    private static string FormatToolEnd(ToolExecutionEndEvent tee)
    {
        string label = tee.IsError ? "✗" : "✓";
        string output = tee.Result.Output ?? string.Empty;
        string preview = output.Length > 600 ? output[..600] + "..." : output;
        return $"{label} {preview.Trim()}";
    }

    private static decimal EstimateCost(int inputTokens, int outputTokens) =>
        inputTokens / 1_000_000m * InputPricePerMillion + outputTokens / 1_000_000m * OutputPricePerMillion;

    private static UiState WithStatus(this UiState state, string status) =>
        state with { Chat = state.Chat with { Status = status } };

    // ── unified update (TEA "update") ──────────────────────────────────────

    /// <summary>
    ///     The single update function for the interactive UI. Maps any
    ///     <see cref="UiMsg" /> to the next immutable <see cref="UiState" /> plus an
    ///     optional <see cref="TuiEffect" /> for the host to run. This is the ONLY
    ///     place where key input, focus, scroll, and input-editing transitions live.
    /// </summary>
    public static (UiState State, TuiEffect Effect) Update(UiState state, UiMsg msg) => msg switch
    {
        UiMsg.Agent a => (Reduce(state, a.Event), new TuiEffect.None()),
        UiMsg.AgentStarted => (state with { Chat = state.Chat with { Status = "running", IsAgentRunning = true } }, new TuiEffect.None()),
        UiMsg.AgentEnded ae => (OnAgentEnded(state, ae), new TuiEffect.None()),
        UiMsg.StatusChanged sc => (state with { Chat = state.Chat with { Status = sc.Status } }, new TuiEffect.None()),
        UiMsg.ConfigureRuntime cr => (state with { Chat = state.Chat with { Model = cr.Model, Provider = cr.Provider, AgentName = cr.AgentName } }, new TuiEffect.None()),
        UiMsg.AppendLine al => (state.AddLine(al.Role, al.Text, al.ToolCallId), new TuiEffect.None()),
        UiMsg.HydrateSession h => (HydrateSession(state, h), new TuiEffect.None()),
        UiMsg.InputText it => (state.SetInput(state.Input.SetText(it.Text)), new TuiEffect.None()),
        UiMsg.Quit => (state with { Ui = state.Ui with { ShouldQuit = true } }, new TuiEffect.None()),
        UiMsg.Reset => (new UiState(), new TuiEffect.None()),
        UiMsg.KeyInput k => UpdateKey(state, k),
        UiMsg.Viewport v => (state with { Ui = state.Ui with { ViewportLines = v.HistoryHeight } }, new TuiEffect.None()),
        UiMsg.HistoryMeasured t => (state with { Ui = state.Ui with { TotalLines = t.TotalLines } }, new TuiEffect.None()),
        UiMsg.TogglePanel tp => (TogglePanel(state, tp.Id), new TuiEffect.None()),
        UiMsg.FocusPanel fp => (FocusPanel(state, fp.Id), new TuiEffect.None()),
        UiMsg.CyclePanelFocus => (CycleFocus(state), new TuiEffect.None()),
        UiMsg.ResizePanel rp => (ResizePanel(state, rp.Id, rp.Delta), new TuiEffect.None()),
        UiMsg.ScrollResetToTail => (state with
        {
            Ui = state.Ui with { ScrollOffset = 0 },
            // Snapshot, don't force: fabricating WasRunning=true breaks the
            // rising-edge invariant (IsRunning && !WasRunning) that tells
            // renderers a run just started (e.g. to snap to tail).
            Chat = state.Chat with { WasRunning = state.IsAgentRunning }
        }, new TuiEffect.None()),
        UiMsg.ScrollClamp sc => (state with
        {
            Ui = state.Ui with
            {
                ScrollOffset = Math.Clamp(state.ScrollOffset, 0, Math.Max(0, sc.MaxScroll))
            }
        }, new TuiEffect.None()),
        UiMsg.SeedPanels sp => (state with
        {
            Ui = state.Ui with
            {
                RegisteredPanelIds = sp.Ids,
                PanelStates = sp.States,
                PanelSizes = sp.Sizes
            }
        }, new TuiEffect.None()),
        UiMsg.SyncSessions ss => (state with
        {
            Chat = state.Chat with
            {
                Sessions = ss.Sessions,
                ActiveSessionId = ss.ActiveSessionId
            }
        }, new TuiEffect.None()),
        UiMsg.SetPanelCursor pc => (SetPanelCursor(state, pc.Id, pc.Cursor), new TuiEffect.None()),
        UiMsg.SetPanelDirectory pd => (SetPanelDirectory(state, pd.Id, pd.Directory), new TuiEffect.None()),
        UiMsg.OpenTab ot => OpenTab(state, ot.Tab),
        UiMsg.ActivateTab at => ActivateTab(state, at.SessionId),
        UiMsg.CloseTab ct => CloseTab(state, ct.SessionId),
        UiMsg.CloseOtherTabs co => CloseOtherTabs(state, co.Keep),
        UiMsg.CloseTabsToRight ctr => CloseTabsToRight(state, ctr.From),
        UiMsg.PinTab pt => (PinTab(state, pt.SessionId, pt.Pinned), new TuiEffect.None()),
        UiMsg.ReorderTab ro => (ReorderTab(state, ro.SessionId, ro.ToIndex), new TuiEffect.None()),
        UiMsg.CycleNextTab => CycleNextTab(state),
        UiMsg.CyclePreviousTab => CyclePreviousTab(state),
        _ => (state, new TuiEffect.None())
    };

    /// <summary>
    ///     Atomic session hydration (#89). Folds Reset + session-chrome bind +
    ///     history replay into one pure transition so the swap rides a single
    ///     store CAS: a concurrent background event applies strictly before
    ///     (superseded by the fresh state) or after (appended in order), never
    ///     interleaved mid-history.
    /// </summary>
    private static UiState HydrateSession(UiState state, UiMsg.HydrateSession h)
    {
        var next = new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Model = h.Model,
                Provider = h.Provider,
                AgentName = h.AgentName,
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
    ///     Run-end fold for the effect host. A null <see cref="UiMsg.AgentEnded.Status" />
    ///     keeps a previously set <c>"error"</c> status (so a failed run does not get
    ///     repainted as a clean finish by the host's finally block) and otherwise
    ///     falls back to <c>"idle"</c>.
    /// </summary>
    private static UiState OnAgentEnded(UiState state, UiMsg.AgentEnded msg)
    {
        var next = state with
        {
            IsAgentRunning = false,
            IsStreaming = false,
            Active = ActiveMessage.Empty,
            Status = msg.Status ?? (state.Status == "error" ? "error" : "idle")
        };
        return msg.Error is null ? next : next.AddLine(ChatRole.Error, msg.Error);
    }

    // ── panel transitions (pure; no IPanelRegistry dependency) ────────────

    /// <summary>Toggle a panel between Hidden ↔ Visible (Focused → Hidden also clears focus).</summary>
    public static UiState TogglePanel(UiState state, string id)
    {
        if (string.IsNullOrEmpty(id) || !state.PanelStates.ContainsKey(id))
            return state;

        var current = state.PanelStates[id];
        var next = current == TuiPanelState.Hidden ? TuiPanelState.Visible : TuiPanelState.Hidden;
        var states = state.PanelStates.SetItem(id, next);

        string? focused = state.FocusedPanelId;
        if (next == TuiPanelState.Hidden && focused == id)
            focused = null;

        return state with { Ui = state.Ui with { PanelStates = states, FocusedPanelId = focused } };
    }

    /// <summary>Focus a specific panel (or chat when <paramref name="id" /> is null).</summary>
    public static UiState FocusPanel(UiState state, string? id)
    {
        if (id is null)
        {
            // Demote any Focused panel back to Visible.
            var states = state.PanelStates;
            if (state.FocusedPanelId is { } prev && states.ContainsKey(prev))
                states = states.SetItem(prev, TuiPanelState.Visible);
            return state with { Ui = state.Ui with { PanelStates = states, FocusedPanelId = null } };
        }

        if (!state.PanelStates.ContainsKey(id))
            return state;

        var next = state.PanelStates;
        if (state.FocusedPanelId is { } prevFocused && next.ContainsKey(prevFocused) && prevFocused != id)
            next = next.SetItem(prevFocused, TuiPanelState.Visible);
        // Make sure the target is at least Visible before focusing.
        if (next[id] == TuiPanelState.Hidden)
            next = next.SetItem(id, TuiPanelState.Visible);
        next = next.SetItem(id, TuiPanelState.Focused);

        return state with { Ui = state.Ui with { PanelStates = next, FocusedPanelId = id } };
    }

    /// <summary>
    ///     Cycle focus to the next visible panel in registration order. If the last
    ///     visible panel is already focused, returns focus to chat (null).
    /// </summary>
    public static UiState CycleFocus(UiState state)
    {
        var visible = state.RegisteredPanelIds
            .Where(id => state.PanelStates.TryGetValue(id, out var s) && s != TuiPanelState.Hidden)
            .ToList();
        if (visible.Count == 0)
            return state.FocusedPanelId is null ? state : FocusPanel(state, null);

        int idx = visible.IndexOf(state.FocusedPanelId ?? string.Empty);
        int nextIdx = idx < 0 ? 0 : (idx + 1) % visible.Count;
        if (idx >= 0 && nextIdx == 0)
            return FocusPanel(state, null);

        return FocusPanel(state, visible[nextIdx]);
    }

    /// <summary>Grow or shrink a panel by <paramref name="delta" />, clamped to [2..200].</summary>
    public static UiState ResizePanel(UiState state, string id, int delta)
    {
        if (string.IsNullOrEmpty(id) || !state.PanelStates.ContainsKey(id) || delta == 0)
            return state;
        int current = state.PanelSizes.TryGetValue(id, out int s) ? s : 0;
        int next = Math.Clamp(current + delta, PanelRegistry.MinSize, PanelRegistry.MaxSize);
        return state with { Ui = state.Ui with { PanelSizes = state.PanelSizes.SetItem(id, next) } };
    }

    /// <summary>
    ///     Store a panel-local cursor keyed by panel id (#360). Pure: no I/O,
    ///     no clamping against content (the provider clamps for display and on
    ///     write using the live item count). Unknown/empty ids still record so
    ///     null-store-degraded providers converge on the next seeded frame.
    /// </summary>
    public static UiState SetPanelCursor(UiState state, string id, int cursor)
    {
        if (string.IsNullOrEmpty(id))
            return state;
        int next = Math.Max(0, cursor);
        if (state.Ui.PanelCursors.TryGetValue(id, out int current) && current == next)
            return state;
        return state with { Ui = state.Ui with { PanelCursors = state.Ui.PanelCursors.SetItem(id, next) } };
    }

    /// <summary>
    ///     Store a panel-local directory keyed by panel id and reset its cursor
    ///     to 0 atomically (#360). The filesystem listing itself stays a
    ///     provider-local cache — the reducer never touches the disk.
    /// </summary>
    public static UiState SetPanelDirectory(UiState state, string id, string directory)
    {
        if (string.IsNullOrEmpty(id))
            return state;
        string dir = directory ?? string.Empty;
        var ui = state.Ui;
        bool sameDir = ui.PanelDirs.TryGetValue(id, out string? current) ? current == dir : dir == string.Empty;
        bool cursorZero = !ui.PanelCursors.TryGetValue(id, out int cur) || cur == 0;
        if (sameDir && cursorZero)
            return state;
        return state with
        {
            Ui = ui with
            {
                PanelDirs = ui.PanelDirs.SetItem(id, dir),
                PanelCursors = ui.PanelCursors.SetItem(id, 0)
            }
        };
    }

    // ── tab transitions (#388, slice 1/3 — pure; the host runs the effect) ──

    /// <summary>
    ///     Open a tab at the right end of the strip and focus it, emitting
    ///     <see cref="TuiEffect.ActivateSession" /> so the host switches session.
    ///     <b>Idempotent:</b> a session that already has a tab is activated
    ///     instead of duplicated, and its position and fields are left alone.
    /// </summary>
    public static (UiState State, TuiEffect Effect) OpenTab(UiState state, SessionTab tab)
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
        return (next, new TuiEffect.ActivateSession(tab.SessionId));
    }

    /// <summary>
    ///     Focus an already-open tab and ask the host to switch to its session.
    ///     Order is untouched (activating never reorders). Unknown session or
    ///     already-active tab → same state instance and no effect, so the store
    ///     does not bump the revision on a no-op.
    /// </summary>
    public static (UiState State, TuiEffect Effect) ActivateTab(UiState state, SessionId sessionId)
    {
        var strip = state.Chat.TabStrip;
        if (strip.IndexOf(sessionId) < 0)
            return (state, new TuiEffect.None());
        if (SameSession(strip.ActiveTabId, sessionId))
            return (state, new TuiEffect.None());

        var next = state with { Chat = state.Chat with { TabStrip = strip with { ActiveTabId = sessionId } } };
        return (next, new TuiEffect.ActivateSession(sessionId));
    }

    /// <summary>
    ///     Close a tab, applying the neighbour rule
    ///     (<see cref="TabNeighbourAfterClose" />) when the active tab was the
    ///     one closed, and the panel-ownership rule
    ///     (<see cref="ReleaseOwnedPanels" />).
    /// </summary>
    public static (UiState State, TuiEffect Effect) CloseTab(UiState state, SessionId sessionId)
    {
        var strip = state.Chat.TabStrip;
        var tabs = strip.Tabs;
        int index = strip.IndexOf(sessionId);
        if (index < 0)
            return (state, new TuiEffect.None());

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
        return (next, effect);
    }

    /// <summary>
    ///     Close every tab except <paramref name="keep" /> and focus the
    ///     survivor. The survivor owns the focus unconditionally — it is the tab
    ///     the gesture was invoked on, so the strip must not end up pointing at a
    ///     tab that no longer exists. The effect fires only when the active tab
    ///     actually changed.
    /// </summary>
    public static (UiState State, TuiEffect Effect) CloseOtherTabs(UiState state, SessionId keep)
    {
        var strip = state.Chat.TabStrip;
        var tabs = strip.Tabs;
        int keepIndex = strip.IndexOf(keep);
        if (keepIndex < 0 || tabs.Length == 1)
            return (state, new TuiEffect.None());

        var remaining = ImmutableArray.Create(tabs[keepIndex]);
        var released = state;
        for (int i = 0; i < tabs.Length; i++)
        {
            if (i != keepIndex)
                released = ReleaseOwnedPanels(released, tabs[i], remaining);
        }

        var next = released with
        {
            Chat = released.Chat with { TabStrip = strip with { Tabs = remaining, ActiveTabId = keep } }
        };
        TuiEffect effect = SameSession(strip.ActiveTabId, keep)
            ? new TuiEffect.None()
            : new TuiEffect.ActivateSession(keep);
        return (next, effect);
    }

    /// <summary>
    ///     Close every tab strictly to the right of <paramref name="from" /> and
    ///     focus <paramref name="from" />, mirroring
    ///     <see cref="CloseOtherTabs" />'s focus and effect rules. Closing the
    ///     last tab to the right is a no-op.
    /// </summary>
    public static (UiState State, TuiEffect Effect) CloseTabsToRight(UiState state, SessionId from)
    {
        var strip = state.Chat.TabStrip;
        var tabs = strip.Tabs;
        int index = strip.IndexOf(from);
        if (index < 0 || index == tabs.Length - 1)
            return (state, new TuiEffect.None());

        var remaining = tabs.RemoveRange(index + 1, tabs.Length - index - 1);
        var released = state;
        for (int i = index + 1; i < tabs.Length; i++)
            released = ReleaseOwnedPanels(released, tabs[i], remaining);

        var next = released with
        {
            Chat = released.Chat with { TabStrip = strip with { Tabs = remaining, ActiveTabId = from } }
        };
        TuiEffect effect = SameSession(strip.ActiveTabId, from)
            ? new TuiEffect.None()
            : new TuiEffect.ActivateSession(from);
        return (next, effect);
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
    public static (UiState State, TuiEffect Effect) CycleNextTab(UiState state) => CycleTab(state, forward: true);

    /// <summary>Focus the previous tab in tab order (wraps around; no-op with fewer than two tabs).</summary>
    public static (UiState State, TuiEffect Effect) CyclePreviousTab(UiState state) => CycleTab(state, forward: false);

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

    private static (UiState State, TuiEffect Effect) CycleTab(UiState state, bool forward)
    {
        var strip = state.Chat.TabStrip;
        var tabs = strip.Tabs;
        if (tabs.Length < 2)
            return (state, new TuiEffect.None());

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
        if (owned.Length == 0 || state.PanelStates.Count == 0)
            return state;

        var panelStates = state.PanelStates;
        string? focused = state.FocusedPanelId;
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

    private static (UiState State, TuiEffect Effect) UpdateKey(UiState state, UiMsg.KeyInput k)
    {
        // While the agent runs most input is suppressed, but a useful subset must still
        // pass through: abort, scroll, and focus toggle. Everything that edits/commits
        // input is blocked so the running prompt is never disturbed.
        if (state.IsAgentRunning)
        {
            // Escape during a run is an abort (not a quit) — see keymap note below.
            if (k.Action is ChatAction.Quit or ChatAction.Abort)
                return TransitionAbort(state);

            if (k.Action is ChatAction.ScrollUpLine or ChatAction.ScrollDownLine
                or ChatAction.ScrollUpPage or ChatAction.ScrollDownPage
                or ChatAction.ScrollTop or ChatAction.ScrollBottom
                or ChatAction.ToggleFocus)
                return UpdateKeyAllowed(state, k);

            return (state, new TuiEffect.None());
        }

        return UpdateKeyAllowed(state, k);
    }

    private static (UiState State, TuiEffect Effect) UpdateKeyAllowed(UiState state, UiMsg.KeyInput k)
    {
        switch (k.Action)
        {
            case ChatAction.Quit:
                return (state, new TuiEffect.QuitApp());

            case ChatAction.Abort:
                return TransitionAbort(state);

            case ChatAction.Submit:
            {
                (var nextInput, string? submitted) = state.Input.Consume();
                var next = state.SetInput(nextInput);
                if (submitted is null)
                    return (next, new TuiEffect.None());

                return ClassifySubmit(next, submitted);
            }

            case ChatAction.ToggleFocus:
                return (state.SetFocus(state.Focus == FocusMode.Input ? FocusMode.Chat : FocusMode.Input),
                    new TuiEffect.None());

            // Scrolling works in both focus modes; the wheel arrives as PageUp/PageDown.
            case ChatAction.ScrollUpLine:
                return (state.SetScroll(state.ScrollOffset + 1), new TuiEffect.None());
            case ChatAction.ScrollDownLine:
                return (state.SetScroll(state.ScrollOffset - 1), new TuiEffect.None());
            case ChatAction.ScrollUpPage:
                return (state.SetScroll(state.ScrollOffset + Math.Max(1, state.ViewportLines - 2)), new TuiEffect.None());
            case ChatAction.ScrollDownPage:
                return (state.SetScroll(state.ScrollOffset - Math.Max(1, state.ViewportLines - 2)), new TuiEffect.None());
            case ChatAction.ScrollTop:
                return (state.SetScroll(int.MaxValue), new TuiEffect.None());
            case ChatAction.ScrollBottom:
                return (state.SetScroll(0), new TuiEffect.None());

            // Input editing only when the input box owns focus.
            case ChatAction.Backspace:
                return state.Focus == FocusMode.Input
                    ? (state.SetInput(InputMsg.Update(state.Input, new InputMsg.Backspace())), new TuiEffect.None())
                    : (state, new TuiEffect.None());
            case ChatAction.InputHistoryPrev:
                return state.Focus == FocusMode.Input
                    ? (state.SetInput(InputMsg.Update(state.Input, new InputMsg.HistoryUp())), new TuiEffect.None())
                    : (state, new TuiEffect.None());
            case ChatAction.InputHistoryNext:
                return state.Focus == FocusMode.Input
                    ? (state.SetInput(InputMsg.Update(state.Input, new InputMsg.HistoryDown())), new TuiEffect.None())
                    : (state, new TuiEffect.None());
            case ChatAction.Autocomplete:
                return state.Focus == FocusMode.Input && state.Input.Text.StartsWith('/')
                    ? (state.SetInput(InputMsg.Update(state.Input,
                        new InputMsg.Autocomplete(TuiEffectHost.KnownSlashCommands))), new TuiEffect.None())
                    : (state, new TuiEffect.None());
            case ChatAction.Char:
                return state.Focus == FocusMode.Input && k.Pressed.Character is { } c
                    ? (state.SetInput(InputMsg.Update(state.Input, new InputMsg.Char(c))), new TuiEffect.None())
                    : (state, new TuiEffect.None());
            case ChatAction.InsertNewline:
                // Enter-family decision lives here (#359): Shift/Alt+Enter
                // appends '\n' (same as typing it). Ctrl combos resolve here
                // only via subset matching — the composer ignores Ctrl+Enter,
                // so the store drops it too instead of inserting a newline.
                return state.Focus == FocusMode.Input && !k.Pressed.Has(KeyModifierSet.Ctrl)
                    ? (state.SetInput(InputMsg.Update(state.Input, new InputMsg.Char('\n'))), new TuiEffect.None())
                    : (state, new TuiEffect.None());

            case ChatAction.Clear:
                return (state.ClearTranscript(), new TuiEffect.None());

            // Panel actions (epic C step 2): resolved actions land on the
            // existing panel transitions — previously fell into default noop.
            case ChatAction.TogglePanelSlot:
                return (TogglePanelSlot(state, k), new TuiEffect.None());
            case ChatAction.CyclePanelFocus:
                return (CycleFocus(state), new TuiEffect.None());
            case ChatAction.ClosePanel:
                return (FocusPanel(state, null), new TuiEffect.None());
            case ChatAction.ResizePanelGrow:
                return (ResizeFocusedPanel(state, +1), new TuiEffect.None());
            case ChatAction.ResizePanelShrink:
                return (ResizeFocusedPanel(state, -1), new TuiEffect.None());
            case ChatAction.HelpPanel:
                return (TogglePanel(state, "help"), new TuiEffect.None());
            case ChatAction.ToggleLogsPanel:
                return (TogglePanel(state, "logs"), new TuiEffect.None());
            case ChatAction.JumpPalette:
                // Hosts register a "jump" panel (or overlay) rendering the
                // worktree jump palette; noop until one exists (TogglePanel
                // ignores unknown ids) so the key is safe on every renderer.
                return (TogglePanel(state, "jump"), new TuiEffect.None());

            case ChatAction.None:
            default:
                return (state, new TuiEffect.None());
        }
    }

    /// <summary>Alt+1..9: slot index from the pressed character.</summary>
    private static UiState TogglePanelSlot(UiState state, UiMsg.KeyInput k)
    {
        if (k.Pressed.Character is not { } c || c is < '1' or > '9')
            return state;
        int idx = c - '1';
        if (idx < 0 || idx >= state.RegisteredPanelIds.Length)
            return state;
        return TogglePanel(state, state.RegisteredPanelIds[idx]);
    }

    /// <summary>Grow/shrink the focused panel; noop when chat owns focus.</summary>
    private static UiState ResizeFocusedPanel(UiState state, int delta) =>
        state.FocusedPanelId is { } id ? ResizePanel(state, id, delta) : state;

    /// <summary>
    ///     Classify a submitted (already consumed) input line into the effect that
    ///     should run. Single source of truth shared by the reducer and the effect
    ///     host so exit words, slash commands, and prompts behave identically wherever
    ///     a line is submitted.
    /// </summary>
    private static (UiState State, TuiEffect Effect) ClassifySubmit(UiState state, string submitted)
    {
        string trimmed = submitted.Trim();
        if (ChatCommands.ExitWords.Contains(trimmed))
            return (state, new TuiEffect.QuitApp());

        if (trimmed.StartsWith('/'))
            return (state, new TuiEffect.RunSlash(trimmed));

        return (state.AddLine(ChatRole.User, submitted), new TuiEffect.PromptAgent(submitted));
    }

    /// <summary>
    ///     Start an abort: emit a plain system note and the host effect that cancels
    ///     the running agent. Partially streamed text is folded into the transcript
    ///     first (same as <see cref="OnMessageEnd" />) so the abort does not eat
    ///     already-received content; only then are the buffers cleared. Colour is
    ///     the renderer's responsibility (driven by <see cref="ChatRole" />), so the
    ///     text here is markup-free.
    /// </summary>
    private static (UiState State, TuiEffect Effect) TransitionAbort(UiState state)
    {
        var next = FlushPending(state);
        if (!string.IsNullOrEmpty(next.Active.ThinkBuffer))
            next = next.AddLine(ChatRole.Thinking, next.Active.ThinkBuffer.Trim());
        if (!string.IsNullOrEmpty(next.Active.TextBuffer))
            next = next.AddLine(ChatRole.Assistant, next.Active.TextBuffer.Trim());
        next = next
                .AddLine(ChatRole.System, "Aborted.")
            with
            {
                IsStreaming = false,
                Active = ActiveMessage.Empty
            };
        return (next, new TuiEffect.AbortAgent());
    }
}
