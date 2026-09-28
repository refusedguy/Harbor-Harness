using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
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
///         Must never call into <c>IAgent</c> or perform I/O — side-effects are the
///         responsibility of <see cref="ITuiEffectRunner" /> (see
///         <see cref="ClassifySubmit" />).
///     </para>
/// </remarks>
public static class ChatAppReducer
{
    /// <summary>Default per-million-token pricing (USD). Override via model table if available.</summary>
    private const decimal InputPricePerMillion = 3m;
    private const decimal OutputPricePerMillion = 15m;

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
            Chat = state.Chat with { Status = "running", IsAgentRunning = true }
        }, new TuiEffect.None()),
        ChatAppMsg.AgentEnded ae => OnAgentEnded(state, ae),
        ChatAppMsg.StatusChanged sc => ReduceResult.NoOp(state with { Chat = state.Chat with { Status = sc.Status } }),
        ChatAppMsg.ConfigureRuntime cr => ReduceResult.NoOp(state with
        {
            Chat = state.Chat with { Model = cr.Model, Provider = cr.Provider, AgentName = cr.AgentName }
        }),
        ChatAppMsg.AppendLine al => ReduceResult.NoOp(state.AddLine(al.Role, al.Text, al.ToolCallId)),
        ChatAppMsg.HydrateSession h => ReduceResult.NoOp(HydrateSession(h)),
        ChatAppMsg.SyncSessions ss => ReduceResult.NoOp(state with
        {
            Chat = state.Chat with { Sessions = ss.Sessions, ActiveSessionId = ss.ActiveSessionId }
        }),
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
                Status = state.Chat.Status == "error" ? "error" : "idle",
                IsAgentRunning = false,
                WasRunning = state.Chat.IsAgentRunning,
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
                WasRunning = state.Chat.IsAgentRunning
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

    private static UiState OnStepFinish(UiState state, Usage usage)
    {
        long nextIn = state.Chat.Cost.TokensIn + usage.InputTokens;
        long nextOut = state.Chat.Cost.TokensOut + usage.OutputTokens;
        return state with
        {
            Chat = state.Chat with
            {
                Cost = new CostSnapshot(
                    nextIn,
                    nextOut,
                    state.Chat.Cost.CostUsd + EstimateCost(usage.InputTokens, usage.OutputTokens))
            }
        };
    }

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

    // ── chat-owned message arms ────────────────────────────────────────────

    /// <summary>
    ///     Atomic session hydration (#89). Folds Reset + session-chrome bind +
    ///     history replay into one pure transition so the swap rides a single
    ///     store CAS: a concurrent background event applies strictly before
    ///     (superseded by the fresh state) or after (appended in order), never
    ///     interleaved mid-history.
    /// </summary>
    private static UiState HydrateSession(ChatAppMsg.HydrateSession h)
    {
        var next = new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Model = h.Model,
                Provider = h.Provider,
                AgentName = h.AgentName,
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
    ///     <c>"idle"</c>.
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
                Status = msg.Status ?? (state.Chat.Status == "error" ? "error" : "idle")
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
