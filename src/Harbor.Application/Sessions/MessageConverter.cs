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

        // #461: the per-kind dispatch lives in the visitor, not in a switch that
        // every consumer had to re-type. An unhandled message kind now throws
        // instead of silently vanishing from the model context.
        new ToLlmMessagesVisitor(result).Walk(messages);

        return result;
    }

    /// <summary>
    ///     Message-level arm of the conversion: the three roles, nothing else.
    ///     Part-level behaviour lives in <see cref="PartsToLlmBlocks" />.
    /// </summary>
    private sealed class ToLlmMessagesVisitor(List<LlmMessage> sink)
        : AgentMessageVisitor<ToLlmMessagesVisitor>
    {
        public override ToLlmMessagesVisitor Visit(UserMessage message)
        {
            sink.Add(ConvertUser(message));
            return this;
        }

        public override ToLlmMessagesVisitor Visit(AssistantMessage message)
        {
            sink.Add(new LlmAssistantMessage(
                ConvertParts(message.Parts),
                StopReasonToLower(message.StopReason)));
            return this;
        }

        public override ToLlmMessagesVisitor Visit(ToolResultMessage message)
        {
            var results = message.Results;
            for (int j = 0; j < results.Count; j++)
            {
                var r = results[j];
                // #235: cap giant payloads before they enter model context.
                // Stores, events and TUI keep the full Output — only the
                // provider-bound copy is trimmed (head kept, tail cut).
                sink.Add(new LlmToolResultMessage(
                    r.ToolCallId,
                    r.ToolName,
                    ToolResultContextTrim.Trim(r.Output),
                    r.IsError));
            }

            return this;
        }
    }

    /// <summary>
    ///     Convert a user turn, carrying any attached images (#386) as
    ///     <see cref="LlmImageBlock" />s after the text block. Text-only turns
    ///     keep the single-block shape <see cref="LlmUserMessage.Text" /> produces,
    ///     so every existing provider payload is byte-identical.
    /// </summary>
    private static LlmUserMessage ConvertUser(UserMessage message)
    {
        if (message.Attachments is not { Count: > 0 } attachments)
            return LlmUserMessage.Text(message.Content);

        var blocks = new LlmContentBlock[attachments.Count + 1];
        blocks[0] = new LlmTextBlock(message.Content);
        for (int i = 0; i < attachments.Count; i++)
        {
            ImageAttachment image = attachments[i];
            blocks[i + 1] = new LlmImageBlock(image.MimeType, image.Data);
        }

        return new LlmUserMessage(blocks);
    }

    private static IReadOnlyList<LlmContentBlock> ConvertParts(IReadOnlyList<ContentPart> parts)
    {
        var visitor = new PartsToLlmBlocks(parts.Count);
        visitor.Walk(parts);
        return visitor.Blocks;
    }

    /// <summary>
    ///     Part-level arm of the conversion (#461). Each kind is an explicit,
    ///     compiler-enforced decision — adding a <see cref="ContentPart" />
    ///     subtype turns every missing arm here into a build break.
    /// </summary>
    private sealed class PartsToLlmBlocks(int capacity) : ContentPartVisitor<PartsToLlmBlocks>
    {
        internal List<LlmContentBlock> Blocks { get; } = new(capacity);

        public override PartsToLlmBlocks Visit(TextPart part)
        {
            Blocks.Add(new LlmTextBlock(part.Text));
            return this;
        }

        public override PartsToLlmBlocks Visit(ThinkingPart part)
        {
            Blocks.Add(new LlmThinkingBlock(part.Text));
            return this;
        }

        public override PartsToLlmBlocks Visit(ToolCallPart part)
        {
            Blocks.Add(new LlmToolCallBlock(part.Id, part.ToolName, part.Args));
            return this;
        }

        /// <summary>
        ///     File parts never reach the provider: the wire format carries images
        ///     as <c>LlmImageBlock</c>s hung off a <see cref="UserMessage" />'s
        ///     <c>Attachments</c> (#386), and an assistant-side file has no LLM
        ///     block equivalent. Explicitly a no-op so a future subtype can never
        ///     be mistaken for this one.
        /// </summary>
        public override PartsToLlmBlocks Visit(FilePart part) => this;
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
