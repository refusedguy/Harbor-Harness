using Harbor.Abstractions.Models;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     The single key joining transcript <see cref="ChatLine" />s, projected
///     transcript block ids, and tool-card widgets for one tool invocation
///     (issue #94). The key is the <c>ToolCallId</c> string originating from
///     <c>ToolExecutionStartEvent</c> (or the LLM <c>ToolCallStartEvent</c> id):
///     the reducer stores it on both the <c>Tool</c> and <c>ToolResult</c>
///     transcript lines, the projector uses it as the block id, and the card
///     paths (CellForge <c>ToolCallBlock</c>, desktop <c>ToolCallViewModel</c>)
///     are keyed by the same string. No path may invent its own
///     (name+index) key — that is what used to split the transcript and the
///     cards into two unjoinable parallel paths.
/// </summary>
public static class ToolCallKey
{
    /// <summary>
    ///     Transcript block id for a line: the <c>ToolCallId</c> when present,
    ///     otherwise the legacy role+first-index fallback.
    /// </summary>
    /// <param name="line">The transcript line.</param>
    /// <param name="firstIndex">First occurrence index of <paramref name="line" /> in the transcript.</param>
    /// <returns>The stable block id.</returns>
    public static string TranscriptBlockId(ChatLine line, int firstIndex) =>
        line.ToolCallId ?? DefaultBlockId(line.Role, firstIndex);

    /// <summary>Role+index fallback block id for lines without a tool-call key.</summary>
    public static string DefaultBlockId(ChatRole role, int index) => role switch
    {
        ChatRole.Tool => $"tool:{index}",
        ChatRole.ToolResult => $"tool-result:{index}",
        _ => $"msg:{index}"
    };

    /// <summary>
    ///     All transcript lines belonging to one tool invocation (the call line
    ///     plus its result line), oldest first.
    /// </summary>
    /// <param name="state">The UI snapshot to search.</param>
    /// <param name="toolCallId">The tool-call key.</param>
    /// <returns>Matching lines; empty when the key is blank or absent.</returns>
    public static IReadOnlyList<ChatLine> FindLines(UiState state, string? toolCallId)
    {
        if (string.IsNullOrEmpty(toolCallId))
            return Array.Empty<ChatLine>();
        var result = new List<ChatLine>(2);
        foreach (var line in state.Lines)
        {
            if (string.Equals(line.ToolCallId, toolCallId, StringComparison.Ordinal))
                result.Add(line);
        }

        return result;
    }

    /// <summary>
    ///     Whether a projected block id or card id belongs to the given tool invocation.
    /// </summary>
    /// <param name="blockId">Block/card id (e.g. <c>UiMessageBlock.Id</c> or card <c>Id</c>).</param>
    /// <param name="toolCallId">The tool-call key.</param>
    /// <returns>True when both name the same invocation.</returns>
    public static bool Matches(string? blockId, string? toolCallId) =>
        !string.IsNullOrEmpty(blockId)
        && !string.IsNullOrEmpty(toolCallId)
        && string.Equals(blockId, toolCallId, StringComparison.Ordinal);
}
