using Harbor.Tui.CellForge.Capabilities;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Parsing;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Issue #2218: CSI ?1004h focus reports decode into typed input events and
/// the enable/disable mode sequences are byte-exact (the writer side of the
/// probe-then-enable contract).
/// </summary>
public class TerminalFocusTests
{
    [Test]
    public async Task Focus_Gained_Decodes_From_Csi_I()
    {
        var events = T.Feed(new EscapeSequenceParser(), "\u001B[I");

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(InputEventKind.Focus);
        await Assert.That(events[0].Focus.IsFocused).IsTrue();
    }

    [Test]
    public async Task Focus_Lost_Decodes_From_Csi_O()
    {
        var events = T.Feed(new EscapeSequenceParser(), "\u001B[O");

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(InputEventKind.Focus);
        await Assert.That(events[0].Focus.IsFocused).IsFalse();
    }

    [Test]
    public async Task Focus_Sequences_Split_Across_Chunks_Still_Decode()
    {
        var events = T.Feed(new EscapeSequenceParser(), "\u001B", "[I", "\u001B[", "O");

        await Assert.That(events.Length).IsEqualTo(2);
        await Assert.That(events[0].Kind).IsEqualTo(InputEventKind.Focus);
        await Assert.That(events[0].Focus.IsFocused).IsTrue();
        await Assert.That(events[1].Kind).IsEqualTo(InputEventKind.Focus);
        await Assert.That(events[1].Focus.IsFocused).IsFalse();
    }

    [Test]
    public async Task Parameterized_Csi_I_Is_Not_Decoded_As_Focus()
    {
        var events = T.Feed(new EscapeSequenceParser(), "\u001B[1I");

        await Assert.That(events.Any(e => e.Kind == InputEventKind.Focus)).IsFalse();
    }

    [Test]
    public async Task Ss3_Escape_O_Is_Still_A_Key_Not_A_Focus_Event()
    {
        var events = T.Feed(new EscapeSequenceParser(), "\u001BOA");

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(InputEventKind.Key);
    }

    [Test]
    public async Task Focus_Mode_Sequences_Are_Exact_Bytes()
    {
        await Assert.That(TerminalQueries.FocusTrackingEnable).IsEqualTo("\u001B[?1004h");
        await Assert.That(TerminalQueries.FocusTrackingDisable).IsEqualTo("\u001B[?1004l");
    }
}
