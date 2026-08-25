using System.Text;
using Harbor.Tui.ConsoleEx.Input;
using Harbor.Tui.ConsoleEx.Parsing;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>
/// Golden-byte vectors: legacy xterm/VT key encodings (raw bytes → expected events).
/// These are the fallback-path contract — kitty-encoded equivalents must decode
/// to the SAME logical keys (zone З.1 golden table cross-checks this).
/// </summary>
public class GoldenLegacyKeyTests
{
    private readonly EscapeSequenceParser _parser = new();

    [Test]
    [Arguments("\u001B[A", KeyCode.Up)]
    [Arguments("\u001B[B", KeyCode.Down)]
    [Arguments("\u001B[C", KeyCode.Right)]
    [Arguments("\u001B[D", KeyCode.Left)]
    [Arguments("\u001B[H", KeyCode.Home)]
    [Arguments("\u001B[F", KeyCode.End)]
    public async Task Csi_Letter_Arrows_And_Navigation(string input, KeyCode expected)
    {
        var events = T.Feed(_parser, input);

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsKey(events[0], expected);
    }

    [Test]
    [Arguments("\u001BOA", KeyCode.Up)]
    [Arguments("\u001BOB", KeyCode.Down)]
    [Arguments("\u001BOC", KeyCode.Right)]
    [Arguments("\u001BOD", KeyCode.Left)]
    [Arguments("\u001BOP", KeyCode.F1)]
    [Arguments("\u001BOQ", KeyCode.F2)]
    [Arguments("\u001BOR", KeyCode.F3)]
    [Arguments("\u001BOS", KeyCode.F4)]
    [Arguments("\u001BOH", KeyCode.Home)]
    [Arguments("\u001BOF", KeyCode.End)]
    public async Task Ss3_Finals_Decode_To_Function_Keys(string input, KeyCode expected)
    {
        var events = T.Feed(_parser, input);

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsKey(events[0], expected);
    }

    [Test]
    [Arguments(1, 5, KeyCode.Up, KeyModifiers.Ctrl)]  // 5−1 = bit2 = Ctrl (xterm legacy order!)
    [Arguments(1, 3, KeyCode.Up, KeyModifiers.Alt)]   // 3−1 = bit1 = Alt
    [Arguments(1, 7, KeyCode.Up, KeyModifiers.Alt | KeyModifiers.Ctrl)]
    [Arguments(1, 2, KeyCode.Up, KeyModifiers.Shift)]
    [Arguments(1, 5, KeyCode.Down, KeyModifiers.Ctrl)]
    [Arguments(1, 5, KeyCode.Right, KeyModifiers.Ctrl)]
    [Arguments(1, 5, KeyCode.Left, KeyModifiers.Ctrl)]
    public async Task Csi_Modified_Arrows_Decode_Modifier_Bits(int firstParam, int mods, KeyCode key, KeyModifiers expectedMods)
    {
        var final = ArrowFinal(key);
        var events = T.Feed(_parser, $"\u001B[{firstParam};{mods}{final}");

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsKey(events[0], key, expectedMods);
    }

    private static string ArrowFinal(KeyCode key) => key switch
    {
        KeyCode.Up => "A",
        KeyCode.Down => "B",
        KeyCode.Right => "C",
        _ => "D",
    };

    [Test]
    [Arguments(2, KeyCode.Insert)]
    [Arguments(3, KeyCode.Delete)]
    [Arguments(5, KeyCode.PageUp)]
    [Arguments(6, KeyCode.PageDown)]
    [Arguments(1, KeyCode.Home)]
    [Arguments(4, KeyCode.End)]
    [Arguments(7, KeyCode.Home)]
    [Arguments(8, KeyCode.End)]
    public async Task Csi_Tilde_Navigation_Keys(int code, KeyCode expected)
    {
        var events = T.Feed(_parser, $"\u001B[{code}~");

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsKey(events[0], expected);
    }

    [Test]
    [Arguments(11, KeyCode.F1)]
    [Arguments(12, KeyCode.F2)]
    [Arguments(13, KeyCode.F3)]
    [Arguments(14, KeyCode.F4)]
    [Arguments(15, KeyCode.F5)]
    [Arguments(17, KeyCode.F6)]
    [Arguments(18, KeyCode.F7)]
    [Arguments(19, KeyCode.F8)]
    [Arguments(20, KeyCode.F9)]
    [Arguments(21, KeyCode.F10)]
    [Arguments(23, KeyCode.F11)]
    [Arguments(24, KeyCode.F12)]
    public async Task Csi_Tilde_Function_Keys(int code, KeyCode expected)
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
        await A.IsKey(events[0], KeyCode.Delete, KeyModifiers.Ctrl);
    }

    [Test]
    [Arguments("\r", KeyCode.Enter)]
    [Arguments("\n", KeyCode.Enter)]
    [Arguments("\t", KeyCode.Tab)]
    [Arguments("\u007F", KeyCode.Backspace)]
    [Arguments("\b", KeyCode.Backspace)]
    public async Task Control_Bytes_Map_To_Logical_Keys(string input, KeyCode expected)
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
        await A.IsChar(events[0], new Rune(letter), KeyModifiers.Ctrl);
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
        await A.IsChar(events[0], new Rune('b'), KeyModifiers.Alt);
    }

    [Test]
    public async Task Alt_Plus_Control_Byte_Is_Alt_Ctrl_Char()
    {
        // ESC + 0x02 (Ctrl+B) → Alt+Ctrl+b (e.g. tmux-style prefix chords).
        var events = T.Feed(_parser, "\u001B\u0002");

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsChar(events[0], new Rune('b'), KeyModifiers.Alt | KeyModifiers.Ctrl);
    }
}
