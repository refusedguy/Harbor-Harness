using Harbor.Terminal.Abstractions;

namespace Harbor.Tui.Tests;

public class GeneratedEscapeCodeTests
{
    [Test]
    public async Task StyleFlagEscapeCodes_Reset_ReturnsCorrectSequence()
    {
        await Assert.That(StyleFlagEscapeCodes.Reset).IsEqualTo("\x1b[0m");
    }

    [Test]
    public async Task StyleFlagEscapeCodes_Bold_ReturnsCorrectSequence()
    {
        await Assert.That(StyleFlagEscapeCodes.Bold).IsEqualTo("\x1b[1m");
    }

    [Test]
    public async Task StyleFlagEscapeCodes_Dim_ReturnsCorrectSequence()
    {
        await Assert.That(StyleFlagEscapeCodes.Dim).IsEqualTo("\x1b[2m");
    }

    [Test]
    public async Task StyleFlagEscapeCodes_Italic_ReturnsCorrectSequence()
    {
        await Assert.That(StyleFlagEscapeCodes.Italic).IsEqualTo("\x1b[3m");
    }

    [Test]
    public async Task StyleFlagEscapeCodes_Underline_ReturnsCorrectSequence()
    {
        await Assert.That(StyleFlagEscapeCodes.Underline).IsEqualTo("\x1b[4m");
    }

    [Test]
    public async Task StyleFlagEscapeCodes_Strike_ReturnsCorrectSequence()
    {
        await Assert.That(StyleFlagEscapeCodes.Strike).IsEqualTo("\x1b[9m");
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
