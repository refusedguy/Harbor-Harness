using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     #1024, print half: a run stopped by a limit must SAY so in the
///     transcript. <c>AgentEndEvent.Limit</c> is the terminal fact every reader
///     gets — without this arm a wall-clock stop (and a step stop) would read
///     as a silent <see cref="SessionStatus.Done" />.
/// </summary>
/// <remarks>
///     <para>
///         The verdict itself stays <see cref="SessionStatus.Done" />, the same
///         precedent the step budget (#1011) set: the loop reached its terminal
///         event without a failure, and <see cref="SessionStatus" /> has no
///         limit member to reach for (adding one would be a new user-visible
///         axis). What must NOT happen is the cancel collapse — a limit reading
///         as <see cref="SessionStatus.Aborted" /> — or the failure collapse
///         into <see cref="SessionStatus.Error" />. Both are pinned below.
///     </para>
///     <para>
///         Non-vacuity: <see cref="AgentEnd_WithoutLimit_PrintsNoLine" /> pins
///         the field to absent on the normal path, so the line is produced by
///         the limit and not by the fact of ending (#591).
///     </para>
/// </remarks>
public class RunLimitPrintTests
{
    [Test]
    public async Task AgentEnd_WithTimeoutLimit_PrintsWallClockLineAndIsNotAborted()
    {
        var ended = ChatAppReducer.Update(
            new UiState(), new ChatAppMsg.Agent(new AgentEndEvent([], Limit: RunLimitKind.Timeout))).State;

        await Assert.That(ended.Chat.SessionStatus).IsEqualTo(SessionStatus.Done);
        await Assert.That(ended.Chat.SessionStatus).IsNotEqualTo(SessionStatus.Aborted);
        await Assert.That(ended.Chat.Status).IsEqualTo("idle");
        await Assert.That(ended.Chat.IsAgentRunning).IsFalse();
        await Assert.That(ended.Chat.Lines.Length).IsEqualTo(1);
        await Assert.That(ended.Chat.Lines[0].Role).IsEqualTo(ChatRole.System);
        await Assert.That(ended.Chat.Lines[0].Text).Contains("wall-clock");
    }

    [Test]
    public async Task AgentEnd_WithMaxStepsLimit_PrintsStepLineAndIsNotAborted()
    {
        var ended = ChatAppReducer.Update(
            new UiState(), new ChatAppMsg.Agent(new AgentEndEvent([], Limit: RunLimitKind.MaxSteps))).State;

        await Assert.That(ended.Chat.SessionStatus).IsEqualTo(SessionStatus.Done);
        await Assert.That(ended.Chat.SessionStatus).IsNotEqualTo(SessionStatus.Aborted);
        await Assert.That(ended.Chat.Lines.Length).IsEqualTo(1);
        await Assert.That(ended.Chat.Lines[0].Role).IsEqualTo(ChatRole.System);
        await Assert.That(ended.Chat.Lines[0].Text).Contains("step");
    }

    [Test]
    public async Task AgentEnd_WithoutLimit_PrintsNoLine()
    {
        // NON-VACUITY. A run that did its work must leave the transcript
        // alone, or the assertions above would hold on any tree that ends.
        var ended = ChatAppReducer.Update(
            new UiState(), new ChatAppMsg.Agent(new AgentEndEvent([]))).State;

        await Assert.That(ended.Chat.SessionStatus).IsEqualTo(SessionStatus.Done);
        await Assert.That(ended.Chat.Lines).IsEmpty();
    }
}
