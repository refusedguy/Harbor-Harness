using System.Collections.ObjectModel;
using System.Diagnostics;
using Harbor.Ui.Framework.State;
using Harbor.Ui.Framework.ViewModels;
using ToolCallViewModel = Harbor.Ui.Framework.ViewModels.ToolCallViewModel;

namespace Harbor.Ui.Framework.Rendering;

/// <summary>
///     Renders <see cref="UiState"/> transitions into the chat-line and
///     tool-call collections the chat view binds to. Encapsulates the
///     append-only projection (the <c>while</c> loop that walks new lines)
///     plus the start/end coalescing of tool-call cards so
///     <c>ChatViewModel</c> shrinks to property + command plumbing.
/// </summary>
/// <remarks>
///     <para>
///         <b>Tool-call coalescing</b> is keyed on
///         <c>ToolName + (lineIndex / 2)</c> — the simplest stable key
///         that works without a real correlation id from
///         <c>AgentEvent</c>. A future refactor should thread a real
///         <c>ToolCallId</c> through <c>UiState</c>.
///     </para>
///     <para>
///         Registered as a singleton in <c>AppHost</c> so tests can drive
///         <see cref="Render"/> with synthetic <see cref="UiState"/>
///         values and assert the projected collections without spinning
///         up the full chat view-model + dispatcher subscription.
///     </para>
/// </remarks>
public sealed class ChatMessageRenderer
{
    /// <summary>
    ///     Project new lines from <paramref name="state"/> into
    ///     <paramref name="lines"/> / <paramref name="toolCalls"/>.
    ///     Idempotent under repeated notifications: only lines with
    ///     index &gt;= the rendered cursor are projected, so calling
    ///     this twice with the same state is a no-op on the second call.
    /// </summary>
    /// <param name="state">The current UiState to render.</param>
    /// <param name="lines">The user-facing transcript collection.</param>
    /// <param name="toolCalls">The tool-call card collection.</param>
    /// <param name="toolCallById">Coalescing map (tool id → card).</param>
    /// <param name="stopwatch">Shared stopwatch measuring card duration.</param>
    /// <param name="renderedLineCount">Cursor: how many lines have been projected so far.</param>
    /// <returns>The new cursor value (= number of lines projected so far).</returns>
    public int Render(
        UiState state,
        ObservableCollection<ChatLineViewModel> lines,
        ObservableCollection<ToolCallViewModel> toolCalls,
        Dictionary<string, ToolCallViewModel> toolCallById,
        Stopwatch stopwatch,
        int renderedLineCount)
    {
        // Append only new lines (idempotent under repeated notifications).
        while (renderedLineCount < state.Lines.Length)
        {
            var line = state.Lines[renderedLineCount];
            // Tool / ToolResult lines are projected into ToolCalls
            // cards instead of (or in addition to) the transcript.
            if (line.Role is ChatRole.Tool or ChatRole.ToolResult)
            {
                ProjectToolLine(line, renderedLineCount, toolCalls, toolCallById, stopwatch);
            }
            else
            {
                lines.Add(new ChatLineViewModel(line.Role, line.Text ?? string.Empty));
            }
            renderedLineCount++;
        }

        return renderedLineCount;
    }

    /// <summary>
    ///     Project a <see cref="ChatRole.Tool"/> (start) or
    ///     <see cref="ChatRole.ToolResult"/> (end) line into a
    ///     <see cref="ToolCallViewModel"/> card. Coalesces start/end
    ///     pairs by tool name + index parity.
    /// </summary>
    private static void ProjectToolLine(
        ChatLine line,
        int lineIndex,
        ObservableCollection<ToolCallViewModel> toolCalls,
        Dictionary<string, ToolCallViewModel> toolCallById,
        Stopwatch stopwatch)
    {
        // Parse "tool_name: args" or "tool_name: result" — best-effort.
        string text = line.Text ?? string.Empty;
        string toolName = text;
        string payload = string.Empty;
        int colon = text.IndexOf(':');
        if (colon > 0)
        {
            toolName = text[..colon].Trim();
            payload = text[(colon + 1)..].Trim();
        }

        // Coalescing key — tool name + line index parity. Two adjacent
        // Tool/ToolResult lines for the same tool name pair up.
        string id = $"{toolName}-{lineIndex / 2}";

        if (line.Role == ChatRole.Tool)
        {
            // Start event — create a running card.
            if (!toolCallById.TryGetValue(id, out var card))
            {
                card = new ToolCallViewModel
                {
                    Id = id,
                    ToolName = toolName,
                    IconText = IconForTool(toolName),
                    Status = ToolCallStatus.Running,
                    ArgsPreview = TruncateForPreview(payload),
                    IsExpanded = false,
                };
                toolCallById[id] = card;
                toolCalls.Add(card);
                stopwatch.Restart();
            }
        }
        else // ChatRole.ToolResult
        {
            // End event — complete the matching card. Fall back to the
            // most recent running card if the coalescing key missed.
            ToolCallViewModel? card = null;
            if (toolCallById.TryGetValue(id, out card))
            {
                // Found by id — use it.
            }
            else
            {
                // Fall back: find the most-recently-added running card.
                card = toolCalls.LastOrDefault(c => c.Status == ToolCallStatus.Running);
                if (card is not null)
                {
                    toolCallById[id] = card;
                }
            }

            if (card is not null)
            {
                bool isError = payload.StartsWith("error", StringComparison.OrdinalIgnoreCase)
                    || payload.StartsWith("fail", StringComparison.OrdinalIgnoreCase);
                card.Complete(
                    status: isError ? ToolCallStatus.Error : ToolCallStatus.Success,
                    resultPreview: TruncateForPreview(payload),
                    duration: stopwatch.Elapsed);
            }
            else
            {
                // No matching start — create a completed card standalone.
                var standalone = new ToolCallViewModel
                {
                    Id = id,
                    ToolName = toolName,
                    IconText = IconForTool(toolName),
                    Status = ToolCallStatus.Success,
                    ResultPreview = TruncateForPreview(payload),
                };
                toolCallById[id] = standalone;
                toolCalls.Add(standalone);
            }
        }
    }

    /// <summary>Truncate a payload string for compact card preview (default 800 chars).</summary>
    /// <param name="text">The text to truncate.</param>
    /// <param name="max">Maximum length before truncation.</param>
    /// <returns>The (possibly truncated) text, with an ellipsis appended when truncated.</returns>
    public static string TruncateForPreview(string text, int max = 800)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// <summary>Pick an emoji icon for a tool by its lowercased name.</summary>
    /// <param name="toolName">The tool name (case-insensitive).</param>
    /// <returns>An emoji string for the tool's icon.</returns>
    public static string IconForTool(string toolName) => toolName.ToLowerInvariant() switch
    {
        "read" => "📖",
        "write" => "✍️",
        "edit" => "✏️",
        "patch" => "🩹",
        "bash" or "sh" => "🖥️",
        "grep" or "ripgrep" => "🔍",
        "glob" => "🌐",
        "ls" => "📁",
        "tree" => "🌲",
        "webfetch" or "fetch" => "🌐",
        "task" => "📋",
        "notebook" => "📓",
        "mcp" => "🔌",
        _ => "🔧",
    };
}
