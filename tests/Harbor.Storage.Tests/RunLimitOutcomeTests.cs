using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Storage.Memory;

namespace Harbor.Storage.Tests;

/// <summary>
///     B2.2 (#403): the read-model side of a limit stop. A run cut short by a
///     limit reconstructs as <see cref="RunStopReason.LimitExceeded" /> and
///     carries WHICH limit — it is not a success, not a failure, and not a
///     cancellation.
/// </summary>
/// <remarks>
///     <para>
///         These tests are the downstream half of
///         <c>RunLimitStampingTests</c> (the loop half). They exist separately
///         because the two can rot apart: if the loop stops stamping, the
///         reconstruction tests still pass, because they construct the terminal
///         event themselves. The pair is what covers the seam.
///     </para>
///     <para>
///         <b>The capacity claim, which is the actual defect.</b> A step-capped
///         run and a run that finished its work leave STRUCTURALLY IDENTICAL
///         message history — the capped run's last assistant message is a
///         tool-use turn, exactly like the last turn of a run that then
///         finished. So no amount of reading the transcript can tell them apart,
///         and the pre-fix chain answered <c>Succeeded</c> to both. Every test
///         below therefore drives the verdict from the terminal fact and not
///         from the messages; that is the property, and it is what
///         <c>nonvacuity403.py</c> models as a control.
///     </para>
///     <para>
///         <b>What this does NOT claim.</b> The record says "the step budget
///         ended this run". It does not say how far the run got, because the
///         turn count is not recoverable from the store — compaction, retries
///         and steering all add messages, and a derived number would be a
///         plausible value standing in for a fact nobody recorded, which is the
///         #993 shape. The transcript is where the progress is; the record is
///         where the reason is.
///     </para>
/// </remarks>
public class RunLimitOutcomeTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static JsonElement JsonArgs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    ///     The history a step-capped run leaves behind. Deliberately identical
    ///     in shape to a run that finished — see the remarks on the class.
    /// </summary>
    private static List<AgentMessage> CappedRunHistory(string sessionId)
    {
        const string toolCallId = "tc-cap-1";
        return
        [
            new UserMessage($"u-1-{Guid.NewGuid():N}", sessionId, BaseTime, "do the thing", "code", "kilo-auto"),
            new AssistantMessage(
                $"a-1-{Guid.NewGuid():N}", sessionId, BaseTime.AddSeconds(1),
                [new ToolCallPart(toolCallId, "read", JsonArgs("""{"path":"README.md"}"""))],
                StopReason.ToolUse, new Usage(10, 5), "kilo-auto"),
            new ToolResultMessage(
                $"t-1-{Guid.NewGuid():N}", sessionId, BaseTime.AddSeconds(2),
                [new ToolResultEntry(toolCallId, "read", "file contents", false)])
        ];
    }

    private static async Task<(string SessionId, IReadOnlyList<AgentMessage> Messages)> StoredAsync()
    {
        var store = new MemorySessionStore();
        var session = await store.CreateAsync("/proj", "code", "kilocode", "kilo-auto");
        await Assert.That(session.IsSuccess).IsTrue();

        foreach (AgentMessage message in CappedRunHistory(session.Value.Id))
            await store.AppendMessageAsync(session.Value.Id, message);

        var stored = await store.GetMessagesAsync(session.Value.Id);
        await Assert.That(stored.IsSuccess).IsTrue();
        return (session.Value.Id, stored.Value);
    }

    [Test]
    public async Task Reconstruct_StepCappedRun_ReportsLimitExceededWithItsKind()
    {
        var (sessionId, messages) = await StoredAsync();
        var runId = RunId.New();

        var outcome = RunOutcome.Reconstruct(
            runId, sessionId, messages,
            new AgentEndEvent(messages, Limit: RunLimitKind.MaxSteps));

        await Assert.That(outcome.StopReason).IsEqualTo(RunStopReason.LimitExceeded);
        await Assert.That(outcome.Limit).IsEqualTo(RunLimitKind.MaxSteps);
        // A limit is not an error: no error text is invented for it.
        await Assert.That(outcome.ErrorMessage).IsNull();
    }

    [Test]
    public async Task Reconstruct_WallClockCappedRun_ReportsLimitExceededWithItsOwnKind()
    {
        // The two kinds must not collapse into one another: a user who hit the
        // clock and a user who hit the step cap are told different things, and
        // only one of them is "you can raise MaxSteps".
        var (sessionId, messages) = await StoredAsync();
        var runId = RunId.New();

        var outcome = RunOutcome.Reconstruct(
            runId, sessionId, messages,
            new AgentEndEvent(messages, Limit: RunLimitKind.Timeout));

        await Assert.That(outcome.StopReason).IsEqualTo(RunStopReason.LimitExceeded);
        await Assert.That(outcome.Limit).IsEqualTo(RunLimitKind.Timeout);
        await Assert.That(outcome.Limit).IsNotEqualTo(RunLimitKind.MaxSteps);
    }

    [Test]
    public async Task Reconstruct_SameHistoryAndDifferentTerminalFact_DifferentVerdicts()
    {
        // THE capacity claim. Identical messages, two different terminal facts,
        // two different verdicts. Before the fix both were Succeeded, so the
        // transcript carried nothing that could have distinguished them.
        var (sessionId, messages) = await StoredAsync();

        var capped = RunOutcome.Reconstruct(
            RunId.New(), sessionId, messages, new AgentEndEvent(messages, Limit: RunLimitKind.MaxSteps));
        var finished = RunOutcome.Reconstruct(
            RunId.New(), sessionId, messages, new AgentEndEvent(messages));

        await Assert.That(capped.StopReason).IsEqualTo(RunStopReason.LimitExceeded);
        await Assert.That(finished.StopReason).IsEqualTo(RunStopReason.Succeeded);
        await Assert.That(finished.Limit).IsNull();
        await Assert.That(capped.StopReason).IsNotEqualTo(finished.StopReason);
    }

    [Test]
    public async Task Reconstruct_TheFourTerminalFacts_ProduceFourDistinctStopReasons()
    {
        // #403: timeout-exhausted, step-exhausted, user-cancelled and errored
        // runs must be four distinguishable outcomes, and none may collapse
        // into another. Driven pairwise, so a collapse cannot hide behind an
        // ordering.
        var (sessionId, messages) = await StoredAsync();

        RunStopReason VerdictOf(AgentEndEvent end, AgentErrorEvent? error = null) =>
            RunOutcome.Reconstruct(RunId.New(), sessionId, messages, end, error).StopReason;

        var succeeded = VerdictOf(new AgentEndEvent(messages));
        var failed = VerdictOf(new AgentEndEvent(messages), new AgentErrorEvent("provider exploded"));
        var stopped = VerdictOf(new AgentEndEvent(messages, Cancelled: true));
        var limited = VerdictOf(new AgentEndEvent(messages, Limit: RunLimitKind.MaxSteps));

        var all = new[] { succeeded, failed, stopped, limited };
        var distinct = new HashSet<RunStopReason>(all);
        await Assert.That(distinct.Count).IsEqualTo(4);
        await Assert.That(succeeded).IsEqualTo(RunStopReason.Succeeded);
        await Assert.That(failed).IsEqualTo(RunStopReason.Failed);
        await Assert.That(stopped).IsEqualTo(RunStopReason.Stopped);
        await Assert.That(limited).IsEqualTo(RunStopReason.LimitExceeded);
    }

    [Test]
    public async Task Reconstruct_LimitStop_IsNotFailedAndCarriesNoErrorText()
    {
        // A limit is a bound the user set, not a malfunction. If it read as
        // Failed, the run's own record would blame the system for the user's
        // own ceiling.
        var (sessionId, messages) = await StoredAsync();

        var outcome = RunOutcome.Reconstruct(
            RunId.New(), sessionId, messages,
            new AgentEndEvent(messages, Limit: RunLimitKind.MaxSteps));

        await Assert.That(outcome.StopReason).IsNotEqualTo(RunStopReason.Failed);
        await Assert.That(outcome.ErrorMessage).IsNull();
        // …and the work that DID happen is still reported. A limit stop is
        // incomplete, not empty: suppressing the partial history would be the
        // same plausible value in the other direction.
        await Assert.That(outcome.MessageIds.Count).IsEqualTo(messages.Count);
        await Assert.That(outcome.ToolCalls.Count).IsEqualTo(1);
    }
}
