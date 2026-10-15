namespace Harbor.Application.Agents;

/// <summary>
///     Message-stream flush choreography extracted from
///     <see cref="TurnRunner" /> (#1101, slice 1): flush pending text/thinking
///     buffers from a <see cref="StreamingCoalescer" /> into an
///     <see cref="AssistantMessage" />. Verbatim move — the loop's private
///     helper bodies, unchanged.
/// </summary>
internal static class MessageFlush
{
    /// <summary>Flush a pending text run into the message.</summary>
    internal static AssistantMessage FlushText(StreamingCoalescer coalescer, AssistantMessage partial) =>
        coalescer.HasPendingText ? partial.AppendText(coalescer.FlushText()) : partial;

    /// <summary>Flush a pending thinking run into the message.</summary>
    internal static AssistantMessage FlushThinking(StreamingCoalescer coalescer, AssistantMessage partial) =>
        coalescer.HasPendingThinking ? partial.AppendThinking(coalescer.FlushThinking()) : partial;

    /// <summary>
    ///     Flush both buffer kinds in wire order (text first, then thinking) —
    ///     the sequence every non-delta arm and the abort path must perform
    ///     (ROP-C П.3: five copies of this dance collapse to one helper).
    /// </summary>
    internal static AssistantMessage FlushAll(StreamingCoalescer coalescer, AssistantMessage partial)
    {
        AssistantMessage flushedText = FlushText(coalescer, partial);
        return FlushThinking(coalescer, flushedText);
    }
}
