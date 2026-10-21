// TranscriptIntegrity.cs — what a cold read found at the tail of a session's
// transcript (#1744).
//
// WHY THIS EXISTS
// ---------------
// A session log is written by a process that can die mid-write. When it does,
// the log can end in two different shapes, and the difference matters to
// whoever reads it next:
//
//   1. a BROKEN RECORD — the bytes of a record are not a complete JSON
//      document, because the write was cut. The record's content is
//      unknowable: there is nothing to salvage from half a JSON object, so
//      the read skips it. What it must NOT do is skip it SILENTLY: a message
//      the user wrote (or the model produced) that vanishes without a trace
//      reads like "the agent never said that", and the artifact then lies
//      about what happened.
//
//   2. an UNCLOSED TURN — the record is intact, but the TURN is not: the
//      assistant asked for tools and the results never landed. The turn is
//      half a turn, and a transcript that carries one is invalid for a
//      provider (Anthropic wants every tool_use answered in the next
//      message). #1751 closes such a turn on the WRITE side, for the turns
//      this build runs; this file is the READ side, for the turns a process
//      never got to close.
//
// The two are different facts and are reported separately: "a record was
// unreadable" says nothing about the turn, and "the turn is not closed"
// says nothing about the bytes. One counter and one flag would have hidden
// one of them.
//
// NOTHING IS WRITTEN BACK
// -----------------------
// The read never repairs the log. It reports what it could not deliver and
// closes the turn IN MEMORY (see TranscriptInspector.CloseUnclosedToolCalls),
// because a read that rewrote the file would be a second writer racing the
// live one.

namespace Harbor.Abstractions.Models;

/// <summary>
///     What a cold read found at the tail of a session's transcript (#1744).
/// </summary>
/// <remarks>
///     <para>
///         The integrity of a transcript is a property of the READ, not of the
///         session: the same log read twice gives the same answer, and a
///         session whose log is whole has a complete integrity whatever the
///         caller does with it.
///     </para>
///     <para>
///         A complete transcript (<see cref="IsComplete" />) is one where every
///         record decoded and every turn it holds is closed. Anything else is
///         named here, so the caller can decide what to do with the gap
///         instead of discovering it as a missing message.
///     </para>
/// </remarks>
/// <param name="UnreadableRecordCount">
///     How many records the read could not decode. Each one is a record whose
///     bytes are not a complete document — the write was cut, or the record is
///     corrupt. The content of such a record is unknowable, so it is skipped;
///     the count is what makes the skip visible.
/// </param>
/// <param name="IsTailTruncated">
///     True when the LAST record of the log is one of the unreadable ones — a
///     write that was cut at the tail, which is the shape a killed process
///     leaves behind. False when the unreadable records sit earlier in the log
///     (a record lost to corruption, not to a crash at the end).
/// </param>
/// <param name="UnclosedToolCalls">
///     The tool calls the transcript asked for and never got results for. An
///     empty list means every turn in the transcript is closed.
/// </param>
public sealed record TranscriptIntegrity(
    int UnreadableRecordCount,
    bool IsTailTruncated,
    IReadOnlyList<ToolCallPart> UnclosedToolCalls)
{
    /// <summary>
    ///     True when the read delivered the whole transcript: no record was
    ///     skipped and every turn it holds is closed.
    /// </summary>
    public bool IsComplete => UnreadableRecordCount == 0 && UnclosedToolCalls.Count == 0;
}

/// <summary>
///     The pure inspection of a transcript: which tool calls it never closed,
///     and how to close them in memory (#1744).
/// </summary>
/// <remarks>
///     <para>
///         Pure functions over a message list — no I/O, no store, no clock.
///         The caller supplies the already-read transcript, which is what
///         keeps the read side of this feature testable without a file and
///         keeps the balance a decision the caller can see.
///     </para>
///     <para>
///         <b>One owner for the model-facing text.</b> The closer this builds
///         hands the model the same words the runtime's own repair hands it
///         (<see cref="UnresolvedToolCall" />), so a transcript closed by a
///         read and a transcript closed by the writer are closed in the same
///         vocabulary.
///     </para>
/// </remarks>
public static class TranscriptInspector
{
    /// <summary>
    ///     The tool calls the transcript opened and never closed: every
    ///     <see cref="ToolCallPart" /> of an assistant message whose call id
    ///     no <see cref="ToolResultMessage" /> answers.
    /// </summary>
    /// <param name="messages">The transcript, in chronological order.</param>
    /// <returns>
    ///     The unanswered calls, in the order the assistant messages asked for
    ///     them. Empty when the transcript is closed.
    /// </returns>
    public static IReadOnlyList<ToolCallPart> FindUnclosedToolCalls(IReadOnlyList<AgentMessage> messages)
    {
        var answered = CollectAnsweredCallIds(messages);

        var unclosed = new List<ToolCallPart>();
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not AssistantMessage assistant)
            {
                continue;
            }

            for (int p = 0; p < assistant.Parts.Count; p++)
            {
                if (assistant.Parts[p] is ToolCallPart call && !answered.Contains(call.Id))
                {
                    unclosed.Add(call);
                }
            }
        }

        return unclosed;
    }

    /// <summary>
    ///     Close the unclosed tool calls IN MEMORY: for every assistant
    ///     message whose calls went unanswered, one tool-result message
    ///     follows it, answering each of those calls with the outcome-unknown
    ///     code. The transcript then folds as a closed one — and nothing is
    ///     written back: the log on disk still ends where it ended.
    /// </summary>
    /// <remarks>
    ///     The closer sits directly AFTER the assistant message that opened the
    ///     calls, which is where a provider wants the answer and where a
    ///     reader expects it. Its id is derived from the assistant message's
    ///     id, so the same transcript always closes to the same one — a read
    ///     is deterministic, and two reads of one file agree.
    /// </remarks>
    /// <param name="messages">The transcript, in chronological order.</param>
    /// <returns>
    ///     The closed transcript, or the very list that was passed in when
    ///     there was nothing to close.
    /// </returns>
    public static IReadOnlyList<AgentMessage> CloseUnclosedToolCalls(IReadOnlyList<AgentMessage> messages)
    {
        var answered = CollectAnsweredCallIds(messages);

        // First pass: the closers, each with the index of the message it
        // follows. Nothing is copied until there is something to copy, so a
        // transcript with nothing to close is handed back as it came in.
        var closers = new List<(int Index, ToolResultMessage Closer)>();
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not AssistantMessage assistant)
            {
                continue;
            }

            ToolResultMessage? closer = BuildCloser(assistant, answered);
            if (closer is not null)
            {
                closers.Add((i, closer));
            }
        }

        if (closers.Count == 0)
        {
            return messages;
        }

        // Second pass: the closed transcript, with each closer right after
        // the assistant message that opened the calls.
        var closed = new List<AgentMessage>(messages.Count + closers.Count);
        int next = 0;
        for (int i = 0; i < messages.Count; i++)
        {
            closed.Add(messages[i]);
            if (next < closers.Count && closers[next].Index == i)
            {
                closed.Add(closers[next].Closer);
                next++;
            }
        }

        return closed;
    }

    /// <summary>
    ///     The ids of the tool calls a result answers. A call id answered by
    ///     ANY result in the transcript counts as answered — the fold does not
    ///     care which message carried the answer.
    /// </summary>
    private static HashSet<string> CollectAnsweredCallIds(IReadOnlyList<AgentMessage> messages)
    {
        var answered = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not ToolResultMessage results)
            {
                continue;
            }

            for (int j = 0; j < results.Results.Count; j++)
            {
                answered.Add(results.Results[j].ToolCallId);
            }
        }

        return answered;
    }

    /// <summary>
    ///     The closer for one assistant message, or null when it asked for no
    ///     tool or every call it asked for was answered.
    /// </summary>
    private static ToolResultMessage? BuildCloser(AssistantMessage assistant, HashSet<string> answered)
    {
        List<ToolResultEntry>? results = null;
        for (int p = 0; p < assistant.Parts.Count; p++)
        {
            if (assistant.Parts[p] is not ToolCallPart call || answered.Contains(call.Id))
            {
                continue;
            }

            results ??= new List<ToolResultEntry>();
            // #1760: the closure carries the structured code too, so a policy
            // reads the code and not the text. The text already leads with it.
            results.Add(new ToolResultEntry(
                call.Id,
                call.ToolName,
                UnresolvedToolCall.OutcomeUnknownText(call.ToolName),
                true)
            {
                ErrorInfo = ToolErrorCodes.Create(UnresolvedToolCall.OutcomeUnknownCode),
            });
        }

        if (results is null)
        {
            return null;
        }

        return new ToolResultMessage(
            $"unclosed-{assistant.Id}",
            assistant.SessionId,
            assistant.CreatedAt,
            results,
            assistant.Id);
    }
}
