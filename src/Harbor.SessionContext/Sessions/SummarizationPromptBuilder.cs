using System.Text;
using Harbor.Abstractions.Extensions;

namespace Harbor.Application.Sessions;

/// <summary>
///     Renders the head slice of a conversation into the compaction summarization
///     prompt. #95 slice: the pure formatting logic moved verbatim out of
///     <see cref="CompactionService" />. It has no instance state, no logging and
///     no <c>Result</c> chain, so behavior is unchanged byte-for-byte.
/// </summary>
internal static class SummarizationPromptBuilder
{
    /// <summary>
    ///     Renders the head slice <c>messages[start..count]</c> into the
    ///     summarization prompt. A non-zero <paramref name="start" /> means the
    ///     oldest head messages were dropped by the prompt cap, so a marker
    ///     says so instead of letting the summary read as the whole history.
    /// </summary>
    internal static string Build(IReadOnlyList<AgentMessage> messages, int start, int count)
    {
        using var sb = StringBuilderPool.Rent(4096);
        var builder = sb.Builder;
        builder.AppendLine("Summarize the following conversation, preserving all important details:");
        builder.AppendLine();
        builder.AppendLine("<conversation>");
        if (start > 0)
        {
            builder.AppendLine("[... earlier history omitted to fit context ...]");
        }
        for (int i = start; i < count; i++)
        {
            var msg = messages[i];
            builder.Append('[').Append(msg.Role).Append("] ");
            // Append the formatted message body inline to avoid the intermediate string
            // that the previous `AppendLine(FormatMessage(msg))` produced.
            AppendFormattedMessage(builder, msg);
            builder.AppendLine();
        }
        builder.AppendLine("</conversation>");
        return builder.ToString();
    }

    /// <summary>
    ///     Renders one message into the summarization prompt. #461: the per-kind
    ///     dispatch lives in <see cref="FormattedMessageVisitor" />; an unknown
    ///     role now throws instead of being stringified by a <c>default:</c> arm.
    /// </summary>
    private static void AppendFormattedMessage(StringBuilder builder, AgentMessage msg) =>
        new FormattedMessageVisitor(builder).Accept(msg);

    /// <summary>
    ///     Message-level arm of the summarization formatter. Byte-for-byte the
    ///     shape the previous switch produced, separators included.
    /// </summary>
    private sealed class FormattedMessageVisitor(StringBuilder builder)
        : AgentMessageVisitor<FormattedMessageVisitor>
    {
        private readonly FormattedPartVisitor _parts = new(builder);

        public override FormattedMessageVisitor Visit(UserMessage message)
        {
            builder.Append(message.Content);
            return this;
        }

        public override FormattedMessageVisitor Visit(AssistantMessage message)
        {
            var parts = message.Parts;
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) builder.Append('\n');
                _parts.Accept(parts[i]);
            }

            return this;
        }

        public override FormattedMessageVisitor Visit(ToolResultMessage message)
        {
            var results = message.Results;
            for (int i = 0; i < results.Count; i++)
            {
                if (i > 0) builder.Append('\n');
                var r = results[i];
                builder.Append("[tool:").Append(r.ToolName).Append("] ").Append(r.Output);
            }

            return this;
        }
    }

    /// <summary>
    ///     Part-level arm of the summarization formatter (#461). Each part kind is
    ///     an explicit decision rather than a <c>switch</c> arm that a new
    ///     <see cref="ContentPart" /> subtype would fall straight through.
    /// </summary>
    private sealed class FormattedPartVisitor(StringBuilder builder)
        : ContentPartVisitor<FormattedPartVisitor>
    {
        public override FormattedPartVisitor Visit(TextPart part)
        {
            builder.Append(part.Text);
            return this;
        }

        public override FormattedPartVisitor Visit(ThinkingPart part)
        {
            builder.Append("[thinking] ").Append(part.Text);
            return this;
        }

        public override FormattedPartVisitor Visit(ToolCallPart part)
        {
            // GetRawText() allocates a string each call; this is the only call site in
            // the formatter, so the cost is one allocation per tool-call part per
            // summarization — acceptable for compaction (runs rarely).
            builder.Append("[tool_call:").Append(part.ToolName).Append("] ").Append(part.Args.GetRawText());
            return this;
        }

        /// <summary>
        ///     File parts carry no summarizable prose — the path and MIME type are
        ///     noise in a conversation summary. Explicitly a no-op so a future
        ///     subtype can never be mistaken for this one.
        /// </summary>
        public override FormattedPartVisitor Visit(FilePart part) => this;
    }
}
