using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Abstractions.Panels;
namespace Harbor.Tui.Abstractions.State;
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
        ToolExecutionStartEvent tes => state.AddLine(ChatRole.Tool, FormatToolStart(tes)),
        ToolExecutionEndEvent tee => state.AddLine(ChatRole.ToolResult, FormatToolEnd(tee)),
        CompactionStartedEvent => state with { Status = "compacting" },
        CompactionCompletedEvent cc => OnCompactionCompleted(state, cc),
        AgentErrorEvent err => state
            .AddLine(ChatRole.Error, err.Message)
            .WithStatus("error"),
        AgentEndEvent => state with
        {
            Status = "idle",
            IsAgentRunning = false,
            WasRunning = state.IsAgentRunning,
            IsStreaming = false,
            Active = ActiveMessage.Empty
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
            Status = "running",
            IsAgentRunning = true,
            WasRunning = state.IsAgentRunning,
            ScrollOffset = 0
        };
        if (next.Lines.Length != 0)
            return next;

        foreach (var m in ase.Messages)
        {
            if (m is UserMessage u)
                next = next.AddLine(ChatRole.User, u.Content);
        }

        return next;
    }

    private static UiState OnMessageStart(UiState state) =>
        state with
        {
            Status = "running",
            IsAgentRunning = true,
            IsStreaming = true,
            Active = ActiveMessage.Empty
        };

    private static UiState OnMessageUpdate(UiState state, MessageUpdateEvent mu) => mu.LlmEvent switch
    {
        TextDeltaEvent td => state with { Active = state.Active with { TextBuffer = state.Active.TextBuffer + td.Delta } },
        ThinkingDeltaEvent thd => state with { Active = state.Active with { ThinkBuffer = state.Active.ThinkBuffer + thd.Delta } },
        ToolCallStartEvent tcs => state.AddLine(ChatRole.Tool, $"→ {tcs.ToolName}"),
        StepFinishEvent sf when sf.Usage is not null => OnStepFinish(state, sf.Usage),
        _ => state
    };

    private static UiState OnStepFinish(UiState state, Usage usage)
    {
        long nextIn = state.Cost.TokensIn + usage.InputTokens;
        long nextOut = state.Cost.TokensOut + usage.OutputTokens;
        return state with
        {
            Cost = new CostSnapshot(
                nextIn,
                nextOut,
                state.Cost.CostUsd + EstimateCost(usage.InputTokens, usage.OutputTokens))
        };
    }

    private static UiState OnMessageEnd(UiState state)
    {
        var next = state;
        if (!string.IsNullOrEmpty(next.Active.ThinkBuffer))
            next = next.AddLine(ChatRole.Thinking, next.Active.ThinkBuffer.Trim());
        if (!string.IsNullOrEmpty(next.Active.TextBuffer))
            next = next.AddLine(ChatRole.Assistant, next.Active.TextBuffer.Trim());
        return next with { IsStreaming = false, Active = ActiveMessage.Empty };
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
        state with { Status = status };

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
        UiMsg.KeyInput k => UpdateKey(state, k),
        UiMsg.Viewport v => (state with { ViewportLines = v.HistoryHeight }, new TuiEffect.None()),
        UiMsg.HistoryMeasured t => (state with { TotalLines = t.TotalLines }, new TuiEffect.None()),
        UiMsg.TogglePanel tp => (TogglePanel(state, tp.Id), new TuiEffect.None()),
        UiMsg.FocusPanel fp => (FocusPanel(state, fp.Id), new TuiEffect.None()),
        UiMsg.CyclePanelFocus => (CycleFocus(state), new TuiEffect.None()),
        UiMsg.ResizePanel rp => (ResizePanel(state, rp.Id, rp.Delta), new TuiEffect.None()),
        UiMsg.ScrollResetToTail => (state with { ScrollOffset = 0, WasRunning = true }, new TuiEffect.None()),
        UiMsg.ScrollClamp sc => (state with
        {
            ScrollOffset = Math.Clamp(state.ScrollOffset, 0, Math.Max(0, sc.MaxScroll))
        }, new TuiEffect.None()),
        UiMsg.SeedPanels sp => (state with
        {
            RegisteredPanelIds = sp.Ids,
            PanelStates = sp.States,
            PanelSizes = sp.Sizes
        }, new TuiEffect.None()),
        _ => (state, new TuiEffect.None())
    };

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

        return state with { PanelStates = states, FocusedPanelId = focused };
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
            return state with { PanelStates = states, FocusedPanelId = null };
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

        return state with { PanelStates = next, FocusedPanelId = id };
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
        int current = state.PanelSizes.TryGetValue(id, out var s) ? s : 0;
        int next = Math.Clamp(current + delta, PanelRegistry.MinSize, PanelRegistry.MaxSize);
        return state with { PanelSizes = state.PanelSizes.SetItem(id, next) };
    }

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

            case ChatAction.Clear:
                return (state.ClearTranscript(), new TuiEffect.None());

            case ChatAction.None:
            default:
                return (state, new TuiEffect.None());
        }
    }

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
    ///     the running agent. Streaming buffers are cleared so a half-rendered message
    ///     does not linger until <see cref="AgentEndEvent" /> arrives. Colour is the
    ///     renderer's responsibility (driven by <see cref="ChatRole" />), so the text
    ///     here is markup-free.
    /// </summary>
    private static (UiState State, TuiEffect Effect) TransitionAbort(UiState state)
    {
        var next = state
                .AddLine(ChatRole.System, "Aborted.")
            with
            {
                IsStreaming = false,
                Active = ActiveMessage.Empty
            };
        return (next, new TuiEffect.AbortAgent());
    }
}
