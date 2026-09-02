namespace Harbor.Application.Sessions;
/// <summary>
///     Converts domain <see cref="AgentMessage" /> instances to LLM-specific <see cref="LlmMessage" /> format.
///     Implements Adapter pattern (GOF).
/// </summary>
public sealed class MessageConverter
{
    /// <summary>
    ///     Convert an ordered list of domain <see cref="AgentMessage" />s to LLM-specific
    ///     <see cref="LlmMessage" />s ready to send to a provider.
    /// </summary>
    /// <param name="messages">The domain messages to convert.</param>
    /// <returns>An ordered list of <see cref="LlmMessage" /> instances.</returns>
    public IReadOnlyList<LlmMessage> ToLlmMessages(IReadOnlyList<AgentMessage> messages)
    {
        // First pass: count the maximum number of LlmMessages we may produce.
        // Each AssistantMessage / UserMessage yields exactly 1 LlmMessage, but a
        // ToolResultMessage expands to one LlmMessage per ToolResultEntry.
        int capacity = 0;
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is ToolResultMessage tr)
            {
                capacity += tr.Results.Count;
            }
            else
            {
                capacity++;
            }
        }

        var result = new List<LlmMessage>(capacity);

        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            switch (msg)
            {
                case UserMessage u:
                    result.Add(LlmUserMessage.Text(u.Content));
                    break;

                case AssistantMessage a:
                    result.Add(new LlmAssistantMessage(
                        ConvertParts(a.Parts),
                        StopReasonToLower(a.StopReason)));
                    break;

                case ToolResultMessage tr:
                    var results = tr.Results;
                    for (int j = 0; j < results.Count; j++)
                    {
                        var r = results[j];
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
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
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
            }
        }

        return blocks;
    }

    /// <summary>
    ///     Lowercase the <see cref="StopReason" /> enum to the wire form expected by OpenAI-style
    ///     providers ("stop", "length", "tool_use", …). Avoids the boxing allocation of
    ///     <see cref="Enum.ToString" /> + the second allocation of <see cref="string.ToLowerInvariant" />
    ///     on every converted assistant message (hot path: one conversion per LLM message).
    /// </summary>
    private static string StopReasonToLower(StopReason reason) => reason switch
    {
        StopReason.Stop => "stop",
        StopReason.Length => "length",
        StopReason.ToolUse => "tool_use",
        StopReason.ContentFilter => "content_filter",
        StopReason.Error => "error",
        StopReason.Aborted => "aborted",
        _ => reason.ToString().ToLowerInvariant()
    };
}
