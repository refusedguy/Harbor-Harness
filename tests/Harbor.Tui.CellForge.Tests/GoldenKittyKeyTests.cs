using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Parsing;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Golden-byte vectors for the kitty keyboard protocol (design §2.3).
/// Modifier contract per CellForge design doc: value = 1 + (shift=1, ctrl=2,
/// alt=4, super/hyper/meta collapse to Meta). The critical composer cases —
/// Enter vs Shift+Enter vs Ctrl+Enter — are pinned here.
/// </summary>
// #58: serialized vs theme tests — rendering reads global TerminalColorPalette.
[NotInParallel("pty")]
public class GoldenKittyKeyTests
{
    private readonly EscapeSequenceParser _parser = new();

    [Test]
    [Arguments("\u001B[13u", EngineInput.KeyCode.Enter)]
    [Arguments("\u001B[27u", EngineInput.KeyCode.Escape)]
    [Arguments("\u001B[9u", EngineInput.KeyCode.Tab)]
    [Arguments("\u001B[127u", EngineInput.KeyCode.Backspace)]
    public async Task Functional_Keys_Decode_From_CsiU(string input, EngineInput.KeyCode expected)
    {
        var events = T.Feed(_parser, input);

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsKey(events[0], expected, kitty: true);
    }

    [Test]
    [Arguments("\u001B[13;2u")]   // 1 + shift
    [Arguments("\u001B[13;4u")]   // 1 + shift|ctrl
    public async Task Enter_Vs_ShiftEnter_Vs_CtrlEnter_Are_Distinguishable(string input)
    {
        var plain = T.Feed(new EscapeSequenceParser(), "\u001B[13u");
        var shifted = T.Feed(_parser, input);
        var ctrl = T.Feed(new EscapeSequenceParser(), "\u001B[13;3u"); // 1 + ctrl

        await Assert.That(plain[0].Key.Modifiers).IsEqualTo(EngineInput.KeyModifiers.None);
        await Assert.That(shifted[0].Key.Modifiers & EngineInput.KeyModifiers.Shift).IsEqualTo(EngineInput.KeyModifiers.Shift);
        await Assert.That(ctrl[0].Key.Modifiers & EngineInput.KeyModifiers.Ctrl).IsEqualTo(EngineInput.KeyModifiers.Ctrl);
        // All three are still Enter — never Tab (\t ≡ Ctrl+I ambiguity lives only in legacy).
        await Assert.That(plain[0].Key.Key).IsEqualTo(EngineInput.KeyCode.Enter);
        await Assert.That(shifted[0].Key.Key).IsEqualTo(EngineInput.KeyCode.Enter);
        await Assert.That(ctrl[0].Key.Key).IsEqualTo(EngineInput.KeyCode.Enter);
    }

    [Test]
    [Arguments("\u001B[97u", EngineInput.KeyModifiers.None, 'a')]
    [Arguments("\u001B[97;2u", EngineInput.KeyModifiers.Shift, 'a')]
    [Arguments("\u001B[97;3u", EngineInput.KeyModifiers.Ctrl, 'a')]      // 1 + ctrl
    [Arguments("\u001B[97;5u", EngineInput.KeyModifiers.Alt, 'a')]       // 1 + alt
    [Arguments("\u001B[97;7u", EngineInput.KeyModifiers.Alt | EngineInput.KeyModifiers.Ctrl, 'a')]
    [Arguments("\u001B[97;9u", EngineInput.KeyModifiers.Meta, 'a')]      // 1 + super → Meta
    public async Task Modified_Chars_Decode_Kitty_Modifier_Bits(string input, EngineInput.KeyModifiers mods, char letter)
    {
        var events = T.Feed(_parser, input);

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsChar(events[0], new Rune(letter), mods, kitty: true);
    }

    [Test]
    [Arguments(":3", EngineInput.KeyEventType.Release)]
    [Arguments(":2", EngineInput.KeyEventType.Repeat)]
    public async Task Event_Type_Subparameter_Encodes_Release_And_Repeat(string sub, EngineInput.KeyEventType expected)
    {
        var events = T.Feed(_parser, $"\u001B[97;1{sub}u");

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsChar(events[0], new Rune('a'), eventType: expected, kitty: true);
    }

    [Test]
    public async Task Associated_Text_Codepoints_Win_Over_Primary()
    {
        // λ U+03BB = 955 arrives as associated text while primary stays 'a'.
        var events = T.Feed(_parser, "\u001B[97;;955u");

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsChar(events[0], new Rune(955), kitty: true);
    }

    [Test]
    public async Task Shifted_Subparameter_Provides_Display_Character()
    {
        // Shift+a on Cyrillic layout: primary 'a'(97), shifted 'A'(65).
        var events = T.Feed(_parser, "\u001B[97:65;2u");

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsChar(events[0], new Rune('A'), EngineInput.KeyModifiers.Shift, kitty: true);
    }

    [Test]
    public async Task Base_Layout_Subparameter_Does_Not_Break_Shifted()
    {
        var events = T.Feed(_parser, "\u001B[97:65:98;2u");

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsChar(events[0], new Rune('A'), EngineInput.KeyModifiers.Shift, kitty: true);
    }

    [Test]
    [Arguments(57399, EngineInput.KeyCode.Keypad0)]
    [Arguments(57400, EngineInput.KeyCode.Keypad1)]
    [Arguments(57401, EngineInput.KeyCode.Keypad2)]
    [Arguments(57402, EngineInput.KeyCode.Keypad3)]
    [Arguments(57403, EngineInput.KeyCode.Keypad4)]
    [Arguments(57404, EngineInput.KeyCode.Keypad5)]
    [Arguments(57405, EngineInput.KeyCode.Keypad6)]
    [Arguments(57406, EngineInput.KeyCode.Keypad7)]
    [Arguments(57407, EngineInput.KeyCode.Keypad8)]
    [Arguments(57408, EngineInput.KeyCode.Keypad9)]
    [Arguments(57409, EngineInput.KeyCode.KeypadDecimal)]
    [Arguments(57412, EngineInput.KeyCode.KeypadMinus)]
    [Arguments(57413, EngineInput.KeyCode.KeypadPlus)]
    [Arguments(57414, EngineInput.KeyCode.KeypadEnter)]
    public async Task Keypad_Functional_Codes_Decode_To_Distinct_Keys(int code, EngineInput.KeyCode expected)
    {
        var events = T.Feed(_parser, $"\u001B[{code}u");

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsKey(events[0], expected, kitty: true);
    }

    [Test]
    public async Task Keypad_Digit_Is_Distinct_From_Top_Row_Digit()
    {
        // The whole point of the primitive: physical position survives decode.
        var keypad = T.Feed(new EscapeSequenceParser(), "\u001B[57399u");
        var topRow = T.Feed(new EscapeSequenceParser(), "\u001B[48u");

        await Assert.That(keypad[0].Key.Key).IsEqualTo(EngineInput.KeyCode.Keypad0);
        await Assert.That(topRow[0].Key.Key).IsEqualTo(EngineInput.KeyCode.Char);
        await Assert.That(topRow[0].Key.Character.Value).IsEqualTo('0');
    }

    [Test]
    public async Task Unmapped_Functional_Codepoints_Preserve_Raw_Codepoint()
    {
        // 57415 is KP_EQUAL: a functional code deliberately outside the
        // keypad set we map, so the Unknown fallback still has a golden vector.
        var events = T.Feed(_parser, "\u001B[57415u");

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Key.Key).IsEqualTo(EngineInput.KeyCode.Unknown);
        await Assert.That((int)events[0].Key.Codepoint).IsEqualTo(57415);
        await Assert.That(events[0].Key.IsKittyEncoded).IsTrue();
    }

    [Test]
    public async Task Kitty_And_Legacy_Encodings_Converge_On_Same_Logical_Keys()
    {
        // Cross-check pairs: identical KeyCode from both encoders.
        (string legacy, string kitty)[] pairs =
        [
            ("\r", "\u001B[13u"),
            ("\t", "\u001B[9u"),
            ("\u007F", "\u001B[127u"),
            ("\u001B[3~", "\u001B[3~"), // Delete keeps legacy form even under kitty
        ];

        foreach (var (legacyForm, kittyForm) in pairs)
        {
            var l = T.Feed(new EscapeSequenceParser(), legacyForm);
            var k = T.Feed(new EscapeSequenceParser(), kittyForm);

            await Assert.That(l[0].Kind).IsEqualTo(InputEventKind.Key);
            await Assert.That(k[0].Kind).IsEqualTo(InputEventKind.Key);
            await Assert.That(l[0].Key.Key).IsEqualTo(k[0].Key.Key);
        }

        // Provenance stays explicit: CSI-u forms are kitty-tagged, raw legacy
        // forms (including tilde finals kept under kitty) are tagged legacy.
        var kittyEnter = T.Feed(new EscapeSequenceParser(), "\u001B[13u");
        await Assert.That(kittyEnter[0].Key.IsKittyEncoded).IsTrue();
        var legacyDelete = T.Feed(new EscapeSequenceParser(), "\u001B[3~");
        await Assert.That(legacyDelete[0].Key.IsKittyEncoded).IsFalse();
    }
}
