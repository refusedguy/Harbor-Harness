// Arbitrary.cs builds seeded agent-event transcripts (#2704 D02).
//
// WHY THIS EXISTS
// ---------------
// The structural assertion in EventTranscriptAssert is only useful if it is
// exercised on more than the three hand-written streams in its own tests. A
// single fixture cannot show that the assertion accepts the shape of a real
// run, where tool calls interleave with message deltas and compaction. A
// generator gives those tests a whole family of well-formed transcripts from
// one seed, and the seed makes any failure replayable.
//
// The events are built with the TestEvents builders, so this file adds no
// second event model and no product assembly to its consumers. Every generated
// transcript satisfies EventTranscriptAssert by construction: starts precede
// ends, tool-call ids are unique, and every end closes an open call.
//
// WHAT COUNT MEANS
// ----------------
// `count` is the number of turns, not the number of events. Each turn always
// contributes a TurnStart and a TurnEnd, so a stream of `count` turns has at
// least 2 * count + 2 events (the run's AgentStart and AgentEnd included).
// Optional message, tool, compaction and bookkeeping events add to that.

using Harbor.Abstractions.Events;

namespace Harbor.TestKit;

/// <summary>
///     Builds deterministic test data from a <see cref="Random" /> instance.
/// </summary>
public static class Arbitrary
{
    /// <summary>
    ///     Builds a well-formed agent-event transcript for <paramref name="count" />
    ///     turns from the current state of <paramref name="rng" />.
    /// </summary>
    /// <param name="rng">
    ///     The source of randomness. Two instances seeded with the same value
    ///     produce the same transcript, so a failing walk can be replayed.
    /// </param>
    /// <param name="count">
    ///     The number of turns. Zero returns an empty transcript; a negative
    ///     value is rejected.
    /// </param>
    /// <returns>The transcript in publish order.</returns>
    public static IReadOnlyList<AgentEvent> EventStream(Random rng, int count)
    {
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var events = new List<AgentEvent>();
        if (count == 0)
            return events;

        const string sessionId = "s1";
        events.Add(TestEvents.AgentStart(sessionId));

        for (int turn = 0; turn < count; turn++)
        {
            events.Add(TestEvents.TurnStart(turn + 1, sessionId));

            if (rng.Next(2) == 0)
                AppendMessage(events, rng);

            if (rng.Next(2) == 0)
                AppendToolExecution(events, rng, turn);

            if (rng.Next(4) == 0)
                AppendCompaction(events, turn);

            if (rng.Next(2) == 0)
                events.Add(TestEvents.SessionStats(sessionId));

            if (rng.Next(3) == 0)
                events.Add(TestEvents.SessionChanged(sessionId));

            if (rng.Next(4) == 0)
                events.Add(TestEvents.AgentError("arbitrary-error"));

            events.Add(TestEvents.TurnEnd(sessionId: sessionId));
        }

        events.Add(TestEvents.AgentEnd());
        return events;
    }

    private static void AppendMessage(List<AgentEvent> events, Random rng)
    {
        events.Add(TestEvents.MessageStart());
        int deltas = rng.Next(3);
        for (int i = 0; i < deltas; i++)
        {
            events.Add(TestEvents.TextDeltaUpdate(id: "t", delta: $"chunk-{i}"));
        }

        events.Add(TestEvents.MessageEnd());
    }

    private static void AppendToolExecution(List<AgentEvent> events, Random rng, int turn)
    {
        string toolCallId = $"tc-{turn}-{rng.Next(1000, 10000)}";
        string argsJson = $"{{\"n\":{rng.Next()}}}";
        events.Add(TestEvents.ToolExecutionStart(toolCallId, "read", argsJson));

        int updates = rng.Next(3);
        for (int i = 0; i < updates; i++)
        {
            events.Add(TestEvents.ToolExecutionUpdate(toolCallId, $"progress-{i}"));
        }

        events.Add(TestEvents.ToolExecutionEnd(toolCallId));
    }

    private static void AppendCompaction(List<AgentEvent> events, int turn)
    {
        string compactionSessionId = $"compact-{turn}";
        events.Add(TestEvents.CompactionStarted(compactionSessionId));
        events.Add(TestEvents.CompactionCompleted(compactionSessionId));
    }
}
