using CSharpFunctionalExtensions;
using Harbor.Application.Sessions;
using Harbor.ChangeTracking;

namespace Harbor.ChangeTracking.Tests;

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

    // #1770: the three illegal-transition copies differed only in the pair of
    // states and the reason fragment — the asserts never changed. They are the
    // table now, and every row keeps its original test name as the display
    // name.
    [Test]
    [Arguments(RunState.Checked, RunState.Accepted, "cannot apply a run that is not Reported", DisplayName = "AcceptBeforeReported_NamesTheReason")]
    [Arguments(RunState.Isolated, RunState.Rejected, "cannot reject a run that is not Reported", DisplayName = "RejectBeforeReported_NamesTheReason")]
    [Arguments(RunState.Pinned, RunState.Reported, "Illegal run transition", DisplayName = "SkipAhead_IsRejected")]
    public async Task IllegalTransition_NamesTheReason(RunState from, RunState to, string expectedReason)
    {
        Result<RunState> step = RunChangeTransitions.TryTransition(from, to);
        await Assert.That(step.IsFailure).IsTrue();
        await Assert.That(step.Error.Contains(expectedReason)).IsTrue();
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

    // #1484: the verdict, reason and terminal flag now live on per-state
    // objects. A new state is one new class plus one table registration —
    // no existing state class is edited. A synthetic enum value stands in
    // for a future lifecycle state; RunState itself is part of the shared
    // vocabulary in Harbor.Application.Sessions and is out of scope here.
    private const RunState ProbeState = (RunState)9001;

    private sealed class ProbeRunState : IRunState
    {
        public RunState Value => ProbeState;

        public IReadOnlyCollection<RunState> LegalTargets => [RunState.Reported];

        public bool IsTerminal => false;

        public string RefusalReason(RunState to) => $"the probe refuses {to}";
    }

    [Test]
    public async Task ProbeState_AddsANewStateWithoutEditingExistingOnes()
    {
        RunTransitionTable table = RunTransitionTable.CreateDefault();
        RunTransitionTable withProbe = new([.. table.States, new ProbeRunState()]);

        Result<RunState> legal = withProbe.TryTransition(ProbeState, RunState.Reported);
        await Assert.That(legal.IsSuccess).IsTrue();
        await Assert.That(legal.Value).IsEqualTo(RunState.Reported);

        // The probe's own reason text is used, and the shared static facade
        // still answers for the built-in states — the probe is additive.
        Result<RunState> refused = withProbe.TryTransition(ProbeState, RunState.Rejected);
        await Assert.That(refused.IsFailure).IsTrue();
        await Assert.That(refused.Error.Contains("the probe refuses Rejected")).IsTrue();

        await Assert.That(withProbe.IsTerminal(ProbeState)).IsFalse();
        await Assert.That(RunChangeTransitions.IsTerminal(RunState.Accepted)).IsTrue();
        Result<RunState> untouched =
            RunChangeTransitions.TryTransition(RunState.Reported, RunState.Rejected);
        await Assert.That(untouched.IsSuccess).IsTrue();
    }
}
