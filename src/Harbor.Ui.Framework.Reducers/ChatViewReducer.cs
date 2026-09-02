using System.Collections.Immutable;
using Harbor.Abstractions.Events;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Reducers;

/// <summary>
///     Pure reducer: <c>(AgentEvent, ChatViewState) → ChatViewState</c>.
/// </summary>
/// <remarks>
///     <para>
///         Maps agent activity into chat view state. Every interactive renderer
///         funnels its events through this — there is no per-renderer
///         <c>switch (AgentEvent)</c> anywhere.
///     </para>
///     <para>
///         Must never call into <c>IAgent</c> or perform I/O — side-effects are the
///         responsibility of the host effect runner.
///     </para>
/// </remarks>
public static partial class ChatViewReducer
{
    /// <summary>
    ///     Apply an agent event to the chat view state, returning the next
    ///     immutable snapshot.
    /// </summary>
    public static ChatViewState Reduce(AgentEvent @event, ChatViewState state) => @event switch
    {
        MessageStartEvent => state with
        {
            IsStreaming = true,
            IsThinking = false,
            StreamingBuffer = string.Empty,
            PendingStreaming = ChunkedBuffer.Empty,
            StatusMessage = string.Empty,
            ToolCalls = []
        },

        MessageUpdateEvent { LlmEvent: TextDeltaEvent tde } => AppendText(state, tde.Delta),

        MessageUpdateEvent { LlmEvent: ThinkingDeltaEvent tde } => AppendThinking(state, tde.Delta),

        MessageUpdateEvent { LlmEvent: ToolCallStartEvent tcse } => state with
        {
            ToolCalls = state.ToolCalls.Add(new ToolCallViewModel(
                tcse.Id,
                tcse.ToolName,
                string.Empty,
                "running",
                string.Empty,
                TimeSpan.Zero,
                false,
                false))
        },

        MessageEndEvent => AddStreamingBuffer(state),

        ToolExecutionStartEvent { ToolCallId: string id } => UpdateToolCallStatus(state, id, "running"),

        ToolExecutionEndEvent { ToolCallId: string id, IsError: bool isError } => UpdateToolCallStatus(
            state,
            id,
            isError ? "error" : "success"),

        AgentStartEvent => ResetForAgentStart(state),

        AgentEndEvent => state with { IsAgentRunning = false },

        SessionChangedEvent => ResetForSessionChange(state),

        _ => state
    };

    /// <summary>
    ///     Append a text delta to the chunked pending buffer and rebuild the
    ///     synced <see cref="ChatViewState.StreamingBuffer" /> string only when
    ///     <see cref="StreamingSync.ShouldFlush" /> demands it.
    /// </summary>
    private static ChatViewState AppendText(ChatViewState state, string delta)
    {
        if (string.IsNullOrEmpty(delta))
            return state;

        ChunkedBuffer pending = state.PendingStreaming.Append(delta);
        if (!StreamingSync.ShouldFlush(state.StreamingBuffer.Length, pending.Length))
            return state with { PendingStreaming = pending };

        return state with
        {
            StreamingBuffer = StreamingSync.Concat(state.StreamingBuffer, pending),
            PendingStreaming = ChunkedBuffer.Empty
        };
    }

    /// <summary>Thinking-delta counterpart of <see cref="AppendText" />.</summary>
    private static ChatViewState AppendThinking(ChatViewState state, string delta)
    {
        if (string.IsNullOrEmpty(delta))
            return state with { IsThinking = true };

        ChunkedBuffer pending = state.PendingStreaming.Append(delta);
        if (!StreamingSync.ShouldFlush(state.StreamingBuffer.Length, pending.Length))
            return state with { PendingStreaming = pending, IsThinking = true };

        return state with
        {
            StreamingBuffer = StreamingSync.Concat(state.StreamingBuffer, pending),
            PendingStreaming = ChunkedBuffer.Empty,
            IsThinking = true
        };
    }

    private static ChatViewState AddStreamingBuffer(ChatViewState state)
    {
        string full = StreamingSync.Concat(state.StreamingBuffer, state.PendingStreaming);
        var next = state with
        {
            IsStreaming = false,
            IsThinking = false,
            StreamingBuffer = string.Empty,
            PendingStreaming = ChunkedBuffer.Empty,
            StatusMessage = string.Empty
        };

        if (!string.IsNullOrEmpty(full))
        {
            var role = state.IsThinking ? ChatRole.Thinking : ChatRole.Assistant;
            var line = new ChatLineViewModel(role, full);
            next = next with { Lines = state.Lines.Add(line) };
        }

        return next;
    }

    private static ChatViewState UpdateToolCallStatus(ChatViewState state, string toolCallId, string status)
    {
        var updated = ImmutableArray.CreateBuilder<ToolCallViewModel>(state.ToolCalls.Length);
        bool changed = false;

        for (int i = 0; i < state.ToolCalls.Length; i++)
        {
            var tc = state.ToolCalls[i];
            if (tc.Id == toolCallId)
            {
                updated.Add(tc with { Status = status });
                changed = true;
            }
            else
            {
                updated.Add(tc);
            }
        }

        return changed ? state with { ToolCalls = updated.ToImmutable() } : state;
    }

    private static ChatViewState ResetForAgentStart(ChatViewState state) => state with
    {
        Lines = [],
        IsStreaming = false,
        IsThinking = false,
        IsAgentRunning = true,
        StreamingBuffer = string.Empty,
        PendingStreaming = ChunkedBuffer.Empty,
        StatusMessage = string.Empty,
        ToolCalls = [],
        PullProgress = 0.0,
        PullOffset = 0.0,
        CanLoadOlder = false,
        ShowPullIndicator = false,
        ContentScale = 1.0
    };

    private static ChatViewState ResetForSessionChange(ChatViewState state) => state with
    {
        Lines = [],
        IsStreaming = false,
        IsThinking = false,
        IsAgentRunning = false,
        StreamingBuffer = string.Empty,
        PendingStreaming = ChunkedBuffer.Empty,
        StatusMessage = string.Empty,
        ToolCalls = [],
        PullProgress = 0.0,
        PullOffset = 0.0,
        CanLoadOlder = false,
        ShowPullIndicator = false,
        ContentScale = 1.0
    };
}
