using Harbor.Abstractions.Extensions;
using Harbor.Terminal.Abstractions.Rendering;
using TUnit.Assertions;

namespace Harbor.Terminal.Abstractions.Tests;

/// <summary>
/// Issue #2646: the string-grid formatter and the span-aware measurer must
/// resolve cell widths from the one canonical table, so a combining mark or
/// astral emoji no longer measures differently per rendering path.
/// </summary>
public class TableWidthConvergenceTests
{
    [Test]
    public async Task MeasureDesiredWidths_UsesCanonicalWidthForCombiningAndEmoji()
    {
        var table = new GfmTable(
            ["a", "b", "c", "d"],
            [["abcdef", "e\u0301e\u0301e\u0301", "中中中", "\U0001F642\U0001F642\U0001F642"]],
            [GfmAlign.Left, GfmAlign.Left, GfmAlign.Left, GfmAlign.Left]);

        int[] widths = TableMeasurer.MeasureDesiredWidths(table);
        // Combining acute is zero-width, CJK and emoji are two cells each.
        await Assert.That(widths).IsEquivalentTo([6, 3, 6, 6]);
    }

    [Test]
    public async Task Format_AllRowsShareTheCanonicalDisplayWidth()
    {
        var table = new GfmTable(
            ["key", "value"],
            [["ascii", "e\u0301中\U0001F642"]],
            [GfmAlign.Left, GfmAlign.Left]);

        var grid = GfmTableFormatter.Format(table, 40);
        int expected = UnicodeDisplayWidth.Width(grid[0].AsSpan());
        foreach (string row in grid)
        {
            await Assert.That(UnicodeDisplayWidth.Width(row.AsSpan())).IsEqualTo(expected);
        }

        await Assert.That(expected).IsLessThanOrEqualTo(40);
    }
}
