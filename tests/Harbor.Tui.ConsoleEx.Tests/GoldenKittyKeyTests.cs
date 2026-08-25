using System.Text;
using Harbor.Tui.ConsoleEx.Input;
using Harbor.Tui.ConsoleEx.Parsing;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>
/// Golden-byte vectors for the kitty keyboard protocol (design §2.3).
/// Modifier contract per ConsoleEx design doc: value = 1 + (shift=1, ctrl=2,
/// alt=4, super/hyper/meta collapse to Meta). The critical composer cases —
/// Enter vs Shift+Enter vs Ctrl+Enter — are pinned here.
/// </summary>
public class GoldenKittyKeyTests
{
    private readonly EscapeSequenceParser _parser = new();

    [Test]
    [Arguments("\u001B[13u", KeyCode.Enter)]
    [Arguments("\u001B[27u", KeyCode.Escape)]
    [Arguments("\u001B[9u", KeyCode.Tab)]
    [Arguments("\u001B[127u", KeyCode.Backspace)]
    public async Task Functional_Keys_Decode_From_CsiU(string input, KeyCode expected)
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

        await Assert.That(plain[0].Key.Modifiers).IsEqualTo(KeyModifiers.None);
        await Assert.That(shifted[0].Key.Modifiers & KeyModifiers.Shift).IsEqualTo(KeyModifiers.Shift);
        await Assert.That(ctrl[0].Key.Modifiers & KeyModifiers.Ctrl).IsEqualTo(KeyModifiers.Ctrl);
        // All three are still Enter — never Tab (\t ≡ Ctrl+I ambiguity lives only in legacy).
        await Assert.That(plain[0].Key.Key).IsEqualTo(KeyCode.Enter);
        await Assert.That(shifted[0].Key.Key).IsEqualTo(KeyCode.Enter);
        await Assert.That(ctrl[0].Key.Key).IsEqualTo(KeyCode.Enter);
    }

    [Test]
    [Arguments("\u001B[97u", KeyModifiers.None, 'a')]
    [Arguments("\u001B[97;2u", KeyModifiers.Shift, 'a')]
    [Arguments("\u001B[97;3u", KeyModifiers.Ctrl, 'a')]      // 1 + ctrl
    [Arguments("\u001B[97;5u", KeyModifiers.Alt, 'a')]       // 1 + alt
    [Arguments("\u001B[97;7u", KeyModifiers.Alt | KeyModifiers.Ctrl, 'a')]
    [Arguments("\u001B[97;9u", KeyModifiers.Meta, 'a')]      // 1 + super → Meta
    public async Task Modified_Chars_Decode_Kitty_Modifier_Bits(string input, KeyModifiers mods, char letter)
    {
        var events = T.Feed(_parser, input);

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsChar(events[0], new Rune(letter), mods, kitty: true);
    }

    [Test]
    [Arguments(":3", KeyEventType.Release)]
    [Arguments(":2", KeyEventType.Repeat)]
    public async Task Event_Type_Subparameter_Encodes_Release_And_Repeat(string sub, KeyEventType expected)
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
        await A.IsChar(events[0], new Rune('A'), KeyModifiers.Shift, kitty: true);
    }

    [Test]
    public async Task Base_Layout_Subparameter_Does_Not_Break_Shifted()
    {
        var events = T.Feed(_parser, "\u001B[97:65:98;2u");

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsChar(events[0], new Rune('A'), KeyModifiers.Shift, kitty: true);
    }

    [Test]
    public async Task Unmapped_Functional_Codepoints_Preserve_Raw_Codepoint()
    {
        var events = T.Feed(_parser, "\u001B[57414u");

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Key.Key).IsEqualTo(KeyCode.Unknown);
        await Assert.That((int)events[0].Key.Codepoint).IsEqualTo(57414);
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
