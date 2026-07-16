using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;

namespace Harbor.Core.Sessions;

/// <summary>
/// Converts domain <see cref="AgentMessage"/> instances to LLM-specific <see cref="LlmMessage"/> format.
/// Implements Adapter pattern (GOF).
/// </summary>
public sealed class MessageConverter
{
    /// <summary>
    /// Convert an ordered list of domain <see cref="AgentMessage"/>s to LLM-specific
    /// <see cref="LlmMessage"/>s ready to send to a provider.
    /// </summary>
    /// <param name="messages">The domain messages to convert.</param>
    /// <returns>An ordered list of <see cref="LlmMessage"/> instances.</returns>
    public IReadOnlyList<LlmMessage> ToLlmMessages(IReadOnlyList<AgentMessage> messages)
    {
        var result = new List<LlmMessage>(messages.Count);

        foreach (var msg in messages)
        {
            switch (msg)
            {
                case UserMessage u:
                    result.Add(LlmUserMessage.Text(u.Content));
                    break;

                case AssistantMessage a:
                    result.Add(new LlmAssistantMessage(
                        Content: ConvertParts(a.Parts),
                        StopReason: a.StopReason.ToString().ToLowerInvariant()));
                    break;

                case ToolResultMessage tr:
                    foreach (var r in tr.Results)
                    {
                        result.Add(new LlmToolResultMessage(
                            r.ToolCallId,
                            r.ToolName,
                            r.Output,
                            r.IsError));
                    }
                    break;
            }
        }

        return result;
    }

    private static IReadOnlyList<LlmContentBlock> ConvertParts(IReadOnlyList<ContentPart> parts)
    {
        var blocks = new List<LlmContentBlock>(parts.Count);
        foreach (var part in parts)
        {
            switch (part)
            {
                case TextPart t:
                    blocks.Add(new LlmTextBlock(t.Text));
                    break;
                case ThinkingPart th:
                    blocks.Add(new LlmThinkingBlock(th.Text));
                    break;
                case ToolCallPart tc:
                    blocks.Add(new LlmToolCallBlock(tc.Id, tc.ToolName, tc.Args));
                    break;
                default:
                    // skip unknown
                    break;
            }
        }

        return blocks;
    }
}
