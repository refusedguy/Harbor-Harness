using Harbor.Terminal.Abstractions;
using Harbor.TestKit;

namespace Harbor.Tui.Tests;

public class GeneratedEscapeCodeTests
{
    // #1810: the escape-code table is an ARRAY, and the array is the whole
    // truth about which members exist: the rows come from it, and the guard
    // below proves it names every member of the enum. Before, six of the
    // nine members had a method of their own and Blink, Reverse and Hidden
    // had none at all — the generated constants for those three were never
    // asserted by anyone.

    /// <summary>The StyleFlag table of escape-code sequences.</summary>
    public static (string Name, StyleFlag Flag, string EscapeCode, string Expected)[] StyleFlagEscapeCodeCases =>
    [
        ("StyleFlagEscapeCodes_Reset_ReturnsCorrectSequence", StyleFlag.Reset, StyleFlagEscapeCodes.Reset, "\x1b[0m"),
        ("StyleFlagEscapeCodes_Bold_ReturnsCorrectSequence", StyleFlag.Bold, StyleFlagEscapeCodes.Bold, "\x1b[1m"),
        ("StyleFlagEscapeCodes_Dim_ReturnsCorrectSequence", StyleFlag.Dim, StyleFlagEscapeCodes.Dim, "\x1b[2m"),
        ("StyleFlagEscapeCodes_Italic_ReturnsCorrectSequence", StyleFlag.Italic, StyleFlagEscapeCodes.Italic, "\x1b[3m"),
        ("StyleFlagEscapeCodes_Underline_ReturnsCorrectSequence", StyleFlag.Underline, StyleFlagEscapeCodes.Underline, "\x1b[4m"),
        ("StyleFlagEscapeCodes_Blink_ReturnsCorrectSequence", StyleFlag.Blink, StyleFlagEscapeCodes.Blink, "\x1b[5m"),
        ("StyleFlagEscapeCodes_Reverse_ReturnsCorrectSequence", StyleFlag.Reverse, StyleFlagEscapeCodes.Reverse, "\x1b[7m"),
        ("StyleFlagEscapeCodes_Hidden_ReturnsCorrectSequence", StyleFlag.Hidden, StyleFlagEscapeCodes.Hidden, "\x1b[8m"),
        ("StyleFlagEscapeCodes_Strike_ReturnsCorrectSequence", StyleFlag.Strike, StyleFlagEscapeCodes.Strike, "\x1b[9m"),
    ];

    [Test]
    [MethodDataSource(nameof(StyleFlagEscapeCodeCases))]
    public async Task StyleFlagEscapeCodes_ReturnsCorrectSequence(
        string name,
        StyleFlag flag,
        string escapeCode,
        string expected)
    {
        await Assert.That(escapeCode).IsEqualTo(expected)
            .Because($"the row for {flag} must name the sequence the generator emits ({name})");
    }

    /// <summary>
    /// #1810: the totality check. The table above names every member of
    /// <c>StyleFlag</c>, and this says so — a member added to the enum has
    /// no row until it gets one.
    /// </summary>
    [Test]
    public async Task Every_StyleFlag_Has_A_Row_In_The_Table()
    {
        await Assert.That(StyleFlagEscapeCodeCases.Length).IsGreaterThan(0).Because("the table has rows to check");
        Exhaustive.AssertAllCases<StyleFlag>(StyleFlagEscapeCodeCases.Select(c => c.Flag).ToHashSet(), "every StyleFlag must have a row in the escape-code table");
    }

    [Test]
    public async Task StyleFlagEscapeCodes_Combine_ProducesCorrectSgr()
    {
        var result = StyleFlagEscapeCodes.Combine(StyleFlag.Bold | StyleFlag.Italic);
        await Assert.That(result).IsEqualTo("\x1b[1;2;3;5;7;9m");
    }

    [Test]
    public async Task StyleFlagEscapeCodes_Combine_EmptyFlags_ReturnsReset()
    {
        var result = StyleFlagEscapeCodes.Combine(StyleFlag.Reset);
        await Assert.That(result).IsEqualTo("\x1b[0m");
    }

    // #1770: the generated escape-code vectors used to be one copy-pasted
    // method each. The assert never changed — only the input did. Every row
    // keeps its original test name as the case display name.
    [Test]
    [Arguments(CursorDirection.Up, "\x1b[A", DisplayName = "CursorDirectionEscapeCodes_Cursor_Up_ReturnsCorrectSequence")]
    [Arguments(CursorDirection.Down, "\x1b[B", DisplayName = "CursorDirectionEscapeCodes_Cursor_Down_ReturnsCorrectSequence")]
    public async Task CursorDirectionEscapeCodes_Cursor_ReturnsCorrectSequence(CursorDirection direction, string expected)
    {
        var result = CursorDirectionEscapeCodes.Cursor(direction);
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    [Arguments(Color8Bit.Black, "\x1b[38;5;0m", DisplayName = "Color8BitEscapeCodes_Foreground_Black_ReturnsCorrectSequence")]
    [Arguments(Color8Bit.Red, "\x1b[38;5;1m", DisplayName = "Color8BitEscapeCodes_Foreground_Red_ReturnsCorrectSequence")]
    [Arguments((Color8Bit)42, "\x1b[38;5;42m", DisplayName = "Color8BitEscapeCodes_Foreground_Unknown_ReturnsDefault")]
    public async Task Color8BitEscapeCodes_Foreground_ReturnsCorrectSequence(Color8Bit color, string expected)
    {
        var result = Color8BitEscapeCodes.Foreground(color);
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task CursorDirectionEscapeCodes_Cursor_WithCount_ReturnsCorrectSequence()
    {
        var result = CursorDirectionEscapeCodes.Cursor(CursorDirection.Up, 5);
        await Assert.That(result).IsEqualTo("\x1b[5A");
    }

    [Test]
    public async Task Color8BitEscapeCodes_Background_Black_ReturnsCorrectSequence()
    {
        var result = Color8BitEscapeCodes.Background(Color8Bit.Black);
        await Assert.That(result).IsEqualTo("\x1b[48;5;0m");
    }
}
