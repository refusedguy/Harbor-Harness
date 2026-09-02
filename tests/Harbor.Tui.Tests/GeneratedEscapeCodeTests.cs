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

    [Test]
    public async Task CursorDirectionEscapeCodes_Cursor_Up_ReturnsCorrectSequence()
    {
        var result = CursorDirectionEscapeCodes.Cursor(CursorDirection.Up);
        await Assert.That(result).IsEqualTo("\x1b[A");
    }

    [Test]
    public async Task CursorDirectionEscapeCodes_Cursor_Down_ReturnsCorrectSequence()
    {
        var result = CursorDirectionEscapeCodes.Cursor(CursorDirection.Down);
        await Assert.That(result).IsEqualTo("\x1b[B");
    }

    [Test]
    public async Task CursorDirectionEscapeCodes_Cursor_WithCount_ReturnsCorrectSequence()
    {
        var result = CursorDirectionEscapeCodes.Cursor(CursorDirection.Up, 5);
        await Assert.That(result).IsEqualTo("\x1b[5A");
    }

    [Test]
    public async Task Color8BitEscapeCodes_Foreground_Black_ReturnsCorrectSequence()
    {
        var result = Color8BitEscapeCodes.Foreground(Color8Bit.Black);
        await Assert.That(result).IsEqualTo("\x1b[38;5;0m");
    }

    [Test]
    public async Task Color8BitEscapeCodes_Foreground_Red_ReturnsCorrectSequence()
    {
        var result = Color8BitEscapeCodes.Foreground(Color8Bit.Red);
        await Assert.That(result).IsEqualTo("\x1b[38;5;1m");
    }

    [Test]
    public async Task Color8BitEscapeCodes_Background_Black_ReturnsCorrectSequence()
    {
        var result = Color8BitEscapeCodes.Background(Color8Bit.Black);
        await Assert.That(result).IsEqualTo("\x1b[48;5;0m");
    }

    [Test]
    public async Task Color8BitEscapeCodes_Foreground_Unknown_ReturnsDefault()
    {
        var result = Color8BitEscapeCodes.Foreground((Color8Bit)42);
        await Assert.That(result).IsEqualTo("\x1b[38;5;42m");
    }
}
