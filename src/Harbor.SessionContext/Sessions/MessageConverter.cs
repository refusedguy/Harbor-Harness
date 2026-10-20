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
        // #1751: the ids of the tool calls the history already answers. A
        // tool_use without a tool_result is a provider 400 (Anthropic wants
        // every tool_use answered in the next message, OpenAI wants a `tool`
        // role message with the same id), so the conversion closes the gap
        // instead of shipping an invalid transcript. The set is built only
        // when the history holds a tool call at all — a text-only
        // conversation pays nothing for the repair.
        HashSet<string>? answeredCallIds = CollectAnsweredCallIds(messages);

        // First pass: count the maximum number of LlmMessages we may produce.
        // Each AssistantMessage / UserMessage yields exactly 1 LlmMessage, but a
        // ToolResultMessage expands to one LlmMessage per ToolResultEntry —
        // and an assistant message with unanswered calls yields one more per
        // such call (#1751).
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
                if (messages[i] is AssistantMessage assistant)
                {
                    capacity += CountUnansweredCalls(assistant, answeredCallIds);
                }
            }
        }

        var result = new List<LlmMessage>(capacity);

        // #461: the per-kind dispatch lives in the visitor, not in a switch that
        // every consumer had to re-type. An unhandled message kind now throws
        // instead of silently vanishing from the model context.
        new ToLlmMessagesVisitor(result, answeredCallIds).Walk(messages);

        return result;
    }

    /// <summary>
    ///     The ids of the tool calls that already have a result (#1751), or
    ///     <see langword="null" /> when the history holds no tool call at all.
    /// </summary>
    /// <param name="messages">The domain messages to inspect.</param>
    /// <returns>
    ///     The answered call ids, or <see langword="null" /> for a history with
    ///     no tool call — the case where the repair has nothing to do and the
    ///     set would be a pure allocation.
    /// </returns>
    private static HashSet<string>? CollectAnsweredCallIds(IReadOnlyList<AgentMessage> messages)
    {
        bool hasToolCall = false;
        for (int i = 0; i < messages.Count && !hasToolCall; i++)
        {
            if (messages[i] is AssistantMessage assistant)
            {
                for (int p = 0; p < assistant.Parts.Count; p++)
                {
                    if (assistant.Parts[p] is ToolCallPart)
                    {
                        hasToolCall = true;
                        break;
                    }
                }
            }
        }

        if (!hasToolCall)
        {
            return null;
        }

        var answered = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is ToolResultMessage tr)
            {
                for (int j = 0; j < tr.Results.Count; j++)
                {
                    answered.Add(tr.Results[j].ToolCallId);
                }
            }
        }

        return answered;
    }

    /// <summary>
    ///     How many of an assistant message's tool calls the history never
    ///     answered (#1751) — the number of results the repair has to add.
    /// </summary>
    /// <param name="assistant">The assistant message to inspect.</param>
    /// <param name="answeredCallIds">The ids that already have a result.</param>
    /// <returns>The count of unanswered tool calls.</returns>
    private static int CountUnansweredCalls(AssistantMessage assistant, HashSet<string>? answeredCallIds)
    {
        int count = 0;
        for (int p = 0; p < assistant.Parts.Count; p++)
        {
            if (assistant.Parts[p] is ToolCallPart call
                && (answeredCallIds is null || !answeredCallIds.Contains(call.Id)))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    ///     Message-level arm of the conversion: the three roles, nothing else.
    ///     Part-level behaviour lives in <see cref="PartsToLlmBlocks" />.
    /// </summary>
    private sealed class ToLlmMessagesVisitor(List<LlmMessage> sink, HashSet<string>? answeredCallIds)
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
            // #1751: every tool_use needs a tool_result. A call the history
            // never answered gets one here, so the request stays valid for the
            // provider even when the transcript was cut short — a killed
            // process, a turn that degraded, a session written by an older
            // build. The code tells the model what it may do about it: the
            // outcome is unknown, so a blind retry is not safe.
            AppendUnansweredResults(message);
            return this;
        }

        /// <summary>
        ///     One synthesized error result per tool call the history never
        ///     answered (#1751).
        /// </summary>
        /// <param name="message">The assistant message that opened the calls.</param>
        private void AppendUnansweredResults(AssistantMessage message)
        {
            for (int i = 0; i < message.Parts.Count; i++)
            {
                if (message.Parts[i] is not ToolCallPart call)
                {
                    continue;
                }

                if (answeredCallIds is not null && answeredCallIds.Contains(call.Id))
                {
                    continue;
                }

                sink.Add(new LlmToolResultMessage(
                    call.Id,
                    call.ToolName,
                    UnresolvedToolCallRepair.OutcomeUnknownText(call.ToolName),
                    true));
            }
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

}
