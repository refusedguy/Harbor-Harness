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
        StepFinishEvent sf when sf.Usage is not null => OnStepFinish(FlushPending(state), sf.Usage),
        _ => state
    };

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

    private static AppState OnStepFinish(AppState state, Usage usage)
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

    private static decimal EstimateCost(int inputTokens, int outputTokens) =>
        inputTokens / 1_000_000m * 3m + outputTokens / 1_000_000m * 15m;
}
