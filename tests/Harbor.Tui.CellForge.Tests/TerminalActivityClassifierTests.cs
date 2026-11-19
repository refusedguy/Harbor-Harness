// #2680 F125. The classifier's observable contract: a fixed precedence table
// over the signal struct, and a stable indicator string per state. These are
// behaviour tests — they assert what a caller sees (the state and the text),
// not how the switch is written.

using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

public class TerminalActivityClassifierTests
{
    [Test]
    [Arguments(false, false, false, false, false, TerminalActivityState.Idle)]
    [Arguments(true, false, false, false, false, TerminalActivityState.Busy)]
    [Arguments(false, true, false, false, false, TerminalActivityState.Retrying)]
    [Arguments(false, false, true, false, false, TerminalActivityState.Waiting)]
    [Arguments(false, false, false, true, false, TerminalActivityState.Error)]
    [Arguments(false, false, false, false, true, TerminalActivityState.Done)]
    // Simultaneous signals: the loudest state wins, not the caller's flag order.
    [Arguments(true, true, true, true, true, TerminalActivityState.Error)]
    [Arguments(true, true, true, false, false, TerminalActivityState.Waiting)]
    [Arguments(true, true, false, false, false, TerminalActivityState.Retrying)]
    [Arguments(true, false, false, false, true, TerminalActivityState.Busy)]
    public async Task Classify_Signals_ResolvesToExpectedState(
        bool busy,
        bool retrying,
        bool waiting,
        bool error,
        bool finished,
        TerminalActivityState expected)
    {
        var signals = new TerminalActivitySignals
        {
            IsBusy = busy,
            IsRetrying = retrying,
            IsWaitingForUser = waiting,
            HasError = error,
            IsFinished = finished,
        };

        var state = TerminalActivityClassifier.Classify(signals);

        await Assert.That(state).IsEqualTo(expected);
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
