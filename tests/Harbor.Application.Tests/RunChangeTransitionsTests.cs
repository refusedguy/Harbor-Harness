using CSharpFunctionalExtensions;
using Harbor.Application.Sessions;

namespace Harbor.Application.Tests;

/// <summary>
///     S9 slice 1 (#397): the run lifecycle is one explicit state machine.
///     Legal steps validate; illegal steps fail with a named reason. Pure
///     logic — no git, no harbor home, runs anywhere.
/// </summary>
public class RunChangeTransitionsTests
{
    [Test]
    public async Task FullChain_PinnedToAccepted_IsLegal()
    {
        RunState[] chain =
        [
            RunState.Pinned,
            RunState.Isolated,
            RunState.Changed,
            RunState.Checked,
            RunState.Reported,
            RunState.Accepted,
        ];
        for (int i = 0; i < chain.Length - 1; i++)
        {
            Result<RunState> step = RunChangeTransitions.TryTransition(chain[i], chain[i + 1]);
            await Assert.That(step.IsSuccess).IsTrue();
            await Assert.That(step.Value).IsEqualTo(chain[i + 1]);
        }
    }

    [Test]
    public async Task ReportedToRejected_IsLegal()
    {
        Result<RunState> step = RunChangeTransitions.TryTransition(RunState.Reported, RunState.Rejected);
        await Assert.That(step.IsSuccess).IsTrue();
        await Assert.That(step.Value).IsEqualTo(RunState.Rejected);
    }

    [Test]
    public async Task IsolatedToReleased_And_RejectedToReleased_AreLegal()
    {
        Result<RunState> first = RunChangeTransitions.TryTransition(RunState.Isolated, RunState.Released);
        await Assert.That(first.IsSuccess).IsTrue();
        Result<RunState> second = RunChangeTransitions.TryTransition(RunState.Rejected, RunState.Released);
        await Assert.That(second.IsSuccess).IsTrue();
    }

    [Test]
    public async Task AcceptBeforeReported_NamesTheReason()
    {
        Result<RunState> step = RunChangeTransitions.TryTransition(RunState.Checked, RunState.Accepted);
        await Assert.That(step.IsFailure).IsTrue();
        await Assert.That(step.Error.Contains("cannot apply a run that is not Reported")).IsTrue();
    }

    [Test]
    public async Task RejectBeforeReported_NamesTheReason()
    {
        Result<RunState> step = RunChangeTransitions.TryTransition(RunState.Isolated, RunState.Rejected);
        await Assert.That(step.IsFailure).IsTrue();
        await Assert.That(step.Error.Contains("cannot reject a run that is not Reported")).IsTrue();
    }

    [Test]
    public async Task SkipAhead_IsRejected()
    {
        Result<RunState> step = RunChangeTransitions.TryTransition(RunState.Pinned, RunState.Reported);
        await Assert.That(step.IsFailure).IsTrue();
        await Assert.That(step.Error.Contains("Illegal run transition")).IsTrue();
    }

    [Test]
    public async Task TerminalStates_HaveNoOutgoingTransitions()
    {
        await Assert.That(RunChangeTransitions.IsTerminal(RunState.Accepted)).IsTrue();
        await Assert.That(RunChangeTransitions.IsTerminal(RunState.Released)).IsTrue();
        await Assert.That(RunChangeTransitions.IsTerminal(RunState.Reported)).IsFalse();

        Result<RunState> fromAccepted =
            RunChangeTransitions.TryTransition(RunState.Accepted, RunState.Rejected);
        await Assert.That(fromAccepted.IsFailure).IsTrue();
        await Assert.That(fromAccepted.Error.Contains("terminal")).IsTrue();

        Result<RunState> fromReleased =
            RunChangeTransitions.TryTransition(RunState.Released, RunState.Isolated);
        await Assert.That(fromReleased.IsFailure).IsTrue();
        await Assert.That(fromReleased.Error.Contains("terminal")).IsTrue();
    }
}
