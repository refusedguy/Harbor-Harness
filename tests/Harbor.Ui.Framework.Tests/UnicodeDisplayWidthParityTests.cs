using System.Text;
using Harbor.Abstractions.Extensions;
using TUnit.Assertions;
using UiWidth = Harbor.Ui.Framework.Rendering.UnicodeWidth;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
/// Issue #2646: the Contracts helper owns the width table for the table
/// formatter/measurer, while the renderer keeps its verbatim port. This guard
/// makes the two copies mechanically checkable instead of hand-synced: a drift
/// in either table fails here rather than silently desyncing a GFM grid.
/// </summary>
public class UnicodeDisplayWidthParityTests
{
    private static readonly int[] AstralSamples =
    [
        0x10000, 0x1F004, 0x1F300, 0x1F600, 0x1F642, 0x1F64F,
        0x1F680, 0x1F6FC, 0x1F900, 0x1FAFF, 0x20000, 0x2FFFD,
        0x30000, 0x3FFFD, 0xE0000, 0xE007F, 0xE0100, 0xE01EF, 0x10FFFF
    ];

    [Test]
    public async Task Helper_MatchesFacade_ForEveryBmpScalar()
    {
        for (int v = 0; v <= 0xFFFF; v++)
        {
            if (v is >= 0xD800 and <= 0xDFFF)
            {
                continue; // lone surrogates are not scalar values
            }

            var rune = new Rune(v);
            await Assert.That(UnicodeDisplayWidth.Width(rune)).IsEqualTo(UiWidth.Width(rune));
        }
    }

    [Test]
    public async Task Helper_MatchesFacade_ForAstralRangeBoundaries()
    {
        foreach (int v in AstralSamples)
        {
            var rune = new Rune(v);
            await Assert.That(UnicodeDisplayWidth.Width(rune)).IsEqualTo(UiWidth.Width(rune));
        }
    }
}
