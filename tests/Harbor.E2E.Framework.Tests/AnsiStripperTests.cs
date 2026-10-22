using Harbor.E2E.Framework;

namespace Harbor.E2E.Framework.Tests;

/// <summary>
///     Unit tests for <c>AnsiStripper</c> — the scrubber that turns a renderer's
///     raw ANSI byte stream into the plain text a test can search.
/// </summary>
/// <remarks>
///     <para>
///         The stripper is what makes the driver's text search work at all:
///         <c>TuiDriver</c> calls <c>Strip</c> on every chunk of the child's
///         output before the chunk reaches the rolling screen buffer, so a
///         renderer that colours its output does not break a <c>Contains</c> on
///         the visible text. Nothing tested that until now: the stripper had no
///         test of its own, and a regression in it surfaces as "the E2E suite
///         cannot find the text it is looking for", which reads like a flake
///         rather than like a broken scrubber.
///     </para>
///     <para>
///         The cases below are the ones the stripper's own doc comment claims:
///         CSI, OSC and bare escapes, plus the newline normalisation and the
///         NUL dropping that the renderers rely on.
///     </para>
/// </remarks>
public class AnsiStripperTests
{
    [Test]
    public async Task Strip_EmptyInput_ReturnsTheEmptyString()
    {
        await Assert.That(AnsiStripper.Strip(string.Empty)).IsEmpty();
        await Assert.That(AnsiStripper.Strip(null!)).IsEmpty();
    }

    [Test]
    public async Task Strip_PlainText_ComesBackUnchanged()
    {
        await Assert.That(AnsiStripper.Strip("harbor")).IsEqualTo("harbor");
    }

    [Test]
    public async Task Strip_ColourSequence_KeepsTheTextAndDropsTheEscape()
    {
        await Assert.That(AnsiStripper.Strip("\u001b[31mred\u001b[0m")).IsEqualTo("red");
    }

    [Test]
    public async Task Strip_OscTitleSequence_KeepsTheTextAndDropsTheEscape()
    {
        await Assert.That(AnsiStripper.Strip("\u001b]0;window title\u0007body")).IsEqualTo("body");
    }

    [Test]
    public async Task Strip_BareEscape_KeepsTheTextAndDropsTheEscape()
    {
        await Assert.That(AnsiStripper.Strip("\u001b=after")).IsEqualTo("after");
    }

    [Test]
    public async Task Strip_PrivateModeSequence_IsRemoved()
    {
        await Assert.That(AnsiStripper.Strip("\u001b[?25lhidden")).IsEqualTo("hidden");
    }

    [Test]
    public async Task Strip_CarriageReturnLineFeed_BecomesOneNewline()
    {
        await Assert.That(AnsiStripper.Strip("a\r\nb")).IsEqualTo("a\nb");
    }

    [Test]
    public async Task Strip_LoneCarriageReturn_BecomesANewline()
    {
        await Assert.That(AnsiStripper.Strip("a\rb")).IsEqualTo("a\nb");
    }

    [Test]
    public async Task Strip_NulByte_IsDropped()
    {
        await Assert.That(AnsiStripper.Strip("a\0b")).IsEqualTo("ab");
    }

    [Test]
    public async Task Strip_SequenceAtTheEndOfTheStream_IsRemoved()
    {
        await Assert.That(AnsiStripper.Strip("text\u001b[0m")).IsEqualTo("text");
    }
}
