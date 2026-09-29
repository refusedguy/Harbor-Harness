using System.Collections.Immutable;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Reducers;

/// <summary>
///     Pure reducer: <c>(AgentEvent, AppState) → AppState</c>.
/// </summary>
/// <remarks>
///     <para>
///         Delegates to domain-specific reducers (<see cref="ChatViewReducer" />,
///         <see cref="ChromeReducer" />, <see cref="SessionsReducer" />) and
///         reassembles the result. Every interactive renderer funnels its events
///         through this — there is no per-renderer <c>switch (AgentEvent)</c>
///         anywhere.
///     </para>
///     <para>
///         Must never call into <c>IAgent</c> or perform I/O — side-effects are
///         the responsibility of the host effect runner.
///     </para>
/// </remarks>
public static partial class AppReducer
{
    /// <summary>
    ///     Apply an agent event to the app state, returning the next immutable snapshot.
    /// </summary>
    public static AppState Reduce(AgentEvent @event, AppState state) => @event switch
    {
        AgentStartEvent e => OnAgentStart(state, e),
        MessageStartEvent => OnMessageStart(state),
        MessageUpdateEvent mu => OnMessageUpdate(state, mu),
        MessageEndEvent => OnMessageEnd(state),
        ToolExecutionStartEvent tes => OnToolStart(state, tes),
        ToolExecutionEndEvent tee => OnToolEnd(state, tee),
        SessionStatsEvent ss => OnSessionStats(state, ss),
        CompactionStartedEvent => state with { Status = "compacting" },
        CompactionCompletedEvent cc => OnCompactionCompleted(state, cc),
        AgentErrorEvent err => OnAgentError(state, err),
        AgentEndEvent => OnAgentEnd(state),
        SessionChangedEvent sce => OnSessionChanged(state, sce),
        _ => state
    };

    private static AppState OnAgentStart(AppState state, AgentStartEvent ase)
    {
        var next = state with
        {
            Status = "running",
            IsAgentRunning = true,
            WasRunning = state.IsAgentRunning,
            ScrollOffset = 0,
            StreamingBuffer = string.Empty,
            ThinkingBuffer = string.Empty,
            PendingStreamText = ChunkedBuffer.Empty,
            PendingStreamThink = ChunkedBuffer.Empty,
            IsThinking = false,
            Chrome = state.Chrome ?? new AppState.ChromeState()
        };

        if (next.Lines.Length != 0)
            return next;

        foreach (var m in ase.Messages)
        {
            if (m is UserMessage u)
                next = next with { Lines = next.Lines.Add(new ChatLine(ChatRole.User, u.Content)) };
        }

        return next;
    }

    private static AppState OnMessageStart(AppState state) => state with
    {
        Status = "running",
        IsAgentRunning = true,
        IsStreaming = true,
        Active = ActiveMessage.Empty,
        StreamingBuffer = string.Empty,
        ThinkingBuffer = string.Empty,
        PendingStreamText = ChunkedBuffer.Empty,
        PendingStreamThink = ChunkedBuffer.Empty,
        IsThinking = false
    };

    private static AppState OnMessageUpdate(AppState state, MessageUpdateEvent mu) => mu.LlmEvent switch
    {
        TextDeltaEvent td => WithTextDelta(state, td.Delta),
        ThinkingDeltaEvent thd => WithThinkingDelta(state, thd.Delta),
        ToolCallStartEvent tcs => FlushPending(state) with
        {
            Lines = state.Lines.Add(new ChatLine(ChatRole.Tool, $"→ {tcs.ToolName}", tcs.Id))
        },
        StepFinishEvent sf => NoteRequestSize(FlushPending(state), sf.Usage),
        _ => state
    };

    /// <summary>
    ///     Records what the context OCCUPIES, from the request the provider just
    ///     accepted (#651) — the same rule <c>ChatAppReducer</c> applies on the
    ///     TEA path. <c>Usage.InputTokens</c> is one request's full input (the
    ///     system prompt and the entire history, re-read every turn), so summing
    ///     it yields the bill and never a window; the occupied figure is the one
    ///     a status cell shows. Money is untouched — the paid totals arrive on
    ///     <see cref="SessionStatsEvent" /> and are the core's (#653).
    /// </summary>
    private static AppState NoteRequestSize(AppState state, Usage? usage) =>
        usage is null
            ? state
            : state with { Cost = state.Cost with { ContextTokens = usage.InputTokens } };

    /// <summary>
    ///     Append a text delta to the chunked pending buffer and rebuild the
    ///     synced <see cref="AppState.StreamingBuffer" /> /
    ///     <see cref="ActiveMessage.TextBuffer" /> strings only when
    ///     <see cref="StreamingSync.ShouldFlush" /> demands it.
    /// </summary>
    private static AppState WithTextDelta(AppState state, string delta)
    {
        if (string.IsNullOrEmpty(delta))
            return state;

        ChunkedBuffer pending = state.PendingStreamText.Append(delta);
        if (!StreamingSync.ShouldFlush(state.StreamingBuffer.Length, pending.Length))
            return state with { PendingStreamText = pending };

        string full = StreamingSync.Concat(state.StreamingBuffer, pending);
        return state with
        {
            StreamingBuffer = full,
            Active = state.Active with { TextBuffer = full },
            PendingStreamText = ChunkedBuffer.Empty
        };
    }

    /// <summary>Thinking-delta counterpart of <see cref="WithTextDelta" />.</summary>
    private static AppState WithThinkingDelta(AppState state, string delta)
    {
        if (string.IsNullOrEmpty(delta))
            return state with { IsThinking = true };

        ChunkedBuffer pending = state.PendingStreamThink.Append(delta);
        if (!StreamingSync.ShouldFlush(state.ThinkingBuffer.Length, pending.Length))
            return state with { PendingStreamThink = pending, IsThinking = true };

        string full = StreamingSync.Concat(state.ThinkingBuffer, pending);
        return state with
        {
            ThinkingBuffer = full,
            Active = state.Active with { ThinkBuffer = full },
            PendingStreamThink = ChunkedBuffer.Empty,
            IsThinking = true
        };
    }

    /// <summary>
    ///     Materialize any pending chunks into the synced buffer strings so
    ///     pause points (tool calls, step finish, message end) observe the
    ///     complete text.
    /// </summary>
    private static AppState FlushPending(AppState state)
    {
        if (state.PendingStreamText.Length == 0 && state.PendingStreamThink.Length == 0)
            return state;

        string text = StreamingSync.Concat(state.StreamingBuffer, state.PendingStreamText);
        string think = StreamingSync.Concat(state.ThinkingBuffer, state.PendingStreamThink);
        return state with
        {
            StreamingBuffer = text,
            ThinkingBuffer = think,
            Active = state.Active with { TextBuffer = text, ThinkBuffer = think },
            PendingStreamText = ChunkedBuffer.Empty,
            PendingStreamThink = ChunkedBuffer.Empty
        };
    }

    /// <summary>
    ///     Adopt the session totals the core published — tokens AND cost — in one
    ///     assignment.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         #653: these are absolute session-cumulative totals, not a delta, so
    ///         they are ASSIGNED. A delta folded here would put a second opinion
    ///         about a bill the core already computed into the status bar; the cost
    ///         also arrives with the core's answer to "is this model's price
    ///         published at all", so an unpriced model renders "—" instead of a
    ///         fabricated "$0.0000".
    ///     </para>
    ///     <para>
    ///         No session check here, for the same reason as the chat reducer: a
    ///         session's events reach its own store, and a sub-agent run
    ///         publishes no totals of its own.
    ///     </para>
    ///     <para>
    ///         #651: the occupied context carried over from the last
    ///         <see cref="StepFinishEvent" /> is preserved rather than cleared —
    ///         these totals describe the whole session, and only a new request may
    ///         replace the size of the last one.
    ///     </para>
    /// </remarks>
    private static AppState OnSessionStats(AppState state, SessionStatsEvent stats) => state with
    {
        Cost = new CostSnapshot(
            stats.Metadata.TokensInput,
            stats.Metadata.TokensOutput,
            stats.Metadata.Cost,
            !stats.Metadata.IsCostKnown,
            state.Cost.ContextTokens)
    };

    private static AppState OnMessageEnd(AppState state)
    {
        var next = FlushPending(state);
        if (!string.IsNullOrEmpty(next.ThinkingBuffer))
            next = next with { Lines = next.Lines.Add(new ChatLine(ChatRole.Thinking, next.ThinkingBuffer.Trim())) };
        if (!string.IsNullOrEmpty(next.StreamingBuffer))
            next = next with { Lines = next.Lines.Add(new ChatLine(ChatRole.Assistant, next.StreamingBuffer.Trim())) };
        return next with
        {
            IsStreaming = false,
            Active = ActiveMessage.Empty,
            StreamingBuffer = string.Empty,
            ThinkingBuffer = string.Empty,
            PendingStreamText = ChunkedBuffer.Empty,
            PendingStreamThink = ChunkedBuffer.Empty,
            IsThinking = false
        };
    }

    private static AppState OnToolStart(AppState state, ToolExecutionStartEvent tes)
    {
        string args = tes.Args.GetRawText();
        string text = string.IsNullOrEmpty(args) || args == "{}"
            ? $"→ {tes.ToolName}"
            : $"→ {tes.ToolName}  {args}";
        return state with
        {
            Lines = state.Lines.Add(new ChatLine(ChatRole.Tool, text, tes.ToolCallId))
        };
    }

    private static AppState OnToolEnd(AppState state, ToolExecutionEndEvent tee)
    {
        string label = tee.IsError ? "✗" : "✓";
        string output = tee.Result.Output ?? string.Empty;
        string preview = output.Length > 600 ? output[..600] + "..." : output;
        return state with
        {
            Lines = state.Lines.Add(new ChatLine(ChatRole.ToolResult, $"{label} {preview.Trim()}", tee.ToolCallId))
        };
    }

    private static AppState OnCompactionCompleted(AppState state, CompactionCompletedEvent cc) => state with
    {
        Lines = state.Lines.Add(new ChatLine(ChatRole.System,
            $"compacted: pruned {cc.PrunedMessageCount} msgs, saved ~{cc.TokensSaved} tokens")),
        Status = "running"
    };

    private static AppState OnAgentError(AppState state, AgentErrorEvent err) => state with
    {
        Lines = state.Lines.Add(new ChatLine(ChatRole.Error, err.Message)),
        Status = "error"
    };

    private static AppState OnAgentEnd(AppState state) => state with
    {
        Status = "idle",
        IsAgentRunning = false,
        WasRunning = state.IsAgentRunning,
        IsStreaming = false,
        Active = ActiveMessage.Empty,
        StreamingBuffer = string.Empty,
        ThinkingBuffer = string.Empty,
        PendingStreamText = ChunkedBuffer.Empty,
        PendingStreamThink = ChunkedBuffer.Empty,
        IsThinking = false
    };

    private static AppState OnSessionChanged(AppState state, SessionChangedEvent sce)
    {
        var chrome = state.Chrome ?? new AppState.ChromeState();
        return state with { Chrome = chrome with { ActiveSessionId = SessionId.Create(sce.SessionId) } };
    }
}
