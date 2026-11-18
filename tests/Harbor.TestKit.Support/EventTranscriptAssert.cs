// EventTranscriptAssert.cs — one call that proves an agent-event transcript is
// structurally possible (#2704 E03).
//
// WHY THIS EXISTS
// ---------------
// A test that asserts "I saw a ToolExecutionEnd" proves less than it looks: a
// renderer can emit an End with no Start, two Ends for one Start, or an End
// whose ToolCallId belongs to a different call, and every count-based assertion
// still passes. The failures are real (the UI draws a result card for work
// that never started) but they hide behind the event that DID arrive.
//
// aspnetcore's EventAssert and zed's observer tests check the SHAPE of the
// whole stream, not the presence of one member. This is the Harbor equivalent:
// Start before End, every End paired with exactly one Start, no orphan updates.
//
// WHAT IS AND IS NOT CHECKED
// --------------------------
// Tool executions are correlated by their stable ToolCallId. Message, turn,
// run and compaction lifetimes are matched by KIND, not by id: the id inside a
// streamed MessageStart and the finalized MessageEnd need not be the same
// object (the live path commits the streamed buffer into a new message), so
// id-matching them would produce false failures on a healthy stream.
//
// Unclosed Starts are NOT violations. A cancellation or a mid-stream error
// legitimately ends a run before a tool or a message closes; forbidding that
// would turn this helper into a second cancellation contract. The invariant
// here is one-directional on purpose: nothing may END that did not START.

using System.Text;
using Harbor.Abstractions.Events;

namespace Harbor.TestKit;

/// <summary>Raised by <see cref="EventTranscriptAssert.AssertWellFormed" />.</summary>
/// <remarks>
///     Every violation found in the transcript is reported at once, so a single
///     failed scenario shows the whole structural problem instead of the first
///     symptom.
/// </remarks>
public sealed class EventTranscriptException : Exception
{
    /// <summary>Creates an exception with no recorded violations.</summary>
    public EventTranscriptException()
        : base("Agent-event transcript is not well formed.") { }

    /// <summary>Creates an exception with a caller-supplied message.</summary>
    public EventTranscriptException(string message)
        : base(message) { }

    /// <summary>Creates an exception with a caller-supplied message and cause.</summary>
    public EventTranscriptException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Creates an exception carrying the full list of violations.</summary>
    public EventTranscriptException(IReadOnlyList<string> violations)
        : base(Describe(violations)) => Violations = violations;

    /// <summary>The structural violations, in transcript order.</summary>
    public IReadOnlyList<string> Violations { get; } = [];

    private static string Describe(IReadOnlyList<string> violations)
    {
        var message = new StringBuilder("Agent-event transcript is not well formed:");
        foreach (string violation in violations)
        {
            message.Append(Environment.NewLine).Append("  - ").Append(violation);
        }

        return message.ToString();
    }
}

/// <summary>
///     Asserts that a sequence of <see cref="AgentEvent" />s satisfies the
///     lifecycle invariants every consumer of the event bus relies on.
/// </summary>
public static class EventTranscriptAssert
{
    /// <summary>
    ///     Checks that no End event appears without a matching earlier Start,
    ///     each tool call has exactly one Start and at most one End, and tool
    ///     updates belong to an open call.
    /// </summary>
    /// <param name="events">
    ///     The transcript, in publish order. An empty transcript is trivially
    ///     well formed.
    /// </param>
    /// <exception cref="EventTranscriptException">
    ///     At least one lifecycle invariant was broken. All violations are
    ///     listed on the exception.
    /// </exception>
    public static void AssertWellFormed(IEnumerable<AgentEvent> events)
    {
        IReadOnlyList<string> violations = FindViolations(events);
        if (violations.Count > 0)
            throw new EventTranscriptException(violations);
    }

    /// <summary>
    ///     Returns every structural violation in the transcript, in publish
    ///     order. An empty result means the transcript is well formed.
    /// </summary>
    /// <param name="events">The transcript, in publish order.</param>
    public static IReadOnlyList<string> FindViolations(IEnumerable<AgentEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var violations = new List<string>();
        var openToolCalls = new HashSet<string>(StringComparer.Ordinal);
        var seenToolCalls = new HashSet<string>(StringComparer.Ordinal);
        var openCompactions = new HashSet<string>(StringComparer.Ordinal);
        var seenCompactions = new HashSet<string>(StringComparer.Ordinal);
        int runsOpen = 0;
        int turnsOpen = 0;
        int messagesOpen = 0;
        int index = -1;

        foreach (AgentEvent item in events)
        {
            index++;
            if (item is null)
            {
                violations.Add($"[{index}] null event in transcript.");
                continue;
            }

            switch (item)
            {
                case AgentStartEvent:
                    runsOpen++;
                    break;

                case AgentEndEvent:
                    if (runsOpen == 0)
                        violations.Add($"[{index}] AgentEnd without a preceding AgentStart.");
                    else
                        runsOpen--;
                    break;

                case TurnStartEvent:
                    turnsOpen++;
                    break;

                case TurnEndEvent:
                    if (turnsOpen == 0)
                        violations.Add($"[{index}] TurnEnd without a preceding TurnStart.");
                    else
                        turnsOpen--;
                    break;

                case MessageStartEvent:
                    messagesOpen++;
                    break;

                case MessageEndEvent:
                    if (messagesOpen == 0)
                        violations.Add($"[{index}] MessageEnd without a preceding MessageStart.");
                    else
                        messagesOpen--;
                    break;

                case ToolExecutionStartEvent start:
                    if (!seenToolCalls.Add(start.ToolCallId))
                        violations.Add($"[{index}] ToolExecutionStart for '{start.ToolCallId}' reuses an earlier call id.");
                    else
                        openToolCalls.Add(start.ToolCallId);
                    break;

                case ToolExecutionUpdateEvent update:
                    if (!openToolCalls.Contains(update.ToolCallId))
                        violations.Add($"[{index}] ToolExecutionUpdate for '{update.ToolCallId}' with no open start.");
                    break;

                case ToolExecutionEndEvent end:
                    if (!openToolCalls.Remove(end.ToolCallId))
                        violations.Add($"[{index}] ToolExecutionEnd for '{end.ToolCallId}' without an open start.");
                    break;

                case CompactionStartedEvent compactionStart:
                    if (!seenCompactions.Add(compactionStart.SessionId))
                        violations.Add($"[{index}] CompactionStarted for '{compactionStart.SessionId}' reuses an earlier session id.");
                    else
                        openCompactions.Add(compactionStart.SessionId);
                    break;

                case CompactionCompletedEvent compactionDone:
                    RequireCompactionEnd(compactionDone.SessionId, "CompactionCompleted", index, openCompactions, violations);
                    break;

                case CompactionFailedEvent compactionFailed:
                    RequireCompactionEnd(compactionFailed.SessionId, "CompactionFailed", index, openCompactions, violations);
                    break;
            }
        }

        return violations;
    }

    private static void RequireCompactionEnd(
        string sessionId,
        string endKind,
        int index,
        HashSet<string> openCompactions,
        List<string> violations)
    {
        if (!openCompactions.Remove(sessionId))
            violations.Add($"[{index}] {endKind} for '{sessionId}' without an open CompactionStarted.");
    }
}
