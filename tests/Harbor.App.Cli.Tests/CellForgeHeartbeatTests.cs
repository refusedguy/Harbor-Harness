using Harbor.App.Cli.Repl;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.App.Cli.Tests;

/// <summary>
/// Issue #170: the 80 ms frame heartbeat stopped in Idle, freezing the mascot
/// mid-reaction on a dead latch. It now outlives Idle while either mascot
/// director still owes motion (see <c>ChatScreenBridge.IsMascotAnimating</c>).
/// </summary>
public class CellForgeHeartbeatTests
{
    [Test]
    public async Task Heartbeat_Runs_While_Running_Or_Compacting()
    {
        await Assert.That(CellForgeReplRunner.ShouldKeepHeartbeat(StatusBarMode.Running, mascotAnimating: false)).IsTrue();
        await Assert.That(CellForgeReplRunner.ShouldKeepHeartbeat(StatusBarMode.Compacting, mascotAnimating: false)).IsTrue();
    }

    [Test]
    public async Task Heartbeat_Runs_In_Idle_While_Mascot_Animating()
    {
        await Assert.That(CellForgeReplRunner.ShouldKeepHeartbeat(StatusBarMode.Idle, mascotAnimating: true)).IsTrue();
        await Assert.That(CellForgeReplRunner.ShouldKeepHeartbeat(StatusBarMode.AwaitingApproval, mascotAnimating: true)).IsTrue();
    }

    [Test]
    public async Task Heartbeat_Stops_In_Quiet_Idle()
    {
        await Assert.That(CellForgeReplRunner.ShouldKeepHeartbeat(StatusBarMode.Idle, mascotAnimating: false)).IsFalse();
        await Assert.That(CellForgeReplRunner.ShouldKeepHeartbeat(StatusBarMode.AwaitingApproval, mascotAnimating: false)).IsFalse();
    }

    [Test]
    public async Task Heartbeat_And_Animation_Pause_When_Window_Is_Not_Focused()
    {
        // #2218: an unfocused window never keeps a heartbeat, even mid-run or
        // with a mascot reaction still owing motion. That is what parks the
        // spinner frames and the gate glow until focus returns.
        await Assert.That(CellForgeReplRunner.ShouldKeepHeartbeat(StatusBarMode.Running, mascotAnimating: true, windowFocused: false)).IsFalse();
        await Assert.That(CellForgeReplRunner.ShouldKeepHeartbeat(StatusBarMode.Compacting, mascotAnimating: false, windowFocused: false)).IsFalse();
        // Focus regained restores the pre-#2218 condition exactly.
        await Assert.That(CellForgeReplRunner.ShouldKeepHeartbeat(StatusBarMode.Running, mascotAnimating: false, windowFocused: true)).IsTrue();
    }
}
