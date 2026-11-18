using Harbor.Tui.CellForge.Rendering;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #2645: the one display-cell truncation owner. The two suffix variants
///     (hard clip vs ellipsis) and the Rune-width rules used to live in two
///     widget-local copies; these cases pin the unified behaviour so a future
///     refactor cannot silently regress to UTF-16 counting.
/// </summary>
public class UnicodeTruncationTests
{
    [Test]
    public async Task HardClip_KeepsLeadingCharsWithinBudget()
    {
        await Assert.That(UnicodeTruncation.Truncate("hello", 3, string.Empty)).IsEqualTo("hel");
        await Assert.That(UnicodeTruncation.Truncate("hi", 10, string.Empty)).IsEqualTo("hi");
        await Assert.That(UnicodeTruncation.Truncate("hi", 0, string.Empty)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Ellipsis_ReservesOneCellWithinBudget()
    {
        // Golden from ColumnsBlockTests.SingleOverWideItem_OneColumnAndTruncates.
        await Assert.That(UnicodeTruncation.Truncate("0123456789abcdef", 8, UnicodeTruncation.Ellipsis))
            .IsEqualTo("0123456…");
        await Assert.That(UnicodeTruncation.Truncate("abc", 1, UnicodeTruncation.Ellipsis))
            .IsEqualTo("…");
        // A text that already fits is returned untouched — no marker is added.
        await Assert.That(UnicodeTruncation.Truncate("abc", 3, UnicodeTruncation.Ellipsis))
            .IsEqualTo("abc");
        await Assert.That(UnicodeTruncation.Truncate("abcd", 3, UnicodeTruncation.Ellipsis))
            .IsEqualTo("ab…");
    }

    [Test]
    public async Task WideGlyphs_AreDroppedWholeNeverSplit()
    {
        string cut = UnicodeTruncation.Truncate("日本語", 3, string.Empty);
        await Assert.That(UnicodeWidth.Width(cut.AsSpan())).IsLessThanOrEqualTo(3);
        await Assert.That("日本語".StartsWith(cut, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task CombiningMarks_TravelWithTheirBaseAtZeroCost()
    {
        // "e" + U+0301 (acute) measures one cell: the mark must not be counted
        // as a second char the way StringTruncation would.
        const string decorated = "e\u0301x";
        string cut = UnicodeTruncation.Truncate(decorated, 1, string.Empty);
        await Assert.That(cut).IsEqualTo("e\u0301");
        await Assert.That(UnicodeWidth.Width(cut.AsSpan())).IsEqualTo(1);
    }

    [Test]
    public async Task MixedEmojiAndCjk_NeverExceedCellBudget()
    {
        const string text = "ab🚀c漢d";
        for (int cells = 1; cells <= UnicodeWidth.Width(text.AsSpan()) + 1; cells++)
        {
            foreach (string suffix in new[] { string.Empty, UnicodeTruncation.Ellipsis })
            {
                string cut = UnicodeTruncation.Truncate(text, cells, suffix);
                await Assert.That(UnicodeWidth.Width(cut.AsSpan())).IsLessThanOrEqualTo(cells);
            }
        }
    }

    [Test]
    public async Task NullEmptyAndDegenerateInputs_DoNotThrow()
    {
        await Assert.That(UnicodeTruncation.Truncate(null, 5, string.Empty)).IsEqualTo(string.Empty);
        await Assert.That(UnicodeTruncation.Truncate(string.Empty, 5, UnicodeTruncation.Ellipsis)).IsEqualTo(string.Empty);
        await Assert.That(UnicodeTruncation.Truncate("abc", -1, UnicodeTruncation.Ellipsis)).IsEqualTo(string.Empty);
    }
}
