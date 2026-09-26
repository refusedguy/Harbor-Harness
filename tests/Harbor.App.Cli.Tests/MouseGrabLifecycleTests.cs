using Harbor.App.Cli.Repl;
using Harbor.Tui.CellForge.Capabilities;

namespace Harbor.App.Cli.Tests;

/// <summary>
/// Issue #36 (decision: full mouse grab) — lifecycle unit contract for the
/// interactive session, without the PTY harness (see
/// <c>TerminalModesScenarioTests</c> for the E2E twin):
/// enter enables X10 click + button-event drag + SGR encoding in order,
/// leave disables everything in reverse order. Byte-exact: any drift breaks
/// the PTY expectation, so the literals are duplicated here on purpose.
/// </summary>
public class MouseGrabLifecycleTests
{
    [Test]
    public async Task Enter_Enables_Full_Grab_In_Order()
    {
        await Assert.That(CellForgeReplRunner.SeqEnterAltScreen)
            .IsEqualTo("\x1B[?1049h\x1B[?25l\x1B[?2004h\x1B[?1000h\x1B[?1002h\x1B[?1006h");
    }

    [Test]
    public async Task Leave_Disables_Everything_In_Reverse_Order()
    {
        await Assert.That(CellForgeReplRunner.SeqLeaveAltScreen)
            .IsEqualTo("\x1B[?2004l\x1B[?25h\x1B[?1049l\x1B[?1006l\x1B[?1002l\x1B[?1000l");
    }

    [Test]
    public async Task Enter_Is_Composed_From_Shared_Constants()
    {
        await Assert.That(CellForgeReplRunner.SeqEnterAltScreen).Contains(TerminalQueries.MouseFullEnable);
        await Assert.That(TerminalQueries.MouseFullEnable).IsEqualTo("\u001B[?1000h\u001B[?1002h\u001B[?1006h");
    }

    [Test]
    public async Task Leave_Is_Composed_From_Shared_Constants()
    {
        await Assert.That(CellForgeReplRunner.SeqLeaveAltScreen).Contains(TerminalQueries.MouseDisable);
        await Assert.That(TerminalQueries.MouseDisable).IsEqualTo("\u001B[?1006l\u001B[?1002l\u001B[?1000l");
    }

    [Test]
    [Arguments("?1000h", "?1000l")]
    [Arguments("?1002h", "?1002l")]
    [Arguments("?1006h", "?1006l")]
    [Arguments("?2004h", "?2004l")]
    [Arguments("?1049h", "?1049l")]
    public async Task Every_Mode_Enabled_On_Enter_Is_Disabled_On_Leave(string enable, string disable)
    {
        await Assert.That(CellForgeReplRunner.SeqEnterAltScreen).Contains(enable);
        await Assert.That(CellForgeReplRunner.SeqLeaveAltScreen).Contains(disable);
        // Enabled modes must not linger in the leave sequence and vice versa.
        await Assert.That(CellForgeReplRunner.SeqLeaveAltScreen.Contains(enable, StringComparison.Ordinal)).IsFalse();
        await Assert.That(CellForgeReplRunner.SeqEnterAltScreen.Contains(disable, StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task Grab_Flags_Are_Ordered_Enable_And_Reverse_Disable()
    {
        string enter = CellForgeReplRunner.SeqEnterAltScreen;
        string leave = CellForgeReplRunner.SeqLeaveAltScreen;

        int e1000 = enter.IndexOf("?1000h", StringComparison.Ordinal);
        int e1002 = enter.IndexOf("?1002h", StringComparison.Ordinal);
        int e1006 = enter.IndexOf("?1006h", StringComparison.Ordinal);
        await Assert.That(e1000 >= 0 && e1000 < e1002 && e1002 < e1006).IsTrue();

        int l1006 = leave.IndexOf("?1006l", StringComparison.Ordinal);
        int l1002 = leave.IndexOf("?1002l", StringComparison.Ordinal);
        int l1000 = leave.IndexOf("?1000l", StringComparison.Ordinal);
        await Assert.That(l1006 >= 0 && l1006 < l1002 && l1002 < l1000).IsTrue();
    }
}
