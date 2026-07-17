using System.Collections.Immutable;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Abstractions.State;

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
///     <para>Must never call into <c>IAgent</c> or perform I/O — side-effects are the
///     responsibility of <see cref="ITuiEffectRunner" /> (see <see cref="Classify" />).</para>
/// </remarks>
public static class UiReducer
{
    /// <summary>Default per-million-token pricing (USD). Override via model table if available.</summary>
    private const decimal InputPricePerMillion = 3m;
    private const decimal OutputPricePerMillion = 15m;

    /// <summary>
    ///     Apply an agent event to the UI state, returning the next immutable snapshot.
    /// </summary>
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
        AgentEndEvent => state with { Status = "idle", IsAgentRunning = false, IsStreaming = false, Active = ActiveMessage.Empty },
        _ => state
    };

    // ── event handlers ─────────────────────────────────────────────────────

    private static UiState OnAgentStart(UiState state, AgentStartEvent ase)
    {
        var next = state with { Status = "running", IsAgentRunning = true };
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
        var nextIn = state.Cost.TokensIn + usage.InputTokens;
        var nextOut = state.Cost.TokensOut + usage.OutputTokens;
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
                $"[dim]compacted: pruned {cc.PrunedMessageCount} msgs, saved ~{cc.TokensSaved} tokens[/]")
            .WithStatus("running");

    // ── formatting helpers (escaping) ─────────────────────────────────────

    private static string FormatToolStart(ToolExecutionStartEvent tes)
    {
        var args = tes.Args.GetRawText();
        return string.IsNullOrEmpty(args) || args == "{}"
            ? $"→ {tes.ToolName}"
            : $"→ {tes.ToolName}  {args}";
    }

    private static string FormatToolEnd(ToolExecutionEndEvent tee)
    {
        var label = tee.IsError ? "✗" : "✓";
        var output = tee.Result.Output ?? string.Empty;
        var preview = output.Length > 600 ? output[..600] + "..." : output;
        return $"{label} {preview.Trim()}";
    }

    private static decimal EstimateCost(int inputTokens, int outputTokens) =>
        inputTokens / 1_000_000m * InputPricePerMillion + outputTokens / 1_000_000m * OutputPricePerMillion;

    private static UiState WithStatus(this UiState state, string status) =>
        state with { Status = status };
}
