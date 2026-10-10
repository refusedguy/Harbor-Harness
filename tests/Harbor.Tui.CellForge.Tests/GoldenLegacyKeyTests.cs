using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Parsing;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Golden-byte vectors: legacy xterm/VT key encodings (raw bytes → expected events).
/// These are the fallback-path contract — kitty-encoded equivalents must decode
/// to the SAME logical keys (zone З.1 golden table cross-checks this).
/// </summary>
// #58: serialized vs theme tests — rendering reads global TerminalColorPalette.
[NotInParallel("pty")]
public class GoldenLegacyKeyTests
{
    private readonly EscapeSequenceParser _parser = new();

    [Test]
    [Arguments("\u001B[A", EngineInput.KeyCode.Up)]
    [Arguments("\u001B[B", EngineInput.KeyCode.Down)]
    [Arguments("\u001B[C", EngineInput.KeyCode.Right)]
    [Arguments("\u001B[D", EngineInput.KeyCode.Left)]
    [Arguments("\u001B[H", EngineInput.KeyCode.Home)]
    [Arguments("\u001B[F", EngineInput.KeyCode.End)]
    public async Task Csi_Letter_Arrows_And_Navigation(string input, EngineInput.KeyCode expected)
    {
        var events = T.Feed(_parser, input);

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsKey(events[0], expected);
    }

    [Test]
    [Arguments("\u001BOA", EngineInput.KeyCode.Up)]
    [Arguments("\u001BOB", EngineInput.KeyCode.Down)]
    [Arguments("\u001BOC", EngineInput.KeyCode.Right)]
    [Arguments("\u001BOD", EngineInput.KeyCode.Left)]
    [Arguments("\u001BOP", EngineInput.KeyCode.F1)]
    [Arguments("\u001BOQ", EngineInput.KeyCode.F2)]
    [Arguments("\u001BOR", EngineInput.KeyCode.F3)]
    [Arguments("\u001BOS", EngineInput.KeyCode.F4)]
    [Arguments("\u001BOH", EngineInput.KeyCode.Home)]
    [Arguments("\u001BOF", EngineInput.KeyCode.End)]
    public async Task Ss3_Finals_Decode_To_Function_Keys(string input, EngineInput.KeyCode expected)
    {
        var events = T.Feed(_parser, input);

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsKey(events[0], expected);
    }

    [Test]
    [Arguments(1, 5, EngineInput.KeyCode.Up, EngineInput.KeyModifiers.Ctrl)]  // 5−1 = bit2 = Ctrl (xterm legacy order!)
    [Arguments(1, 3, EngineInput.KeyCode.Up, EngineInput.KeyModifiers.Alt)]   // 3−1 = bit1 = Alt
    [Arguments(1, 7, EngineInput.KeyCode.Up, EngineInput.KeyModifiers.Alt | EngineInput.KeyModifiers.Ctrl)]
    [Arguments(1, 2, EngineInput.KeyCode.Up, EngineInput.KeyModifiers.Shift)]
    [Arguments(1, 5, EngineInput.KeyCode.Down, EngineInput.KeyModifiers.Ctrl)]
    [Arguments(1, 5, EngineInput.KeyCode.Right, EngineInput.KeyModifiers.Ctrl)]
    [Arguments(1, 5, EngineInput.KeyCode.Left, EngineInput.KeyModifiers.Ctrl)]
    public async Task Csi_Modified_Arrows_Decode_Modifier_Bits(int firstParam, int mods, EngineInput.KeyCode key, EngineInput.KeyModifiers expectedMods)
    {
        var final = ArrowFinal(key);
        var events = T.Feed(_parser, $"\u001B[{firstParam};{mods}{final}");

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsKey(events[0], key, expectedMods);
    }

    private static string ArrowFinal(EngineInput.KeyCode key) => key switch
    {
        EngineInput.KeyCode.Up => "A",
        EngineInput.KeyCode.Down => "B",
        EngineInput.KeyCode.Right => "C",
        _ => "D",
    };

    [Test]
    [Arguments(2, EngineInput.KeyCode.Insert)]
    [Arguments(3, EngineInput.KeyCode.Delete)]
    [Arguments(5, EngineInput.KeyCode.PageUp)]
    [Arguments(6, EngineInput.KeyCode.PageDown)]
    [Arguments(1, EngineInput.KeyCode.Home)]
    [Arguments(4, EngineInput.KeyCode.End)]
    [Arguments(7, EngineInput.KeyCode.Home)]
    [Arguments(8, EngineInput.KeyCode.End)]
    public async Task Csi_Tilde_Navigation_Keys(int code, EngineInput.KeyCode expected)
    {
        var events = T.Feed(_parser, $"\u001B[{code}~");

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsKey(events[0], expected);
    }

    [Test]
    [Arguments(11, EngineInput.KeyCode.F1)]
    [Arguments(12, EngineInput.KeyCode.F2)]
    [Arguments(13, EngineInput.KeyCode.F3)]
    [Arguments(14, EngineInput.KeyCode.F4)]
    [Arguments(15, EngineInput.KeyCode.F5)]
    [Arguments(17, EngineInput.KeyCode.F6)]
    [Arguments(18, EngineInput.KeyCode.F7)]
    [Arguments(19, EngineInput.KeyCode.F8)]
    [Arguments(20, EngineInput.KeyCode.F9)]
    [Arguments(21, EngineInput.KeyCode.F10)]
    [Arguments(23, EngineInput.KeyCode.F11)]
    [Arguments(24, EngineInput.KeyCode.F12)]
    public async Task Csi_Tilde_Function_Keys(int code, EngineInput.KeyCode expected)
    {
        var events = T.Feed(_parser, $"\u001B[{code}~");

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsKey(events[0], expected);
    }

    [Test]
    public async Task Modified_Tilde_Carries_Modifiers()
    {
        var events = T.Feed(_parser, "\u001B[3;5~");

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsKey(events[0], EngineInput.KeyCode.Delete, EngineInput.KeyModifiers.Ctrl);
    }

    [Test]
    [Arguments("\r", EngineInput.KeyCode.Enter)]
    [Arguments("\n", EngineInput.KeyCode.Enter)]
    [Arguments("\t", EngineInput.KeyCode.Tab)]
    [Arguments("\u007F", EngineInput.KeyCode.Backspace)]
    [Arguments("\b", EngineInput.KeyCode.Backspace)]
    public async Task Control_Bytes_Map_To_Logical_Keys(string input, EngineInput.KeyCode expected)
    {
        var events = T.Feed(_parser, input);

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsKey(events[0], expected);
    }

    [Test]
    [Arguments(1, 'a')]
    [Arguments(3, 'c')]
    [Arguments(4, 'd')]
    [Arguments(21, 'u')]
    [Arguments(23, 'w')]
    [Arguments(26, 'z')]
    public async Task Ctrl_Letter_Control_Bytes_Decode_As_Char_With_Ctrl(int raw, char letter)
    {
        var events = T.FeedBytes(_parser, [(byte)raw]);

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsChar(events[0], new Rune(letter), EngineInput.KeyModifiers.Ctrl);
    }

    [Test]
    public async Task Printable_Text_Produces_Char_Events()
    {
        var events = T.Feed(_parser, "hi!");

        await Assert.That(events.Length).IsEqualTo(3);
        await A.IsChar(events[0], new Rune('h'));
        await A.IsChar(events[1], new Rune('i'));
        await A.IsChar(events[2], new Rune('!'));
    }

    [Test]
    public async Task Alt_Plus_Printable_Is_Alt_Char()
    {
        var events = T.Feed(_parser, "\u001Bb");

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsChar(events[0], new Rune('b'), EngineInput.KeyModifiers.Alt);
    }

    [Test]
    public async Task Alt_Plus_Control_Byte_Is_Alt_Ctrl_Char()
    {
        // ESC + 0x02 (Ctrl+B) → Alt+Ctrl+b (e.g. tmux-style prefix chords).
        var events = T.Feed(_parser, "\u001B\u0002");

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsChar(events[0], new Rune('b'), EngineInput.KeyModifiers.Alt | EngineInput.KeyModifiers.Ctrl);
    }
}
