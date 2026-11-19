// #2680 F125. The classifier's observable contract: a fixed precedence table
// over the signal struct, and a stable indicator string per state. These are
// behaviour tests — they assert what a caller sees (the state and the text),
// not how the switch is written.

namespace Harbor.Tui.CellForge.Tests;

public class TerminalActivityClassifierTests
{
    [Test]
    public async Task Classify_NoSignals_IsIdle()
    {
        var state = TerminalActivityClassifier.Classify(TerminalActivitySignals.None);

        await Assert.That(state).IsEqualTo(TerminalActivityState.Idle);
    }

    [Test]
    public async Task Classify_BusyOnly_IsBusy()
    {
        var state = TerminalActivityClassifier.Classify(new TerminalActivitySignals { IsBusy = true });

        await Assert.That(state).IsEqualTo(TerminalActivityState.Busy);
    }

    [Test]
    public async Task Classify_RetryingOnly_IsRetrying()
    {
        var state = TerminalActivityClassifier.Classify(new TerminalActivitySignals { IsRetrying = true });

        await Assert.That(state).IsEqualTo(TerminalActivityState.Retrying);
    }

    [Test]
    public async Task Classify_WaitingForUserOnly_IsWaiting()
    {
        var state = TerminalActivityClassifier.Classify(new TerminalActivitySignals { IsWaitingForUser = true });

        await Assert.That(state).IsEqualTo(TerminalActivityState.Waiting);
    }

    [Test]
    public async Task Classify_HasErrorOnly_IsError()
    {
        var state = TerminalActivityClassifier.Classify(new TerminalActivitySignals { HasError = true });

        await Assert.That(state).IsEqualTo(TerminalActivityState.Error);
    }

    [Test]
    public async Task Classify_FinishedOnly_IsDone()
    {
        var state = TerminalActivityClassifier.Classify(new TerminalActivitySignals { IsFinished = true });

        await Assert.That(state).IsEqualTo(TerminalActivityState.Done);
    }

    [Test]
    public async Task Classify_AllSignalsSet_ErrorWins()
    {
        var signals = new TerminalActivitySignals
        {
            IsBusy = true,
            IsRetrying = true,
            IsWaitingForUser = true,
            HasError = true,
            IsFinished = true,
        };

        var state = TerminalActivityClassifier.Classify(signals);

        await Assert.That(state).IsEqualTo(TerminalActivityState.Error);
    }

    [Test]
    public async Task Classify_WaitingWithBusyAndRetrying_WaitingWins()
    {
        var signals = new TerminalActivitySignals
        {
            IsBusy = true,
            IsRetrying = true,
            IsWaitingForUser = true,
        };

        var state = TerminalActivityClassifier.Classify(signals);

        await Assert.That(state).IsEqualTo(TerminalActivityState.Waiting);
    }

    [Test]
    public async Task Classify_RetryingWithBusy_RetryingWins()
    {
        var signals = new TerminalActivitySignals
        {
            IsBusy = true,
            IsRetrying = true,
        };

        var state = TerminalActivityClassifier.Classify(signals);

        await Assert.That(state).IsEqualTo(TerminalActivityState.Retrying);
    }

    [Test]
    public async Task Classify_BusyWithFinished_BusyWins()
    {
        var signals = new TerminalActivitySignals
        {
            IsBusy = true,
            IsFinished = true,
        };

        var state = TerminalActivityClassifier.Classify(signals);

        await Assert.That(state).IsEqualTo(TerminalActivityState.Busy);
    }

    [Test]
    [Arguments(TerminalActivityState.Idle, "○ idle")]
    [Arguments(TerminalActivityState.Busy, "● busy")]
    [Arguments(TerminalActivityState.Retrying, "↻ retrying")]
    [Arguments(TerminalActivityState.Waiting, "? attention")]
    [Arguments(TerminalActivityState.Error, "✖ error")]
    [Arguments(TerminalActivityState.Done, "✓ done")]
    public async Task Format_EachState_HasStableIndicator(TerminalActivityState state, string expected)
    {
        var text = TerminalActivityClassifier.Format(state);

        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    public async Task Format_UnknownStateValue_FallsBackToIdleIndicator()
    {
        var text = TerminalActivityClassifier.Format((TerminalActivityState)200);

        await Assert.That(text).IsEqualTo("○ idle");
    }
}
