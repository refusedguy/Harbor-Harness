// QuitGestureWindowTests.cs — issue #3619.
//
// The quit gesture now takes its abort / quit command from ChatAction and lets
// the reducer own the busy split. What stays in the REPL is the #49 PR1
// confirmation window: the first idle Ctrl+C only hints, the second inside the
// 2 s window quits, and a press after the window hints again. These tests drive
// the same pure predicate the input loop calls, so the timing contract is
// checked without a PTY.

using Harbor.App.Cli.Repl;

namespace Harbor.App.Cli.Tests;

public class QuitGestureWindowTests
{
    private const long FirstPressMs = 120_000; // realistic TickCount64 uptime

    [Test]
    public async Task FirstPress_Sentinel_DoesNotQuit()
    {
        // CE-5 finding: the "no press yet" sentinel must never satisfy the window,
        // otherwise the very first idle Ctrl+C exits without the hint.
        await Assert.That(ReplInputLoop.IsSecondPressWithinWindow(FirstPressMs, -1)).IsFalse();
    }

    [Test]
    public async Task SecondPress_InsideWindow_Quits()
    {
        await Assert.That(ReplInputLoop.IsSecondPressWithinWindow(FirstPressMs + 1_999, FirstPressMs)).IsTrue();
        await Assert.That(ReplInputLoop.IsSecondPressWithinWindow(FirstPressMs + 2_000, FirstPressMs)).IsTrue();
    }

    [Test]
    public async Task SecondPress_AfterWindow_HintsAgain()
    {
        await Assert.That(ReplInputLoop.IsSecondPressWithinWindow(FirstPressMs + 2_001, FirstPressMs)).IsFalse();
        await Assert.That(ReplInputLoop.IsSecondPressWithinWindow(FirstPressMs + 2_300, FirstPressMs)).IsFalse();
    }
}
