// ToastOverlayGlyphTests.cs — #1543: the terminal toast overlay leads every
// row with the glyph the shared table names.
//
// WHAT THIS ASSERTS
// -----------------
// The overlay used to hold its own glyph switch. It was right, and it was free
// to drift: the desktop shell had already drifted from it (the same two kinds
// drawn as "▲" and "✕" here and as "!" and "✗" there), and nothing in the
// tree compared the two.
//
// So the assertion is not "the row starts with a check mark" — the golden
// frame pins that already. It is "the row starts with the glyph the TABLE
// names", which is the property that fails the day somebody re-introduces a
// second opinion about what a kind looks like.

using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Services;
using TUnit.Assertions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #1543 — the CellForge toast overlay paints the glyph the shared table
///     names.
/// </summary>
public class ToastOverlayGlyphTests
{
    /// <summary>
    ///     The toast row is "accent strip, glyph, space, message", so the
    ///     glyph is the first cell of the text run — the fourth column of the
    ///     painted strip, the accent taking the first three.
    /// </summary>
    private const int GlyphColumn = 3;

    /// <summary>
    ///     Every kind paints the glyph the shared table names.
    /// </summary>
    [Test]
    public async Task Every_ToastKind_Leads_Its_Row_With_The_Tables_Glyph()
    {
        foreach ((ToastKind kind, ToastPresentation presentation) in ToastPresentations.All)
        {
            var overlay = new ToastOverlay();
            overlay.Show("hello", kind);

            var buffer = new ScreenBuffer(40, 6);
            overlay.Paint(buffer, new Rect(0, 0, 40, 6)); // first pass drains the queue
            overlay.Paint(buffer, new Rect(0, 0, 40, 6)); // second pass paints

            await Assert.That(buffer.At(GlyphColumn, 0).Rune).IsEqualTo((int)presentation.Glyph)
                .Because($"{kind} must lead the toast row with the glyph the shared table names");
        }
    }
}
