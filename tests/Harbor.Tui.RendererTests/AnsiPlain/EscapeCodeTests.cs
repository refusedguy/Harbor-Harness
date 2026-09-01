namespace Harbor.Tui.RendererTests;

using System.Text;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Tui.AnsiPlain.EscapeCodes;
using Harbor.Tui.RendererTests.Support;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

/// <summary>
///     Golden-frame regression for the generated ECMA-48 tables
///     (codegen-boilerplate sprint, Task 1): every sequence the AnsiPlain
///     renderer emits through the <see cref="EscapeCodes" /> tables is pinned
///     byte-for-byte, and the hand-written strategy is asserted equivalent to
///     the generated source of truth.
/// </summary>
public class EscapeCodeTests
{
    [Test]
    public async Task GeneratedTables_ComposeCanonicalFrame()
    {
        var frame = new StringBuilder();

        // Palette spans (8-bit SGR slots).
        frame.Append(EscapeCodes.Foreground(Color8Bit.Red));
        frame.Append("red");
        frame.Append(EscapeCodes.Foreground(Color8Bit.BrightCyan));
        frame.Append("bright-cyan");
        frame.Append(EscapeCodes.Background(Color8Bit.Blue));
        frame.Append("on-blue");
        frame.Append(EscapeCodes.BackgroundDefault);
        frame.Append(EscapeCodes.ForegroundDefault);
        frame.Append(EscapeCodes.Reset);

        // Truecolor SGR formatters (stackalloc, zero-alloc emission).
        frame.Append(FormatForeground(12, 34, 56));
        frame.Append("rgb-fg");
        frame.Append(FormatBackground(255, 128, 0));
        frame.Append("rgb-bg");
        frame.Append(EscapeCodes.Reset);

        // Cursor movement + positioning.
        frame.Append(FormatMove(CursorDirection.Up, 3));
        frame.Append("up3");
        frame.Append(FormatMove(CursorDirection.Backward, 7));
        frame.Append("back7");
        frame.Append(FormatPosition(12, 40));
        frame.Append("cup");
        frame.Append(EscapeCodes.ClearLine);

        // Style parameters (SGR decoration combos, generated order).
        frame.Append('\x1b').Append('[').Append(FormatStyle(StyleFlag.Bold | StyleFlag.Underline)).Append('m');
        frame.Append("bold-underline");
        frame.Append('\x1b').Append('[').Append(FormatStyle(StyleFlag.Strike | StyleFlag.Reverse)).Append('m');
        frame.Append("strike-reverse");
        frame.Append('\x1b').Append('[').Append(FormatStyle(StyleFlag.Italic | StyleFlag.Dim)).Append('m');
        frame.Append("italic-dim");
        frame.Append(EscapeCodes.Reset);

        // Screen control.
        frame.Append(EscapeCodes.HideCursor);
        frame.Append("hidden");
        frame.Append(EscapeCodes.ShowCursor);
        frame.Append(EscapeCodes.EnterAlternateScreen);
        frame.Append(EscapeCodes.ClearScreen);
        frame.Append("alt-screen");
        frame.Append(EscapeCodes.ExitAlternateScreen);

        await GoldenFrames.AssertGoldenAsync("escapecodes-tables", frame.ToString());
    }

    [Test]
    public async Task Strategy_SequenceEmission_IsGeneratedTableSourced()
    {
        var strategy = AnsiEscapeStrategy.Instance;

        // Control sequences byte-equal the generated tables.
        await Assert.That(strategy.Reset).IsEqualTo(EscapeCodes.Reset.ToString());
        await Assert.That(strategy.HideCursor).IsEqualTo(EscapeCodes.HideCursor.ToString());
        await Assert.That(strategy.ShowCursor).IsEqualTo(EscapeCodes.ShowCursor.ToString());
        await Assert.That(strategy.ClearLine).IsEqualTo(EscapeCodes.ClearLine.ToString());
        await Assert.That(strategy.ClearScreen).IsEqualTo(EscapeCodes.ClearScreen.ToString());
        await Assert.That(strategy.EnterAlternateScreen).IsEqualTo(EscapeCodes.EnterAlternateScreen.ToString());
        await Assert.That(strategy.ExitAlternateScreen).IsEqualTo(EscapeCodes.ExitAlternateScreen.ToString());

        // Truecolor SGR bytes equal the generated formatters.
        var color = TuiColorFromRgb(12, 200, 90);
        await Assert.That(strategy.Foreground(color)).IsEqualTo(FormatForeground(12, 200, 90));
        await Assert.That(strategy.Background(color)).IsEqualTo(FormatBackground(12, 200, 90));

        // Style param lists equal the generated FormatStyle mapping.
        await Assert.That(strategy.Style(TuiStyle.Bold)).IsEqualTo(FormatStyle(StyleFlag.Bold));
        await Assert.That(strategy.Style(TuiStyle.Dim | TuiStyle.Italic)).IsEqualTo(FormatStyle(StyleFlag.Dim | StyleFlag.Italic));
        await Assert.That(strategy.Style(TuiStyle.Underline | TuiStyle.Strike | TuiStyle.Reverse))
            .IsEqualTo(FormatStyle(StyleFlag.Underline | StyleFlag.Strike | StyleFlag.Reverse));
        await Assert.That(strategy.Style(TuiStyle.None)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task PaletteSlots_MapToStandardSgrCodes()
    {
        await Assert.That(EscapeCodes.Foreground(Color8Bit.Red).ToString()).IsEqualTo("\x1b[31m");
        await Assert.That(EscapeCodes.Foreground(Color8Bit.BrightBlack).ToString()).IsEqualTo("\x1b[90m");
        await Assert.That(EscapeCodes.Background(Color8Bit.Green).ToString()).IsEqualTo("\x1b[42m");
        await Assert.That(EscapeCodes.Background(Color8Bit.BrightWhite).ToString()).IsEqualTo("\x1b[107m");
        await Assert.That(EscapeCodes.Foreground(Color8Bit.Default).ToString()).IsEqualTo("\x1b[39m");
        await Assert.That(EscapeCodes.Background(Color8Bit.Default).ToString()).IsEqualTo("\x1b[49m");
    }

    private static string FormatForeground(byte r, byte g, byte b)
    {
        Span<char> buf = stackalloc char[EscapeCodes.RgbFormatLength];
        int n = EscapeCodes.FormatForeground(r, g, b, buf);
        return new string(buf[..n]);
    }

    private static string FormatBackground(byte r, byte g, byte b)
    {
        Span<char> buf = stackalloc char[EscapeCodes.RgbFormatLength];
        int n = EscapeCodes.FormatBackground(r, g, b, buf);
        return new string(buf[..n]);
    }

    private static string FormatMove(CursorDirection direction, int count)
    {
        Span<char> buf = stackalloc char[EscapeCodes.CsiFormatLength];
        int n = EscapeCodes.FormatMove(direction, count, buf);
        return new string(buf[..n]);
    }

    private static string FormatPosition(int row, int col)
    {
        Span<char> buf = stackalloc char[EscapeCodes.CsiFormatLength + 1];
        int n = EscapeCodes.FormatPosition(row, col, buf);
        return new string(buf[..n]);
    }

    private static string FormatStyle(StyleFlag style)
    {
        Span<char> buf = stackalloc char[EscapeCodes.StyleFormatLength];
        int n = EscapeCodes.FormatStyle(style, buf);
        return n == 0 ? string.Empty : new string(buf[..n]);
    }

    private static Harbor.Terminal.Abstractions.Renderers.TuiColor TuiColorFromRgb(byte r, byte g, byte b) => new(r, g, b);
}
